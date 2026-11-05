using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Dsh.Sandbox;

// Temporary diagnostic instrumentation, not a production implementation.
internal static class WindowsSandboxTiming
{
    private const int ProgressInterval = 4096;
    private const int SnapshotIntervalSeconds = 15;
    private const int MaximumSnapshots = 3;
    private const int ActiveSlotCapacity = 32;
    [ThreadStatic] private static uint _nativeThreadId;
    private static long _nextOwner;
    private static long _nextObservation;
    private static readonly object ObserverGate = new();
    private static Observation? _observer;

    internal static Observation Observe() => new();

    internal static IDisposable? Register(Costs costs)
    {
        lock (ObserverGate)
            return _observer?.Register(costs);
    }

    public static void Mark(Costs owner, string phase, int count = 0, int child = 0) =>
        Write($"SANDBOX_PHASE pid={Environment.ProcessId} owner={owner.Owner} child={child} phase={phase} count={count} ticks={Stopwatch.GetTimestamp()} frequency={Stopwatch.Frequency}");

    public static void Progress(Costs owner, string phase, int count)
    {
        if (count % ProgressInterval == 0)
            Mark(owner, phase, count);
    }

    private static void Write(string message)
    {
        // A diagnostic sink must never replace the original native error or cancellation.
        try { Trace.WriteLine(message); }
        catch (Exception) { }
    }

    internal enum CostStage
    {
        EntryOpen,
        GetSecurityInfo,
        DescriptorMaterialize,
        DaclMaterialize,
        StableAclValidation,
        AmbientAclValidation,
        InheritedGrantValidation,
        ProofDescriptorImage,
        ProofCacheLookup,
        AccessCheck,
        ProofCacheInsert,
        InheritanceProjection,
        WriteAclMaterialize,
        SetSecurityInfo,
        Count
    }

    internal enum NativeStage
    {
        CollectorOpen,
        CollectorQuery,
        ReadEntryOpen
    }

    // Slots contain only numeric operation identity, never paths, descriptors or handles.
    internal sealed class ActiveSlot
    {
        public long Version;
        public uint ThreadId;
        public NativeStage Stage;
        public long Started;
    }

    internal readonly struct NativeActivity : IDisposable
    {
        private readonly ActiveSlot? _slot;
        private readonly long _version;

        internal NativeActivity(ActiveSlot slot, long version)
        {
            _slot = slot;
            _version = version;
        }

        public void Dispose()
        {
            if (_slot is not null)
                Interlocked.CompareExchange(ref _slot.Version, 0, _version);
        }

    }

    internal sealed class Costs
    {
        private readonly long[] _counts = new long[(int)CostStage.Count];
        private readonly long[] _ticks = new long[(int)CostStage.Count];
        private readonly object _collectionGate = new();
        private readonly List<Collection> _collections = [];
        private readonly ActiveSlot[] _active = CreateActiveSlots();
        private long _nextActivity;
        public long Owner { get; } = Interlocked.Increment(ref _nextOwner);

        public void Add(Collection collection)
        {
            lock (_collectionGate)
                _collections.Add(collection);
        }

        public void Remove(Collection collection)
        {
            lock (_collectionGate)
                _collections.Remove(collection);
        }

        private static ActiveSlot[] CreateActiveSlots()
        {
            var slots = new ActiveSlot[ActiveSlotCapacity];
            for (var index = 0; index < slots.Length; index++)
                slots[index] = new ActiveSlot();
            return slots;
        }

        public NativeActivity ObserveNative(NativeStage stage) => StartNative(stage);

        private NativeActivity StartNative(NativeStage stage)
        {
            if (Volatile.Read(ref _observer) is null || !OperatingSystem.IsWindows())
                return default;
            try
            {
                if (_nativeThreadId == 0)
                    _nativeThreadId = GetCurrentThreadId();
                if (_nativeThreadId == 0)
                    return default;
                foreach (var slot in _active)
                {
                    if (Interlocked.CompareExchange(ref slot.Version, -1, 0) != 0)
                        continue;
                    slot.ThreadId = _nativeThreadId;
                    slot.Stage = stage;
                    slot.Started = Stopwatch.GetTimestamp();
                    var version = Interlocked.Increment(ref _nextActivity);
                    Volatile.Write(ref slot.Version, version);
                    return new NativeActivity(slot, version);
                }
            }
            catch (Exception) { }
            // Full/unsupported observation is omitted; business operations never wait for a slot.
            return default;
        }

        private void EmitNative(long observation, int snapshot)
        {
            if (!OperatingSystem.IsWindows())
                return;
            try
            {
                using var process = Process.GetCurrentProcess();
                var threads = process.Threads;
                try
                {
                    foreach (var slot in _active)
                        EmitNativeSlot(slot, threads, observation, snapshot);
                }
                finally
                {
                    foreach (ProcessThread thread in threads)
                        thread.Dispose();
                }
            }
            catch (Exception) { }
        }

        private void EmitNativeSlot(ActiveSlot slot, ProcessThreadCollection threads, long observation, int snapshot)
        {
            if (!OperatingSystem.IsWindows())
                return;
            var version = Volatile.Read(ref slot.Version);
            if (version <= 0)
                return;
            var threadId = slot.ThreadId;
            var stage = slot.Stage;
            var started = slot.Started;
            int? state = null, reason = null;
            foreach (ProcessThread thread in threads)
            {
                try
                {
                    if ((uint)thread.Id != threadId)
                        continue;
                    var threadState = thread.ThreadState;
                    state = (int)threadState;
                    if (threadState == System.Diagnostics.ThreadState.Wait)
                    {
                        try { reason = (int)thread.WaitReason; }
                        catch (Exception) { }
                    }
                    break;
                }
                catch (Exception) { }
            }
            if (Volatile.Read(ref slot.Version) != version)
                return;
            // OS thread state and slot identity are not an atomic snapshot. A matching slot
            // can finish just after this check; neither state nor wait reason identifies a cause.
            var stateFields = state is { } value ? $" state={value}" : "";
            var reasonFields = reason is { } wait ? $" wait-reason={wait}" : "";
            Write($"SANDBOX_NATIVE pid={Environment.ProcessId} owner={Owner} observation={observation} snapshot={snapshot} operation={stage} tid={threadId} started-ticks={started} ticks={Stopwatch.GetTimestamp()} frequency={Stopwatch.Frequency}{stateFields}{reasonFields}");
        }

        public void Snapshot(long observation, int snapshot)
        {
            Emit("snapshot", observation, snapshot);
            lock (_collectionGate)
                foreach (var collection in _collections)
                    collection.Emit("snapshot", observation, snapshot);
            EmitNative(observation, snapshot);
        }

        public void Record(CostStage stage, long started)
        {
            var elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref _ticks[(int)stage], elapsed);
            Interlocked.Increment(ref _counts[(int)stage]);
        }

        public void Progress(int count)
        {
            if (count % ProgressInterval == 0)
                Emit("grants-progress");
        }

        // Worker intervals overlap. A live snapshot is approximate, not a wall-clock partition.
        public void Emit(string phase, long observation = 0, int snapshot = 0)
        {
            for (var index = 0; index < (int)CostStage.Count; index++)
                Write($"SANDBOX_COST pid={Environment.ProcessId} owner={Owner} observation={observation} snapshot={snapshot} phase={phase} stage={(CostStage)index} count={Interlocked.Read(ref _counts[index])} ticks={Interlocked.Read(ref _ticks[index])} frequency={Stopwatch.Frequency}");
        }
    }

    internal sealed class Collection : IDisposable
    {
        private readonly Costs _owner;
        private readonly int _root;
        private long _openStarted, _openCount, _openTicks, _queryStarted, _queryCount, _queryTicks;
        private long _entries, _files, _directories, _published;

        public Collection(Costs owner, int root)
        {
            _owner = owner;
            _root = root;
            owner.Add(this);
        }

        public void Dispose() => _owner.Remove(this);

        public void OpenStarted() => Interlocked.Increment(ref _openStarted);
        public void QueryStarted() => Interlocked.Increment(ref _queryStarted);
        public NativeActivity ObserveNative(NativeStage stage) => _owner.ObserveNative(stage);

        public void Open(long started)
        {
            Interlocked.Add(ref _openTicks, Stopwatch.GetTimestamp() - started);
            Interlocked.Increment(ref _openCount);
        }

        public void Query(long started)
        {
            Interlocked.Add(ref _queryTicks, Stopwatch.GetTimestamp() - started);
            Interlocked.Increment(ref _queryCount);
        }

        public void Entry(bool directory)
        {
            if (directory)
                Interlocked.Increment(ref _directories);
            else
                Interlocked.Increment(ref _files);
            var entries = Interlocked.Increment(ref _entries);
            if (entries == 1)
                Emit("collect-first-entry");
            else if (entries % ProgressInterval == 0)
                Emit("collect-progress");
        }

        public void Published() => Interlocked.Increment(ref _published);

        // Read completed first so the approximate live in-flight difference cannot be negative.
        // Final emission follows every worker join and therefore has exact completed totals.
        public void Emit(string phase, long observation = 0, int snapshot = 0)
        {
            var openCompleted = Interlocked.Read(ref _openCount);
            var openStarted = Interlocked.Read(ref _openStarted);
            var queryCompleted = Interlocked.Read(ref _queryCount);
            var queryStarted = Interlocked.Read(ref _queryStarted);
            Write($"SANDBOX_COLLECTION pid={Environment.ProcessId} owner={_owner.Owner} root={_root} observation={observation} snapshot={snapshot} phase={phase} entries={Interlocked.Read(ref _entries)} files={Interlocked.Read(ref _files)} directories={Interlocked.Read(ref _directories)} published={Interlocked.Read(ref _published)} open-count={openCompleted} open-started={openStarted} open-completed={openCompleted} open-inflight={openStarted - openCompleted} open-ticks={Interlocked.Read(ref _openTicks)} query-count={queryCompleted} query-started={queryStarted} query-completed={queryCompleted} query-inflight={queryStarted - queryCompleted} query-ticks={Interlocked.Read(ref _queryTicks)} ticks={Stopwatch.GetTimestamp()} frequency={Stopwatch.Frequency}");
        }
    }

    // Only this explicitly scoped test observer retains active numeric owners; never leases or paths.
    internal sealed class Observation : IDisposable
    {
        private readonly object _gate = new();
        private readonly HashSet<Costs> _owners = [];
        private readonly long _id = Interlocked.Increment(ref _nextObservation);
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly RuntimeCounters _runtime = new();
        private readonly MachineCounters _machine = new();
        private readonly Timer _timer;
        private int _snapshots;
        private int _snapshotRunning;
        private bool _disposed;

        public Observation()
        {
            _timer = new Timer(static state => ((Observation)state!).Snapshot(), this,
                TimeSpan.FromSeconds(SnapshotIntervalSeconds), TimeSpan.FromSeconds(SnapshotIntervalSeconds));
            lock (ObserverGate)
                _observer = this;
        }

        internal IDisposable Register(Costs costs)
        {
            lock (_gate)
            {
                if (!_disposed)
                    _owners.Add(costs);
            }
            return new Registration(this, costs);
        }

        private void Unregister(Costs costs)
        {
            lock (_gate)
                _owners.Remove(costs);
        }

        private void Snapshot()
        {
            // At most three scheduled samples: approximately 15, 30 and 45 seconds.
            // Skip overlapping callbacks; timer delivery and native state reads are best-effort.
            if (Interlocked.CompareExchange(ref _snapshotRunning, 1, 0) != 0)
                return;
            try
            {
                Costs[] owners;
                int snapshot;
                lock (_gate)
                {
                    if (_disposed || _snapshots == MaximumSnapshots)
                        return;
                    snapshot = ++_snapshots;
                    owners = _owners.ToArray();
                }
                // Never hold the owner registration gate across OS queries or trace callbacks.
                var now = Stopwatch.GetTimestamp();
                Write($"SANDBOX_SNAPSHOT pid={Environment.ProcessId} observation={_id} snapshot={snapshot} owners={owners.Length} ticks={now} elapsed-ticks={now - _started} frequency={Stopwatch.Frequency}");
                foreach (var costs in owners)
                    costs.Snapshot(_id, snapshot);
                _runtime.Emit(_id, snapshot);
                _machine.Emit(_id, snapshot);
            }
            catch (Exception) { }
            finally { Volatile.Write(ref _snapshotRunning, 0); }
        }

        public void Dispose()
        {
            lock (ObserverGate)
                if (ReferenceEquals(_observer, this))
                    _observer = null;
            lock (_gate)
            {
                _disposed = true;
                _owners.Clear();
            }
            // Join queued/running callbacks before the test removes its numeric trace sink.
            using var drained = new ManualResetEvent(false);
            if (_timer.Dispose(drained))
                drained.WaitOne();
            _runtime.Dispose();
        }

        private sealed class Registration(Observation observation, Costs costs) : IDisposable
        {
            public void Dispose() => observation.Unregister(costs);
        }
    }

    private sealed class MachineCounters
    {
        private CpuSample? _previous;
        private ProcessSample? _process;

        public MachineCounters()
        {
            _previous = ReadCpu();
            _process = ReadProcessCpu();
        }

        public void Emit(long observation, int snapshot)
        {
            EmitCpu(observation, snapshot);
            EmitProcessCpu(observation, snapshot);
            EmitMemory(observation, snapshot);
        }

        private void EmitCpu(long observation, int snapshot)
        {
            var current = ReadCpu();
            var previous = _previous;
            _previous = current;
            if (current is not { } now || previous is not { } before
                || now.Idle < before.Idle || now.Kernel < before.Kernel || now.User < before.User)
                return;
            var idle = now.Idle - before.Idle;
            // GetSystemTimes kernel time includes idle; values aggregate logical CPUs.
            // ReadCpu omits multi-group systems rather than label one group's load machine-wide.
            var total = (double)(now.Kernel - before.Kernel) + (now.User - before.User);
            if (total <= 0 || idle > total)
                return;
            var busy = (total - idle) * 100 / total;
            Write($"SANDBOX_MACHINE_CPU pid={Environment.ProcessId} observation={observation} snapshot={snapshot} busy-percent={busy.ToString("R", CultureInfo.InvariantCulture)} idle-100ns={idle} total-100ns={total.ToString("R", CultureInfo.InvariantCulture)} interval-ticks={now.Ticks - before.Ticks} frequency={Stopwatch.Frequency}");
        }

        private void EmitProcessCpu(long observation, int snapshot)
        {
            var current = ReadProcessCpu();
            var previous = _process;
            _process = current;
            if (current is not { } now || previous is not { } before
                || now.CpuTicks < before.CpuTicks || now.Ticks <= before.Ticks)
                return;
            // Process CPU is capacity-normalized over this observation interval, unlike the
            // independent latest System.Runtime cpu-usage event's own sampling interval.
            var busy = (now.CpuTicks - before.CpuTicks) / (double)TimeSpan.TicksPerSecond
                / ((now.Ticks - before.Ticks) / (double)Stopwatch.Frequency)
                / Environment.ProcessorCount * 100;
            Write($"SANDBOX_PROCESS_CPU pid={Environment.ProcessId} observation={observation} snapshot={snapshot} busy-percent={busy.ToString("R", CultureInfo.InvariantCulture)} cpu-100ns={now.CpuTicks - before.CpuTicks} processors={Environment.ProcessorCount} interval-ticks={now.Ticks - before.Ticks} frequency={Stopwatch.Frequency}");
        }

        private static CpuSample? ReadCpu()
        {
            if (!OperatingSystem.IsWindows())
                return null;
            try
            {
                if (GetActiveProcessorGroupCount() != 1)
                    return null;
                if (GetSystemTimes(out var idle, out var kernel, out var user))
                    return new CpuSample(idle, kernel, user, Stopwatch.GetTimestamp());
            }
            catch (Exception) { }
            return null;
        }

        private static ProcessSample? ReadProcessCpu()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return new ProcessSample(process.TotalProcessorTime.Ticks, Stopwatch.GetTimestamp());
            }
            catch (Exception) { }
            return null;
        }

        private static void EmitMemory(long observation, int snapshot)
        {
            if (!OperatingSystem.IsWindows())
                return;
            try
            {
                if (!GetPerformanceInfo(out var info, (uint)Marshal.SizeOf<PerformanceInformation>()))
                    return;
                // These are pages, not bytes: commit headroom is not free physical RAM.
                Write($"SANDBOX_MACHINE_MEMORY pid={Environment.ProcessId} observation={observation} snapshot={snapshot} physical-available-pages={info.PhysicalAvailable} physical-total-pages={info.PhysicalTotal} commit-total-pages={info.CommitTotal} commit-limit-pages={info.CommitLimit} page-size={info.PageSize}");
                if (info.CommitLimit >= info.CommitTotal)
                    Write($"SANDBOX_MACHINE_COMMIT pid={Environment.ProcessId} observation={observation} snapshot={snapshot} available-pages={info.CommitLimit - info.CommitTotal} page-size={info.PageSize}");
            }
            catch (Exception) { }
        }

        private readonly record struct CpuSample(ulong Idle, ulong Kernel, ulong User, long Ticks);
        private readonly record struct ProcessSample(long CpuTicks, long Ticks);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak;
        public nuint PhysicalTotal, PhysicalAvailable, SystemCache;
        public nuint KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("psapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation information, uint size);

    private sealed class RuntimeCounters : EventListener
    {
        private const string CounterIntervalSeconds = "5";
        private static readonly string[] Names =
        [
            "cpu-usage", "gc-heap-size", "time-in-gc", "alloc-rate",
            "gen-0-gc-count", "gen-1-gc-count", "gen-2-gc-count",
            "threadpool-queue-length", "threadpool-thread-count", "threadpool-completed-items-count"
        ];
        private readonly object _gate = new();
        private readonly Sample?[] _samples = new Sample?[Names.Length];
        private bool _ready;

        public RuntimeCounters() => Volatile.Write(ref _ready, true);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name != "System.Runtime")
                return;
            try
            {
                EnableEvents(eventSource, EventLevel.Informational, EventKeywords.All,
                    new Dictionary<string, string?> { ["EventCounterIntervalSec"] = CounterIntervalSeconds });
            }
            catch (Exception) { }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            // EventListener's base constructor can call back before instance fields exist.
            if (!Volatile.Read(ref _ready) || eventData.EventSource.Name != "System.Runtime"
                || eventData.EventName != "EventCounters")
                return;
            try
            {
                if (eventData.Payload is not { Count: > 0 } payload
                    || payload[0] is not IDictionary<string, object> counter
                    || !counter.TryGetValue("Name", out var name) || name is not string counterName)
                    return;
                var index = Array.IndexOf(Names, counterName);
                if (index < 0 || !counter.TryGetValue("CounterType", out var type))
                    return;
                var field = (type as string) switch
                {
                    "Mean" => "Mean",
                    "Sum" => "Increment",
                    _ => null
                };
                if (field is null || !counter.TryGetValue(field, out var raw))
                    return;
                var value = raw switch
                {
                    double number => number,
                    float number => number,
                    long number => number,
                    int number => number,
                    _ => double.NaN
                };
                if (!double.IsFinite(value))
                    return;
                lock (_gate)
                    if (_ready)
                        _samples[index] = new Sample(value, field == "Mean", Stopwatch.GetTimestamp());
            }
            catch (Exception) { }
        }


        public void Emit(long observation, int snapshot)
        {
            lock (_gate)
                for (var index = 0; index < _samples.Length; index++)
                    if (_samples[index] is { } sample)
                        Write($"SANDBOX_RUNTIME pid={Environment.ProcessId} observation={observation} snapshot={snapshot} counter={Names[index]} field={(sample.Mean ? "mean" : "increment")} value={sample.Value.ToString("R", CultureInfo.InvariantCulture)} sample-ticks={sample.Ticks} ticks={Stopwatch.GetTimestamp()} frequency={Stopwatch.Frequency}");
        }

        public override void Dispose()
        {
            lock (_gate)
            {
                Volatile.Write(ref _ready, false);
                Array.Clear(_samples);
            }
            base.Dispose();
        }

        private readonly record struct Sample(double Value, bool Mean, long Ticks);
    }

    // Overlapping worker operation intervals are not a wall-clock partition.
    internal static Measurement Measure(Costs? costs, CostStage stage) => new(costs, stage);

    internal readonly struct Measurement(Costs? costs, CostStage stage) : IDisposable
    {
        private readonly long _started = costs is null ? 0 : Stopwatch.GetTimestamp();
        public void Dispose() => costs?.Record(stage, _started);
    }
}
