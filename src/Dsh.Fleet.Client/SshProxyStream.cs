using System.Buffers;
using System.Diagnostics;
using System.Net;
using Dsh.Runtime;

namespace Dsh.Fleet;

/// <summary>Owns an SSH process, bounded diagnostic reader and both sides of its TCP stream.</summary>
internal sealed class SshProxyStream : Stream, IOutboundTransportLifetime
{
    private const int DiagnosticCapacity = 8192;
    private readonly object _gate = new();
    private MachineSpec? _machine;
    private readonly DnsEndPoint _endpoint;
    private readonly Action<SshProxyStream> _released;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _failure = new();
    private readonly TaskCompletionSource _authenticated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _exitClassified = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _diagnostics = Task.CompletedTask;
    private Task _observing = Task.CompletedTask;
    private Stream? _input;
    private Stream? _output;
    private string? _askPass;
    private string? _authenticationMarker;
    private string _failureDetail = "The SSH proxy transport failed.";
    private bool _started;
    private bool _disposed;
    private bool _processReleased;
    private bool _ownerReleased;
    private bool _discardDiagnosticLine;

    internal SshProxyStream(MachineSpec machine, DnsEndPoint endpoint, Action<SshProxyStream> released)
    {
        _machine = machine;
        _endpoint = endpoint;
        _released = released;
        Process = new Process { EnableRaisingEvents = true };
        Process.Exited += OnExited;
        FailureToken = _failure.Token;
    }

    internal Process Process { get; }
    public CancellationToken FailureToken { get; }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            (ProcessStartInfo StartInfo, string? AskPass, string AuthenticationMarker) setup;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                setup = SshTransport.CreateProxyStart(_machine!, _endpoint);
            }
            finally
            {
                _machine = null;
            }
            Process.StartInfo = setup.StartInfo;
            _askPass = setup.AskPass;
            _authenticationMarker = setup.AuthenticationMarker;
            if (!Process.Start())
                throw new IOException("Failed to start the SSH proxy transport.");
            _started = true;
            _input = Process.StandardInput.BaseStream;
            _output = Process.StandardOutput.BaseStream;
            _diagnostics = DrainDiagnosticsAsync(Process.StandardError);
            _observing = ObserveExitAsync();
            if (Process.HasExited)
                _exited.TrySetResult();
        }
        using var registration = cancellationToken.UnsafeRegister(static state => ((SshProxyStream)state!).Dispose(), this);
        await _authenticated.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
    }

    private void OnExited(object? sender, EventArgs args) => _exited.TrySetResult();

    private async Task ObserveExitAsync()
    {
        try
        {
            await _exited.Task.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            var exitCode = Process.ExitCode;
            try
            {
                if (exitCode != 0)
                    SignalFailure();
                else if (!_authenticated.Task.IsCompleted)
                    _authenticated.TrySetException(new IOException("SSH ended before authentication completed."));
            }
            finally
            {
                // Failure callbacks retire the HTTP generation before any reader can expose EOF.
                _exitClassified.TrySetResult(exitCode);
            }
        }
        finally
        {
            await _closed.Task.ConfigureAwait(false);
            try
            {
                await _diagnostics.ConfigureAwait(false);
            }
            finally
            {
                ReleaseProcess();
            }
        }
    }

    private void SignalFailure()
    {
        if (Volatile.Read(ref _disposed))
            return;
        _authenticated.TrySetException(new IOException(_failureDetail));
        try
        {
            _failure.Cancel();
        }
        finally
        {
            Dispose();
        }
    }

    private async Task DrainDiagnosticsAsync(StreamReader reader)
    {
        var buffer = ArrayPool<char>.Shared.Rent(DiagnosticCapacity);
        var used = 0;
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(used, DiagnosticCapacity - used), _stop.Token).ConfigureAwait(false) is var read && read != 0)
                used = ConsumeDiagnostics(buffer, used + read);
            if (used != 0 && !_discardDiagnosticLine)
                ObserveDiagnosticLine(buffer.AsSpan(0, used));
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            if (!Volatile.Read(ref _disposed))
            {
                _authenticated.TrySetException(new IOException("The SSH diagnostic stream failed before authentication completed."));
                Dispose();
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }

    private int ConsumeDiagnostics(char[] buffer, int count)
    {
        var consumed = 0;
        for (var index = 0; index < count; index++)
        {
            if (buffer[index] != '\n')
                continue;
            if (!_discardDiagnosticLine)
                ObserveDiagnosticLine(buffer.AsSpan(consumed, index - consumed));
            _discardDiagnosticLine = false;
            consumed = index + 1;
        }
        var remaining = count - consumed;
        if (remaining == DiagnosticCapacity)
        {
            _discardDiagnosticLine = true;
            return 0;
        }
        buffer.AsSpan(consumed, remaining).CopyTo(buffer);
        return remaining;
    }

    private void ObserveDiagnosticLine(ReadOnlySpan<char> line)
    {
        if (_authenticationMarker is not null && line.Trim().Equals(_authenticationMarker, StringComparison.Ordinal))
        {
            DeleteAskPass();
            _authenticated.TrySetResult();
        }
        else if (line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            _failureDetail = "SSH authentication failed.";
        else if (line.Contains("open failed", StringComparison.OrdinalIgnoreCase))
            _failureDetail = "SSH could not connect to the remote proxy endpoint.";
    }

    private void DeleteAskPass()
    {
        lock (_gate)
        {
            if (_askPass is not { } path)
                return;
            try
            {
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Keep ownership so disposal can retry cleanup; never expose the secret path.
                throw new IOException("The SSH authentication secret could not be removed.");
            }
            _askPass = null;
        }
    }

    private void ReleaseProcess()
    {
        lock (_gate)
        {
            if (!_processReleased)
            {
                Process.Exited -= OnExited;
                Process.Dispose();
                _failure.Dispose();
                _stop.Dispose();
                _processReleased = true;
            }
        }
        ReleaseOwnerIfComplete();
    }

    private void ReleaseOwnerIfComplete()
    {
        lock (_gate)
        {
            if (!_processReleased || _askPass is not null || _ownerReleased)
                return;
            _ownerReleased = true;
        }
        _released(this);
    }

    private void CloseInputAtEnd()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // -W may keep SSH alive after remote output EOF until its local input also ends.
            _input!.Dispose();
        }
    }

    private void CheckEnd(int exitCode)
    {
        if (exitCode != 0)
            throw new IOException("The SSH proxy transport ended unexpectedly.");
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
    }

    public override bool CanRead => !Volatile.Read(ref _disposed) && _output is not null;
    public override bool CanWrite => !Volatile.Read(ref _disposed) && _input is not null;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _input!.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _input!.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (buffer.Length == 0)
            return 0;
        var count = _output!.Read(buffer);
        if (count == 0)
        {
            CloseInputAtEnd();
            CheckEnd(_exitClassified.Task.GetAwaiter().GetResult());
        }
        return count;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length == 0)
            return 0;
        using var registration = cancellationToken.UnsafeRegister(static state => ((SshProxyStream)state!).Dispose(), this);
        var count = await _output!.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseInputAtEnd();
            var exitCode = await _exitClassified.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CheckEnd(exitCode);
        }
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        _input!.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        using var registration = cancellationToken.UnsafeRegister(static state => ((SshProxyStream)state!).Dispose(), this);
        await _input!.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    DeleteAskPass();
                    return;
                }
                _disposed = true;
                _machine = null;
                try
                {
                    _stop.Cancel();
                    if (_started && !Process.HasExited)
                        Process.Kill(entireProcessTree: true);
                }
                finally
                {
                    try
                    {
                        _input?.Dispose();
                        _output?.Dispose();
                    }
                    finally
                    {
                        CompleteClose();
                    }
                }
            }
        }
        finally
        {
            if (!_started)
                ReleaseProcess();
            else
                ReleaseOwnerIfComplete();
        }
        base.Dispose(disposing);
    }

    private void CompleteClose()
    {
        try
        {
            DeleteAskPass();
        }
        finally
        {
            _closed.TrySetResult();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            Dispose();
        }
        finally
        {
            await _observing.ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
