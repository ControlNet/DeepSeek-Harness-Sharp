using System.Threading.Channels;

namespace Dsh.Pty;

/**
 * Stream-style reads over a channel of output chunks: a chunk larger than the caller's buffer is handed out over several reads.
 * The unread tail of a chunk is served before waiting on the channel again.
 * Waiting first used to park the tail until the next chunk arrived, so the last output of an idle program never reached the reader (CI: SNAPMARK/LIVE-42 missing on subscribed panes).
 */
internal sealed class PtyOutputReader(ChannelReader<byte[]> chunks)
{
    private ReadOnlyMemory<byte> _pending;

    /** Returns 0 only once the channel is complete and every buffered byte has been read. */
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
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
}
