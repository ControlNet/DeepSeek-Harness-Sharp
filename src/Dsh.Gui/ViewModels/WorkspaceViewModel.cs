using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Gui.ViewModels;

/** Where the workspace asks the main window to go. */
public enum WorkspaceDestination
{
    Dashboard,
    Session,
    Page,
}

/**
 * Root of the multi-machine workspace: the fleet connection, the dashboard, the notification center, the quick switcher and the session in view.
 * A session is shown by at most one view at a time (the main window or its own window), because a client attaches to a terminal or subscribes to a session once.
 * Contributed pages (files, Git, machine management) open through Pages with the machine and project of the session in view.
 */
public sealed partial class WorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dictionary<(string Machine, string Session), FleetSessionViewModel> _windows = [];
    private readonly Action<string, string, bool> _toast;

    public WorkspaceViewModel(FleetWorkspace fleet, Func<HarnessModelChoice> harnessModel, Action<string, string, bool> toast)
    {
        Fleet = fleet;
        _toast = toast;
        Actions = new FleetSessionActions(fleet, harnessModel);
        Dashboard = new DashboardViewModel(fleet);
        Notifications = new NotificationCenterViewModel(fleet, (machine, session) => Dashboard.Find(machine, session)?.Title) { Toast = toast };
        Palette = new CommandPaletteViewModel(() => Dashboard.AllCards);
        Machines = new MachinesViewModel(fleet.Store, fleet.Options);
        Machines.Load();
        Machines.AttachFleet(fleet.Client);
        Machines.MachinesChanged += () => _ = ReloadMachinesAsync();
        Dashboard.OpenRequested += card => OpenSession(card.Machine, card.SessionId);
        Dashboard.OpenInWindowRequested += OpenInWindow;
        Dashboard.StopRequested += card => _ = StopAsync(card);
        Dashboard.ResumeRequested += card => _ = ResumeAsync(card);
        Dashboard.NewSessionRequested += NewSession;
        Notifications.OpenSessionRequested += OpenSession;
        Notifications.AnswerRequested += decision => DecisionRequested?.Invoke(decision) ?? Task.FromResult<object?>(null);
        Palette.OpenRequested += card => OpenSession(card.Machine, card.SessionId);
        Fleet.Changed += OnFleetChanged;
    }

    public FleetWorkspace Fleet { get; }

    public FleetSessionActions Actions { get; }

    public DashboardViewModel Dashboard { get; }

    public NotificationCenterViewModel Notifications { get; }

    public CommandPaletteViewModel Palette { get; }

    public WorkspacePages Pages { get; } = new();

    /** Machine management, shared by its workspace page and the settings page's remote section. */
    public MachinesViewModel Machines { get; }

    [ObservableProperty]
    private FleetSessionViewModel? _activeSession;

    /** Content of the contributed page in view (see WorkspacePageEntry); a disposable content is disposed when the page closes. */
    [ObservableProperty]
    private object? _pageContent;

    partial void OnPageContentChanged(object? oldValue, object? newValue)
    {
        if (oldValue is IDisposable disposable)
            disposable.Dispose();
    }

    [ObservableProperty]
    private WorkspacePageEntry? _activePage;

    public event Action<WorkspaceDestination>? NavigationRequested;

    /** The view opens a window for the session and calls SessionWindowClosed when it closes. */
    public event Action<FleetSessionViewModel>? SessionWindowRequested;

    /** The session is already shown in its own window; the view brings that window to the front. */
    public event Action<FleetSessionViewModel>? SessionWindowFocusRequested;

    /** The machine list changed under the session's window; the view closes it and calls SessionWindowClosedAsync. */
    public event Action<FleetSessionViewModel>? SessionWindowCloseRequested;

    /** The view shows the new-session dialog for the form. */
    public event Action<NewSessionViewModel>? NewSessionDialogRequested;

    /** Questions are answered in the decision dialog that the main window shows. */
    public event Func<DecisionViewModel, Task<object?>>? DecisionRequested;

    /** The main chat shell can handle local harness cards with its full composer and trace UI. */
    public event Action<string>? LocalChatRequested;

    public void Start() => Fleet.Start();

    /** Shows a session in the main window, unless its own window already shows it. */
    public void OpenSession(string machine, string sessionId)
    {
        if (_windows.TryGetValue((machine, sessionId), out var windowed))
        {
            SessionWindowFocusRequested?.Invoke(windowed);
            return;
        }
        if (ActiveSession is { } active && active.Machine == machine && active.SessionId == sessionId)
        {
            NavigationRequested?.Invoke(WorkspaceDestination.Session);
            return;
        }
        if (Dashboard.Find(machine, sessionId) is not { } card)
        {
            _toast("会话不存在", $"{machine} 上没有会话 {sessionId}", true);
            return;
        }
        ShowInMain(card);
    }

    /** Moves the session into its own window; when the main window shows it, the same view moves over without attaching again. */
    public void OpenInWindow(SessionCardViewModel card)
    {
        var key = (card.Machine, card.SessionId);
        if (_windows.TryGetValue(key, out var existing))
        {
            SessionWindowFocusRequested?.Invoke(existing);
            return;
        }
        var session = ActiveSession is { } active && active.Machine == card.Machine && active.SessionId == card.SessionId ? active : CreateSession(card);
        if (ReferenceEquals(session, ActiveSession))
        {
            ActiveSession = null;
            NavigationRequested?.Invoke(WorkspaceDestination.Dashboard);
        }
        session.IsInWindow = true;
        _windows[key] = session;
        SessionWindowRequested?.Invoke(session);
    }

    public async Task SessionWindowClosedAsync(FleetSessionViewModel session)
    {
        _windows.Remove((session.Machine, session.SessionId));
        await session.DisposeAsync();
    }

    /** Back to the dashboard; the session view is closed, the dashboard keeps its filter, search and scroll position. */
    [RelayCommand]
    public void ShowDashboard()
    {
        _ = CloseActiveAsync();
        PageContent = null;
        ActivePage = null;
        NavigationRequested?.Invoke(WorkspaceDestination.Dashboard);
    }

    [RelayCommand]
    public void OpenPage(WorkspacePageEntry? entry)
    {
        if (entry is null)
            return;
        var context = new WorkspacePageContext(Fleet, ActiveSession?.Machine, ActiveSession?.Card.ProjectPath);
        PageContent = entry.Create(context);
        ActivePage = entry;
        NavigationRequested?.Invoke(WorkspaceDestination.Page);
    }

    /**
     * The pages this module contributes: files and Git on the machine and project of the session in view (this computer's home directory from the dashboard), and machine management.
     * Files and Git need the host capabilities fs.transfer and git.inspect; a new session asked for from a Git worktree opens the new-session form in that worktree.
     */
    public void RegisterHostPages(Geometry filesIcon, Geometry gitIcon, Geometry machinesIcon)
    {
        Pages.Register(new WorkspacePageEntry(WorkspacePages.Files, "文件", filesIcon, context => new HostPageViewModel(
            Fleet, "文件", FilesProtocol.Capability, context.Machine ?? MachineSpec.LocalName, context.ProjectPath ?? RemotePaths.Home,
            async (host, _, path) =>
            {
                var files = new FilesViewModel(host, path);
                await files.LoadAsync();
                return files;
            })));
        Pages.Register(new WorkspacePageEntry(WorkspacePages.Git, "Git", gitIcon, context => new HostPageViewModel(
            Fleet, "Git", GitProtocol.Capability, context.Machine ?? MachineSpec.LocalName, context.ProjectPath ?? "",
            async (host, machine, path) =>
            {
                var git = new GitViewModel(host, path);
                git.NewSessionRequested += worktree => NewSession(machine, worktree.Path);
                await git.RefreshAsync();
                return git;
            })));
        Pages.Register(new WorkspacePageEntry(WorkspacePages.Machines, "机器", machinesIcon, _ => Machines));
    }

    /** Opens the new-session form, preset to a machine and project when started from one. */
    public void NewSession(string? machine, string? projectPath)
    {
        var form = new NewSessionViewModel(Fleet, Actions, machine, projectPath);
        form.Completed += OnNewSessionCompleted;
        NewSessionDialogRequested?.Invoke(form);
    }

    /**
     * Reconnects to the saved machine list; session views hold the previous connections, so they are closed first.
     * A contributed page in view stays: host pages follow their machine's new connection, and machine management shows the new client's states.
     */
    public async Task ReloadMachinesAsync()
    {
        if (ActivePage is not null)
            await CloseActiveAsync();
        else if (ActiveSession is not null)
            ShowDashboard();
        foreach (var session in _windows.Values.ToList())
            SessionWindowCloseRequested?.Invoke(session);
        await Fleet.ReloadAsync();
        Machines.AttachFleet(Fleet.Client);
    }

    public async ValueTask DisposeAsync()
    {
        Fleet.Changed -= OnFleetChanged;
        PageContent = null;
        Machines.AttachFleet(null);
        await CloseActiveAsync();
        foreach (var session in _windows.Values.ToList())
            await session.DisposeAsync();
        _windows.Clear();
        Notifications.Dispose();
        Dashboard.Dispose();
        await Fleet.DisposeAsync();
    }

    private void ShowInMain(SessionCardViewModel card)
    {
        _ = CloseActiveAsync();
        if (card.Machine == MachineSpec.LocalName && card.Kind == HostSessionKinds.Harness && LocalChatRequested is { } openChat)
        {
            PageContent = null;
            ActivePage = null;
            openChat(card.SessionId);
            return;
        }
        ActiveSession = CreateSession(card);
        PageContent = null;
        ActivePage = null;
        NavigationRequested?.Invoke(WorkspaceDestination.Session);
    }

    private FleetSessionViewModel CreateSession(SessionCardViewModel card)
    {
        var session = new FleetSessionViewModel(Fleet, Actions, card, Notifications);
        session.BackRequested += _ => ShowDashboard();
        session.StopRequested += item => _ = StopAsync(item);
        session.ResumeRequested += item => _ = ResumeAsync(item);
        session.OpenInWindowRequested += OpenInWindow;
        return session;
    }

    private async Task CloseActiveAsync()
    {
        if (ActiveSession is not { } active)
            return;
        ActiveSession = null;
        await active.DisposeAsync();
    }

    private async Task StopAsync(SessionCardViewModel card)
    {
        try
        {
            await Actions.StopAsync(card.Machine, card.SessionId, CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or JsonRpcException or InvalidOperationException or OperationCanceledException)
        {
            _toast($"停止失败 · {card.Title}", error.Message, true);
        }
    }

    /** A terminal-mode agent comes back under a new session id; the view follows it when it showed the old one. */
    private async Task ResumeAsync(SessionCardViewModel card)
    {
        try
        {
            var resumed = await Actions.ResumeAsync(card.Machine, card.Session, CancellationToken.None);
            if (resumed.SessionId != card.SessionId && ActiveSession?.SessionId == card.SessionId)
                await OpenWhenListedAsync(card.Machine, resumed);
        }
        catch (Exception error) when (error is IOException or JsonRpcException or InvalidOperationException or OperationCanceledException)
        {
            _toast($"恢复失败 · {card.Title}", error.Message, true);
        }
    }

    private void OnNewSessionCompleted(NewSessionViewModel form)
    {
        form.Completed -= OnNewSessionCompleted;
        if (form.Machine is { } machine && form.Started is { } started)
            _ = OpenWhenListedAsync(machine, started);
    }

    /** The actions already recorded the session on its machine; refreshing now gives it a card before the coalesced update arrives. */
    private async Task OpenWhenListedAsync(string machine, RemoteHostSession session)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Dashboard.Refresh();
            OpenSession(machine, session.SessionId);
        });
    }

    private void OnFleetChanged()
    {
        if (Palette.IsOpen)
            Palette.Search();
    }
}
