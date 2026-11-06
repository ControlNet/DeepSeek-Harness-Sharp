using System.IO.Pipes;
using System.Runtime.InteropServices;
using Dsh.Sandbox;

namespace Dsh.Tests;

public sealed class WindowsThreadPendingIoCalibrationTests
{
    private const byte ProbeByte = 42;

    [Fact(Timeout = 5000)]
    public async Task OwnedSynchronousPipeRead_HasPendingIoUntilTheByteArrives()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("The native pending-I/O observation is Windows-only.");
        using var writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var reader = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                ready.TrySetResult(GetCurrentThreadId());
                // A synchronous read keeps the I/O request owned by this native thread.
                var value = reader.ReadByte();
                received.TrySetResult(value);
                release.Wait();
            }
            catch (Exception error)
            {
                ready.TrySetException(error);
                received.TrySetException(error);
            }
        }) { IsBackground = true };
        worker.Start();
        try
        {
            var token = TestContext.Current.CancellationToken;
            var threadId = await ready.Task.WaitAsync(token);
            WindowsIoStageTiming.ThreadIoSample before;
            do
            {
                token.ThrowIfCancellationRequested();
                before = WindowsIoStageTiming.QueryOwnThreadPendingIo(threadId);
                Assert.Equal(WindowsIoStageTiming.ThreadIoStatus.Known, before.Status);
                Assert.Equal(0, before.CloseError);
                if (before.Pending == 1) break;
                await Task.Yield();
            } while (true);
            await writer.WriteAsync(new byte[] { ProbeByte }, token);
            await writer.FlushAsync(token);
            Assert.Equal((int)ProbeByte, await received.Task.WaitAsync(token));
            var after = WindowsIoStageTiming.QueryOwnThreadPendingIo(threadId);
            Assert.Equal(WindowsIoStageTiming.ThreadIoStatus.Known, after.Status);
            Assert.Equal(0, after.CloseError);
            Assert.Equal(0, after.Pending);
            await Console.Error.WriteLineAsync($"THREAD_PENDING_IO_CALIBRATION before={before.Pending} after={after.Pending} beforeStatus={(int)before.Status} afterStatus={(int)after.Status} beforeCloseError={before.CloseError} afterCloseError={after.CloseError} ownerScope=currentProcess realPipe=1");
        }
        finally
        {
            writer.Dispose();
            release.Set();
            worker.Join();
            if (received.Task.IsFaulted) _ = received.Task.Exception;
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();
}
