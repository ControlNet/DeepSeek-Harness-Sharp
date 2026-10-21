using Avalonia.Threading;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Gui.Services;

/** A GUI projection of a Host-owned chat. Disposing detaches the view, never stops the agent. */
public sealed class HostChatSession : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _subscription;
    private readonly List<SessionEvent> _events = [];
    private long _generation;
    private long _navigationGeneration;
    private long _reloadGeneration;
    private MachineConnection? _followingMachine;

    private HostChatSession(FleetWorkspace fleet, RemoteSessionInfo info, RemoteSessionSnapshot snapshot, RemoteModelSelection? selection)
    {
        Fleet = fleet;
        Info = info;
        Selection = selection;
        Header = DshJson.Deserialize<SessionHeader>(snapshot.HeaderJson) ?? throw new InvalidDataException("Host returned no session header");
        ReplaceEvents(snapshot);
    }

    public FleetWorkspace Fleet { get; }
    public RemoteSessionInfo Info { get; private set; }
    public SessionHeader Header { get; private set; }
    public SessionId Id => Header.Id;
    public RemoteModelSelection? Selection { get; private set; }
    public IReadOnlyList<SessionEvent> Events => _events;
    public IReadOnlyList<RemoteCommandDescriptor> Commands { get; private set; } = [];
    public event Action? Reset;
    public event Action<IReadOnlyList<SessionEvent>>? Received;
    public event Action<string>? Failed;

    public static async Task<HostChatSession> OpenAsync(FleetWorkspace fleet, string cwd, string? sessionId, HarnessModelChoice model, CancellationToken cancellationToken = default)
    {
        fleet.Start();
        var lease = await fleet.Find(MachineSpec.LocalName)!.WaitConnectedAsync(cancellationToken);
        if (!lease.Client.Supports(SessionEditingProtocol.Capability)
            || !lease.Client.Supports(PluginsProtocol.Capability)
            || !lease.Client.Supports(ModelsProtocol.Capability))
            throw new InvalidOperationException("本机 Host 版本不支持完整 Chat；请升级 dsharp 并重启 Host");
        var info = sessionId is null
            ? await lease.Client.CreateSessionAsync(cwd, model.Selection, model.Provider, cancellationToken)
            : await lease.Client.ResumeSessionAsync(sessionId, model.Selection, model.Provider, cancellationToken);
        var snapshot = await lease.Client.GetSessionSnapshotAsync(info.Id, cancellationToken);
        var chat = new HostChatSession(fleet, info, snapshot, model.Selection);
        chat.Commands = await lease.Client.SessionEditing.ListSessionCommandsAsync(info.Id, cancellationToken);
        return chat;
    }

    public void Follow()
    {
        _subscription?.Cancel();
        _subscription = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var generation = ++_generation;
        _followingMachine = Fleet.Find(MachineSpec.LocalName)!;
        _ = FollowAsync(_followingMachine, Info.Id, generation, _subscription.Token);
    }

    public void RefreshConnection()
    {
        if (!_lifetime.IsCancellationRequested && !ReferenceEquals(_followingMachine, Fleet.Find(MachineSpec.LocalName)))
            Follow();
    }

    private async Task FollowAsync(MachineConnection machine, string id, long generation, CancellationToken cancellationToken)
    {
        var events = new DispatcherBridge<SessionEvent>(batch =>
        {
            if (generation != _generation || cancellationToken.IsCancellationRequested)
                return;
            foreach (var sessionEvent in batch)
                if (sessionEvent.Seq >= _events.Count)
                    _events.Add(sessionEvent);
            Received?.Invoke(batch);
        });
        try
        {
            await foreach (var item in machine.SubscribeAsync(id, _events.Count, cancellationToken))
            {
                if (item.Reset)
                {
                    await ReloadAsync(cancellationToken);
                    continue;
                }
                if (DshJson.Deserialize<SessionEvent>(item.EventJson) is not { } sessionEvent)
                    continue;
                events.Enqueue(sessionEvent);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            Dispatcher.UIThread.Post(() => { if (generation == _generation && !_lifetime.IsCancellationRequested) Failed?.Invoke(error.Message); });
        }
    }

    public async Task<IRemoteHost> ClientAsync(CancellationToken cancellationToken = default)
        => (await Fleet.Find(MachineSpec.LocalName)!.WaitConnectedAsync(cancellationToken)).Client;

    public async Task SwitchAsync(string id, HarnessModelChoice model, CancellationToken cancellationToken = default)
    {
        var navigation = ++_navigationGeneration;
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        var info = await client.ResumeSessionAsync(id, model.Selection, model.Provider, cancellationToken).ConfigureAwait(false);
        var snapshotTask = client.GetSessionSnapshotAsync(id, cancellationToken);
        var commandsTask = client.SessionEditing.ListSessionCommandsAsync(id, cancellationToken);
        await Task.WhenAll(snapshotTask, commandsTask).ConfigureAwait(false);
        var snapshot = await snapshotTask.ConfigureAwait(false);
        var commands = await commandsTask.ConfigureAwait(false);
        // 反序列化放到 UI 线程外: 长会话的快照有上千个事件, 在 UI 线程上逐条 JSON 解析会直接冻结窗口。
        var header = DshJson.Deserialize<SessionHeader>(snapshot.HeaderJson) ?? throw new InvalidDataException("Host returned no session header");
        var events = DeserializeEvents(snapshot);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (navigation != _navigationGeneration || _lifetime.IsCancellationRequested)
                return;
            _subscription?.Cancel();
            ++_generation;
            Info = info;
            Selection = model.Selection;
            Header = header;
            _events.Clear();
            _events.AddRange(events);
            Commands = commands;
            Reset?.Invoke();
            Follow();
        });
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var id = Info.Id;
        var generation = _generation;
        var reload = ++_reloadGeneration;
        var snapshot = await (await ClientAsync(cancellationToken).ConfigureAwait(false)).GetSessionSnapshotAsync(id, cancellationToken).ConfigureAwait(false);
        var header = DshJson.Deserialize<SessionHeader>(snapshot.HeaderJson) ?? throw new InvalidDataException("Host returned no session header");
        var replacement = DeserializeEvents(snapshot);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _generation || reload != _reloadGeneration || _lifetime.IsCancellationRequested)
                return;
            var titleChanged = !string.Equals(Header.Title, header.Title, StringComparison.Ordinal);
            Header = header;
            // 命令通常只在尾部追加事件(例如 /model、/reasoning、/preset)。全量 ReplaceEvents + Reset 会清空并重建
            // 整个消息/视觉树, 长会话每次选择都要付一遍 O(历史) 的布局与样式分配。
            if (titleChanged || !TryAppend(replacement, out var appended))
            {
                ReplaceEvents(snapshot);
                Reset?.Invoke();
                return;
            }

            if (appended.Count == 0)
                return;
            _events.AddRange(appended);
            Received?.Invoke(appended);
        });
    }

    /** 快照只是旧事件列表的尾部追加时才返回 true; 截断/重写会话仍需整屏重建。 */
    private bool TryAppend(List<SessionEvent> replacement, out List<SessionEvent> appended)
    {
        appended = [];
        if (replacement.Count < _events.Count)
            return false;
        for (var index = 0; index < _events.Count; index++)
        {
            if (replacement[index].Seq != _events[index].Seq)
                return false;
        }

        appended = replacement.GetRange(_events.Count, replacement.Count - _events.Count);
        return true;
    }

    private static List<SessionEvent> DeserializeEvents(RemoteSessionSnapshot snapshot)
    {
        var events = new List<SessionEvent>(snapshot.Events.Count);
        foreach (var item in snapshot.Events)
            if (DshJson.Deserialize<SessionEvent>(item.EventJson) is { } sessionEvent)
                events.Add(sessionEvent);
        return events;
    }

    private void ReplaceEvents(RemoteSessionSnapshot snapshot)
    {
        _events.Clear();
        foreach (var item in snapshot.Events)
            if (DshJson.Deserialize<SessionEvent>(item.EventJson) is { } sessionEvent)
                _events.Add(sessionEvent);
    }

    public async Task RefreshCommandsAsync()
    {
        var id = Info.Id;
        var commands = await (await ClientAsync(_lifetime.Token)).SessionEditing.ListSessionCommandsAsync(id, _lifetime.Token);
        if (Info.Id == id && !_lifetime.IsCancellationRequested)
            Commands = commands;
    }

    public async Task<RemoteCommandResult> ExecuteAsync(string line)
    {
        var result = await (await ClientAsync(_lifetime.Token)).SessionEditing.ExecuteSessionCommandAsync(Info.Id, line, _lifetime.Token);
        if (result.Known)
        {
            await ReloadAsync(_lifetime.Token);
            await RefreshCommandsAsync();
        }
        return result;
    }

    public async Task<string> RunAsync(string line)
    {
        var result = await ExecuteAsync(line);
        return result.Known ? result.Text ?? "" : $"未知命令: {line}";
    }

    public async Task SendAsync(string text, IReadOnlyList<RemoteImageBlock>? images = null)
    {
        var client = await ClientAsync(_lifetime.Token);
        await client.ResumeSessionAsync(Info.Id, null, null, _lifetime.Token);
        await client.SendMessageAsync(Info.Id, text, images ?? [], _lifetime.Token);
    }

    public void Dispose()
    {
        ++_generation;
        _lifetime.Cancel();
        _subscription?.Cancel();
    }
}
