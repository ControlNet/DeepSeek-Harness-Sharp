using System.Text.Json;
using Dsh.AgentAdapters;
using Dsh.Fleet;
using Xunit.Sdk;

namespace Dsh.Tests;

/**
 * The web UI in real headless Chrome against the hub bridge of two real `dsharp host serve` processes (plus an offline SSH machine).
 * Sessions are real: terminal sessions run the machine's shell (TestShell); the ACP approval test runs the installed `opencode acp` with its configured model (LlmSmoke).
 * Screenshots land in artifacts020/webui; every page must finish without CSP violations or uncaught errors.
 */
[Collection(SerialProcessCollection.Name)]
public sealed class WebUiBrowserTests
{
    private const string LocalMachine = MachineSpec.LocalName;
    private const string SecondMachine = WebBridgeHubTests.SecondMachine;
    private const string PhoneWidth = "390";
    private const int PhoneWidthPixels = 390;
    private const int PhoneHeightPixels = 844;
    private const string NoProblems = "[]";

    private static readonly TimeSpan SessionWait = TimeSpan.FromSeconds(60);

    /** A real model plans, asks for permission and runs the command; a turn can take minutes. */
    private static readonly TimeSpan AgentWait = TimeSpan.FromMinutes(4);

    /** Project-level OpenCode configuration: shell commands ask for permission instead of running. */
    private const string AskForShellConfig = """{"$schema":"https://opencode.ai/config.json","permission":{"bash":"ask"}}""";

    private const string ShortShellTitle = "short shell";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Artifacts => HeadlessChrome.ArtifactDirectory("webui");

    /**
     * Two browsers (separate profiles, so separate logins) on the same bridge: the dashboard shows both machines, terminal sessions started from the UI run real commands,
     * a terminal that exits leaves a notification whose read state follows from one browser to the other, and files and Git show the real repository.
     */
    [Fact(Timeout = 300000)]
    public async Task Desktop_TwoBrowsersShareSessionsNotificationsFilesAndGit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var hub = await WebBridgeHubTests.HubFixture.StartAsync();
        using var repository = new GitTestRepository();
        repository.Commit(("README.md", "hello web\n"));
        repository.Write("README.md", "hello web\nchanged line\n");
        await using var first = await LoggedInBrowserAsync(hub);
        await using var second = await LoggedInBrowserAsync(hub);

        await DashboardShowsEveryMachineAsync(first);
        await TerminalRunsARealCommandAsync(first, hub.SecondHome);
        var exited = await TerminalExitsAsync(first, repository.Root);
        await DashboardFiltersAsync(first, exited);
        await NotificationReadStateIsSharedAsync(first, second, exited);
        await FilesAndGitAsync(first, repository.Root);

        Assert.Equal(NoProblems, await first.ProblemsAsync(ct));
        Assert.Equal(NoProblems, await second.ProblemsAsync(ct));
    }

    /** A real ACP agent (the installed `opencode acp`) asks to run a shell command: the approval reaches both browsers and the first answer wins. */
    [Fact(Timeout = 600000)]
    [Trait("Category", "LlmSmoke")]
    public async Task Desktop_RealAcpAgentApprovalIsSharedAcrossBrowsers()
    {
        var ct = TestContext.Current.CancellationToken;
        if (AgentExecutableLocator.Resolve("opencode") is null)
            Assert.Skip("opencode is not installed on this machine");
        await using var hub = await WebBridgeHubTests.HubFixture.StartAsync();
        using var repository = new GitTestRepository();
        repository.Write("opencode.json", AskForShellConfig);
        await using var first = await LoggedInBrowserAsync(hub);
        await using var second = await LoggedInBrowserAsync(hub);

        await ApprovalIsSharedAndFirstAnswerWinsAsync(first, second, repository.Root);

        Assert.Equal(NoProblems, await first.ProblemsAsync(ct));
        Assert.Equal(NoProblems, await second.ProblemsAsync(ct));
    }

    /** A 390x844 touch phone: no horizontal scrolling, a collapsible menu, and a terminal whose key bar sends Ctrl+C to a real shell. */
    [Fact(Timeout = 300000)]
    public async Task Phone_LayoutFitsAndTerminalKeyBarWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var hub = await WebBridgeHubTests.HubFixture.StartAsync();
        await using var phone = await LoggedInBrowserAsync(hub, phone: true);
        await phone.WaitForAsync($"document.querySelectorAll('section.machine').length === 3", Ct);

        Assert.Equal(PhoneWidth, await phone.EvaluateAsync("String(window.innerWidth)", Ct));
        await AssertNoHorizontalScrollAsync(phone);
        await phone.WaitForAsync("document.querySelector(\"section.machine[data-machine='lab'] .error\")", Ct);
        var pageText = await phone.EvaluateAsync("document.body.textContent", Ct);
        Assert.All(WebBridgeHubTests.SecretValues, secret => Assert.DoesNotContain(secret, pageText, StringComparison.Ordinal));
        Assert.Equal("none", await phone.EvaluateAsync("getComputedStyle(document.getElementById('nav')).display", Ct));
        await phone.ScreenshotAsync(Path.Combine(Artifacts, "phone-dashboard.png"), Ct);
        await phone.TapAsync("#nav-toggle", Ct);
        await phone.WaitUntilAsync("document.getElementById('nav-toggle').getAttribute('aria-expanded')", "true", Ct);
        Assert.Equal("true", await phone.EvaluateAsync("String([...document.querySelectorAll('#nav a')].every((item) => item.getBoundingClientRect().height >= 44))", Ct));
        await phone.ScreenshotAsync(Path.Combine(Artifacts, "phone-menu.png"), Ct);

        await StartSessionThroughFormAsync(phone, LocalMachine, AgentKinds.Terminal, TestShell.CommandLine, hub.LocalHome, prompt: null);
        await phone.WaitForAsync("document.querySelector('.xterm-rows')", Ct, SessionWait);
        Assert.Equal("flex", await phone.EvaluateAsync("getComputedStyle(document.querySelector('.key-bar')).display", Ct));
        Assert.Equal("true", await phone.EvaluateAsync("String([...document.querySelectorAll('.key-bar .key')].every((key) => key.getBoundingClientRect().height >= 44))", Ct));
        // Ctrl+C goes in only once the shell runs the wait, so it cannot reach the shell before the wait starts.
        await TypeInTerminalAsync(phone, TestShell.Sequence(TestShell.EchoAnswer("busy"), TestShell.Wait(120)), tap: true);
        await WaitForTerminalTextAsync(phone, "busy-42");
        await phone.TapAsync(".key-bar .key[aria-label='Ctrl+C']", Ct);
        await TypeInTerminalAsync(phone, TestShell.EchoAnswer("phone"), tap: true);
        await WaitForTerminalTextAsync(phone, "phone-42");
        await AssertNoHorizontalScrollAsync(phone);
        await phone.ScreenshotAsync(Path.Combine(Artifacts, "phone-terminal-keybar.png"), Ct);
        await phone.NavigateAsync(new Uri($"{await phone.EvaluateAsync("location.origin", Ct)}/#/"), Ct);
        await phone.WaitForAsync(Query("a.session-row"), Ct);
        await AssertNoHorizontalScrollAsync(phone);
        await phone.ScreenshotAsync(Path.Combine(Artifacts, "phone-dashboard-with-session.png"), Ct);
        Assert.Equal(NoProblems, await phone.ProblemsAsync(ct));
    }

    private static async Task<HeadlessChrome> LoggedInBrowserAsync(WebBridgeHubTests.HubFixture hub, bool phone = false)
    {
        var chrome = await HeadlessChrome.StartAsync(Artifacts, Ct);
        if (phone)
            await chrome.EmulatePhoneAsync(PhoneWidthPixels, PhoneHeightPixels, Ct);
        await chrome.NavigateAsync(hub.Access.Url("/"), Ct);
        await chrome.WaitUntilAsync("document.getElementById('status').dataset.state", "login", Ct);
        await chrome.ClickAsync("#token", Ct);
        await chrome.InsertTextAsync(hub.Access.Token, Ct);
        await chrome.PressEnterAsync(Ct);
        await chrome.WaitUntilAsync("document.getElementById('status').dataset.state", "connected", Ct);
        await chrome.WaitForAsync("document.getElementById('status').textContent.startsWith('2/3 machines connected')", Ct, SessionWait);
        return chrome;
    }

    private static async Task DashboardShowsEveryMachineAsync(HeadlessChrome chrome)
    {
        await chrome.WaitUntilAsync("[...document.querySelectorAll('section.machine')].map((section) => `${section.dataset.machine}:${section.querySelector('.chip').textContent}`).join(',')",
            $"{LocalMachine}:Connected,{SecondMachine}:Connected,{WebBridgeHubTests.OfflineMachine}:Offline", Ct, SessionWait);
        Assert.Equal("1/3", await chrome.EvaluateAsync("document.querySelector('#tile-offline .tile-value').textContent", Ct));
        await chrome.ScreenshotAsync(Path.Combine(Artifacts, "dashboard-machines.png"), Ct);
    }

    /** Starts the shell as a terminal session on the second machine from the New session form and types a command into xterm. */
    private static async Task TerminalRunsARealCommandAsync(HeadlessChrome chrome, string project)
    {
        await StartSessionThroughFormAsync(chrome, SecondMachine, AgentKinds.Terminal, TestShell.CommandLine, project, prompt: null);
        await chrome.WaitForAsync("document.querySelector('.xterm-rows')", Ct, SessionWait);
        await TypeInTerminalAsync(chrome, TestShell.EchoAnswer("web"), tap: false);
        await WaitForTerminalTextAsync(chrome, "web-42");
        await chrome.ScreenshotAsync(Path.Combine(Artifacts, "terminal-session.png"), Ct);
    }

    /**
     * Starts a titled shell in the repository on the bridge's machine and exits it with code 3; the host records the exit as a notification. Returns the session id.
     * The session's project is also what lets the files and Git views reach a repository outside the user's home (the checkout drive on Windows CI).
     */
    private static async Task<string> TerminalExitsAsync(HeadlessChrome chrome, string project)
    {
        var sessionId = await StartSessionThroughFormAsync(chrome, LocalMachine, AgentKinds.Terminal, TestShell.CommandLine, project, prompt: null, ShortShellTitle);
        await chrome.WaitForAsync("document.querySelector('.xterm-rows')", Ct, SessionWait);
        await TypeInTerminalAsync(chrome, "exit 3", tap: false);
        await chrome.NavigateAsync(new Uri($"{await chrome.EvaluateAsync("location.origin", Ct)}/#/"), Ct);
        await chrome.WaitForAsync($"document.querySelector({JsonSerializer.Serialize($"a.session-row[data-session='{sessionId}']")})?.textContent.includes('exit 3')", Ct, SessionWait);
        return sessionId;
    }

    /**
     * Starts the real ACP agent on the bridge's machine with a first prompt; its permission request shows in the session view of one browser and on the pending page of the other.
     * Allowing it in the first removes it from the second, and the command's real output reaches the transcript.
     */
    private static async Task ApprovalIsSharedAndFirstAnswerWinsAsync(HeadlessChrome first, HeadlessChrome second, string project)
    {
        const string prompt = "Run exactly this shell command once with your bash tool: echo acp-$((6*7)) . Then reply with its output only.";
        await StartSessionThroughFormAsync(first, LocalMachine, AgentKinds.Acp, "opencode acp", project, prompt);
        await first.WaitForAsync(Query(".session-pending article.pending[data-kind='approval']"), Ct, AgentWait);
        await second.NavigateAsync(new Uri($"{await second.EvaluateAsync("location.origin", Ct)}/#/pending"), Ct);
        await second.WaitForAsync(Query("#pending-list article.pending[data-kind='approval']"), Ct, SessionWait);
        Assert.Contains("acp-", await second.EvaluateAsync("document.querySelector('#pending-list article.pending').textContent", Ct), StringComparison.Ordinal);
        Assert.Equal("1", await second.EvaluateAsync("document.getElementById('pending-count').textContent", Ct));
        await first.ScreenshotAsync(Path.Combine(Artifacts, "acp-approval-session.png"), Ct);
        await second.ScreenshotAsync(Path.Combine(Artifacts, "acp-approval-pending-page.png"), Ct);

        await first.ClickAsync(".session-pending article.pending button.primary", Ct);
        await second.WaitUntilAsync("String(document.querySelectorAll('#pending-list article.pending').length)", "0", Ct);
        await second.WaitForAsync("document.getElementById('pending-count').hidden", Ct);
        await first.WaitForAsync("[...document.querySelectorAll('#transcript .tool-result')].some((result) => result.textContent.includes('acp-42'))", Ct, AgentWait);
        await second.ScreenshotAsync(Path.Combine(Artifacts, "acp-approval-gone-in-other-browser.png"), Ct);
        await first.EvaluateAsync("document.querySelectorAll('#transcript .tool').forEach((tool) => tool.open = true)", Ct);
        await first.ScreenshotAsync(Path.Combine(Artifacts, "acp-transcript-after-allow.png"), Ct);
    }

    /** The search box narrows the dashboard to the matching session; the dark theme follows the system preference. */
    private static async Task DashboardFiltersAsync(HeadlessChrome chrome, string shortShell)
    {
        await chrome.NavigateAsync(new Uri($"{await chrome.EvaluateAsync("location.origin", Ct)}/#/"), Ct);
        await chrome.WaitForAsync("document.querySelectorAll('a.session-row').length === 2", Ct, SessionWait);
        await chrome.ClickAsync("#session-search", Ct);
        await chrome.InsertTextAsync(ShortShellTitle, Ct);
        await chrome.WaitUntilAsync("[...document.querySelectorAll('a.session-row')].map((row) => row.dataset.session).join(',')", shortShell, Ct);
        await chrome.EvaluateAsync("const search = document.getElementById('session-search'); search.value = ''; search.dispatchEvent(new Event('input'))", Ct);
        await chrome.SetColorSchemeAsync("dark", Ct);
        await chrome.WaitForAsync("document.querySelectorAll('a.session-row').length === 2", Ct);
        await AssertNoHorizontalScrollAsync(chrome);
        await chrome.ScreenshotAsync(Path.Combine(Artifacts, "dashboard-dark.png"), Ct);
        await chrome.SetColorSchemeAsync("light", Ct);
    }

    /**
     * The exited shell left a notification on the bridge's machine.
     * Opening it in the second browser jumps to that session and marks it read in the first; "Mark all read" in the first clears the second's badge.
     */
    private static async Task NotificationReadStateIsSharedAsync(HeadlessChrome first, HeadlessChrome second, string exitedSession)
    {
        foreach (var chrome in new[] { first, second })
        {
            await chrome.NavigateAsync(new Uri($"{await chrome.EvaluateAsync("location.origin", Ct)}/#/notifications"), Ct);
            await chrome.WaitForAsync(Query("#notification-list button.notification.unread"), Ct, SessionWait);
        }
        await second.ScreenshotAsync(Path.Combine(Artifacts, "notifications-unread.png"), Ct);
        var opened = await second.EvaluateAsync("document.querySelector('#notification-list button.notification.unread').dataset.id", Ct);
        await second.ClickAsync($"#notification-list button.notification[data-id='{opened}']", Ct);
        await second.WaitUntilAsync("location.hash", $"#/m/{LocalMachine}/s/{Uri.EscapeDataString(exitedSession)}", Ct);
        await first.WaitUntilAsync($"document.querySelector(\"#notification-list button.notification[data-id='{opened}']\").dataset.read", "true", Ct);

        await second.NavigateAsync(new Uri($"{await second.EvaluateAsync("location.origin", Ct)}/#/notifications"), Ct);
        await first.ClickAsync("#mark-all-read", Ct);
        await second.WaitUntilAsync("String(document.querySelectorAll('#notification-list button.notification.unread').length)", "0", Ct);
        await second.WaitForAsync("document.getElementById('unread-count').hidden", Ct);
        await second.ScreenshotAsync(Path.Combine(Artifacts, "notifications-read-in-other-browser.png"), Ct);
    }

    /** Browses the repository, previews a file, and shows the working-tree diff of the changed file. */
    private static async Task FilesAndGitAsync(HeadlessChrome chrome, string repository)
    {
        var origin = await chrome.EvaluateAsync("location.origin", Ct);
        await chrome.NavigateAsync(new Uri($"{origin}/#/m/{LocalMachine}/files?path={Uri.EscapeDataString(repository)}"), Ct);
        await chrome.WaitForAsync("[...document.querySelectorAll('#file-list .file-row')].some((row) => row.textContent === 'README.md')", Ct);
        await chrome.EvaluateAsync("[...document.querySelectorAll('#file-list button.file-row')].find((row) => row.textContent === 'README.md').click()", Ct);
        await chrome.WaitUntilAsync("document.getElementById('file-text')?.textContent", "hello web\nchanged line\n", Ct);
        await chrome.ScreenshotAsync(Path.Combine(Artifacts, "files-preview.png"), Ct);

        await chrome.NavigateAsync(new Uri($"{origin}/#/m/{LocalMachine}/git?project={Uri.EscapeDataString(repository)}"), Ct);
        await chrome.WaitForAsync("document.querySelector('#git-status button.file-row')", Ct);
        await chrome.ClickAsync("#git-status button.file-row", Ct);
        await chrome.WaitUntilAsync("document.querySelector('#diff-text .diff-line.added')?.textContent", "+changed line\n", Ct);
        await chrome.ScreenshotAsync(Path.Combine(Artifacts, "git-diff.png"), Ct);
    }

    /** Fills in and submits the New session form like a user; returns the new session's id from the session page the form opens. */
    private static async Task<string> StartSessionThroughFormAsync(
        HeadlessChrome chrome, string machine, string agentKind, string command, string project, string? prompt, string? title = null)
    {
        var origin = await chrome.EvaluateAsync("location.origin", Ct);
        await chrome.NavigateAsync(new Uri($"{origin}/#/new?machine={Uri.EscapeDataString(machine)}"), Ct);
        await chrome.WaitForAsync($"document.querySelector('#new-agent option[value={JsonSerializer.Serialize(agentKind)}]')", Ct);
        await chrome.EvaluateAsync($$"""
            (() => {
              const set = (id, value) => {
                const element = document.getElementById(id);
                element.value = value;
                element.dispatchEvent(new Event('change', { bubbles: true }));
              };
              set('new-agent', {{JsonSerializer.Serialize(agentKind)}});
              set('new-command', {{JsonSerializer.Serialize(command)}});
              set('new-project', {{JsonSerializer.Serialize(project)}});
              set('new-prompt', {{JsonSerializer.Serialize(prompt ?? "")}});
              set('new-title', {{JsonSerializer.Serialize(title ?? "")}});
            })()
            """, Ct);
        await chrome.ClickAsync("#new-start", Ct);
        var routePrefix = $"#/m/{machine}/s/";
        await chrome.WaitForAsync($"location.hash.startsWith({JsonSerializer.Serialize(routePrefix)})", Ct, SessionWait);
        return Uri.UnescapeDataString((await chrome.EvaluateAsync("location.hash", Ct))[routePrefix.Length..]);
    }

    private static async Task TypeInTerminalAsync(HeadlessChrome chrome, string line, bool tap)
    {
        if (tap)
            await chrome.TapAsync("#terminal-screen", Ct);
        else
            await chrome.ClickAsync("#terminal-screen", Ct);
        await chrome.InsertTextAsync(line, Ct);
        await chrome.PressEnterAsync(Ct);
    }

    /** A failure shows what the terminal does show. */
    private static async Task WaitForTerminalTextAsync(HeadlessChrome chrome, string expected)
    {
        try
        {
            await chrome.WaitForAsync($"document.querySelector('.xterm-rows').textContent.includes({JsonSerializer.Serialize(expected)})", Ct, SessionWait);
        }
        catch (XunitException error)
        {
            Assert.Fail($"{error.Message}; terminal: {await chrome.EvaluateAsync("document.querySelector('.xterm-rows').textContent", Ct)}");
        }
    }

    /** A selector as a "some element matches" expression. */
    private static string Query(string selector) => $"document.querySelector({JsonSerializer.Serialize(selector)})";

    /** Long paths and session lists must wrap or truncate, never widen the page. */
    private static async Task AssertNoHorizontalScrollAsync(HeadlessChrome chrome)
        => Assert.Equal("true", await chrome.EvaluateAsync("String(document.documentElement.scrollWidth <= window.innerWidth)", Ct));
}
