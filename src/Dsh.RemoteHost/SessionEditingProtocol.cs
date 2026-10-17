using System.Text.Json.Serialization;

namespace Dsh.RemoteHost;

/** Optional SessionEditing wire contract; method and capability values are stable. */
public static class SessionEditingProtocol
{
    public const string Capability = "session.editing";
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodSessionCommand, MethodSessionCommands, MethodSessionRename, MethodSessionDelete, MethodSessionApprovalPolicy, MethodSessionSubagents]);

    public const string MethodSessionCommand = "host.session.command";
    public const string MethodSessionCommands = "host.session.commands";
    public const string MethodSessionRename = "host.session.rename";
    public const string MethodSessionDelete = "host.session.delete";
    public const string MethodSessionApprovalPolicy = "host.session.approvalPolicy";
    public const string MethodSessionSubagents = "host.session.subagents";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemoteCommandRequest))]
[JsonSerializable(typeof(RemoteCommandResult))]
[JsonSerializable(typeof(RemoteCommandDescriptor))]
[JsonSerializable(typeof(IReadOnlyList<RemoteCommandDescriptor>))]
[JsonSerializable(typeof(RemoteSessionRenameRequest))]
[JsonSerializable(typeof(RemoteSessionPolicyRequest))]
[JsonSerializable(typeof(RemoteSubagentInfo))]
[JsonSerializable(typeof(IReadOnlyList<RemoteSubagentInfo>))]
[JsonSerializable(typeof(RemoteSessionRef))]
[JsonSerializable(typeof(bool))]
public partial class SessionEditingProtocolJsonContext : JsonSerializerContext;
