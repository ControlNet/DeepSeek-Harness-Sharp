using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Plugins;

namespace Dsh.Host;

/**
 * 远端宿主能力后端: 直连进程内 harness 服务(会话 / 事件 / 工具), PTY 交给 PTY daemon 托管。
 * 事件经 ctx 通知转成推送流; 审批/问答经 HarnessDecisionBridge 进入待决策队列; 会话状态经 HarnessSessionTracker 进入登记表。
 * External agents (terminal and ACP mode) live in ExternalAgentSessions; ACP sessions answer host.session.subscribe/message/interrupt
 * from their transcript, so clients drive them like harness sessions.
 */
public sealed class HarnessRemoteHostBackend : IRemoteHostBackend, IAsyncDisposable
{
    /** 会话登记表在 harness 根下的位置(不放 run/: 那里是可随时清掉的运行时端点)。 */
    private const string RegistryFileName = "sessions.json";

    private readonly HarnessApp _app;
    private readonly ConcurrentDictionary<string, AgentHandle> _handles = new(StringComparer.Ordinal);
    private readonly Lock _providerGate = new();
    private readonly HashSet<string> _registeredProviders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HostPtySessions _ptys;
    private readonly ExternalAgentSessions _agents;
    private readonly HarnessDecisionBridge _decisions;
    private readonly HarnessSessionTracker _tracker;

    private HarnessRemoteHostBackend(HarnessApp app, AcpSessionServices services, HostPtySessions ptys, ExternalAgentSessions agents)
    {
        _app = app;
        Pending = services.Pending;
        Sessions = services.Registry;
        Notifications = services.Notifications;
        Files = new HostFileSystem(HostPathPolicy.ForHome(Sessions, app.Home.Root));
        _ptys = ptys;
        _agents = agents;
        _decisions = new HarnessDecisionBridge(app.Ctx, Pending);
        _tracker = new HarnessSessionTracker(app.Ctx, Sessions, Pending);
    }

    /** 读回会话登记表并与 PTY daemon 对账; ptyDaemon 为空时用与 `dsharp tui list` 相同的默认 daemon。 */
    public static async Task<HarnessRemoteHostBackend> CreateAsync(HarnessApp app, HostPtyDaemon? ptyDaemon = null, CancellationToken cancellationToken = default)
    {
        var pending = new HostPendingDecisions();
        var sessions = await HostSessionRegistry.OpenAsync(Path.Combine(app.Home.HostPath, RegistryFileName), pending, cancellationToken).ConfigureAwait(false);
        var notifications = await HostNotificationCenter.OpenAsync(Path.Combine(app.Home.HostPath, HostNotificationCenter.FileName), sessions, pending, cancellationToken).ConfigureAwait(false);
        var services = new AcpSessionServices(sessions, pending, notifications);
        var ptys = new HostPtySessions(sessions, ptyDaemon ?? HostPtyDaemon.Default(), app.Home.Root);
        await ptys.RestoreAsync(cancellationToken).ConfigureAwait(false);
        var agents = await ExternalAgentSessions.OpenAsync(app.Home.HostPath, app.Ctx, services, ptys, cancellationToken).ConfigureAwait(false);
        return new HarnessRemoteHostBackend(app, services, ptys, agents);
    }

    public HostPendingDecisions Pending { get; }

    public HostSessionRegistry Sessions { get; }

    public HostFileSystem Files { get; }

    public HostNotificationCenter Notifications { get; }

    private AgentRegistry Registry => _app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)
        ?? throw new InvalidOperationException("AgentRegistry 未注册");

    private ISessionPersistence? Persistence => _app.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, strict: false);

    public Task<HostInfo> InfoAsync(CancellationToken cancellationToken)
        => Task.FromResult(new HostInfo(HostProtocol.Version, RemoteHostServer.ReleaseVersion, Platform(), _app.Home.Root));

    public Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, RemoteSessionInfo>(StringComparer.Ordinal);
        foreach (var snapshot in Persistence?.List() ?? [])
        {
            if (snapshot.Header.IsSubagent)
                continue;
            byId[snapshot.Header.Id.Value] = Describe(snapshot.Header);
        }
        foreach (var agent in Registry.List())
        {
            if (agent.Session.Header.IsSubagent)
                continue;
            byId[agent.Id.Value] = Describe(agent);
        }
        return Task.FromResult<IReadOnlyList<RemoteSessionInfo>>([.. byId.Values.OrderByDescending(info => info.UpdatedAt)]);
    }

    public async Task<RemoteSessionInfo> CreateSessionAsync(string cwd, RemoteModelSelection? selection, RemoteProviderSpec? provider, CancellationToken cancellationToken)
    {
        // 会话 cwd 必须是绝对路径(POSIX 与 Windows 皆然): 空/相对一律解析到远端家目录。
        var resolved = HostPathPolicy.Expand(cwd);
        var options = ResolveAgentOptions(selection, provider);
        var handle = await Registry.Create(new CreateAgentOptions(Cwd: resolved, AgentOptions: options), cancellationToken).ConfigureAwait(false);
        _handles[handle.Agent.Id.Value] = handle;
        _tracker.Track(handle.Agent);
        return Describe(handle.Agent);
    }

    public async Task<RemoteSessionInfo> ResumeSessionAsync(string sessionId, RemoteModelSelection? selection, RemoteProviderSpec? provider, CancellationToken cancellationToken)
    {
        var id = SessionId.Create(sessionId);
        if (Registry.Get(id) is { } live)
        {
            _tracker.Track(live);
            return Describe(live);
        }
        var options = ResolveAgentOptions(selection, provider);
        var handle = await Registry.Resume(new ResumeAgentOptions(id, options), cancellationToken).ConfigureAwait(false);
        _handles[handle.Agent.Id.Value] = handle;
        ApplySelectionOnResume(handle.Agent, selection);
        _tracker.Track(handle.Agent);
        return Describe(handle.Agent);
    }

    public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken)
    {
        var id = SessionId.Create(sessionId);
        var approvals = _app.Ctx.Get<ApprovalService>(ApprovalService.ServiceName, false);
        if (Registry.Get(id) is { } agent)
            return Task.FromResult(new RemoteSessionSnapshot(DshJson.Serialize(agent.Session.Header),
                [.. agent.Session.SnapshotEvents().Select(item => ToInfo(sessionId, item))],
                approvals?.EffectivePolicy(agent.Session).ToString()));
        using var handle = Persistence?.Open(id, SessionAccess.Read)
            ?? throw new InvalidOperationException($"unknown session: {sessionId}");
        var events = handle.Read();
        var restored = Session.FromRestore(id, events, handle.Header, handle.InheritedEventCount);
        return Task.FromResult(new RemoteSessionSnapshot(DshJson.Serialize(handle.Header),
            [.. events.Select(item => ToInfo(sessionId, item))], approvals?.EffectivePolicy(restored).ToString()));
    }

    public async Task<RemoteCommandResult> ExecuteSessionCommandAsync(string sessionId, string line, CancellationToken cancellationToken)
    {
        var commands = _app.Ctx.Get<CommandsService>(CommandsService.ServiceName, false);
        var execution = commands is null ? null : await commands.Execute(Require(sessionId), line).ConfigureAwait(false);
        return execution?.Result switch
        {
            CommandResult.Success result => new(true, result.Text, result.FollowupPrompt),
            CommandResult.Error result => new(true, result.Text, result.FollowupPrompt),
            _ => new(false, null, null),
        };
    }

    public Task<IReadOnlyList<RemoteCommandDescriptor>> ListSessionCommandsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var commands = _app.Ctx.Get<CommandsService>(CommandsService.ServiceName, false)?.List(Require(sessionId)) ?? [];
        return Task.FromResult<IReadOnlyList<RemoteCommandDescriptor>>([.. commands.Select(command => new RemoteCommandDescriptor(command.Name, command.Description))]);
    }

    public Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken)
    {
        var id = SessionId.Create(sessionId);
        var persistence = Persistence ?? throw new InvalidOperationException("session persistence is unavailable");
        if (Registry.Get(id) is { } agent)
            agent.Session.Rename(title);
        persistence.Rename(id, title);
        Sessions.SetTitle(sessionId, title);
        return Task.CompletedTask;
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (Sessions.Get(sessionId) is not null)
            await StopAgentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        (Persistence ?? throw new InvalidOperationException("session persistence is unavailable")).Delete(SessionId.Create(sessionId));
        Sessions.Remove(sessionId);
    }

    public Task SetSessionApprovalPolicyAsync(string sessionId, string policy, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ApprovalPolicy>(policy, out var parsed) || !Enum.IsDefined(parsed))
            throw new ArgumentException($"unknown approval policy: {policy}", nameof(policy));
        var approvals = _app.Ctx.Get<ApprovalService>(ApprovalService.ServiceName)
            ?? throw new InvalidOperationException("approval service is unavailable");
        approvals.SetPolicy(Require(sessionId), parsed);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<RemoteSubagentInfo>> ListSubagentsAsync(string rootSessionId, CancellationToken cancellationToken)
    {
        var runtime = _app.Ctx.Get<ISubagentService>(ISubagentService.ServiceName, false);
        if (runtime is null)
            return [];
        var entries = await runtime.ListDescendantsAsync(SessionId.Create(rootSessionId), signal: cancellationToken).ConfigureAwait(false);
        return [.. entries.OfType<SubagentListEntry.Child>().Select(child =>
        {
            var live = runtime.GetLive(child.Id);
            var header = live?.Session.Header ?? Persistence?.Stat(child.Id)?.Header;
            return new RemoteSubagentInfo(child.Id.Value, child.Parent?.Value, child.Depth ?? 1, child.Mode, child.Label,
                child.HasChildren, live is not null, header?.CreatedAt ?? 0);
        })];
    }

    public Task<IReadOnlyList<RemotePluginInfo>> ListPluginsAsync(CancellationToken cancellationToken)
    {
        var manager = _app.Ctx.Get<IPluginManager>("pluginManager", false)
            ?? throw new InvalidOperationException("plugin manager is unavailable");
        var settings = HarnessSettings.Load(_app.Home);
        return Task.FromResult<IReadOnlyList<RemotePluginInfo>>([.. manager.PackageNames.Order(StringComparer.Ordinal).Select(package =>
        {
            var setting = settings.Plugins.GetValueOrDefault(package);
            var permissions = setting?.Permissions ?? PluginPermissions.AllAllowed;
            return new RemotePluginInfo(package, manager.Describe(package), setting?.Enabled ?? true,
                permissions.Read, permissions.Write, permissions.Network);
        })]);
    }

    public Task<string> SetPluginPermissionAsync(string package, string permission, bool allowed, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<PluginPermission>(permission, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            throw new ArgumentException($"unknown plugin permission: {permission}", nameof(permission));
        var manager = _app.Ctx.Get<IPluginManager>("pluginManager", false)
            ?? throw new InvalidOperationException("plugin manager is unavailable");
        return Task.FromResult(manager.SetPermission(package, parsed, allowed));
    }

    public Task<IReadOnlyList<string>> ListReasoningEffortsAsync(string provider, string model, CancellationToken cancellationToken)
    {
        var llm = _app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false);
        return Task.FromResult<IReadOnlyList<string>>([.. (llm?.ResolveModelInfo(provider, model).Reasoning?.Efforts ?? [])
            .Select(effort => effort.Id.Value)]);
    }

    public Task<IReadOnlyList<string>> ListProviderModelsAsync(string provider, CancellationToken cancellationToken)
    {
        var llm = _app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false);
        return Task.FromResult<IReadOnlyList<string>>([.. (llm?.ListModels(provider) ?? []).Select(model => model.Id)]);
    }

    /**
     * 把客户端的模型选择落成 AgentOptions; 若带了 provider 规格则先在远端按需注册适配器。
     * 远端可能没有任何 settings.yaml: 客户端提供完整端点, 避免远端 NO_ADAPTER。
     */
    private AgentOptions? ResolveAgentOptions(RemoteModelSelection? selection, RemoteProviderSpec? provider)
    {
        if (selection is null || !HasSelection(selection))
            return null;
        if (provider is not null)
            EnsureProviderRegistered(provider);
        return new AgentOptions(selection.Provider, selection.Model, EffortOf(selection));
    }

    /** 同一个 provider id 每个进程只注册一次: 已注册(本体设置或上次远端选择)即跳过, 失败把原因带进异常。 */
    private void EnsureProviderRegistered(RemoteProviderSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Id))
            throw new InvalidOperationException("远端模型选择缺少 provider id, 无法注册适配器");
        lock (_providerGate)
        {
            if (_registeredProviders.Contains(spec.Id) || IsProviderRegistered(spec.Id))
                return;
            var settings = new ProviderSettings
            {
                Type = spec.Type,
                Options = new ProviderOptions { BaseUrl = spec.BaseUrl, ApiKey = spec.ApiKey, ApiKeyEnv = spec.ApiKeyEnv },
            };
            var result = ProviderRegistrar.RegisterProvider(_app.Ctx, new HarnessOptions(_app.Home), spec.Id, settings, isDefault: false);
            if (result.Handle is null)
                throw new InvalidOperationException($"远端 provider \"{spec.Id}\" 注册失败: {result.Error}");
            _registeredProviders.Add(spec.Id);
        }
    }

    private bool IsProviderRegistered(string id)
        => _app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false)?.ListProviders()
            .Any(provider => string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase)) ?? false;

    /**
     * 恢复会话时补一条请求头: 会话内既有的请求头优先于 AgentOptions(见 AgentLoopAgent.BuildRequest),
     * 因此仅在已有请求头且与客户端选择不一致时追加 Change 记录, 让客户端选择真正覆盖旧模型; 无请求头时 AgentOptions 即生效。
     */
    private static void ApplySelectionOnResume(IAgent agent, RemoteModelSelection? selection)
    {
        if (selection is null || !HasSelection(selection))
            return;
        if (agent.Session.RequestHeader() is not { } current || MatchesSelection(current, selection))
            return;
        agent.Session.Append(new RequestHeaderPayload(
            RequestHeader.Canonicalize(new EpochHeader(new LlmCallConfig(selection.Provider, selection.Model, EffortOf(selection)))),
            RequestHeaderReasons.Change,
            true));
    }

    private static bool HasSelection(RemoteModelSelection selection)
        => !string.IsNullOrWhiteSpace(selection.Provider) && !string.IsNullOrWhiteSpace(selection.Model);

    private static ReasoningEffortId? EffortOf(RemoteModelSelection selection)
        => string.IsNullOrWhiteSpace(selection.ReasoningEffort) ? null : ReasoningEffortId.Create(selection.ReasoningEffort);

    private static bool MatchesSelection(EpochHeader header, RemoteModelSelection selection)
        => string.Equals(header.Config.Provider, selection.Provider, StringComparison.Ordinal)
            && string.Equals(header.Config.Model, selection.Model, StringComparison.Ordinal)
            && header.Config.ReasoningEffort == EffortOf(selection);

    public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken)
    {
        if (_agents.IsAcp(sessionId))
            return PromptAgentAsync(sessionId, text, cancellationToken);
        var content = new List<ContentBlock>();
        if (text.Length > 0)
            content.Add(new TextBlock(text));
        if (images.Count > 0 && _app.Ctx.Get<IAttachmentStore>(FileAttachmentStore.ServiceName, false) is { } store)
        {
            foreach (var image in images)
                content.Add(new ImageBlock(store.Put(Convert.FromBase64String(image.Base64), image.MediaType, image.Width, image.Height, image.Name)));
        }
        Require(sessionId).Followup(MessageFactory.CreateUserMessage(content));
        return Task.CompletedTask;
    }

    /** 中断即放弃这一轮: 该会话还在等回答的审批/问答一并取消。 */
    public Task InterruptAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_agents.IsAcp(sessionId))
            return _agents.CancelAsync(sessionId, cancellationToken);
        Require(sessionId).Cancel(new AgentCancelCause.User());
        Pending.CancelSession(sessionId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var runtime = _app.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName, false);
        if (runtime is null)
            return Task.FromResult<IReadOnlyList<string>>([]);
        var scope = Registry.Get(SessionId.Create(sessionId))?.ScopeKey;
        IReadOnlyList<string> names = [.. runtime.Schemas(scope).Select(schema => schema.Name)];
        return Task.FromResult(names);
    }

    public Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken)
        => ExternalAgentSessions.CatalogAsync(cancellationToken);

    /** Harness sessions (agent kind dsharp) go through host.session.create semantics; every other kind is an external agent. */
    public async Task<RemoteHostSession> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken)
    {
        if (request.AgentKind != HostAgentKinds.Dsharp)
            return await _agents.StartAsync(request, cancellationToken).ConfigureAwait(false);
        var directory = await ExternalAgentSessions.PrepareDirectoryAsync(request.ProjectPath, request.Worktree, cancellationToken).ConfigureAwait(false);
        var created = await CreateSessionAsync(directory, request.Selection, request.Provider, cancellationToken).ConfigureAwait(false);
        if (request.InitialPrompt is { } prompt)
            await SendMessageAsync(created.Id, prompt, [], cancellationToken).ConfigureAwait(false);
        return Sessions.Get(created.Id) ?? throw new InvalidOperationException($"session {created.Id} was not registered");
    }

    public Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken)
    {
        if (_agents.IsAcp(sessionId))
        {
            _agents.Prompt(sessionId, text);
            return Task.CompletedTask;
        }
        return KindOf(sessionId) == HostSessionKinds.Harness
            ? SendMessageAsync(sessionId, text, [], cancellationToken)
            : throw new InvalidOperationException($"session {sessionId} takes input through its terminal (host.pty.write)");
    }

    public Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken)
        => KindOf(sessionId) switch
        {
            HostSessionKinds.Harness => InterruptAsync(sessionId, cancellationToken),
            HostSessionKinds.Acp => _agents.CancelAsync(sessionId, cancellationToken),
            _ => throw new InvalidOperationException($"session {sessionId} is cancelled through its terminal (host.pty.write)"),
        };

    /** A harness session ends by releasing its agent; terminals and external agents end their process. */
    public Task StopAgentAsync(string sessionId, CancellationToken cancellationToken)
    {
        switch (KindOf(sessionId))
        {
            case HostSessionKinds.Harness:
                if (_handles.TryRemove(sessionId, out var handle))
                    handle.Dispose.Dispose();
                return Task.CompletedTask;
            case HostSessionKinds.Terminal:
                return _ptys.StopAsync(sessionId, cancellationToken);
            default:
                return _agents.StopAsync(sessionId, cancellationToken);
        }
    }

    public async Task<RemoteHostSession> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (KindOf(sessionId) != HostSessionKinds.Harness)
            return await _agents.ResumeAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await ResumeSessionAsync(sessionId, null, null, cancellationToken).ConfigureAwait(false);
        return Sessions.Get(sessionId) ?? throw new InvalidOperationException($"session {sessionId} was not registered");
    }

    private string KindOf(string sessionId)
        => Sessions.Get(sessionId)?.Kind ?? throw new InvalidOperationException($"unknown session: {sessionId}");

    public Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken)
        => _ptys.StartAsync(request, HostPathPolicy.Expand(request.Cwd ?? ""), cancellationToken);

    public Task WritePtyAsync(string ptyId, byte[] data, CancellationToken cancellationToken)
        => _ptys.WriteAsync(ptyId, data, cancellationToken);

    public Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken)
        => _ptys.ResizeAsync(ptyId, columns, rows, cancellationToken);

    public Task StopPtyAsync(string ptyId, CancellationToken cancellationToken)
        => _ptys.StopAsync(ptyId, cancellationToken);

    public Task<IAsyncEnumerable<RemotePtyOutput>> AttachPtyAsync(string ptyId, CancellationToken cancellationToken)
        => _ptys.AttachAsync(ptyId, cancellationToken);

    /** 历史和实时通知使用同一个会话来源，代理重启不会要求 UI 重新订阅。 */
    public async Task<IAsyncEnumerable<RemoteEventInfo>> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
    {
        var id = SessionId.Create(sessionId);
        var session = _agents.IsAcp(sessionId)
            ? await _agents.SessionAsync(sessionId).ConfigureAwait(false)
            : Registry.Get(id)?.Session;
        var stored = session is null ? Persistence?.Stat(id) : null;
        if (session is null && stored is null)
            throw new InvalidOperationException($"unknown session: {sessionId}");
        var reset = fromSeq > (session?.Seq ?? stored?.EventCount ?? long.MaxValue);
        var buffer = Channel.CreateUnbounded<RemoteEventInfo>();
        var unsubscribe = _app.Ctx.On<SessionEventNotification>(notification =>
        {
            if (notification.Session.Id == id)
                buffer.Writer.TryWrite(ToInfo(sessionId, notification.Event));
        });
        var unsubscribeTruncate = _app.Ctx.On<SessionTruncateNotification>(notification =>
        {
            if (notification.Session.Id != id)
                return;
            buffer.Writer.TryWrite(new RemoteEventInfo(sessionId, "", 0, Reset: true));
            foreach (var item in notification.Session.SnapshotEvents())
                buffer.Writer.TryWrite(ToInfo(sessionId, item));
        });
        var history = session?.SnapshotEvents(reset ? 0 : fromSeq) ?? ReadPersisted(id, reset ? 0 : fromSeq);
        return LiveEventsAsync(sessionId, history, buffer, () =>
        {
            unsubscribeTruncate();
            return unsubscribe();
        }, fromSeq, reset, cancellationToken);
    }

    private static async IAsyncEnumerable<RemoteEventInfo> LiveEventsAsync(
        string sessionId,
        IReadOnlyList<SessionEvent> history,
        Channel<RemoteEventInfo> buffer,
        Func<bool> unsubscribe,
        long fromSeq,
        bool reset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
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
        finally
        {
            unsubscribe();
            buffer.Writer.TryComplete();
        }
    }

    private IAgent Require(string sessionId)
        => Registry.Get(SessionId.Create(sessionId)) ?? throw new InvalidOperationException($"unknown session: {sessionId}");

    private static RemoteSessionInfo Describe(IAgent agent)
    {
        var header = agent.Session.Header;
        return new RemoteSessionInfo(
            header.Id.Value,
            header.Cwd ?? "",
            agent.Status.ToString(),
            header.CreatedAt,
            header.Title,
            Summarize(agent.Session),
            LastEventTime(agent.Session));
    }

    private static RemoteSessionInfo Describe(SessionHeader header)
        => new(header.Id.Value, header.Cwd ?? "", "Idle", header.CreatedAt, header.Title, null, header.CreatedAt);

    /** 会话摘要: 最后一条用户文本, 供侧栏列表显示。 */
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
        using var handle = Persistence?.Open(id, SessionAccess.Read);
        return handle is null ? [] : handle.Read(fromSeq);
    }

    private static string Platform()
        => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /**
     * 先停通知(host 自己退出造成的会话结束不进历史), 再解开瀑布并取消全部未决请求(放开还在等回答的工具), 再结束会话;
     * ACP agents end with the host; PTY 留在 daemon 里, 登记表最后落盘。
     */
    public async ValueTask DisposeAsync()
    {
        await Notifications.DisposeAsync().ConfigureAwait(false);
        _decisions.Dispose();
        Pending.Dispose();
        _tracker.Dispose();
        foreach (var handle in _handles.Values)
            handle.Dispose.Dispose();
        _handles.Clear();
        await _agents.DisposeAsync().ConfigureAwait(false);
        await _ptys.DisposeAsync().ConfigureAwait(false);
        await Sessions.DisposeAsync().ConfigureAwait(false);
    }
}
