namespace Dsh.WebBridge;

/** Browser projection notifications, separate from the RemoteHost wire methods they carry. */
public static class BridgeProtocol
{
    public const string NotificationView = "bridge.view";
    public const string NotificationStreamError = "bridge.stream.error";
    public const string SessionStream = "session";
    public const string TerminalStream = "terminal";
}
