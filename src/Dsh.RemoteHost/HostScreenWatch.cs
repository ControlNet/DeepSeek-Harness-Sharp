using System.Threading.Channels;
using Dsh.AgentAdapters;
using Dsh.Pty;

namespace Dsh.RemoteHost;

/**
 * Classifies one terminal-mode agent's screen with its adapter's rules whenever output arrives.
 * Output pokes the watch; it waits SettleDelay, reads the daemon's screen and classifies the tail, so a steady stream of output
 * costs one reading per SettleDelay and the last reading always follows the last output.
 * Only state changes are reported; the readings feed status and notifications, never task completion.
 */
internal sealed class HostScreenWatch : IAsyncDisposable
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

    private readonly Channel<bool> _pokes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new();
    private readonly AgentScreenRules _rules;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _readScreen;
    private readonly Action<AgentScreenState> _changed;
    private readonly Task _loop;
    private AgentScreenState _state = AgentScreenState.Unknown;

    public HostScreenWatch(AgentScreenRules rules, Func<CancellationToken, Task<IReadOnlyList<string>>> readScreen, Action<AgentScreenState> changed)
    {
        _rules = rules;
        _readScreen = readScreen;
        _changed = changed;
        _loop = Task.Run(() => LoopAsync(_stop.Token));
        Poke();
    }

    public void Poke() => _pokes.Writer.TryWrite(true);

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in _pokes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(SettleDelay, cancellationToken).ConfigureAwait(false);
                await ClassifyAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /** A reading that fails (the PTY is going away, the daemon restarts) is skipped; the next output tries again. */
    private async Task ClassifyAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> lines;
        try
        {
            lines = await _readScreen(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or PtyDaemonNotRunningException)
        {
            return;
        }
        var state = _rules.Classify(lines);
        if (state == _state)
            return;
        _state = state;
        _changed(state);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _pokes.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
