using Dsh.Boot;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Interaction;
using Dsh.Host;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** GUI 组件测试的最小真实环境: 真实插件组合 + 真实持久化, 不启动 UI。 */
public sealed class GuiTestEnvironment : IAsyncDisposable
{
    private readonly string _directory;
    private bool _ownsDirectory = true;
    private readonly List<HostChatSession> _chats = [];
    private HarnessRemoteHostBackend? _backend;
    private FleetWorkspace? _fleet;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listener;
    private IDisposable? _testProvider;

    private GuiTestEnvironment(string directory, HarnessApp app, AgentLoopAgent agent)
    {
        _directory = directory;
        App = app;
        Agent = agent;
    }

    public HarnessApp App { get; }

    public AgentLoopAgent Agent { get; }

    /**
     * Compose plugins off the shared headless UI thread so their async setup cannot capture its dispatcher.
     * Async callers use CreateAsync; CreateBlocking is retained for synchronous UI setup.
     */
    public static GuiTestEnvironment CreateBlocking()
        => Task.Run(() => CreateAsync()).GetAwaiter().GetResult();

    public static async Task<GuiTestEnvironment> CreateAsync()
        => await CreateAsync(HomeSettings);

    public static async Task<GuiTestEnvironment> CreateAsync(string homeSettings) => await Task.Run(async () =>
    {
        var directory = TempTree.CreateSocketDirectory("g");
        var homePath = directory;
        // 关掉文件日志: 句柄释放时机不受测试控制, 会让临时目录清理偶发失败。
        File.WriteAllText(Path.Combine(homePath, "settings.yaml"), homeSettings);
        var app = await ConfigBoot.Compose(new HarnessOptions(HarnessHome.Resolve(homePath), directory));
        return new GuiTestEnvironment(directory, app, await CreateAgentAsync(app, directory));
    });

    /** 模拟"重启应用": 旧实例关闭, 用同一个 home 重新组合, 并新建一个会话(旧会话不再 live)。 */
    public async Task<GuiTestEnvironment> RestartAsync() => await Task.Run(async () =>
    {
        await StopHostAsync();
        App.Dispose();
        _ownsDirectory = false;
        var homePath = _directory;
        var app = await ConfigBoot.Compose(new HarnessOptions(HarnessHome.Resolve(homePath), _directory));
        return new GuiTestEnvironment(_directory, app, await CreateAgentAsync(app, _directory));
    });

    /** 关掉文件日志:它的句柄释放时机不受测试控制,会让临时目录清理偶发失败。 */
    public const string HomeSettings = """
        logging:
          file: false
        """;

    private static async Task<AgentLoopAgent> CreateAgentAsync(HarnessApp app, string cwd)
    {
        var agents = app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid()}"),
            cwd,
            new AgentOptions("test", "test-model")));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        return agent;
    }

    /** Every GUI uses the production Host backend and Fleet client over the authenticated local endpoint. */
    public async Task<HostChatSession> OpenChatAsync(AgentLoopAgent? selected = null)
        => await OpenChatAsync((selected ?? Agent).Id);

    public async Task<HostChatSession> OpenChatAsync(SessionId sessionId)
    {
        var store = App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        foreach (var session in store.List())
            await store.Flush(session);
        if (_backend is null)
        {
            if (!App.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!.ListProviders().Any(provider => provider.Id == "test"))
            {
                var registration = ProviderRegistrar.RegisterProvider(App.Ctx, new HarnessOptions(App.Home), "test",
                    new ProviderSettings
                    {
                        Type = ProviderTypes.OpenAiCompatible,
                        Options = new ProviderOptions { BaseUrl = "http://127.0.0.1:1/v1", ApiKey = "test" },
                        Models = new() { ["test-model"] = new ProviderModelSettings() },
                    }, isDefault: false);
                _testProvider = registration.Handle ?? throw new InvalidOperationException(registration.Error);
            }
            _backend = await HarnessRemoteHostBackend.CreateAsync(App, HostPtyDaemonForTests.Unused, TestContext.Current.CancellationToken);
            _listenerCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            _listener = Task.Run(() => RemoteHostListener.ServeLoopbackAsync(
                new RemoteHostServer(new RemoteHostServerOptions(null), _backend), App.Home.Root, _listenerCancellation.Token));
            _fleet = FleetWorkspace.ForContext(App.Ctx, App.Home, startLocalHost: null);
        }
        var chat = await HostChatSession.OpenAsync(_fleet!, _directory, sessionId.Value,
            new HarnessModelChoice(new RemoteModelSelection("test", "test-model"), null), TestContext.Current.CancellationToken);
        _chats.Add(chat);
        var machine = _fleet!.Find(MachineSpec.LocalName)!;
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void SessionsChanged(MachineConnection connection)
        {
            if (connection.Sessions.Any(session => session.SessionId == chat.Id.Value))
                registered.TrySetResult();
        }
        machine.SessionsChanged += SessionsChanged;
        try
        {
            SessionsChanged(machine);
            await registered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally { machine.SessionsChanged -= SessionsChanged; }
        return chat;
    }

    public static async Task WaitForAsync(FleetWorkspace source, Func<bool> condition)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed()
        {
            if (condition())
                completion.TrySetResult();
        }
        source.Changed += Changed;
        try
        {
            if (!condition())
                await PumpAsync(completion.Task);
        }
        finally { source.Changed -= Changed; }
    }

    public static async Task WaitForAsync(INotifyPropertyChanged source, Func<bool> condition)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (condition())
                completion.TrySetResult();
        }
        source.PropertyChanged += Changed;
        try
        {
            if (!condition())
                await PumpAsync(completion.Task);
        }
        finally { source.PropertyChanged -= Changed; }
    }

    public static Task WaitForAsync<T>(ObservableCollection<T> source, Func<bool> condition, Func<string>? diagnostics = null)
        => WaitForAsync((INotifyCollectionChanged)source, condition, diagnostics);

    public static async Task WaitForAsync(INotifyCollectionChanged source, Func<bool> condition, Func<string>? diagnostics = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (condition())
                completion.TrySetResult();
        }
        source.CollectionChanged += Changed;
        try
        {
            if (!condition())
                await PumpAsync(completion.Task);
        }
        catch (TimeoutException error) when (diagnostics is not null)
        {
            throw new TimeoutException($"Collection condition was not reached. {diagnostics()}", error);
        }
        finally { source.CollectionChanged -= Changed; }
    }

    public static string DescribeChatState(MainViewModel viewModel)
        => $"Status: {viewModel.StatusText}; busy: {viewModel.IsBusy}; message kinds: [{string.Join(", ", viewModel.Messages.Select(message => message.Kind))}]; system messages: [{string.Join(" | ", viewModel.Messages.Where(message => message.Kind == MessageKind.System).Select(message => message.Text))}]";

    /** HeadlessGui keeps the dispatcher running while the real RPC/event completion is awaited. */
    public static Task PumpAsync(Task task)
        => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    public static async Task<T> PumpAsync<T>(Task<T> task)
    {
        await PumpAsync((Task)task);
        return await task;
    }

    private async Task StopHostAsync()
    {
        foreach (var chat in _chats)
            chat.Dispose();
        _chats.Clear();
        if (_fleet is not null)
            await _fleet.DisposeAsync().ConfigureAwait(false);
        if (_listenerCancellation is not null)
        {
            await _listenerCancellation.CancelAsync().ConfigureAwait(false);
            try { await _listener!.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _listenerCancellation.Dispose();
        }
        if (_backend is not null)
            await _backend.DisposeAsync().ConfigureAwait(false);
        _testProvider?.Dispose();
        _testProvider = null;
        _fleet = null;
        _backend = null;
        _listenerCancellation = null;
        _listener = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopHostAsync();
        App.Dispose();
        if (!_ownsDirectory)
            return;
        TempTree.Delete(_directory);
    }
}
