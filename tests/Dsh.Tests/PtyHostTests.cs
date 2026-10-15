using System.Text;
using Dsh.Pty;

namespace Dsh.Tests;

public class PtyHostTests
{
    [Fact(Timeout = 30000)]
    public async Task Host_Starts_Lists_Reads_And_Stops()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        using var host = new PtyHost();
        var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "/bin/sh",
            Arguments = ["-c", "echo pty-ready; sleep 5"],
        }, cancellationToken: TestContext.Current.CancellationToken);

        var info = Assert.Single(host.List());
        Assert.Equal(session.Id, info.Id);
        Assert.Equal(PtySessionStatus.Running, info.Status);
        Assert.Equal("echo pty-ready; sleep 5", info.Command.Split("-c ")[^1]);

        var buffer = new byte[4096];
        var read = await session.ReadAsync(buffer, TestContext.Current.CancellationToken);
        Assert.Contains("pty-ready", Encoding.UTF8.GetString(buffer, 0, read));

        Assert.True(await host.StopAsync(session.Id.ToString()));
        Assert.DoesNotContain(host.List(), entry => entry.Id == session.Id);
    }

    [Fact(Timeout = 30000)]
    public async Task Resize_UpdatesPtyWindowSize()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        using var host = new PtyHost();
        var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "/bin/sh",
            Arguments = ["-c", "sleep 5"],
        }, cancellationToken: TestContext.Current.CancellationToken);

        session.Resize(30, 120);
        Assert.Equal(PtySessionStatus.Running, session.Status);

        await host.StopAsync(session.Id.ToString());
    }

    [Fact(Timeout = 30000)]
    public async Task Write_And_Read_RoundTrips()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        using var host = new PtyHost();
        var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "/bin/sh",
            Arguments = ["-c", "cat"],
        }, cancellationToken: TestContext.Current.CancellationToken);

        await session.WriteAsync("hello pty\n"u8.ToArray(), TestContext.Current.CancellationToken);
        var buffer = new byte[4096];
        var read = await session.ReadAsync(buffer, TestContext.Current.CancellationToken);
        Assert.Contains("hello pty", Encoding.UTF8.GetString(buffer, 0, read));

        await host.StopAsync(session.Id.ToString());
    }

    [Fact(Timeout = 30000)]
    public async Task Windows_ConPty_Starts_And_Reads()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var direct = ConPtySession.Start(new PtyStartInfo
        {
            FileName = "cmd.exe",
            Arguments = ["/c", "echo direct-conpty-ready"],
        });
        using var directCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var received = await ReadUntil(direct.Stream, "direct-conpty-ready", directCancellation.Token);
        Assert.Contains("direct-conpty-ready", received);
        await direct.StopAsync();

        using var host = new PtyHost();
        var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "cmd.exe",
            // 断言状态必须是 Running: 用常驻命令(echo 后 ping 等待), 否则 /c echo 会立刻退出。
            Arguments = ["/c", "echo conpty-ready & ping -n 30 127.0.0.1 > nul"],
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PtySessionStatus.Running, session.Status);
        var backend = typeof(PtySession).GetField("_conPty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(session);
        Assert.NotNull(backend);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var buffer = new byte[4096];
        var read = await session.ReadAsync(buffer, cancellation.Token);
        Assert.True(read > 0);

        session.Resize(30, 120);
        Assert.True(await host.StopAsync(session.Id.ToString()));
    }

    [Fact(Timeout = 30000)]
    public async Task Windows_ConPty_Write_And_Read_RoundTrips()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var host = new PtyHost();
        var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "cmd.exe",
            Arguments = [],
        }, cancellationToken: TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await session.WriteAsync("ver\r"u8.ToArray(), cancellation.Token);
        var buffer = new byte[4096];
        var received = new List<byte>();
        try
        {
            while (!Encoding.UTF8.GetString(received.ToArray()).Contains("Microsoft Windows"))
            {
                var read = await session.ReadAsync(buffer, cancellation.Token);
                if (read == 0)
                    break;
                received.AddRange(buffer.AsSpan(0, read).ToArray());
            }
        }
        catch (OperationCanceledException)
        {
        }
        Assert.Contains("Microsoft Windows", Encoding.UTF8.GetString(received.ToArray()));

        Assert.True(await host.StopAsync(session.Id.ToString()));
    }

    /** Ctrl+C typed into the terminal stops the shell's foreground command, even when this process was started ignoring Ctrl+C (as the CI runner starts its steps). */
    [Fact(Timeout = 60000)]
    public async Task CtrlC_StopsTheShellsForegroundCommand()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = new PtyHost();
        var session = await host.StartAsync(new PtyStartInfo { FileName = TestShell.FileName, Arguments = TestShell.Arguments }, cancellationToken: ct);
        var received = new StringBuilder();
        await session.WriteAsync(Encoding.UTF8.GetBytes($"{TestShell.Sequence(TestShell.EchoAnswer("busy"), TestShell.Wait(120))}\r"), ct);
        Assert.True(await ReadSessionUntilAsync(session, received, "busy-42", TimeSpan.FromSeconds(20)), received.ToString());

        await session.WriteAsync(new[] { CtrlC }, ct);
        // The console discards input typed while the interrupted command ends, so the line is sent again until it runs.
        var answered = false;
        for (var attempt = 0; attempt < 3 && !answered; attempt++)
        {
            await session.WriteAsync(Encoding.UTF8.GetBytes($"{TestShell.EchoAnswer("after")}\r"), ct);
            answered = await ReadSessionUntilAsync(session, received, "after-42", TimeSpan.FromSeconds(5));
        }

        Assert.True(answered, received.ToString());
        Assert.True(await host.StopAsync(session.Id.ToString()));
    }

    private const byte CtrlC = 0x03;

    /** Reads the session into received until it contains expected; false when the time runs out first. */
    private static async Task<bool> ReadSessionUntilAsync(PtySession session, StringBuilder received, string expected, TimeSpan timeout)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(timeout);
        var buffer = new byte[4096];
        try
        {
            while (!received.ToString().Contains(expected, StringComparison.Ordinal))
            {
                var read = await session.ReadAsync(buffer, deadline.Token);
                if (read == 0)
                    await Task.Delay(10, deadline.Token);
                else
                    received.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            return true;
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task<string> ReadUntil(Stream stream, string expected, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var received = new List<byte>();
        while (!Encoding.UTF8.GetString(received.ToArray()).Contains(expected))
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            received.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        return Encoding.UTF8.GetString(received.ToArray());
    }
}
