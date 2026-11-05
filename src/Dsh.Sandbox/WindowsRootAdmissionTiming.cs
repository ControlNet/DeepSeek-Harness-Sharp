using System.Diagnostics;

namespace Dsh.Sandbox;

// Temporary numeric diagnostic instrumentation, not a production implementation.
// Remove this module and its explicitly passed timing arguments together after the experiment.
internal static class WindowsRootAdmissionTiming
{
    private const int ApplyCapacity = 16;
    private const int RootCapacity = 256;
    private const int GroupCapacity = 256;
    private static Observation? _observation;
    private static long _nextApply;

    internal enum RootKind { File, Directory }
    internal enum Decision { Pending, WindowsExcluded, PublishedMetadataReuse, FileRecorded, DirectoryRequested }
    internal enum FlushReason { End, Intersection }
    internal enum Phase { CollectionStart, CollectionComplete, AncestorsStart, AncestorsComplete, GrantsStart, ProofFirstProbe, GrantsComplete, ApplyComplete, ApplyFinal, Count }
    internal enum TestPhase { Start, BackendStart, BackendComplete, ConnectStart, ConnectComplete, PermittedStart, PermittedComplete, DeniedStart, DeniedComplete, AssertionsComplete }

    internal static Observation? Observe()
    {
        try { return new Observation(); }
        catch (Exception) { return null; }
    }

    internal static void MarkTest(TestPhase phase)
    {
        try { Trace.WriteLine($"ROOT_ADMISSION_TEST phase={phase} ticks={Stopwatch.GetTimestamp()} frequency={Stopwatch.Frequency}"); }
        catch (Exception) { }
    }

    internal static Session? BeginApply()
    {
        var observation = Volatile.Read(ref _observation);
        if (observation is null)
            return null;
        try { return observation.Add(); }
        catch (Exception) { observation.Failed(); return null; }
    }

    internal sealed class Observation : IAsyncDisposable
    {
        private readonly Session?[] _applies = new Session?[ApplyCapacity];
        private readonly object _dumpGate = new();
        private readonly Timer _timer;
        private readonly NumericTraceListener _listener = new();
        private int _count;
        private int _failures;
        private int _polls;
        private int _disposed;

        internal Observation()
        {
            _timer = new Timer(static state => ((Observation)state!).Snapshot(), this,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            try
            {
                Trace.Listeners.Add(_listener);
                _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                Interlocked.Exchange(ref _observation, this);
            }
            catch (Exception)
            {
                _timer.Dispose();
                Trace.Listeners.Remove(_listener);
                throw;
            }
        }

        internal Session? Add()
        {
            var index = Interlocked.Increment(ref _count) - 1;
            if (index >= _applies.Length)
                return null;
            var session = new Session(Interlocked.Increment(ref _nextApply));
            Volatile.Write(ref _applies[index], session);
            return session;
        }

        internal void Failed() => Interlocked.Increment(ref _failures);

        private void Snapshot()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            var poll = Interlocked.Increment(ref _polls);
            Dump(poll <= 45 && poll % 15 == 0 ? poll / 15 : 0);
        }

        private void Dump(int snapshot)
        {
            try
            {
                lock (_dumpGate)
                {
                    if (snapshot != 0)
                        Trace.WriteLine($"ROOT_ADMISSION observation={snapshot} applies={Volatile.Read(ref _count)} applyOverflow={Math.Max(0, Volatile.Read(ref _count) - ApplyCapacity)} observerFailures={Volatile.Read(ref _failures)} ticks={Stopwatch.GetTimestamp()} frequency={Stopwatch.Frequency}");
                    for (var index = 0; index < _applies.Length; index++)
                    {
                        var session = Volatile.Read(ref _applies[index]);
                        if (snapshot == 0)
                            session?.EmitProgress();
                        else
                            session?.Dump(snapshot);
                    }
                }
            }
            catch (Exception) { Failed(); }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Interlocked.CompareExchange(ref _observation, null, this);
            try { await _timer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { Failed(); }
            Dump(4);
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
        public override void Write(string? message) => WriteLine(message);
        public override void WriteLine(string? message)
        {
            if (message is null || !message.StartsWith("ROOT_ADMISSION", StringComparison.Ordinal))
                return;
            try { Console.Error.WriteLineAsync(message).GetAwaiter().GetResult(); }
            catch (Exception) { }
        }
    }

    internal sealed class Session(long owner)
    {
        private readonly Root?[] _roots = new Root?[RootCapacity];
        private readonly Group?[] _groups = new Group?[GroupCapacity];
        private readonly Root?[] _queued = new Root?[RootCapacity];
        private readonly long[] _phases = new long[(int)Phase.Count];
        private int _rootCount;
        private int _groupCount;
        private int _failures;
        private int _paths;
        private int _proofCompleted;
        private int _reportedPhases;
        internal long Owner { get; } = owner;
        internal Root? QueuedRoot(int index) => index < _queued.Length ? _queued[index] : null;
        internal void QueueRoot(int index, Root? root)
        {
            if (index < _queued.Length)
                _queued[index] = root;
        }

        internal Root? AddRoot(bool directory)
        {
            var ordinal = Interlocked.Increment(ref _rootCount);
            if (ordinal > _roots.Length)
                return null;
            try
            {
                var root = new Root(this, ordinal, directory ? RootKind.Directory : RootKind.File);
                Volatile.Write(ref _roots[ordinal - 1], root);
                return root;
            }
            catch (Exception) { Interlocked.Increment(ref _failures); return null; }
        }

        internal Group? BeginGroup(int roots, int requests, FlushReason reason, int trigger, int intersection)
        {
            var ordinal = Interlocked.Increment(ref _groupCount);
            if (ordinal > _groups.Length)
                return null;
            try
            {
                var group = new Group(ordinal, roots, requests, reason, trigger, intersection);
                Volatile.Write(ref _groups[ordinal - 1], group);
                return group;
            }
            catch (Exception) { Interlocked.Increment(ref _failures); return null; }
        }

        internal void Mark(Phase phase, int paths = -1)
        {
            if (paths >= 0)
                Volatile.Write(ref _paths, paths);
            Volatile.Write(ref _phases[(int)phase], Stopwatch.GetTimestamp());
        }

        internal void GrantPrefixCompleted(int count) => Volatile.Write(ref _proofCompleted, count);
        internal void ProofFirstProbe()
        {
            if (Volatile.Read(ref _phases[(int)Phase.ProofFirstProbe]) == 0)
                Interlocked.CompareExchange(ref _phases[(int)Phase.ProofFirstProbe], Stopwatch.GetTimestamp(), 0);
        }

        // Only the observer timer touches publication cursors, under its own dump gate, never a native-work lock.
        internal void EmitProgress()
        {
            for (var index = 0; index < _phases.Length; index++)
            {
                var ticks = Volatile.Read(ref _phases[index]);
                if (ticks == 0 || (_reportedPhases & (1 << index)) != 0)
                    continue;
                Trace.WriteLine($"ROOT_ADMISSION_PHASE observation=0 owner={Owner} phase={(Phase)index} ticks={ticks}");
                _reportedPhases |= 1 << index;
            }
            var groups = Math.Min(Volatile.Read(ref _groupCount), _groups.Length);
            for (var index = 0; index < groups; index++)
                Volatile.Read(ref _groups[index])?.EmitProgress(Owner);
            var roots = Math.Min(Volatile.Read(ref _rootCount), _roots.Length);
            for (var index = 0; index < roots; index++)
                Volatile.Read(ref _roots[index])?.EmitProgress();
        }

        internal void Dump(int snapshot)
        {
            Trace.WriteLine($"ROOT_ADMISSION_APPLY observation={snapshot} owner={Owner} roots={Volatile.Read(ref _rootCount)} rootOverflow={Math.Max(0, Volatile.Read(ref _rootCount) - RootCapacity)} groups={Volatile.Read(ref _groupCount)} groupOverflow={Math.Max(0, Volatile.Read(ref _groupCount) - GroupCapacity)} observerFailures={Volatile.Read(ref _failures)} paths={Volatile.Read(ref _paths)} grantPrefixCompleted={Volatile.Read(ref _proofCompleted)} ticks={Stopwatch.GetTimestamp()}");
            for (var index = 0; index < _phases.Length; index++)
                Trace.WriteLine($"ROOT_ADMISSION_PHASE observation={snapshot} owner={Owner} phase={(Phase)index} ticks={Volatile.Read(ref _phases[index])}");
            for (var index = 0; index < _groups.Length; index++)
                Volatile.Read(ref _groups[index])?.Dump(snapshot, Owner);
            for (var index = 0; index < _roots.Length; index++)
                Volatile.Read(ref _roots[index])?.Dump(snapshot);
        }
    }

    internal sealed class Group(int ordinal, int roots, int requests, FlushReason reason, int trigger, int intersection)
    {
        private int _reported;
        private readonly long _started = Stopwatch.GetTimestamp();
        private long _collectionStarted;
        private long _collectionCompleted;
        private long _merged;
        internal int Ordinal { get; } = ordinal;
        internal void CollectionStarted() => Volatile.Write(ref _collectionStarted, Stopwatch.GetTimestamp());
        internal void CollectionCompleted() => Volatile.Write(ref _collectionCompleted, Stopwatch.GetTimestamp());

        internal void EmitProgress(long owner)
        {
            var state = Volatile.Read(ref _merged) != 0 ? 4
                : Volatile.Read(ref _collectionCompleted) != 0 ? 3
                : Volatile.Read(ref _collectionStarted) != 0 ? 2 : 1;
            if (state <= _reported)
                return;
            Dump(0, owner);
            _reported = state;
        }
        internal void Merged() => Volatile.Write(ref _merged, Stopwatch.GetTimestamp());

        internal void Dump(int snapshot, long owner) => Trace.WriteLine(
            $"ROOT_ADMISSION_GROUP observation={snapshot} owner={owner} group={Ordinal} roots={roots} requests={requests} reason={reason} trigger={trigger} intersection={intersection} started={_started} collectionStarted={Volatile.Read(ref _collectionStarted)} collectionCompleted={Volatile.Read(ref _collectionCompleted)} merged={Volatile.Read(ref _merged)}");
    }

    // These are the two root-admission slots, not native-thread activity slots.
    // A pending directory read contributes its recorded entries only when that read exits, so unfinished roots report a lower bound.
    internal sealed class Root(Session session, int ordinal, RootKind kind)
    {
        private readonly long _seen = Stopwatch.GetTimestamp();
        private int _decision;
        private long _decided;
        private int _group;
        private int _request = -1;
        private int _slot = -1;
        private long _entered;
        private long _admitted;
        private long _workCompleted;
        private long _slotReleased;
        private long _published;
        private long _merged;
        private long _recorded;
        private long _directories;
        private int _existingWrite = -1;
        private int _existingDirectory = -1;
        internal int Ordinal { get; } = ordinal;
        internal void Decide(Decision decision)
        {
            Volatile.Write(ref _decision, (int)decision);
            Volatile.Write(ref _decided, Stopwatch.GetTimestamp());
        }
        internal void Reused(bool write, bool directory)
        {
            Volatile.Write(ref _existingWrite, write ? 1 : 0);
            Volatile.Write(ref _existingDirectory, directory ? 1 : 0);
            Decide(Decision.PublishedMetadataReuse);
        }

        internal void Assign(Group? group, int request)
        {
            Volatile.Write(ref _group, group?.Ordinal ?? 0);
            Volatile.Write(ref _request, request);
        }

        internal void Enter(int slot)
        {
            Volatile.Write(ref _slot, slot);
            Volatile.Write(ref _entered, Stopwatch.GetTimestamp());
        }

        internal void Admitted() => Volatile.Write(ref _admitted, Stopwatch.GetTimestamp());
        internal void WorkCompleted() => Volatile.Write(ref _workCompleted, Stopwatch.GetTimestamp());
        internal void SlotReleased() => Interlocked.CompareExchange(ref _slotReleased, Stopwatch.GetTimestamp(), 0);
        internal void Published() => Volatile.Write(ref _published, Stopwatch.GetTimestamp());
        internal void Merged() => Volatile.Write(ref _merged, Stopwatch.GetTimestamp());

        // Called once per directory, including a partially recorded directory on failure, never once per entry.
        internal void Recorded(int entries)
        {
            Interlocked.Add(ref _recorded, entries);
            Interlocked.Increment(ref _directories);
        }

        // This cursor belongs to the observer timer; producer threads publish only numeric fields.
        private int _reported;
        internal void EmitProgress()
        {
            var state = Volatile.Read(ref _merged) != 0 ? 6
                : Volatile.Read(ref _published) != 0 ? 5
                : Volatile.Read(ref _workCompleted) != 0 ? 4
                : Volatile.Read(ref _entered) != 0 ? 3
                : Volatile.Read(ref _decided) != 0 ? 2 : 1;
            if (state <= _reported)
                return;
            Dump(0);
            _reported = state;
        }

        internal void Dump(int snapshot)
        {
            var workCompleted = Volatile.Read(ref _workCompleted);
            var published = Volatile.Read(ref _published);
            var entered = Volatile.Read(ref _entered);
            var merged = Volatile.Read(ref _merged);
            Trace.WriteLine($"ROOT_ADMISSION_ROOT observation={snapshot} owner={session.Owner} root={Ordinal} kind={kind} decision={(Decision)Volatile.Read(ref _decision)} existingWrite={Volatile.Read(ref _existingWrite)} existingDirectory={Volatile.Read(ref _existingDirectory)} group={Volatile.Read(ref _group)} request={Volatile.Read(ref _request)} slot={Volatile.Read(ref _slot)} seen={_seen} decided={Volatile.Read(ref _decided)} entered={entered} admitted={Volatile.Read(ref _admitted)} workCompleted={workCompleted} workTicks={(workCompleted == 0 || entered == 0 ? 0 : workCompleted - entered)} slotReleased={Volatile.Read(ref _slotReleased)} published={published} publishTicks={(published == 0 || entered == 0 ? 0 : published - entered)} merged={merged} rootTicks={(merged == 0 ? 0 : merged - _seen)} descendantEntries={Volatile.Read(ref _recorded)} recordedDirectories={Volatile.Read(ref _directories)} recordedLowerBound={(workCompleted == 0 && kind == RootKind.Directory ? 1 : 0)}");
        }
    }
}
