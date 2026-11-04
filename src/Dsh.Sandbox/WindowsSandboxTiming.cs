using System.Diagnostics;

namespace Dsh.Sandbox;

// Temporary diagnostic instrumentation, not a production implementation.
internal static class WindowsSandboxTiming
{
    private const int ProgressInterval = 4096;
    private static long _nextOwner;

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
        public long Owner { get; } = Interlocked.Increment(ref _nextOwner);

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

        // Emit outside parallel probe batches so count/tick pairs cover completed operations.
        public void Emit(string phase)
        {
            for (var index = 0; index < (int)CostStage.Count; index++)
                Write($"SANDBOX_COST pid={Environment.ProcessId} owner={Owner} phase={phase} stage={(CostStage)index} count={Interlocked.Read(ref _counts[index])} ticks={Interlocked.Read(ref _ticks[index])} frequency={Stopwatch.Frequency}");
        }
    }

    internal sealed class Collection(Costs owner, int root)
    {
        private long _openCount, _openTicks, _queryCount, _queryTicks;
        private long _entries, _files, _directories, _published;

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

        // Live progress is approximate across workers; final emission follows every worker join.
        public void Emit(string phase) =>
            Write($"SANDBOX_COLLECTION pid={Environment.ProcessId} owner={owner.Owner} root={root} phase={phase} entries={Interlocked.Read(ref _entries)} files={Interlocked.Read(ref _files)} directories={Interlocked.Read(ref _directories)} published={Interlocked.Read(ref _published)} open-count={Interlocked.Read(ref _openCount)} open-ticks={Interlocked.Read(ref _openTicks)} query-count={Interlocked.Read(ref _queryCount)} query-ticks={Interlocked.Read(ref _queryTicks)} ticks={Stopwatch.GetTimestamp()} frequency={Stopwatch.Frequency}");
    }

    // Overlapping worker operation intervals are not a wall-clock partition.
    internal static Measurement Measure(Costs? costs, CostStage stage) => new(costs, stage);

    internal readonly struct Measurement(Costs? costs, CostStage stage) : IDisposable
    {
        private readonly long _started = costs is null ? 0 : Stopwatch.GetTimestamp();
        public void Dispose() => costs?.Record(stage, _started);
    }
}
