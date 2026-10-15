using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dsh.Pty;

namespace Dsh.Tests;

/** TEMPORARY diagnostics for issue #41 (diag branch only): timings around the oversized-line connect failure. */
public sealed class DiagOversizeTests
{
    private static readonly string LogPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/diag-oversize.log"));

    [Theory(Timeout = 60000)]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public async Task Oversize_Timings(int run)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var ct = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("dov");
        var socketPath = Path.Combine(root, "dsh.sock");
        var portFile = Path.Combine(root, "dsh.port");
        await using var daemon = new PtyDaemon(new PtyHost(), socketPath, portFile);
        await daemon.StartAsync(ct);
        var port = int.Parse(await File.ReadAllTextAsync(portFile, ct));
        var total = Stopwatch.StartNew();
        string phase;
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            await socket.ConnectAsync(IPAddress.Loopback, port, ct);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var write = Stopwatch.StartNew();
            try { await stream.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 2 * 1024 * 1024) + "\n"), ct); phase = "write-ok"; }
            catch (IOException e) { phase = $"write-io({e.InnerException?.GetType().Name})"; }
            phase += $" {write.ElapsedMilliseconds}ms";
            var read = Stopwatch.StartNew();
            try { var n = await stream.ReadAsync(new byte[16], ct); phase += $" read={n}"; }
            catch (IOException e) { phase += $" read-io({(e.InnerException as SocketException)?.SocketErrorCode})"; }
            phase += $" {read.ElapsedMilliseconds}ms";
        }
        var connect = Stopwatch.StartNew();
        string result;
        try
        {
            await PtyDaemonClient.ListAsync(socketPath, portFile, ct);
            result = "list-ok";
        }
        catch (PtyDaemonNotRunningException)
        {
            result = "list-NOTRUNNING";
        }
        result += $" {connect.ElapsedMilliseconds}ms";
        var raw = Stopwatch.StartNew();
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            await probe.ConnectAsync(IPAddress.Loopback, port, ct);
        var line = $"run={run} {phase} | {result} | rawconnect {raw.ElapsedMilliseconds}ms | total {total.ElapsedMilliseconds}ms | threads={ThreadPool.ThreadCount} pending={ThreadPool.PendingWorkItemCount}";
        lock (LogPath)
            File.AppendAllText(LogPath, line + Environment.NewLine);
        TempTree.Delete(root);
    }
}
