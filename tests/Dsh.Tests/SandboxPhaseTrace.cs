using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Dsh.Tests;

internal static class SandboxPhaseTrace
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Trace.Listeners.Add(new ConsoleTraceListener(useErrorStream: true));
        Trace.AutoFlush = true;
    }
}
