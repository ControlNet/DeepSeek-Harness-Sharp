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
    public string? ApprovalPolicy { get; private set; }
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
        chat.Commands = await lease.Client.ListSessionCommandsAsync(info.Id, cancellationToken);
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
        var client = await ClientAsync(cancellationToken);
        var info = await client.ResumeSessionAsync(id, model.Selection, model.Provider, cancellationToken);
        var snapshot = await client.GetSessionSnapshotAsync(id, cancellationToken);
        var commands = await client.ListSessionCommandsAsync(id, cancellationToken);
        if (navigation != _navigationGeneration || _lifetime.IsCancellationRequested)
            return;
        _subscription?.Cancel();
        ++_generation;
        Info = info;
        Selection = model.Selection;
        Header = DshJson.Deserialize<SessionHeader>(snapshot.HeaderJson) ?? throw new InvalidDataException("Host returned no session header");
        ReplaceEvents(snapshot);
        Commands = commands;
        Reset?.Invoke();
        Follow();
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var id = Info.Id;
        var generation = _generation;
        var reload = ++_reloadGeneration;
        var snapshot = await (await ClientAsync(cancellationToken)).GetSessionSnapshotAsync(id, cancellationToken);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _generation || reload != _reloadGeneration || _lifetime.IsCancellationRequested)
                return;
            Header = DshJson.Deserialize<SessionHeader>(snapshot.HeaderJson) ?? throw new InvalidDataException("Host returned no session header");
            ReplaceEvents(snapshot);
            Reset?.Invoke();
        });
    }

    private void ReplaceEvents(RemoteSessionSnapshot snapshot)
    {
        ApprovalPolicy = snapshot.ApprovalPolicy;
        _events.Clear();
        foreach (var item in snapshot.Events)
            if (DshJson.Deserialize<SessionEvent>(item.EventJson) is { } sessionEvent)
                _events.Add(sessionEvent);
    }

    public async Task RefreshCommandsAsync()
    {
        var id = Info.Id;
        var commands = await (await ClientAsync(_lifetime.Token)).ListSessionCommandsAsync(id, _lifetime.Token);
        if (Info.Id == id && !_lifetime.IsCancellationRequested)
            Commands = commands;
    }

    public async Task<RemoteCommandResult> ExecuteAsync(string line)
    {
        var result = await (await ClientAsync(_lifetime.Token)).ExecuteSessionCommandAsync(Info.Id, line, _lifetime.Token);
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
