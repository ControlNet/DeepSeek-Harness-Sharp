using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;

namespace Dsh.Sandbox;

// Temporary diagnostic instrumentation, not a production implementation.
internal static class WindowsSandboxTiming
{
    private const int ProgressInterval = 4096;
    private const int SnapshotIntervalSeconds = 15;
    private const int MaximumSnapshots = 3;
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

    internal sealed class Costs
    {
        private readonly long[] _counts = new long[(int)CostStage.Count];
        private readonly long[] _ticks = new long[(int)CostStage.Count];
        private readonly object _collectionGate = new();
        private readonly List<Collection> _collections = [];
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

        public void Snapshot(long observation, int snapshot)
        {
            Emit("snapshot", observation, snapshot);
            lock (_collectionGate)
                foreach (var collection in _collections)
                    collection.Emit("snapshot", observation, snapshot);
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
        private readonly Timer _timer;
        private int _snapshots;
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
            // Timer delivery is best-effort, including during ThreadPool starvation.
            try
            {
                lock (_gate)
                {
                    if (_disposed || _snapshots == MaximumSnapshots)
                        return;
                    var snapshot = ++_snapshots;
                    var now = Stopwatch.GetTimestamp();
                    Write($"SANDBOX_SNAPSHOT pid={Environment.ProcessId} observation={_id} snapshot={snapshot} owners={_owners.Count} ticks={now} elapsed-ticks={now - _started} frequency={Stopwatch.Frequency}");
                    foreach (var costs in _owners)
                        costs.Snapshot(_id, snapshot);
                    _runtime.Emit(_id, snapshot);
                }
            }
            catch (Exception) { }
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
