using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Dsh.Sandbox;

internal static class WindowsSandboxTiming
{
    private const int ProgressInterval = 4096;

    public static void Mark(object owner, string phase, int count = 0) =>
        Trace.WriteLine($"SANDBOX_PHASE pid={Environment.ProcessId} owner={RuntimeHelpers.GetHashCode(owner)} phase={phase} count={count} ticks={Stopwatch.GetTimestamp()} frequency={Stopwatch.Frequency}");

    public static void Progress(object owner, string phase, int count)
    {
        if (count % ProgressInterval == 0)
            Mark(owner, phase, count);
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

        public void Record(CostStage stage, long started)
        {
            var elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref _ticks[(int)stage], elapsed);
            Interlocked.Increment(ref _counts[(int)stage]);
        }

        public void Progress(object owner, int count)
        {
            if (count % ProgressInterval == 0)
                Emit(owner, "grants-progress");
        }

        // Call only outside parallel batches so each count/tick pair describes the same completed intervals.
        public void Emit(object owner, string phase)
        {
            for (var index = 0; index < (int)CostStage.Count; index++)
                Trace.WriteLine($"SANDBOX_COST pid={Environment.ProcessId} owner={RuntimeHelpers.GetHashCode(owner)} phase={phase} stage={(CostStage)index} count={Interlocked.Read(ref _counts[index])} ticks={Interlocked.Read(ref _ticks[index])} frequency={Stopwatch.Frequency}");
        }
    }

    // These are operation intervals, not a wall-clock partition: different probe workers overlap.
    internal static Measurement Measure(Costs? costs, CostStage stage) => new(costs, stage);

    internal readonly struct Measurement(Costs? costs, CostStage stage) : IDisposable
    {
        private readonly long _started = costs is null ? 0 : Stopwatch.GetTimestamp();

        public void Dispose() => costs?.Record(stage, _started);
    }
}
