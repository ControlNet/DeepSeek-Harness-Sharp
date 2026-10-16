using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Dsh.Tests;

/**
 * Output side of a JsonRpcLineTransport under test: each line is published only once it is complete.
 * Tests await the next message instead of polling a StringWriter that the transport thread is still appending to.
 */
internal sealed class JsonRpcLineCapture : TextWriter
{
    private readonly StringBuilder _partial = new();
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly Lock _gate = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_gate)
        {
            if (value != '\n')
            {
                _partial.Append(value);
                return;
            }

            _lines.Writer.TryWrite(_partial.ToString().TrimEnd('\r'));
            _partial.Clear();
        }
    }

    /** The next complete JSON-RPC message; bounded only by the test's own timeout. */
    public async Task<JsonDocument> NextMessageAsync(CancellationToken cancellationToken)
        => JsonDocument.Parse(await _lines.Reader.ReadAsync(cancellationToken));
}
