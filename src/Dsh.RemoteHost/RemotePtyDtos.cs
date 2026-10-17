namespace Dsh.RemoteHost;

public sealed record RemotePtyAttachRequest(string PtyId, string SubscriptionId);

public readonly record struct RemotePtyFrame(string SubscriptionId, RemotePtyOutput Output);

/** 起 PTY: Cwd/Title 为空时取远端家目录与命令行; 尺寸为空时用 daemon 默认值。 */
public sealed record RemotePtyStartRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? Cwd = null,
    int? Columns = null,
    int? Rows = null,
    string? Title = null);

public sealed record RemotePtyResize(string PtyId, int Columns, int Rows);

public sealed record RemotePtyRef(string PtyId);

public sealed record RemotePtyInput(string PtyId, string Base64);

public sealed record RemotePtyOutput(string PtyId, string Base64, bool Eof);
