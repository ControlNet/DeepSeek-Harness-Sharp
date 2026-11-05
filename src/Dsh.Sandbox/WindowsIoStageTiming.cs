using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dsh.Sandbox;

// Temporary numeric observation with a bounded registry, monotonic stamps and a drained timer.
// Formatting is confined to samples; call boundaries only update fixed numeric storage.
internal static class WindowsIoStageTiming
{
    // Numeric payload maximum: collections * (slots * (184 + 3 * operations * 8)
    // + roots * 28) + threadSamples * 32 + provenance * 12 + categories * 4
    // + applies * phases * 8 + testPhases * 8, using x64 CLR field layout.
    // Reference arrays add (provenance + applies + collections) * IntPtr.Size bytes.
    // This excludes object headers, existing work/path objects, formatting and BCL snapshots.
    // Each resource sample retains managed BCL thread objects for O(own-process threads).
    // Process.Threads internally queries a system-wide native process/thread buffer before filtering this process.
    // Its transient native allocation scales with system processes and threads, not the captured slot capacity.
    // The fixed numeric payload is therefore not a bound on total diagnostic allocation.
    // At most CollectionCapacity * SlotCapacity captured slots are searched, with one CPU query per matching stable slot.
    // One timer and one process-wide filtered listener exist only during the observed test scope.
    // Directory IDs add one atomic and a relative-name scan; entry accounting adds sequence atomics.
    // Native operation pairs add two clock reads and seven atomic updates before sampling costs.
    private const int ApplyCapacity = 16;
    private const int CollectionCapacity = 16;
    private const int SlotCapacity = 257; // Caller 0, actual current-source workers 1..256.
    private const int RootCapacity = 256;
    private const int ProvenanceCapacity = 4096;
    private const int ThreadSampleCapacity = CollectionCapacity * SlotCapacity;
    private const int MaximumNumericArrayBytes = CollectionCapacity
        * (SlotCapacity * (184 + 3 * (int)Operation.Count * sizeof(long))
            + RootCapacity * 28)
        + ThreadSampleCapacity * 32 + ProvenanceCapacity * 12 + (int)RootCategory.Count * sizeof(int)
        + ApplyCapacity * (int)Phase.Count * sizeof(long) + (int)TestPhase.Count * sizeof(long);
    private static readonly int MaximumReferenceArrayBytes =
        (ProvenanceCapacity + ApplyCapacity + CollectionCapacity) * IntPtr.Size;
    private const int PreDeadlineSeconds = 59;
    private static Observation? _observation;
    [ThreadStatic] private static bool _threadIdAttempted;
    [ThreadStatic] private static uint _nativeThreadId;

    private static uint CaptureThreadId()
    {
        if (_threadIdAttempted) return _nativeThreadId;
        _threadIdAttempted = true;
        try
        {
            if (OperatingSystem.IsWindows()) _nativeThreadId = GetCurrentThreadId();
        }
        catch (Exception) { }
        return _nativeThreadId;
    }

    internal struct ThreadSnapshot
    {
        internal int Sequence, Root, Admission, Operation;
        internal uint ThreadId;
        internal int Captured;
        internal long Entered;
    }

    internal enum Phase { CollectionStart, CollectionComplete, AncestorsStart, AncestorsComplete, GrantsStart, FirstProof, FirstMutation, GrantsComplete, ApplyFinal, Count }
    internal enum Operation { None, Open, Query, CloseDispose, QueueWait, RootAdmissionWait, RootName, WorkerJoin, Count }
    internal enum TestPhase { Start, BackendStart, BackendComplete, ConnectStart, ConnectComplete, PermittedStart, PermittedComplete, DeniedStart, DeniedComplete, AssertionsComplete, Count }

    internal enum RootCategory
    {
        Unknown, Sdk, Packs, Manifests, Shared, Host, Executable,
        ExplicitReadOnly, ExplicitWritable, ExplicitDenied, Count
    }
    internal readonly record struct Provenance(RootCategory Category, int SourceOrdinal);

    internal static bool IsObserving => Volatile.Read(ref _observation) is not null;

    internal static void RuntimeRoot(string original, RootCategory category)
    {
        var observation = Volatile.Read(ref _observation);
        if (observation is null) return;
        try { observation.RecordRuntimeRoot(original, category); }
        catch (Exception error) when (error is not ThreadInterruptedException) { observation.Failed(); }
    }

    internal static void PolicyRoot(object key, string original, RootCategory category, int sourceOrdinal)
    {
        var observation = Volatile.Read(ref _observation);
        if (observation is null) return;
        try { observation.RecordPolicyRoot(key, original, category, sourceOrdinal); }
        catch (Exception error) when (error is not ThreadInterruptedException) { observation.Failed(); }
    }

    internal static Provenance RootProvenance(object key, RootCategory knownCategory = RootCategory.Unknown)
    {
        var observation = Volatile.Read(ref _observation);
        if (observation is null) return default;
        try
        {
            var provenance = observation.FindProvenance(key);
            return provenance.Category == RootCategory.Unknown ? new Provenance(knownCategory, 0) : provenance;
        }
        catch (Exception error) when (error is not ThreadInterruptedException) { observation.Failed(); return new Provenance(knownCategory, 0); }
    }

    internal static Observation? Observe()
    {
        try { return new Observation(); }
        catch (Exception) { return null; }
    }

    internal static Apply? BeginApply()
    {
        var observation = Volatile.Read(ref _observation);
        if (observation is null) return null;
        try { return observation.AddApply(); }
        catch (Exception) { observation.Failed(); return null; }
    }

    internal static Collection? BeginCollection(Apply? apply)
    {
        var observation = Volatile.Read(ref _observation);
        if (observation is null) return null;
        try { return observation.AddCollection(apply?.Ordinal ?? 0); }
        catch (Exception) { observation.Failed(); return null; }
    }

    internal sealed class Observation : IAsyncDisposable
    {
        private readonly Apply?[] _applies = new Apply?[ApplyCapacity];
        private readonly Collection?[] _collections = new Collection?[CollectionCapacity];
        private readonly long[] _testPhases = new long[(int)TestPhase.Count];
        private readonly ThreadSnapshot[] _threadSamples = new ThreadSnapshot[ThreadSampleCapacity];
        private readonly object?[] _provenanceKeys = new object?[ProvenanceCapacity];
        private readonly int[] _provenanceKinds = new int[ProvenanceCapacity];
        private readonly Provenance[] _provenance = new Provenance[ProvenanceCapacity];
        private readonly int[] _categoryOrdinals = new int[(int)RootCategory.Count];
        private readonly object _provenanceGate = new();
        private int _provenanceCount, _provenanceOverflow;
        private readonly Timer _timer;
        private readonly NumericTraceListener _listener = new();
        private readonly object _dumpGate = new();
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _applyCount, _collectionCount, _errors, _sample, _disposed;
        private long _previousCpu = -1, _previousCpuStamp, _previousPause = -1, _previousPauseStamp;
        private CpuSample? _previousMachine;

        internal Observation()
        {
            CaptureResourceBaseline();
            _timer = new Timer(static state => ((Observation)state!).Sample(), this,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            try
            {
                Trace.Listeners.Add(_listener);
                _timer.Change(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
                if (Interlocked.CompareExchange(ref _observation, this, null) is not null)
                    throw new InvalidOperationException("An IO observation is already active.");
            }
            catch (Exception)
            {
                _timer.Dispose();
                Trace.Listeners.Remove(_listener);
                throw;
            }
        }

        internal Apply? AddApply()
        {
            var index = Interlocked.Increment(ref _applyCount) - 1;
            if ((uint)index >= ApplyCapacity) return null;
            var apply = new Apply(index + 1);
            Volatile.Write(ref _applies[index], apply);
            return apply;
        }

        internal Collection? AddCollection(int apply)
        {
            var index = Interlocked.Increment(ref _collectionCount) - 1;
            if ((uint)index >= CollectionCapacity) return null;
            var collection = new Collection(index + 1, apply);
            Volatile.Write(ref _collections[index], collection);
            return collection;
        }

        internal void Failed() => Interlocked.Increment(ref _errors);

        internal void RecordRuntimeRoot(string original, RootCategory category)
        {
            lock (_provenanceGate)
            {
                for (var index = 0; index < _provenanceCount; index++)
                    if (_provenanceKinds[index] == 0 && _provenanceKeys[index] is string path
                        && string.Equals(path, original, StringComparison.OrdinalIgnoreCase))
                        return;
                var ordinal = ++_categoryOrdinals[(int)category];
                if (ordinal <= 0) { Failed(); StoreProvenance(original, 0, default); return; }
                StoreProvenance(original, 0, new Provenance(category, ordinal));
            }
        }

        internal void RecordPolicyRoot(object key, string original, RootCategory category, int sourceOrdinal)
        {
            lock (_provenanceGate)
            {
                var provenance = new Provenance(category, category == RootCategory.Unknown ? 0 : sourceOrdinal);
                var runtimeCategory = category is RootCategory.Unknown or RootCategory.Host or RootCategory.Executable;
                var matched = false;
                if (runtimeCategory)
                    for (var index = 0; index < _provenanceCount; index++)
                        if (_provenanceKinds[index] == 0 && _provenanceKeys[index] is string path
                            && string.Equals(path, original, StringComparison.OrdinalIgnoreCase)
                            && (category == RootCategory.Unknown || _provenance[index].Category == category))
                        {
                            provenance = _provenance[index];
                            matched = true;
                            break;
                        }
                if (!matched && category is RootCategory.Host or RootCategory.Executable)
                {
                    var ordinal = ++_categoryOrdinals[(int)category];
                    provenance = ordinal > 0 ? new Provenance(category, ordinal) : default;
                    if (ordinal <= 0) Failed();
                    StoreProvenance(original, 0, provenance);
                }
                StoreProvenance(key, 1, provenance);
            }
        }

        private void StoreProvenance(object key, int kind, Provenance provenance)
        {
            if (_provenanceCount == ProvenanceCapacity) { _provenanceOverflow++; return; }
            _provenanceKeys[_provenanceCount] = key;
            _provenanceKinds[_provenanceCount] = kind;
            _provenance[_provenanceCount++] = provenance;
        }

        internal Provenance FindProvenance(object key)
        {
            lock (_provenanceGate)
                for (var index = 0; index < _provenanceCount; index++)
                    if (_provenanceKinds[index] == 1 && ReferenceEquals(_provenanceKeys[index], key))
                        return _provenance[index];
            return default;
        }
        internal void MarkTest(TestPhase phase) =>
            Volatile.Write(ref _testPhases[(int)phase], Stopwatch.GetTimestamp());

        private void Sample()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var sample = Interlocked.Increment(ref _sample);
            if (sample == 3)
            {
                try
                {
                    var due = TimeSpan.FromSeconds(PreDeadlineSeconds) - Stopwatch.GetElapsedTime(_started);
                    _timer.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                }
                catch (Exception) { Failed(); }
            }
            if (sample <= 4) Dump(sample);
        }

        private void Dump(int sample)
        {
            try
            {
                lock (_dumpGate)
                {
                    DumpResources(sample);
                    Trace.WriteLine($"IO_STAGE sample={sample} final={(sample == 5 ? 1 : 0)} requestedSeconds={(sample == 4 ? PreDeadlineSeconds : sample < 4 ? sample * 15 : -1)} algorithmSource=c8b2e8684dd7f1d37604549d6fedfb5ac260782b requiredPreConsumers=234 requiredMcpBudgetMs=60000 started={_started} ticks={Stopwatch.GetTimestamp()} frequency={Stopwatch.Frequency} applies={Volatile.Read(ref _applyCount)} applyOverflow={Math.Max(0, Volatile.Read(ref _applyCount) - ApplyCapacity)} collections={Volatile.Read(ref _collectionCount)} collectionOverflow={Math.Max(0, Volatile.Read(ref _collectionCount) - CollectionCapacity)} observerErrors={Volatile.Read(ref _errors)} listenerErrors={_listener.Errors} scope=managedNativeCallBoundary totals=overlapping notWallPartitions=1 unknownIdle=1 closeIsSafeHandleDisposeBoundary=1");
                    Trace.WriteLine($"IO_STAGE_PROVENANCE sample={sample} retained={Volatile.Read(ref _provenanceCount)} capacity={ProvenanceCapacity} overflow={Volatile.Read(ref _provenanceOverflow)} preObservationRuntimeRootsUnknown=1 unknownCategory=0 unknownSourceOrdinal=0 categoryOrdinalScope=runtimeObservedSourcesOrExplicitCategoryInputs firstKnownRuntimeSourceWins=1 registryLookup=boundedLinear noPathHashes=1 numericArrayBytesMaximum={MaximumNumericArrayBytes} referenceArrayBytesMaximum={MaximumReferenceArrayBytes} headersAndOtherObservationObjectsExcluded=1");
                    for (var phase = 0; phase < _testPhases.Length; phase++)
                        Trace.WriteLine($"IO_STAGE_TEST sample={sample} phase={(TestPhase)phase} ticks={Volatile.Read(ref _testPhases[phase])} known={(Volatile.Read(ref _testPhases[phase]) == 0 ? 0 : 1)}");
                    foreach (var apply in _applies) apply?.Dump(sample);
                    foreach (var collection in _collections) collection?.Dump(sample);
                }
            }
            catch (Exception) { Failed(); }
        }

        private void CaptureResourceBaseline()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                _previousCpu = process.TotalProcessorTime.Ticks;
                _previousCpuStamp = Stopwatch.GetTimestamp();
            }
            catch (Exception) { _previousCpu = -1; }
            try
            {
                _previousPause = GC.GetTotalPauseDuration().Ticks;
                _previousPauseStamp = Stopwatch.GetTimestamp();
            }
            catch (Exception) { _previousPause = -1; }
            _previousMachine = ReadMachineCpu(out _);
        }

        private void DumpResources(int sample)
        {
            var started = Stopwatch.GetTimestamp();
            for (var index = 0; index < _collections.Length; index++)
                _collections[index]?.CaptureThreads(_threadSamples.AsSpan(index * SlotCapacity, SlotCapacity));
            Process? process = null;
            ProcessThreadCollection? threads = null;
            var collectionError = 0;
            var disposalErrors = 0;
            var threadSnapshotStarted = Stopwatch.GetTimestamp();
            var threadSnapshotFinished = threadSnapshotStarted;
            try
            {
                try
                {
                    process = Process.GetCurrentProcess();
                    threads = process.Threads;
                }
                catch (Exception error) { collectionError = error.HResult; }
                threadSnapshotFinished = Stopwatch.GetTimestamp();
                DumpProcessCpu(sample, process);
                DumpGcPause(sample);
                DumpMachine(sample);
                for (var index = 0; index < _collections.Length; index++)
                    _collections[index]?.DumpThreads(sample,
                        _threadSamples.AsSpan(index * SlotCapacity, SlotCapacity), threads,
                        threadSnapshotStarted, threadSnapshotFinished, collectionError);
            }
            finally
            {
                if (threads is not null)
                    foreach (ProcessThread thread in threads)
                        try { thread.Dispose(); }
                        catch (Exception) { disposalErrors++; Failed(); }
                try { process?.Dispose(); }
                catch (Exception) { disposalErrors++; Failed(); }
            }
            Trace.WriteLine($"IO_STAGE_RESOURCE_COST sample={sample} started={started} finished={Stopwatch.GetTimestamp()} ownProcessThreads={(threads is null ? -1 : threads.Count)} threadCollectionKnown={(threads is null ? 0 : 1)} threadCollectionError={collectionError} disposalErrors={disposalErrors} fixedNumericArrayBytesMaximum={MaximumNumericArrayBytes} fixedThreadSnapshotBytes={ThreadSampleCapacity * 32} capturedSlotCapacity={ThreadSampleCapacity} threadCpuQueriesMaximum={ThreadSampleCapacity} bclRetainedManagedStorageOwnThreadsOnly=1 bclNativeQuerySystemWide=1 bclTransientNativeStorageBoundedBySlots=0 bclTransientNativeBytesUnknown=1 onlyOwnThreadMetadataEmitted=1 samplingAndLoggingPerturbationUnknown=1");
        }

        private void DumpProcessCpu(int sample, Process? process)
        {
            long cpu = -1;
            var error = 0;
            if (process is not null)
                try { cpu = process.TotalProcessorTime.Ticks; }
                catch (Exception failure) { error = failure.HResult; }
            var stamp = Stopwatch.GetTimestamp();
            var deltaKnown = cpu >= 0 && _previousCpu >= 0 && cpu >= _previousCpu && stamp > _previousCpuStamp;
            Trace.WriteLine($"IO_STAGE_PROCESS_CPU sample={sample} known={(cpu >= 0 ? 1 : 0)} error={error} cpu100ns={cpu} deltaKnown={(deltaKnown ? 1 : 0)} delta100ns={(deltaKnown ? cpu - _previousCpu : -1)} intervalTicks={(deltaKnown ? stamp - _previousCpuStamp : -1)} ticks={stamp} processors={Environment.ProcessorCount} includesObserverAndOtherProcessThreads=1");
            _previousCpu = cpu;
            _previousCpuStamp = stamp;
        }

        private void DumpGcPause(int sample)
        {
            long pause = -1;
            var error = 0;
            try { pause = GC.GetTotalPauseDuration().Ticks; }
            catch (Exception failure) { error = failure.HResult; }
            var stamp = Stopwatch.GetTimestamp();
            var deltaKnown = pause >= 0 && _previousPause >= 0 && pause >= _previousPause && stamp > _previousPauseStamp;
            Trace.WriteLine($"IO_STAGE_GC_PAUSE sample={sample} known={(pause >= 0 ? 1 : 0)} error={error} cumulative100ns={pause} deltaKnown={(deltaKnown ? 1 : 0)} delta100ns={(deltaKnown ? pause - _previousPause : -1)} intervalTicks={(deltaKnown ? stamp - _previousPauseStamp : -1)} ticks={stamp} processWideNotThreadAttribution=1 completedGcPauseCounter=1");
            _previousPause = pause;
            _previousPauseStamp = stamp;
        }

        private void DumpMachine(int sample)
        {
            var current = ReadMachineCpu(out var cpuStatus);
            var previous = _previousMachine;
            _previousMachine = current;
            long idle = -1, kernel = -1, user = -1, interval = -1;
            if (current is { } now && previous is { } before
                && now.Idle >= before.Idle && now.Kernel >= before.Kernel && now.User >= before.User
                && now.Ticks > before.Ticks)
            {
                idle = (long)(now.Idle - before.Idle);
                kernel = (long)(now.Kernel - before.Kernel);
                user = (long)(now.User - before.User);
                interval = now.Ticks - before.Ticks;
            }
            Trace.WriteLine($"IO_STAGE_MACHINE_CPU sample={sample} known={(current is null ? 0 : 1)} status={cpuStatus} deltaKnown={(interval > 0 ? 1 : 0)} idleDelta100ns={idle} kernelDelta100ns={kernel} userDelta100ns={user} intervalTicks={interval} kernelIncludesIdle=1");
            var memoryKnown = false;
            var memoryStatus = OperatingSystem.IsWindows() ? 1 : 3;
            PerformanceInformation memory = default;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    memoryKnown = GetPerformanceInfo(out memory, (uint)Marshal.SizeOf<PerformanceInformation>());
                    memoryStatus = memoryKnown ? 0 : 1;
                }
            }
            catch (Exception) { memoryStatus = 4; }
            Trace.WriteLine($"IO_STAGE_MACHINE_MEMORY sample={sample} known={(memoryKnown ? 1 : 0)} status={memoryStatus} physicalAvailablePages={(memoryKnown ? (long)memory.PhysicalAvailable : -1)} pageSize={(memoryKnown ? (long)memory.PageSize : -1)} ticks={Stopwatch.GetTimestamp()}");
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.CompareExchange(ref _observation, null, this);
            try { await _timer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { Failed(); }
            Dump(5);
            try
            {
                Trace.Listeners.Remove(_listener);
                _listener.Dispose();
            }
            catch (Exception) { Failed(); }
        }
    }

    private sealed class NumericTraceListener : TraceListener
    {
        private int _errors;
        internal int Errors => Volatile.Read(ref _errors);
        public override void Write(string? message) => WriteLine(message);
        public override void WriteLine(string? message)
        {
            if (message is null || !message.StartsWith("IO_STAGE", StringComparison.Ordinal)) return;
            try { Console.Error.WriteLineAsync(message).GetAwaiter().GetResult(); }
            catch (Exception) { Interlocked.Increment(ref _errors); }
        }
    }

    internal sealed class Apply(int ordinal)
    {
        private readonly long[] _phases = new long[(int)Phase.Count];
        private int _paths, _grants, _proofEntered, _proofExited, _mutationEntered, _mutationExited;
        internal int Ordinal { get; } = ordinal;
        internal void Mark(Phase phase, int paths = -1)
        {
            if (paths >= 0) Volatile.Write(ref _paths, paths);
            if (Volatile.Read(ref _phases[(int)phase]) == 0)
                Interlocked.CompareExchange(ref _phases[(int)phase], Stopwatch.GetTimestamp(), 0);
        }
        internal void Granted() => Interlocked.Increment(ref _grants);
        internal void ProofEntered() { Mark(Phase.FirstProof); Interlocked.Increment(ref _proofEntered); }
        internal void ProofExited() => Interlocked.Increment(ref _proofExited);
        internal void MutationEntered() { Mark(Phase.FirstMutation); Interlocked.Increment(ref _mutationEntered); }
        internal void MutationExited() => Interlocked.Increment(ref _mutationExited);
        internal void Dump(int sample)
        {
            Trace.WriteLine($"IO_STAGE_APPLY sample={sample} apply={Ordinal} paths={Volatile.Read(ref _paths)} grantsCompleted={Volatile.Read(ref _grants)} proofEntered={Volatile.Read(ref _proofEntered)} proofExited={Volatile.Read(ref _proofExited)} mutationEntered={Volatile.Read(ref _mutationEntered)} mutationExited={Volatile.Read(ref _mutationExited)} mutationsAreNativeWriteBoundaries=1 successfulWritesUnknown=1 ticks={Stopwatch.GetTimestamp()}");
            Trace.WriteLine($"IO_STAGE_DURATION sample={sample} apply={Ordinal} collectionTicks={Duration(Phase.CollectionStart, Phase.CollectionComplete)} ancestorTicks={Duration(Phase.AncestorsStart, Phase.AncestorsComplete)} grantTicks={Duration(Phase.GrantsStart, Phase.GrantsComplete)} unknownDuration=-1");
            for (var phase = 0; phase < _phases.Length; phase++)
                Trace.WriteLine($"IO_STAGE_PHASE sample={sample} apply={Ordinal} phase={(Phase)phase} ticks={Volatile.Read(ref _phases[phase])} known={(Volatile.Read(ref _phases[phase]) == 0 ? 0 : 1)}");
        }
        private long Duration(Phase start, Phase end)
        {
            var entered = Volatile.Read(ref _phases[(int)start]);
            var exited = Volatile.Read(ref _phases[(int)end]);
            return entered == 0 || exited == 0 ? -1 : exited - entered;
        }
    }

    internal sealed class Collection(int ordinal, int apply)
    {
        // Directory identity and progress live only in fixed active slots; no pending-work registry.
        private readonly Slot[] _slots = CreateSlots();
        private readonly long[] _enters = new long[SlotCapacity * (int)Operation.Count];
        private readonly long[] _exits = new long[SlotCapacity * (int)Operation.Count];
        private readonly long[] _elapsed = new long[SlotCapacity * (int)Operation.Count];
        private readonly long[] _rootEntered = new long[RootCapacity];
        private readonly long[] _rootCompleted = new long[RootCapacity];
        private readonly int[] _rootAdmission = new int[RootCapacity];
        private readonly Provenance[] _rootProvenance = new Provenance[RootCapacity];
        private long _nextDirectory;
        private int _numericOverflow;
        private readonly long _started = Stopwatch.GetTimestamp();
        private long _finished;
        private int _workers, _roots, _errors, _slotOverflow, _stopKind;

        private struct Slot
        {
            internal int Sequence, Root, Admission, Operation, Assigned, Retired;
            internal uint ThreadId;
            internal int Depth;
            internal long Entered, Exited, Entries, DirectoriesCompleted;
            internal long Directory, DirectoryStarted, Queries, Returned, Accepted;
            internal long QueryTicks, MaximumQueryTicks, LastQueryTicks, DirectoriesStarted;
            internal long TotalQueries, TotalReturned, TotalQueryTicks, MaximumDirectoryQueries, MaximumDirectoryTicks;
            internal long TotalMaximumQueryTicks;
        }

        private static Slot[] CreateSlots()
        {
            var slots = new Slot[SlotCapacity];
            slots[0].ThreadId = CaptureThreadId();
            return slots;
        }

        internal int Worker()
        {
            var slot = Interlocked.Increment(ref _workers);
            if (slot >= SlotCapacity) { Interlocked.Increment(ref _slotOverflow); return -1; }
            _slots[slot].ThreadId = CaptureThreadId();
            Volatile.Write(ref _slots[slot].Assigned, 1);
            return slot;
        }

        internal int Root(int admission, Provenance provenance)
        {
            var root = Interlocked.Increment(ref _roots);
            if (root <= RootCapacity)
            {
                Volatile.Write(ref _rootAdmission[root - 1], admission);
                _rootProvenance[root - 1] = provenance;
                Volatile.Write(ref _rootEntered[root - 1], Stopwatch.GetTimestamp());
            }
            return root;
        }

        internal void RootCompleted(int root)
        {
            if (root > 0 && root <= RootCapacity)
                Interlocked.CompareExchange(ref _rootCompleted[root - 1], Stopwatch.GetTimestamp(), 0);
        }

        // One ID atomic and one scan of the existing relative name per admitted directory.
        // ID order is active admission/allocation order, not dequeue or discovery/publication order.
        // Parent identity is unavailable.
        internal void BeginDirectory(int slot, ReadOnlySpan<char> relativePath)
        {
            if ((uint)slot >= SlotCapacity) return;
            var directory = Interlocked.Increment(ref _nextDirectory);
            if (directory <= 0) Interlocked.Increment(ref _numericOverflow);
            var depth = relativePath.IsEmpty ? 0 : 1;
            foreach (var character in relativePath)
                if (character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar)
                    depth++;
            ref var state = ref _slots[slot];
            Interlocked.Increment(ref state.Sequence);
            state.Directory = directory > 0 ? directory : 0;
            state.Depth = depth;
            state.DirectoryStarted = Stopwatch.GetTimestamp();
            state.Queries = state.Returned = state.Accepted = state.QueryTicks = state.MaximumQueryTicks = state.LastQueryTicks = 0;
            state.DirectoriesStarted++;
            Interlocked.Increment(ref state.Sequence);
        }

        internal void EndDirectory(int slot)
        {
            if ((uint)slot >= SlotCapacity) return;
            ref var state = ref _slots[slot];
            if (state.DirectoryStarted == 0) return;
            Interlocked.Increment(ref state.Sequence);
            state.MaximumDirectoryQueries = Math.Max(state.MaximumDirectoryQueries, state.Queries);
            state.MaximumDirectoryTicks = Math.Max(state.MaximumDirectoryTicks,
                Stopwatch.GetTimestamp() - state.DirectoryStarted);
            state.Directory = state.DirectoryStarted = 0;
            state.Queries = state.Returned = state.Accepted = state.QueryTicks = state.MaximumQueryTicks = state.LastQueryTicks = 0;
            state.Depth = -1;
            Interlocked.Increment(ref state.Sequence);
        }

        internal void ReturnedEntry(int slot)
        {
            if ((uint)slot >= SlotCapacity) return;
            ref var state = ref _slots[slot];
            Interlocked.Increment(ref state.Sequence);
            state.Returned++;
            state.TotalReturned++;
            Interlocked.Increment(ref state.Sequence);
        }

        internal void Bind(int slot, int root, int admission)
        {
            if ((uint)slot >= SlotCapacity) return;
            ref var state = ref _slots[slot];
            Interlocked.Increment(ref state.Sequence);
            state.Root = root;
            state.Admission = admission;
            state.Assigned = 1;
            Interlocked.Increment(ref state.Sequence);
        }

        internal void Enter(int slot, Operation operation)
        {
            if ((uint)slot >= SlotCapacity) return;
            ref var state = ref _slots[slot];
            Interlocked.Increment(ref state.Sequence);
            if (state.Operation != 0) Interlocked.Increment(ref _errors);
            state.Entered = Stopwatch.GetTimestamp();
            state.Operation = (int)operation;
            if (operation == Operation.Query) { state.Queries++; state.TotalQueries++; }
            Interlocked.Increment(ref _enters[slot * (int)Operation.Count + (int)operation]);
            Interlocked.Increment(ref state.Sequence);
        }

        internal void Exit(int slot, Operation operation)
        {
            if ((uint)slot >= SlotCapacity) return;
            ref var state = ref _slots[slot];
            Interlocked.Increment(ref state.Sequence);
            var now = Stopwatch.GetTimestamp();
            if (state.Operation != (int)operation) Interlocked.Increment(ref _errors);
            var index = slot * (int)Operation.Count + (int)operation;
            if (operation == Operation.Query)
            {
                var elapsed = now - state.Entered;
                state.QueryTicks += elapsed;
                state.TotalQueryTicks += elapsed;
                state.LastQueryTicks = elapsed;
                state.MaximumQueryTicks = Math.Max(state.MaximumQueryTicks, elapsed);
                state.TotalMaximumQueryTicks = Math.Max(state.TotalMaximumQueryTicks, elapsed);
            }
            Interlocked.Add(ref _elapsed[index], now - state.Entered);
            Interlocked.Increment(ref _exits[index]);
            state.Exited = now;
            state.Operation = 0;
            Interlocked.Increment(ref state.Sequence);
        }

        internal void Retire(int slot)
        {
            if ((uint)slot >= SlotCapacity) return;
            ref var state = ref _slots[slot];
            Interlocked.Increment(ref state.Sequence);
            state.Retired = 1;
            Interlocked.Increment(ref state.Sequence);
        }
        internal void Finished() => Volatile.Write(ref _finished, Stopwatch.GetTimestamp());
        // 0 unknown/not stopped, 1 cancellation, 2 interruption, 3 other primary failure.
        internal void Stopped(int kind) => Interlocked.CompareExchange(ref _stopKind, kind, 0);
        internal void EntryCollected(int slot)
        {
            if ((uint)slot >= SlotCapacity) return;
            ref var state = ref _slots[slot];
            Interlocked.Increment(ref state.Sequence);
            state.Entries++;
            state.Accepted++;
            Interlocked.Increment(ref state.Sequence);
        }
        internal void DirectoryCompleted(int slot)
        {
            if ((uint)slot < SlotCapacity) Interlocked.Increment(ref _slots[slot].DirectoriesCompleted);
        }

        internal void CaptureThreads(Span<ThreadSnapshot> snapshots)
        {
            for (var slot = 0; slot < SlotCapacity; slot++)
            {
                snapshots[slot] = default;
                ref var state = ref _slots[slot];
                if (Volatile.Read(ref state.Assigned) == 0) continue;
                var sequence = Volatile.Read(ref state.Sequence);
                var snapshot = new ThreadSnapshot
                {
                    Sequence = sequence,
                    Root = Volatile.Read(ref state.Root),
                    Admission = Volatile.Read(ref state.Admission),
                    Operation = Volatile.Read(ref state.Operation),
                    ThreadId = Volatile.Read(ref state.ThreadId),
                    Entered = Volatile.Read(ref state.Entered)
                };
                var retired = Volatile.Read(ref state.Retired);
                var coherent = (sequence & 1) == 0 && sequence == Volatile.Read(ref state.Sequence);
                if (snapshot.Operation == 0 || retired != 0) continue;
                snapshot.Captured = coherent ? 1 : 2;
                snapshots[slot] = snapshot;
            }
        }

        internal void DumpThreads(int sample, ReadOnlySpan<ThreadSnapshot> snapshots,
            ProcessThreadCollection? threads, long snapshotStarted, long snapshotFinished, int collectionError)
        {
            for (var slot = 0; slot < snapshots.Length; slot++)
            {
                ref readonly var snapshot = ref snapshots[slot];
                if (snapshot.Captured == 0) continue;
                var state = -1;
                var reason = -1;
                long cpu = -1;
                var found = false;
                var metadataError = 0;
                if (snapshot.Captured == 1 && snapshot.ThreadId != 0 && threads is not null)
                    foreach (ProcessThread thread in threads)
                    {
                        try
                        {
                            if (unchecked((uint)thread.Id) != snapshot.ThreadId) continue;
                        }
                        catch (Exception error) { metadataError = error.HResult; continue; }
                        found = true;
                        try
                        {
                            var threadState = thread.ThreadState;
                            state = (int)threadState;
                            if (threadState == System.Diagnostics.ThreadState.Wait)
                                reason = (int)thread.WaitReason;
                        }
                        catch (Exception error) { metadataError = error.HResult; }
                        try { cpu = thread.TotalProcessorTime.Ticks; }
                        catch (Exception error) { metadataError = error.HResult; }
                        break;
                    }
                var currentSequence = Volatile.Read(ref _slots[slot].Sequence);
                var stable = snapshot.Captured == 1 && snapshot.Sequence == currentSequence;
                if (!stable) { state = -1; reason = -1; cpu = -1; }
                var stateKnown = state >= 0 && state != (int)System.Diagnostics.ThreadState.Unknown;
                var reasonKnown = reason >= 0 && reason != (int)ThreadWaitReason.Unknown;
                // The OS state snapshot is bracketed by the same active operation sequence.
                // State, CPU and GC counters still do not form an atomic snapshot or establish a wait cause.
                Trace.WriteLine($"IO_STAGE_THREAD sample={sample} collection={ordinal} slot={slot} root={snapshot.Root} admission={snapshot.Admission} operation={(Operation)snapshot.Operation} sequence={snapshot.Sequence} sequenceAfter={currentSequence} entered={snapshot.Entered} tid={snapshot.ThreadId} tidKnown={(snapshot.ThreadId == 0 ? 0 : 1)} capturedCoherent={(snapshot.Captured == 1 ? 1 : 0)} stable={(stable ? 1 : 0)} threadCollectionKnown={(threads is null ? 0 : 1)} threadCollectionError={collectionError} threadFound={(found ? 1 : 0)} metadataError={metadataError} stateKnown={(stateKnown ? 1 : 0)} state={state} waitReasonKnown={(reasonKnown ? 1 : 0)} waitReason={reason} cpuKnown={(cpu >= 0 ? 1 : 0)} cpu100ns={cpu} osSnapshotStarted={snapshotStarted} osSnapshotFinished={snapshotFinished} ticks={Stopwatch.GetTimestamp()} atomicSnapshot=0 waitCauseUnknown=1");
            }
        }

        internal void Dump(int sample)
        {
            long paths = 0, directories = 0;
            for (var slot = 0; slot < SlotCapacity; slot++)
            {
                paths += Volatile.Read(ref _slots[slot].Entries);
                directories += Volatile.Read(ref _slots[slot].DirectoriesCompleted);
            }
            Trace.WriteLine($"IO_STAGE_PROGRESS sample={sample} collection={ordinal} discoveredPaths={paths} directoriesReadToCompletion={directories} atomicSnapshot=0");
            Trace.WriteLine($"IO_STAGE_COLLECTION sample={sample} collection={ordinal} apply={apply} started={_started} finished={Volatile.Read(ref _finished)} stopKind={Volatile.Read(ref _stopKind)} workersEntered={Volatile.Read(ref _workers)} roots={Volatile.Read(ref _roots)} rootOverflow={Math.Max(0, Volatile.Read(ref _roots) - RootCapacity)} slotOverflow={Volatile.Read(ref _slotOverflow)} observerErrors={Volatile.Read(ref _errors)} ticks={Stopwatch.GetTimestamp()}");
            for (var operation = 1; operation < (int)Operation.Count; operation++)
            {
                long entered = 0, exited = 0, elapsed = 0;
                for (var slot = 0; slot < SlotCapacity; slot++)
                {
                    var index = slot * (int)Operation.Count + operation;
                    entered += Volatile.Read(ref _enters[index]);
                    exited += Volatile.Read(ref _exits[index]);
                    elapsed += Volatile.Read(ref _elapsed[index]);
                }
                Trace.WriteLine($"IO_STAGE_TOTAL sample={sample} collection={ordinal} operation={(Operation)operation} entered={entered} exited={exited} completedTicks={elapsed} atomicSnapshot=0");
            }
            long directoryStarts = 0, queries = 0, returned = 0, queryTicks = 0;
            long maximumQueryTicks = 0, maximumDirectoryQueries = 0, maximumDirectoryTicks = 0;
            var incoherentSlots = 0;
            for (var slot = 0; slot < SlotCapacity; slot++)
            {
                ref var state = ref _slots[slot];
                if (Volatile.Read(ref state.Assigned) == 0) continue;
                var sequence = Volatile.Read(ref state.Sequence);
                var captured = state;
                Thread.MemoryBarrier();
                var sequenceAfter = Volatile.Read(ref state.Sequence);
                var coherent = (sequence & 1) == 0 && sequence == sequenceAfter;
                var now = Stopwatch.GetTimestamp();
                if (!coherent) incoherentSlots++;
                else
                {
                    directoryStarts += captured.DirectoriesStarted;
                    queries += captured.TotalQueries;
                    returned += captured.TotalReturned;
                    queryTicks += captured.TotalQueryTicks;
                    maximumQueryTicks = Math.Max(maximumQueryTicks, captured.TotalMaximumQueryTicks);
                    maximumDirectoryQueries = Math.Max(maximumDirectoryQueries,
                        Math.Max(captured.MaximumDirectoryQueries, captured.Directory == 0 ? 0 : captured.Queries));
                    maximumDirectoryTicks = Math.Max(maximumDirectoryTicks,
                        Math.Max(captured.MaximumDirectoryTicks, captured.Directory == 0 ? 0 : now - captured.DirectoryStarted));
                }
                if (captured.Retired == 1 && captured.Operation == 0 && coherent) continue;
                if (!coherent) captured = default;
                Trace.WriteLine($"IO_STAGE_SLOT sample={sample} collection={ordinal} slot={slot} root={captured.Root} admission={captured.Admission} operation={(Operation)captured.Operation} sequence={sequence} sequenceAfter={sequenceAfter} tid={captured.ThreadId} tidKnown={(coherent && captured.ThreadId != 0 ? 1 : 0)} entered={captured.Entered} exited={captured.Exited} retired={captured.Retired} coherent={(coherent ? 1 : 0)} directory={captured.Directory} directoryKnown={(coherent && captured.Directory > 0 ? 1 : 0)} parentKnown=0 parentIdentityNotRecorded=1 depth={(coherent && captured.Directory > 0 ? captured.Depth : -1)} directoryStarted={captured.DirectoryStarted} directoryAgeTicks={(coherent && captured.Directory > 0 ? now - captured.DirectoryStarted : -1)} directoryQueries={captured.Queries} parsedReturnedEntries={captured.Returned} acceptedEntries={captured.Accepted} directoryCompletedQueryTicks={captured.QueryTicks} directoryMaximumCompletedQueryTicks={captured.MaximumQueryTicks} lastCompletedQueryTicks={captured.LastQueryTicks} countsKnown={(coherent ? 1 : 0)} ticks={now}");
            }
            Trace.WriteLine($"IO_STAGE_DIRECTORY_TOTAL sample={sample} collection={ordinal} directoryStarts={directoryStarts} queriesStarted={queries} parsedReturnedEntries={returned} completedQueryTicks={queryTicks} maximumCompletedQueryTicks={maximumQueryTicks} maximumDirectoryQueries={maximumDirectoryQueries} maximumDirectoryTicks={maximumDirectoryTicks} maximaIncludeActiveDirectories=1 activeQueryElapsedExcluded=1 incoherentSlotsExcluded={incoherentSlots} totalsKnown={(incoherentSlots == 0 ? 1 : 0)} atomicSnapshot=0 directoryIdOverflow={Volatile.Read(ref _numericOverflow)} otherNumericCounterOverflowUnknown=1 directoryIdScope=collection directoryIdOrder=admission parsedReturnedEntriesMayExcludeUnvisitedReturnedRecords=1");
            var roots = Math.Min(Volatile.Read(ref _roots), RootCapacity);
            for (var root = 0; root < roots; root++)
            {
                var entered = Volatile.Read(ref _rootEntered[root]);
                var provenance = entered == 0 ? default : _rootProvenance[root];
                Trace.WriteLine($"IO_STAGE_ROOT sample={sample} collection={ordinal} root={root + 1} admission={Volatile.Read(ref _rootAdmission[root])} category={(int)provenance.Category} categoryKnown={(provenance.Category == RootCategory.Unknown ? 0 : 1)} sourceOrdinal={provenance.SourceOrdinal} sourceOrdinalKnown={(provenance.SourceOrdinal > 0 ? 1 : 0)} entered={entered} completed={Volatile.Read(ref _rootCompleted[root])}");
            }
        }
    }

    // Machine status is 0 for success, 1 for an unavailable API result, 2 for a non-single processor group, 3 for unsupported OS and 4 for an exception.
    private static CpuSample? ReadMachineCpu(out int status)
    {
        status = 3;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (GetActiveProcessorGroupCount() != 1) { status = 2; return null; }
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                status = 0;
                return new CpuSample(idle, kernel, user, Stopwatch.GetTimestamp());
            }
            status = 1;
        }
        catch (Exception) { status = 4; }
        return null;
    }

    private readonly record struct CpuSample(ulong Idle, ulong Kernel, ulong User, long Ticks);

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
}
