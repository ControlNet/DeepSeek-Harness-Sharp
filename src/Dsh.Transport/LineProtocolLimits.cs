namespace Dsh.Transport;

/** Limits shared by the newline-delimited JSON protocols: PTY daemon requests and the JSON-RPC line transport of ACP, LSP and the SDK. */
public static class LineProtocolLimits
{
    /** Upper bound of one line in UTF-8 bytes; the largest legitimate line is a PTY publish-panes request, whose text (at most PaneBridge.MaxPublishedBytes) JSON escaping can inflate several times. */
    public const int MaxLineBytes = 1024 * 1024;
}
