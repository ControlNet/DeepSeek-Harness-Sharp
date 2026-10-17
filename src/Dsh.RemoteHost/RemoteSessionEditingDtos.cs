namespace Dsh.RemoteHost;

public sealed record RemoteCommandRequest(string SessionId, string Line);

public sealed record RemoteCommandResult(bool Known, string? Text, string? FollowupPrompt);

public sealed record RemoteCommandDescriptor(string Name, string Description);

public sealed record RemoteSessionRenameRequest(string SessionId, string Title);

public sealed record RemoteSessionPolicyRequest(string SessionId, string Policy);

public sealed record RemoteSubagentInfo(string SessionId, string? Parent, int Depth, string Mode, string? Label, bool HasChildren, bool IsLive, long CreatedAt);
