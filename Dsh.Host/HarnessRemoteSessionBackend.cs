using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Host;

/** Core harness sessions and their ordered history/live event stream. */
internal sealed class HarnessRemoteSessionBackend(HarnessRemoteRuntime runtime, ExternalAgentSessions agents) : IRemoteSessionBackend
{
    public Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, RemoteSessionInfo>(StringComparer.Ordinal);
        foreach (var snapshot in runtime.Persistence?.List() ?? [])
        {
            if (snapshot.Header.IsSubagent)
                continue;
            byId[snapshot.Header.Id.Value] = Describe(snapshot.Header);
        }
        foreach (var agent in runtime.Registry.List())
        {
            if (agent.Session.Header.IsSubagent)
                continue;
            byId[agent.Id.Value] = Describe(agent);
        }
        return Task.FromResult<IReadOnlyList<RemoteSessionInfo>>([.. byId.Values.OrderByDescending(info => info.UpdatedAt)]);
    }

    public async Task<RemoteSessionInfo> CreateSessionAsync(string cwd, RemoteModelSelection? selection, RemoteProviderSpec? provider, CancellationToken cancellationToken)
    {
        var resolved = HostPathPolicy.Expand(cwd);
        var options = runtime.ResolveAgentOptions(selection, provider);
        var handle = await runtime.Registry.Create(new CreateAgentOptions(Cwd: resolved, AgentOptions: options), cancellationToken).ConfigureAwait(false);
        runtime.Own(handle);
        runtime.Track(handle.Agent);
        return Describe(handle.Agent);
    }

    public async Task<RemoteSessionInfo> ResumeSessionAsync(string sessionId, RemoteModelSelection? selection, RemoteProviderSpec? provider, CancellationToken cancellationToken)
    {
        var id = SessionId.Create(sessionId);
        if (runtime.Registry.Get(id) is { } live)
        {
            runtime.Track(live);
            return Describe(live);
        }
        var options = runtime.ResolveAgentOptions(selection, provider);
        var handle = await runtime.Registry.Resume(new ResumeAgentOptions(id, options), cancellationToken).ConfigureAwait(false);
        runtime.Own(handle);
        ApplySelectionOnResume(handle.Agent, selection);
        runtime.Track(handle.Agent);
        return Describe(handle.Agent);
    }

    public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken)
    {
        var id = SessionId.Create(sessionId);
        var approvals = runtime.App.Ctx.Get<ApprovalService>(ApprovalService.ServiceName, false);
        if (runtime.Registry.Get(id) is { } agent)
            return Task.FromResult(new RemoteSessionSnapshot(DshJson.Serialize(agent.Session.Header),
                [.. agent.Session.SnapshotEvents().Select(item => ToInfo(sessionId, item))],
                approvals?.EffectivePolicy(agent.Session).ToString()));
        using var handle = runtime.Persistence?.Open(id, SessionAccess.Read)
            ?? throw new InvalidOperationException($"unknown session: {sessionId}");
        var events = handle.Read();
        var restored = Session.FromRestore(id, events, handle.Header, handle.InheritedEventCount);
        return Task.FromResult(new RemoteSessionSnapshot(DshJson.Serialize(handle.Header),
            [.. events.Select(item => ToInfo(sessionId, item))], approvals?.EffectivePolicy(restored).ToString()));
    }

    /** Existing request headers outrank AgentOptions; append a change only when the requested selection differs. */
    private static void ApplySelectionOnResume(IAgent agent, RemoteModelSelection? selection)
    {
        if (selection is null || !HarnessRemoteRuntime.HasSelection(selection))
            return;
        if (agent.Session.RequestHeader() is not { } current || MatchesSelection(current, selection))
            return;
        agent.Session.Append(new RequestHeaderPayload(
            RequestHeader.Canonicalize(new EpochHeader(new LlmCallConfig(selection.Provider, selection.Model, HarnessRemoteRuntime.EffortOf(selection)))),
            RequestHeaderReasons.Change,
            true));
    }

    private static bool MatchesSelection(EpochHeader header, RemoteModelSelection selection)
        => string.Equals(header.Config.Provider, selection.Provider, StringComparison.Ordinal)
            && string.Equals(header.Config.Model, selection.Model, StringComparison.Ordinal)
            && header.Config.ReasoningEffort == HarnessRemoteRuntime.EffortOf(selection);

    public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken)
    {
        if (agents.IsAcp(sessionId))
        {
            agents.Prompt(sessionId, text);
            return Task.CompletedTask;
        }
        var content = new List<ContentBlock>();
        if (text.Length > 0)
            content.Add(new TextBlock(text));
        if (images.Count > 0 && runtime.App.Ctx.Get<IAttachmentStore>(FileAttachmentStore.ServiceName, false) is { } store)
        {
            foreach (var image in images)
                content.Add(new ImageBlock(store.Put(Convert.FromBase64String(image.Base64), image.MediaType, image.Width, image.Height, image.Name)));
        }
        runtime.Require(sessionId).Followup(MessageFactory.CreateUserMessage(content));
        return Task.CompletedTask;
    }

    /** Interrupt abandons this turn and cancels its outstanding approval/question decisions. */
    public Task InterruptAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (agents.IsAcp(sessionId))
            return agents.CancelAsync(sessionId, cancellationToken);
        runtime.Require(sessionId).Cancel(new AgentCancelCause.User());
        runtime.Pending.CancelSession(sessionId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var tools = runtime.App.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName, false);
        if (tools is null)
            return Task.FromResult<IReadOnlyList<string>>([]);
        var scope = runtime.Registry.Get(SessionId.Create(sessionId))?.ScopeKey;
        IReadOnlyList<string> names = [.. tools.Schemas(scope).Select(schema => schema.Name)];
        return Task.FromResult(names);
    }

    /** Install live listeners before taking history so subscribe replies cannot lose subsequent events. */
    public async Task<RemoteHostStream<RemoteEventInfo>> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
    {
        var id = SessionId.Create(sessionId);
        var session = agents.IsAcp(sessionId)
            ? await agents.SessionAsync(sessionId).ConfigureAwait(false)
            : runtime.Registry.Get(id)?.Session;
        var stored = session is null ? runtime.Persistence?.Stat(id) : null;
        if (session is null && stored is null)
            throw new InvalidOperationException($"unknown session: {sessionId}");
        var reset = fromSeq > (session?.Seq ?? stored?.EventCount ?? long.MaxValue);
        var buffer = Channel.CreateUnbounded<RemoteEventInfo>();
        var unsubscribe = runtime.App.Ctx.On<SessionEventNotification>(notification =>
        {
            if (notification.Session.Id == id)
                buffer.Writer.TryWrite(ToInfo(sessionId, notification.Event));
        });
        var unsubscribeTruncate = runtime.App.Ctx.On<SessionTruncateNotification>(notification =>
        {
            if (notification.Session.Id != id)
                return;
            buffer.Writer.TryWrite(new RemoteEventInfo(sessionId, "", 0, Reset: true));
            foreach (var item in notification.Session.SnapshotEvents())
                buffer.Writer.TryWrite(ToInfo(sessionId, item));
        });
        ValueTask Release()
        {
            unsubscribeTruncate();
            unsubscribe();
            buffer.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
        try
        {
            var history = session?.SnapshotEvents(reset ? 0 : fromSeq) ?? ReadPersisted(id, reset ? 0 : fromSeq);
            return new RemoteHostStream<RemoteEventInfo>(
                LiveEventsAsync(sessionId, history, buffer, fromSeq, reset, cancellationToken), Release);
        }
        catch
        {
            await Release().ConfigureAwait(false);
            throw;
        }
    }

    private static async IAsyncEnumerable<RemoteEventInfo> LiveEventsAsync(
        string sessionId,
        IReadOnlyList<SessionEvent> history,
        Channel<RemoteEventInfo> buffer,
        long fromSeq,
        bool reset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var next = reset ? 0 : fromSeq;
        if (reset)
            yield return new RemoteEventInfo(sessionId, "", 0, Reset: true);
        foreach (var sessionEvent in history)
        {
            next = sessionEvent.Seq + 1;
            yield return ToInfo(sessionId, sessionEvent);
        }
        await foreach (var item in buffer.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!item.Reset && item.Sequence < next)
                continue;
            next = item.Reset ? 0 : item.Sequence + 1;
            yield return item;
        }
    }

    private static RemoteSessionInfo Describe(IAgent agent)
    {
        var header = agent.Session.Header;
        return new RemoteSessionInfo(header.Id.Value, header.Cwd ?? "", agent.Status.ToString(), header.CreatedAt,
            header.Title, Summarize(agent.Session), LastEventTime(agent.Session));
    }

    private static RemoteSessionInfo Describe(SessionHeader header)
        => new(header.Id.Value, header.Cwd ?? "", "Idle", header.CreatedAt, header.Title, null, header.CreatedAt);

    private static string? Summarize(Session session)
    {
        var events = session.SnapshotEvents();
        for (var index = events.Count - 1; index >= 0; index--)
        {
            if (events[index].Data is not UserMessagePayload { Message.Source: UserMessageSource } user)
                continue;
            var text = string.Concat(user.Message.Content.OfType<TextBlock>().Select(block => block.Text));
            if (text.Length > 0)
                return text.Length > 120 ? text[..120] : text;
        }
        return null;
    }

    private static long LastEventTime(Session session)
    {
        var events = session.SnapshotEvents();
        return events.Count == 0 ? session.Header.CreatedAt : events[^1].Time;
    }

    private static RemoteEventInfo ToInfo(string sessionId, SessionEvent sessionEvent)
        => new(sessionId, DshJson.Serialize(sessionEvent), sessionEvent.Seq);

    private IReadOnlyList<SessionEvent> ReadPersisted(SessionId id, long fromSeq)
    {
        using var handle = runtime.Persistence?.Open(id, SessionAccess.Read);
        return handle is null ? [] : handle.Read(fromSeq);
    }
}
