namespace Dsh.RemoteHost;

/** An acquired backend reader. Dispose its owner even when cancellation prevents the first enumeration. */
public sealed class RemoteHostStream<T>(IAsyncEnumerable<T> stream, Func<ValueTask>? release = null) : IAsyncEnumerable<T>, IAsyncDisposable
{
    private Func<ValueTask>? _release = release;

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => stream.GetAsyncEnumerator(cancellationToken);

    public ValueTask DisposeAsync()
        => Interlocked.Exchange(ref _release, null)?.Invoke() ?? ValueTask.CompletedTask;
}
