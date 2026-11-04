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
}
