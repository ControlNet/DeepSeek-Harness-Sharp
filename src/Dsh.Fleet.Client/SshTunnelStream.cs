using System.Diagnostics;

namespace Dsh.Fleet;

/**
 * The raw byte channel of one `ssh -T ... dsharp host --serve --stdio`: reads come from ssh's stdout and writes go to its stdin.
 * The remote stdio bridge presents the host token on its own side, so the channel carries host frames from the first byte.
 * A password-authenticated tunnel keeps its askpass script only until the remote first answers (authentication is over by then) or the tunnel ends.
 * Disposing ends the ssh process.
 */
public sealed class SshTunnelStream : Stream
{
    private readonly Process _process;
    private readonly Stream _fromRemote;
    private readonly Stream _toRemote;
    private readonly Func<string> _errorOutput;
    private string? _askPass;

    internal SshTunnelStream(Process process, string? askPass, Func<string> errorOutput)
    {
        _process = process;
        _fromRemote = process.StandardOutput.BaseStream;
        _toRemote = process.StandardInput.BaseStream;
        _askPass = askPass;
        _errorOutput = errorOutput;
    }

    /** What ssh and the remote command wrote to stderr so far, to report why a tunnel ended. */
    public string ErrorOutput => _errorOutput();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _fromRemote.Read(buffer, offset, count);
        ReleaseAskPass();
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _fromRemote.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        ReleaseAskPass();
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => _toRemote.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _toRemote.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _toRemote.WriteAsync(buffer, offset, count, cancellationToken);

    public override void Flush() => _toRemote.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _toRemote.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SshHostLauncher.Kill(_process);
            ReleaseAskPass();
        }

        base.Dispose(disposing);
    }

    private void ReleaseAskPass()
    {
        if (Interlocked.Exchange(ref _askPass, null) is { } path)
            SshHostLauncher.TryDelete(path);
    }
}
