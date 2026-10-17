using System.Text.Json.Serialization;

namespace Dsh.RemoteHost;

/** Optional Pty wire contract; method and capability values are stable. */
public static class PtyProtocol
{
    public const string Capability = "pty.daemon";
    public const string ResizeCapability = "pty.resize";
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodPtyStart, MethodPtyWrite, MethodPtyStop, MethodPtyAttach, MethodPtyDetach, MethodPtyResize]);

    public const string MethodPtyStart = "host.pty.start";
    public const string MethodPtyWrite = "host.pty.write";
    public const string MethodPtyStop = "host.pty.stop";
    public const string MethodPtyAttach = "host.pty.attach";
    public const string MethodPtyDetach = "host.pty.detach";
    public const string MethodPtyResize = "host.pty.resize";
    public const string NotificationPtyOutput = "host.pty.output";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemotePtyStartRequest))]
[JsonSerializable(typeof(RemotePtyRef))]
[JsonSerializable(typeof(RemotePtyInput))]
[JsonSerializable(typeof(RemotePtyOutput))]
[JsonSerializable(typeof(RemotePtyResize))]
[JsonSerializable(typeof(RemotePtyAttachRequest))]
[JsonSerializable(typeof(RemotePtyFrame))]
[JsonSerializable(typeof(RemoteSubscriptionRef))]
[JsonSerializable(typeof(bool))]
public partial class PtyProtocolJsonContext : JsonSerializerContext;
