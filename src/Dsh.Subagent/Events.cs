using Dsh.Runtime.Events;

namespace Dsh.Subagent;

public sealed record SubagentProviderAddedNotification(ISubagentProvider Provider) : INotification
{
    public static string EventName => "subagent/provider-added";
}

public sealed record SubagentProviderRemovedNotification(string Name) : INotification
{
    public static string EventName => "subagent/provider-removed";
}
