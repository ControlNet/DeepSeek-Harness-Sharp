using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dsh.Core;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

public sealed class RemoteHostReaderInitializationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory(Timeout = 60000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringInitializationReleasesRealReaderAndPreservesAnotherView(bool pty)
    {
        DelayedBackend? delayed = null;
        await using var host = await HarnessHostFixture.CreateAsync("reader-init", Ct,
            backend => delayed = new DelayedBackend(backend, pty));
        var client = await host.ConnectAsync("owner", Ct);
        string resource;
        if (pty)
        {
            var shell = RemoteHostMultiClientTests.Shell;
            resource = await client.Pty.StartPtyAsync(new RemotePtyStartRequest(shell.FileName, shell.Arguments), Ct);
        }
        else
        {
            resource = (await client.CreateSessionAsync(host.Environment.App.Home.Root, cancellationToken: Ct)).Id;
            RemoteHostMultiClientTests.Agent(host, resource).Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("history")));
        }

        await using var transport = await RemoteHostListener.ConnectAsync(host.Root, Ct);
        await using var peer = new JsonRpcPeer(transport);
        var received = Channel.CreateUnbounded<JsonRpcMessage>();
        peer.NotificationReceived += message => received.Writer.TryWrite(message);
        peer.Start();
        var gate = delayed!;
        var pending = peer.RequestAsync(pty ? PtyProtocol.MethodPtyAttach : HostProtocol.MethodSessionSubscribe,
            Parameters(pty, resource, "cancelled-view"), Ct);
        try
        {
            await gate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            // The second view reads the same real resource while the first is still initializing.
            await peer.RequestAsync(pty ? PtyProtocol.MethodPtyAttach : HostProtocol.MethodSessionSubscribe,
                Parameters(pty, resource, "surviving-view"), Ct);
            await peer.RequestAsync(pty ? PtyProtocol.MethodPtyDetach : HostProtocol.MethodSessionUnsubscribe,
                JsonSerializer.SerializeToElement(new RemoteSubscriptionRef("cancelled-view"), HostProtocolJsonContext.Default.RemoteSubscriptionRef), Ct);
            gate.Release.TrySetResult();
            try
            {
                await pending;
            }
            catch (JsonRpcException)
            {
                // Cancellation may reject the outstanding subscribe, but initialization errors must never be ACKed early.
            }
            await gate.ReaderReleased.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            const string marker = "reader-survives-42";
            if (pty)
            {
                var command = OperatingSystem.IsWindows()
                    ? "set /a readerResult=40+2 & echo reader-survives-!readerResult!\r\n"
                    : "printf 'reader-survives-%s\\n' \"$((40+2))\"\n";
                await peer.RequestAsync(PtyProtocol.MethodPtyWrite,
                    JsonSerializer.SerializeToElement(new RemotePtyInput(resource, Convert.ToBase64String(Encoding.UTF8.GetBytes(command))), PtyProtocolJsonContext.Default.RemotePtyInput), Ct);
            }
            else
            {
                RemoteHostMultiClientTests.Agent(host, resource).Session.Append(new UserMessagePayload(MessageFactory.CreateUserText(marker)));
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var output = new StringBuilder();
            await foreach (var message in received.Reader.ReadAllAsync(deadline.Token))
            {
                if (pty && message.Method == PtyProtocol.NotificationPtyOutput && message.Params is { } bytes)
                {
                    var frame = JsonSerializer.Deserialize(bytes, PtyProtocolJsonContext.Default.RemotePtyFrame)!;
                    Assert.NotEqual("cancelled-view", frame.SubscriptionId);
                    if (frame.SubscriptionId == "surviving-view")
                        output.Append(Encoding.UTF8.GetString(Convert.FromBase64String(frame.Output.Base64)));
                }
                else if (!pty && message.Method == HostProtocol.NotificationEvent && message.Params is { } events)
                {
                    var frame = JsonSerializer.Deserialize(events, HostProtocolJsonContext.Default.RemoteEventFrame)!;
                    Assert.NotEqual("cancelled-view", frame.SubscriptionId);
                    if (frame.SubscriptionId == "surviving-view")
                        output.Append(frame.Event.EventJson);
                }
                if (output.ToString().Contains(marker, StringComparison.Ordinal))
                    return;
            }
            Assert.Fail("surviving view ended before receiving the real session/terminal output");
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    }

    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownResourceFailsTheInitializingRpc(bool pty)
    {
        await using var host = await HarnessHostFixture.CreateAsync("missing-reader", Ct);
        await using var transport = await RemoteHostListener.ConnectAsync(host.Root, Ct);
        await using var peer = new JsonRpcPeer(transport);
        peer.Start();
        await Assert.ThrowsAsync<JsonRpcException>(() => peer.RequestAsync(
            pty ? PtyProtocol.MethodPtyAttach : HostProtocol.MethodSessionSubscribe,
            Parameters(pty, "missing-resource", "missing-view"), Ct));
    }

    private static JsonElement Parameters(bool pty, string resource, string reader)
        => pty
            ? JsonSerializer.SerializeToElement(new RemotePtyAttachRequest(resource, reader), PtyProtocolJsonContext.Default.RemotePtyAttachRequest)
            : JsonSerializer.SerializeToElement(new RemoteSubscribeRequest(resource, 0, reader), HostProtocolJsonContext.Default.RemoteSubscribeRequest);

    /** Delays only delivery of an already acquired real reader; it does not replace session or PTY behavior. */
    private sealed class DelayedBackend(IRemoteHostBackend backend, bool delayPty) : IRemoteHostBackend, IRemoteSessionBackend, IRemotePtyBackend
    {
        private int _delayed;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReaderReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HostPendingDecisions Pending => backend.Pending;
        public HostSessionRegistry Sessions => backend.Sessions;
        public IRemoteSessionBackend Session => this;
        public IRemotePtyBackend Pty => this;
        public IRemoteAgentBackend? Agent => backend.Agent;
        public HostFileSystem? Files => backend.Files;
        public HostGitService? Git => backend.Git;
        public HostNotificationCenter? Notifications => backend.Notifications;
        public IRemoteSessionEditingBackend? SessionEditing => backend.SessionEditing;
        public IRemotePluginsBackend? Plugins => backend.Plugins;
        public IRemoteModelsBackend? Models => backend.Models;
        public Task<HostInfo> InfoAsync(CancellationToken ct) => backend.InfoAsync(ct);
        public Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken ct) => backend.Session.ListSessionsAsync(ct);
        public Task<RemoteSessionInfo> CreateSessionAsync(string cwd, RemoteModelSelection? selection, RemoteProviderSpec? provider, CancellationToken ct) => backend.Session.CreateSessionAsync(cwd, selection, provider, ct);
        public Task<RemoteSessionInfo> ResumeSessionAsync(string id, RemoteModelSelection? selection, RemoteProviderSpec? provider, CancellationToken ct) => backend.Session.ResumeSessionAsync(id, selection, provider, ct);
        public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string id, CancellationToken ct) => backend.Session.GetSessionSnapshotAsync(id, ct);
        public Task SendMessageAsync(string id, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken ct) => backend.Session.SendMessageAsync(id, text, images, ct);
        public Task InterruptAsync(string id, CancellationToken ct) => backend.Session.InterruptAsync(id, ct);
        public Task<IReadOnlyList<string>> ListToolsAsync(string id, CancellationToken ct) => backend.Session.ListToolsAsync(id, ct);
        public Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken ct) => backend.Pty!.StartPtyAsync(request, ct);
        public Task WritePtyAsync(string id, byte[] data, CancellationToken ct) => backend.Pty!.WritePtyAsync(id, data, ct);
        public Task ResizePtyAsync(string id, int columns, int rows, CancellationToken ct) => backend.Pty!.ResizePtyAsync(id, columns, rows, ct);
        public Task StopPtyAsync(string id, CancellationToken ct) => backend.Pty!.StopPtyAsync(id, ct);
        public async Task<IAsyncEnumerable<RemoteEventInfo>> SubscribeAsync(string id, long from, CancellationToken ct)
            => await DelayAsync(await backend.Session.SubscribeAsync(id, from, ct), false, ct);
        public async Task<IAsyncEnumerable<RemotePtyOutput>> AttachPtyAsync(string id, CancellationToken ct)
            => await DelayAsync(await backend.Pty!.AttachPtyAsync(id, ct), true, ct);

        private async Task<IAsyncEnumerable<T>> DelayAsync<T>(IAsyncEnumerable<T> stream, bool pty, CancellationToken ct)
        {
            if (pty != delayPty || Interlocked.Exchange(ref _delayed, 1) != 0)
                return stream;
            var reader = stream.GetAsyncEnumerator(ct);
            Assert.True(await reader.MoveNextAsync());
            var observed = new ObservedStream<T>(reader, ReaderReleased);
            Ready.TrySetResult();
            await Release.Task; // Intentionally finishes after stop, like an asynchronous acquisition already in flight.
            return observed;
        }
    }

    private sealed class ObservedStream<T>(IAsyncEnumerator<T> reader, TaskCompletionSource released) : IAsyncEnumerable<T>, IAsyncDisposable
    {
        private int _disposed;
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        private async IAsyncEnumerable<T> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                yield return reader.Current;
                while (await reader.MoveNextAsync())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return reader.Current;
                }
            }
            finally
            {
                await DisposeAsync();
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            await reader.DisposeAsync();
            released.TrySetResult();
        }
    }
}
