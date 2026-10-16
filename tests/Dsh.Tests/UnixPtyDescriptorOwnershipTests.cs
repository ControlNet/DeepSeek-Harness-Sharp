using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Dsh.Pty;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Tests;

// Descriptor-table assertions need the existing process-isolated collection; the spawn test explicitly starts its two PTYs concurrently.
[Collection(SerialProcessCollection.Name)]
public sealed class UnixPtyDescriptorOwnershipTests
{
    private static readonly Type Native = typeof(PtySession).Assembly.GetType("Dsh.Pty.UnixPtyNative")!;
    private const BindingFlags NativeMembers = BindingFlags.Static | BindingFlags.NonPublic;

    [Fact(Timeout = 30000)]
    public async Task ConcurrentSpawns_DoNotInheritSlaves_AndOriginalSlaveClosesIndependently()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var held = OpenPty();
        using var master = new SafeFileHandle((IntPtr)held.Master, ownsHandle: true);
        using var slave = new SafeFileHandle((IntPtr)held.Slave, ownsHandle: true);
        using var firstHost = new PtyHost();
        using var secondHost = new PtyHost();
        var cancellationToken = TestContext.Current.CancellationToken;
        var script = "printf 'OWN:'; tty; printf 'READY\\n'; read -r peer; leaked=0; "
            + "for descriptor in /dev/fd/*; do if [ \"$descriptor\" -ef \"$peer\" ]; then leaked=1; fi; done; "
            + $"if [ -e /dev/fd/{held.Master} ] || [ -e /dev/fd/{held.Slave} ]; then leaked=1; fi; "
            + "if [ \"$leaked\" -eq 0 ]; then echo FD-ISOLATED; else echo FD-LEAKED; fi; echo FD-CHECK-DONE; read -r finish";
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStart = Task.Run(async () =>
        {
            await start.Task;
            return await firstHost.StartAsync(new PtyStartInfo { FileName = "/bin/sh", Arguments = ["-c", script] }, cancellationToken: cancellationToken);
        }, cancellationToken);
        var secondStart = Task.Run(async () =>
        {
            await start.Task;
            return await secondHost.StartAsync(new PtyStartInfo { FileName = "/bin/sh", Arguments = ["-c", script] }, cancellationToken: cancellationToken);
        }, cancellationToken);
        start.SetResult();
        var sessions = await Task.WhenAll(firstStart, secondStart);
        var ready = await Task.WhenAll(ReadUntil(sessions[0], "READY"), ReadUntil(sessions[1], "READY"));
        var names = ready.Select(output => output.Split('\n').Single(line => line.StartsWith("OWN:", StringComparison.Ordinal))[4..].TrimEnd('\r')).ToArray();
        Assert.All(names, name => Assert.StartsWith("/dev/", name));
        Assert.NotEqual(names[0], names[1]);
        await sessions[0].WriteAsync(Encoding.UTF8.GetBytes(names[1] + "\n"), cancellationToken);
        await sessions[1].WriteAsync(Encoding.UTF8.GetBytes(names[0] + "\n"), cancellationToken);
        var reports = await Task.WhenAll(ReadUntil(sessions[0], "FD-CHECK-DONE"), ReadUntil(sessions[1], "FD-CHECK-DONE"));
        Assert.All(reports, report =>
        {
            Assert.Contains("FD-ISOLATED", report);
            Assert.DoesNotContain("FD-LEAKED", report);
        });

        using (var process = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", $"if [ -e /dev/fd/{held.Master} ] || [ -e /dev/fd/{held.Slave} ]; then echo FD-LEAKED; else echo FD-ISOLATED; fi" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!)
        {
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("FD-ISOLATED", output.Trim());
        }

        // Both unrelated PTYs remain alive. Their existence cannot keep the original master's stream open.
        slave.Dispose();
        var descriptor = new PollDescriptor { FileDescriptor = held.Master, Events = 1 };
        Assert.Equal(1, poll(ref descriptor, 1, 1000));
        Assert.NotEqual(0, descriptor.ReturnedEvents & 0x10); // POLLHUP: no slave owner remains.
        Assert.All(sessions, session => Assert.Equal(PtySessionStatus.Running, session.Status));
    }

    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public void MarkingFailure_ClosesTheOwnedDescriptor(bool invalidMaster)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        using var file = File.Open("/dev/null", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var descriptor = (int)file.SafeFileHandle.DangerousGetHandle();
        var prepare = Native.GetMethod("PreparePty", NativeMembers)!;
        // Transfer the real descriptor to the production ownership boundary. The other descriptor is genuinely invalid.
        file.SafeFileHandle.SetHandleAsInvalid();
        try
        {
            var exception = Assert.Throws<TargetInvocationException>(() => prepare.Invoke(null,
                [invalidMaster ? int.MaxValue : descriptor, invalidMaster ? descriptor : int.MaxValue, IntPtr.Zero]));
            var nativeError = Assert.IsType<Win32Exception>(exception.InnerException);
            Assert.Equal(9, nativeError.NativeErrorCode); // EBADF on Linux and Darwin.
            Assert.Equal(-1, fcntl(descriptor, 1)); // F_GETFD: the valid descriptor was closed on either failure path.
        }
        finally
        {
            if (fcntl(descriptor, 1) != -1)
                close(descriptor);
        }
    }

    [Fact(Timeout = 30000)]
    public void SpawnFailure_ClosesBothDescriptors()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        var warm = OpenPty();
        using (new SafeFileHandle((IntPtr)warm.Master, ownsHandle: true))
        using (new SafeFileHandle((IntPtr)warm.Slave, ownsHandle: true))
        {
        }
        var directory = OperatingSystem.IsMacOS() ? "/dev/fd" : "/proc/self/fd";
        var spawn = Native.GetMethod("Spawn", BindingFlags.Static | BindingFlags.Public)!;
        var command = new PtyStartInfo { FileName = "/dsh-nonexistent-" + Guid.NewGuid().ToString("N"), Arguments = [] };
        var before = Directory.GetFileSystemEntries(directory).Length;
        var exception = Assert.Throws<TargetInvocationException>(() => spawn.Invoke(null, [command]));
        Assert.Equal(2, Assert.IsType<Win32Exception>(exception.InnerException).NativeErrorCode); // ENOENT.
        Assert.Equal(before, Directory.GetFileSystemEntries(directory).Length);
    }

    private static (int Master, int Slave, string SlaveName) OpenPty()
    {
        // Use the same gate as the production allocation path, rather than opening an unprotected test-only race window.
        using var allocation = ((Lock)Native.GetField("SpawnGate", NativeMembers)!.GetValue(null)!).EnterScope();
        return ((int, int, string))Native.GetMethod("OpenPty", NativeMembers)!.Invoke(null, [20, 80])!;
    }

    private static async Task<string> ReadUntil(PtySession session, string marker)
    {
        var output = new StringBuilder();
        var buffer = new byte[4096];
        while (!output.ToString().Contains(marker, StringComparison.Ordinal))
        {
            var read = await session.ReadAsync(buffer, TestContext.Current.CancellationToken);
            if (read == 0)
                break;
            output.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
        Assert.Contains(marker, output.ToString());
        return output.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref PollDescriptor descriptors, nuint count, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int descriptor, int command);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);
}
