using System.Threading.Channels;

namespace Dsh.Pty;

/** Serves each output chunk's unread tail before waiting for another chunk. */
internal sealed class PtyOutputReader(ChannelReader<byte[]> chunks)
{
    private ReadOnlyMemory<byte> _pending;

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return 0;

        while (_pending.IsEmpty)
        {
            if (!await chunks.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                return 0;
            if (chunks.TryRead(out var chunk))
                _pending = chunk;
        }

        var take = Math.Min(_pending.Length, buffer.Length);
        _pending.Span[..take].CopyTo(buffer.Span);
        _pending = _pending[take..];
        return take;
    }

    /** Discards both a partially read chunk and queued output before a fresh attach. */
    public void DrainPendingOutput()
    {
        _pending = default;
        while (chunks.TryRead(out _))
        {
        }
    }
}
