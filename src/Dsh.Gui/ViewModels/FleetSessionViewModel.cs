using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.Terminal;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Gui.ViewModels;

/**
 * The view of one session on any machine: a header (machine, project, agent, status, pending requests) and tabs.
 * Harness and ACP sessions show their conversation; terminal-mode agents and terminal sessions show their PTY, which other clients may share.
 * Extra tabs run a plain shell in the project directory on the same machine and end with their tab.
 */
public sealed partial class FleetSessionViewModel : ObservableObject, IAsyncDisposable
{
    private const string WindowsPlatform = "windows";
    private const string WindowsShell = "cmd.exe";
    private const string PosixShell = "/bin/sh";

    /** Runs the user's login shell where $SHELL names one, else a POSIX login shell. */
    private static readonly string[] PosixShellArguments = ["-c", "exec \"${SHELL:-/bin/sh}\" -l"];

    private readonly FleetWorkspace _fleet;
    private readonly MachineConnection _machine;
    private readonly NotificationCenterViewModel _notifications;
    private int _shellCount;
    private readonly List<Task> _tabClosures = [];
    private readonly CancellationTokenSource _retirement = new();
    private Task? _disposal;

    public FleetSessionViewModel(FleetWorkspace fleet, FleetSessionActions actions, SessionCardViewModel card, NotificationCenterViewModel notifications)
    {
        _fleet = fleet;
        _notifications = notifications;
        _machine = fleet.Find(card.Machine) ?? throw new InvalidOperationException($"no machine named \"{card.Machine}\"");
        Card = card;
        Tabs.Add(CreateSessionTab(actions));
        SelectTab(Tabs[0]);
        _fleet.Changed += RefreshPending;
        RefreshPending();
    }

    public SessionCardViewModel Card { get; }

    public string Machine => Card.Machine;

    public string SessionId => Card.SessionId;

    public string ProjectLabel => Card.ProjectPath ?? "未指定项目目录";

    public ObservableCollection<SessionTabViewModel> Tabs { get; } = [];

    /** This session's approvals and questions, answerable in the header; the notification center lists the same items. */
    public ObservableCollection<PendingDecisionViewModel> Pending { get; } = [];

    [ObservableProperty]
    private SessionTabViewModel? _selectedTab;

    [ObservableProperty]
    private string _error = "";

    /** Shown in its own window rather than the main window, which has no dashboard to go back to. */
    [ObservableProperty]
    private bool _isInWindow;

    public bool HasPending => Pending.Count > 0;

    public bool HasError => Error.Length > 0;

    public event Action<FleetSessionViewModel>? BackRequested;

    public event Action<SessionCardViewModel>? StopRequested;

    public event Action<SessionCardViewModel>? ResumeRequested;

    public event Action<SessionCardViewModel>? OpenInWindowRequested;

    public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        _fleet.Changed -= RefreshPending;
        try
        {
            await _retirement.CancelAsync();
            if (AddShellCommand.ExecutionTask is { } starting)
                await starting;
        }
        finally
        {
            try
            {
                foreach (var tab in Tabs)
                    _tabClosures.Add(tab.CloseAsync().AsTask());
                await Task.WhenAll(_tabClosures);
                Tabs.Clear();
                _tabClosures.Clear();
            }
            finally
            {
                _retirement.Dispose();
            }
        }
    }

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    private void SelectTab(SessionTabViewModel? tab)
    {
        if (tab is null)
            return;
        foreach (var item in Tabs)
            item.IsSelected = ReferenceEquals(item, tab);
        SelectedTab = tab;
    }

    [RelayCommand]
    private async Task AddShellAsync()
    {
        if (_disposal is not null)
            return;
        var terminal = new TerminalViewModel($"终端 {++_shellCount}");
        try
        {
            var connection = await RemotePtyConnection.StartAsync(_machine, ShellRequest(), _retirement.Token);
            if (_retirement.IsCancellationRequested)
            {
                await connection.DisposeAsync();
                return;
            }
            terminal.AttachRemote(connection, $"{Machine} · {ProjectLabel}");
        }
        catch (OperationCanceledException) when (_retirement.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (error is IOException or JsonRpcException or InvalidOperationException or OperationCanceledException)
        {
            Error = $"终端启动失败: {error.Message}";
            return;
        }
        var tab = SessionTabViewModel.ForShell(terminal.Title, terminal);
        Tabs.Add(tab);
        SelectTab(tab);
    }

    [RelayCommand]
    private async Task CloseTabAsync(SessionTabViewModel? tab)
    {
        if (_disposal is not null || tab is not { IsClosable: true } || !Tabs.Contains(tab))
            return;
        var index = Tabs.IndexOf(tab);
        var closing = tab.CloseAsync().AsTask();
        _tabClosures.RemoveAll(task => task.IsCompletedSuccessfully);
        _tabClosures.Add(closing);
        Tabs.Remove(tab);
        if (tab.IsSelected)
            SelectTab(Tabs[Math.Max(0, index - 1)]);
        await closing;
    }

    [RelayCommand]
    private void Back()
    {
        if (!IsInWindow)
            BackRequested?.Invoke(this);
    }

    [RelayCommand]
    private void Stop() => StopRequested?.Invoke(Card);

    [RelayCommand]
    private void Resume() => ResumeRequested?.Invoke(Card);

    [RelayCommand]
    private void OpenInWindow() => OpenInWindowRequested?.Invoke(Card);

    private SessionTabViewModel CreateSessionTab(FleetSessionActions actions)
    {
        if (Card.Kind is HostSessionKinds.Harness or HostSessionKinds.Acp)
            return SessionTabViewModel.ForTranscript(new SessionTranscriptViewModel(_machine, actions, Card));
        var terminal = new TerminalViewModel(Card.Title);
        terminal.AttachRemote(RemotePtyConnection.Attach(_machine, SessionId), $"{Machine} · {Card.AgentLabel}");
        return SessionTabViewModel.ForSessionTerminal(terminal);
    }

    private RemotePtyStartRequest ShellRequest()
    {
        var windows = _machine.Info?.Platform == WindowsPlatform;
        return new RemotePtyStartRequest(
            windows ? WindowsShell : PosixShell,
            windows ? [] : PosixShellArguments,
            Cwd: Card.ProjectPath,
            Title: $"{Card.Title} · 终端");
    }

    private void RefreshPending()
    {
        CollectionSync.Sync(Pending, _notifications.PendingOf(Machine, SessionId));
        OnPropertyChanged(nameof(HasPending));
    }
}
