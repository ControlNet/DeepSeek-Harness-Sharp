using System.Diagnostics;

namespace Dsh.Sandbox;

// Temporary numeric observation with a bounded registry, monotonic stamps and a drained timer.
// Formatting is confined to samples; call boundaries only update fixed numeric storage.
internal static class WindowsIoStageTiming
{
    // At most 16 collections retain about 816 KiB of numeric array payload, excluding array and object headers.
    // One timer and one process-wide filtered listener exist only during the observed test scope.
    // Every observed operation pair adds two clock reads and seven atomic updates before sampling costs.
    private const int ApplyCapacity = 16;
    private const int CollectionCapacity = 16;
    private const int SlotCapacity = 256; // Caller 0, actual demand-started workers 1..255.
    private const int RootCapacity = 256;
    private const int PreDeadlineSeconds = 59;
    private static Observation? _observation;

    internal enum Phase { CollectionStart, CollectionComplete, AncestorsStart, AncestorsComplete, GrantsStart, FirstProof, GrantsComplete, ApplyFinal, Count }
    internal enum Operation { None, Open, Query, CloseDispose, QueueWait, RootAdmissionWait, Count }
    internal enum TestPhase { Start, BackendStart, BackendComplete, ConnectStart, ConnectComplete, PermittedStart, PermittedComplete, DeniedStart, DeniedComplete, AssertionsComplete, Count }

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
        private readonly Timer _timer;
        private readonly NumericTraceListener _listener = new();
        private readonly object _dumpGate = new();
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _applyCount, _collectionCount, _errors, _sample, _disposed;

        internal Observation()
        {
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
                    Trace.WriteLine($"IO_STAGE sample={sample} final={(sample == 5 ? 1 : 0)} requestedSeconds={(sample == 4 ? PreDeadlineSeconds : sample < 4 ? sample * 15 : -1)} started={_started} ticks={Stopwatch.GetTimestamp()} frequency={Stopwatch.Frequency} applies={Volatile.Read(ref _applyCount)} applyOverflow={Math.Max(0, Volatile.Read(ref _applyCount) - ApplyCapacity)} collections={Volatile.Read(ref _collectionCount)} collectionOverflow={Math.Max(0, Volatile.Read(ref _collectionCount) - CollectionCapacity)} observerErrors={Volatile.Read(ref _errors)} listenerErrors={_listener.Errors} scope=managedNativeCallBoundary totals=overlapping notWallPartitions=1 unknownIdle=1 closeIsSafeHandleDisposeBoundary=1");
                    for (var phase = 0; phase < _testPhases.Length; phase++)
                        Trace.WriteLine($"IO_STAGE_TEST sample={sample} phase={(TestPhase)phase} ticks={Volatile.Read(ref _testPhases[phase])} known={(Volatile.Read(ref _testPhases[phase]) == 0 ? 0 : 1)}");
                    foreach (var apply in _applies) apply?.Dump(sample);
                    foreach (var collection in _collections) collection?.Dump(sample);
                }
            }
            catch (Exception) { Failed(); }
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
        private int _paths, _grants;
        internal int Ordinal { get; } = ordinal;
        internal void Mark(Phase phase, int paths = -1)
        {
            if (paths >= 0) Volatile.Write(ref _paths, paths);
            if (Volatile.Read(ref _phases[(int)phase]) == 0)
                Interlocked.CompareExchange(ref _phases[(int)phase], Stopwatch.GetTimestamp(), 0);
        }
        internal void Granted() => Interlocked.Increment(ref _grants);
        internal void Dump(int sample)
        {
            Trace.WriteLine($"IO_STAGE_APPLY sample={sample} apply={Ordinal} paths={Volatile.Read(ref _paths)} grantsCompleted={Volatile.Read(ref _grants)} ticks={Stopwatch.GetTimestamp()}");
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
        // No directory-specific observer objects, delegates, identifiers or native handles.
        private readonly Slot[] _slots = new Slot[SlotCapacity];
        private readonly long[] _enters = new long[SlotCapacity * (int)Operation.Count];
        private readonly long[] _exits = new long[SlotCapacity * (int)Operation.Count];
        private readonly long[] _elapsed = new long[SlotCapacity * (int)Operation.Count];
        private readonly long[] _rootEntered = new long[RootCapacity];
        private readonly long[] _rootCompleted = new long[RootCapacity];
        private readonly int[] _rootAdmission = new int[RootCapacity];
        private readonly long _started = Stopwatch.GetTimestamp();
        private long _finished;
        private int _workers, _roots, _errors, _slotOverflow, _stopKind;

        private struct Slot
        {
            internal int Sequence, Root, Admission, Operation, Assigned, Retired;
            internal long Entered, Exited;
        }

        internal int Worker()
        {
            var slot = Interlocked.Increment(ref _workers);
            if (slot >= SlotCapacity) { Interlocked.Increment(ref _slotOverflow); return -1; }
            Volatile.Write(ref _slots[slot].Assigned, 1);
            return slot;
        }

        internal int Root(int admission)
        {
            var root = Interlocked.Increment(ref _roots);
            if (root <= RootCapacity)
            {
                Volatile.Write(ref _rootAdmission[root - 1], admission);
                Volatile.Write(ref _rootEntered[root - 1], Stopwatch.GetTimestamp());
            }
            return root;
        }

        internal void RootCompleted(int root)
        {
            if (root > 0 && root <= RootCapacity)
                Interlocked.CompareExchange(ref _rootCompleted[root - 1], Stopwatch.GetTimestamp(), 0);
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

        internal void Dump(int sample)
        {
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
            for (var slot = 0; slot < SlotCapacity; slot++)
            {
                ref var state = ref _slots[slot];
                if (Volatile.Read(ref state.Assigned) == 0) continue;
                var sequence = Volatile.Read(ref state.Sequence);
                var root = Volatile.Read(ref state.Root);
                var admission = Volatile.Read(ref state.Admission);
                var operation = Volatile.Read(ref state.Operation);
                var entered = Volatile.Read(ref state.Entered);
                var exited = Volatile.Read(ref state.Exited);
                var retired = Volatile.Read(ref state.Retired);
                var coherent = (sequence & 1) == 0 && sequence == Volatile.Read(ref state.Sequence);
                if (retired == 1 && operation == 0 && coherent) continue;
                Trace.WriteLine($"IO_STAGE_SLOT sample={sample} collection={ordinal} slot={slot} root={root} admission={admission} operation={(Operation)operation} entered={entered} exited={exited} retired={retired} coherent={(coherent ? 1 : 0)} ticks={Stopwatch.GetTimestamp()}");
            }
            var roots = Math.Min(Volatile.Read(ref _roots), RootCapacity);
            for (var root = 0; root < roots; root++)
                Trace.WriteLine($"IO_STAGE_ROOT sample={sample} collection={ordinal} root={root + 1} admission={Volatile.Read(ref _rootAdmission[root])} entered={Volatile.Read(ref _rootEntered[root])} completed={Volatile.Read(ref _rootCompleted[root])}");
        }
    }
}
