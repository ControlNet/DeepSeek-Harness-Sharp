using System.Threading.Channels;

namespace Dsh.Tests;

/** In-memory input of a JSON-RPC line transport: each line written to the channel is read back with its terminating newline, in as many chunks as the reader asks for. */
internal sealed class ChannelTextReader(Channel<string> channel) : TextReader
{
    private string _line = "";
    private int _offset;

    public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset == _line.Length)
        {
            try
            {
                _line = $"{await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)}\n";
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
            _offset = 0;
        }
        var count = Math.Min(buffer.Length, _line.Length - _offset);
        _line.AsSpan(_offset, count).CopyTo(buffer.Span);
        _offset += count;
        return count;
    }
}
