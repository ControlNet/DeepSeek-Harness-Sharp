using System.Runtime.CompilerServices;
using Dsh.Llm;

namespace Dsh.Tests;

public sealed class AgentLoopRequestMarkerTests
{
    [Fact]
    public void MarkedRequest_IsRecognized_AndDoesNotOutliveItsOwner()
    {
        var kept = AgentLoopRequestMarker.Mark(new GenerateOptions { Provider = "p", Model = "m", Messages = [] });
        Assert.True(AgentLoopRequestMarker.IsMarked(kept));
        Assert.False(AgentLoopRequestMarker.IsMarked(new GenerateOptions { Provider = "p", Model = "m", Messages = [] }));

        var dropped = MarkAndForget();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(dropped.IsAlive);
        GC.KeepAlive(kept);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference MarkAndForget()
        => new(AgentLoopRequestMarker.Mark(new GenerateOptions { Provider = "p", Model = "m", Messages = [] }));
}
