using System.Text.Json;
using Dsh.AgentAdapters;
using Dsh.Fleet;
using Dsh.Pty;
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

    /** A real ACP agent shares approval across browsers and completes after its originating browser tab closes. */
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

        var sessionId = await ApprovalIsSharedAndFirstAnswerWinsAsync(first, second, repository.Root);
        var stopped = await FleetCliProcess.RunAsync(RealHostProcesses.Executable, hub.LocalHome, "session", "stop", $"{LocalMachine}/{sessionId}");
        Assert.True(stopped.ExitCode == 0, stopped.Error);
        await second.WaitUntilAsync("String(document.getElementById('composer-send').disabled)", "true", ct);
        await second.ScreenshotAsync(Path.Combine(Artifacts, "acp-stopped-by-cli.png"), ct);

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
        // Do not let xterm's opening autofocus hide a broken touch-to-focus path.
        Assert.Equal("false", await phone.EvaluateAsync("""
            (() => {
              document.activeElement?.blur();
              return String(document.activeElement === document.querySelector('#terminal-screen .xterm-helper-textarea'));
            })()
            """, Ct));
        // Ctrl+C goes in only once the shell runs the wait, so it cannot reach the shell before the wait starts.
        await TypeInTerminalAsync(phone, TestShell.Sequence(TestShell.EchoAnswer("busy"), TestShell.Wait(120)), tap: true);
        await WaitForTerminalTextAsync(phone, "busy-42", hub.LocalHome);
        await phone.TapAsync(".key-bar .key[aria-label='Ctrl+C']", Ct);
        Assert.Equal("true", await phone.EvaluateAsync(
            "String(document.activeElement === document.querySelector('#terminal-screen .xterm-helper-textarea'))", Ct));
        await TypeInTerminalAsync(phone, TestShell.EchoAnswer("phone"), tap: true);
        await WaitForTerminalTextAsync(phone, "phone-42", hub.LocalHome);
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
        if (phone)
            await RecordTerminalDeliveryAsync(chrome);
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
        await WaitForTerminalTextAsync(chrome, "web-42", project);
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
     * The originating browser allows a deliberately long-running command and closes its tab; the second browser receives the real result from the same Host session.
     */
    private static async Task<string> ApprovalIsSharedAndFirstAnswerWinsAsync(HeadlessChrome first, HeadlessChrome second, string project)
    {
        const string prompt = "Run exactly this shell command once with your bash tool: sleep 12; echo acp-$((6*7)) . Then reply with its output only.";
        var sessionId = await StartSessionThroughFormAsync(first, LocalMachine, AgentKinds.Acp, "opencode acp", project, prompt);
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
        Assert.Equal(NoProblems, await first.ProblemsAsync(Ct));
        Assert.Equal("false", await first.EvaluateAsync("String([...document.querySelectorAll('#transcript .tool-result')].some((result) => result.textContent.includes('acp-42')))", Ct));
        await first.SendAsync("Page.close", "{}", Ct);
        await second.NavigateAsync(new Uri($"{await second.EvaluateAsync("location.origin", Ct)}/#/m/{LocalMachine}/s/{sessionId}"), Ct);
        await second.WaitForAsync("[...document.querySelectorAll('#transcript .tool-result')].some((result) => result.textContent.includes('acp-42'))", Ct, AgentWait);
        await second.ScreenshotAsync(Path.Combine(Artifacts, "acp-approval-gone-in-other-browser.png"), Ct);
        await second.EvaluateAsync("document.querySelectorAll('#transcript .tool').forEach((tool) => tool.open = true)", Ct);
        await second.ScreenshotAsync(Path.Combine(Artifacts, "acp-transcript-after-client-close.png"), Ct);
        return sessionId;
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
        // Creating xterm's DOM is not attachment readiness: consume the first
        // real PTY snapshot before typing, without bypassing user focus events.
        await chrome.WaitUntilAsync("document.getElementById('terminal-screen').getAttribute('aria-busy')", "false", Ct, SessionWait);
        if (tap)
            await chrome.TapAsync("#terminal-screen", Ct);
        else
            await chrome.ClickAsync("#terminal-screen", Ct);
        Assert.Equal("true", await chrome.EvaluateAsync(
            "String(document.activeElement === document.querySelector('#terminal-screen .xterm-helper-textarea'))", Ct));
        await chrome.InsertTextAsync(line, Ct);
        await chrome.PressEnterAsync(Ct);
    }

    /** Observe the real RPC and xterm boundaries without replacing the shell, transport or renderer. Never return their raw text. */
    private static async Task RecordTerminalDeliveryAsync(HeadlessChrome chrome)
    {
        Assert.Equal("installed", await chrome.EvaluateAsync("""
            (async () => {
              const { HostRpc } = await import('/rpc.js');
              const { Terminal } = await import('/vendor/xterm-6.0.0/xterm.mjs');
              const peers = [];
              const terminals = [];
              const byRpc = new WeakMap();
              const byTerminal = new WeakMap();
              const stream = () => ({ bytes: 0, chunks: 0, tail: '', decoder: new TextDecoder() });
              const append = (target, bytes) => {
                target.bytes += bytes.length;
                target.chunks++;
                target.tail = (target.tail + target.decoder.decode(bytes, { stream: true })).slice(-65536);
              };
              const summary = (target, expected) => ({
                bytes: target.bytes, chunks: target.chunks, containsExpected: target.tail.includes(expected)
              });
              const connect = HostRpc.connect;
              HostRpc.connect = async function(url) {
                const peer = { machine: new URL(url).searchParams.get('machine'), state: 'connecting', output: new Map(), requests: {} };
                peers.push(peer);
                try {
                  const rpc = await connect.call(this, url);
                  byRpc.set(rpc, peer);
                  peer.state = 'open';
                  rpc.onClose((event) => { peer.state = 'closed'; peer.closeCode = event.code; });
                  return rpc;
                } catch (error) {
                  peer.state = 'failed';
                  peer.closeCode = error.code;
                  throw error;
                }
              };
              const on = HostRpc.prototype.on;
              HostRpc.prototype.on = function(method, handler) {
                if (method !== 'host.pty.output')
                  return on.call(this, method, handler);
                const peer = byRpc.get(this);
                return on.call(this, method, (output) => {
                  let received = peer.output.get(output.PtyId);
                  if (received === undefined) {
                    received = stream();
                    peer.output.set(output.PtyId, received);
                  }
                  append(received, Uint8Array.from(atob(output.Base64), (character) => character.charCodeAt(0)));
                  received.eof = output.Eof;
                  handler(output);
                });
              };
              const request = HostRpc.prototype.request;
              HostRpc.prototype.request = function(method, params) {
                const peer = byRpc.get(this);
                if (!method.startsWith('host.pty.'))
                  return request.call(this, method, params);
                const count = peer.requests[method] ??= { sent: 0, completed: 0, failed: 0 };
                count.sent++;
                return request.call(this, method, params).then((result) => {
                  count.completed++;
                  return result;
                }, (error) => {
                  count.failed++;
                  throw error;
                });
              };
              const open = Terminal.prototype.open;
              Terminal.prototype.open = function(screen) {
                const terminal = { term: this, screen, output: stream(), parsed: 0, renders: 0, sizes: [] };
                terminals.push(terminal);
                byTerminal.set(this, terminal);
                this.onWriteParsed(() => terminal.parsed++);
                this.onRender(() => terminal.renders++);
                this.onResize(({ cols, rows }) => terminal.sizes.push({ cols, rows }));
                return open.call(this, screen);
              };
              const write = Terminal.prototype.write;
              Terminal.prototype.write = function(bytes, callback) {
                append(byTerminal.get(this).output, typeof bytes === 'string' ? new TextEncoder().encode(bytes) : bytes);
                return write.call(this, bytes, callback);
              };
              window.__terminalDelivery = (expected) => ({
                peers: peers.map((peer) => ({
                  machine: peer.machine, state: peer.state, closeCode: peer.closeCode, requests: peer.requests,
                  output: [...peer.output].map(([ptyId, received]) => ({ ptyId, ...summary(received, expected), eof: received.eof }))
                })),
                terminals: terminals.map(({ term, screen, output, parsed, renders, sizes }) => {
                  const buffer = term.buffer.active;
                  const lines = Array.from({ length: buffer.length }, (_, row) => buffer.getLine(row).translateToString());
                  const rect = screen.getBoundingClientRect();
                  return {
                    ptyId: screen.dataset.ptyId, attachedToDocument: screen.isConnected,
                    output: summary(output, expected), parsed, renders, sizes,
                    cols: term.cols, rows: term.rows, bufferType: buffer.type, bufferLines: buffer.length,
                    bufferContainsExpected: lines.some((line) => line.includes(expected)),
                    nonblankBufferLines: lines.filter((line) => line.trim().length > 0).length,
                    viewportY: buffer.viewportY, baseY: buffer.baseY, cursorX: buffer.cursorX, cursorY: buffer.cursorY,
                    renderedContainsExpected: screen.querySelector('.xterm-rows')?.textContent.includes(expected),
                    renderedRows: screen.querySelector('.xterm-rows')?.children.length,
                    renderPaused: term._core?._renderService?._isPaused,
                    screen: { top: rect.top, left: rect.left, width: rect.width, height: rect.height,
                      display: getComputedStyle(screen).display, visibility: getComputedStyle(screen).visibility },
                    viewport: { width: innerWidth, height: innerHeight, visibility: document.visibilityState }
                  };
                })
              });
              return 'installed';
            })()
            """, Ct));
    }

    /** Compare the rendered terminal with the original daemon when a real command does not return. */
    private static async Task WaitForTerminalTextAsync(HeadlessChrome chrome, string expected, string home)
    {
        try
        {
            await chrome.WaitForAsync($"document.querySelector('.xterm-rows').textContent.includes({JsonSerializer.Serialize(expected)})", Ct, SessionWait);
        }
        catch (XunitException error)
        {
            var browser = await chrome.EvaluateAsync($$"""
                JSON.stringify({
                  terminalCount: document.querySelectorAll('#terminal-screen').length,
                  renderedTerminalCount: document.querySelectorAll('.xterm-rows').length,
                  containsExpected: document.querySelector('.xterm-rows').textContent.includes({{JsonSerializer.Serialize(expected)}}),
                  renderedCharacters: document.querySelector('.xterm-rows').textContent.length,
                  state: (() => {
                    const text = document.getElementById('terminal-state').textContent;
                    return { empty: text.length === 0, attaching: text === 'Attaching…', ended: text === 'The process has ended.',
                      attachError: text.startsWith('Cannot attach:'), inputError: text.startsWith('Input not delivered:') };
                  })(),
                  ptyId: document.getElementById('terminal-screen').dataset.ptyId,
                  delivery: window.__terminalDelivery?.({{JsonSerializer.Serialize(expected)}})
                })
                """, Ct);
            string daemon;
            try
            {
                var ptyId = await chrome.EvaluateAsync("document.getElementById('terminal-screen').dataset.ptyId", Ct);
                var socket = PtyDaemonPaths.SocketPath(home);
                var port = OperatingSystem.IsWindows() ? PtyDaemonPaths.PortFile(home) : null;
                var sessions = await PtyDaemonClient.ListAsync(socket, port, Ct);
                var snapshot = await PtyDaemonClient.SnapshotAsync(ptyId, socket, port, Ct);
                daemon = JsonSerializer.Serialize(new
                {
                    sessions = sessions.Select(session => new { session.Id, session.Pid, session.Status, session.ExitCode, session.Columns, session.Rows }),
                    containsExpected = snapshot.Contains(expected, StringComparison.Ordinal),
                    snapshotBytes = System.Text.Encoding.UTF8.GetByteCount(snapshot),
                });
            }
            catch (Exception diagnosticError)
            {
                daemon = diagnosticError.GetType().Name;
            }
            var message = $"{error.Message}; browser: {browser}; daemon: {daemon}"
                .Replace(home, "<test-home>", StringComparison.OrdinalIgnoreCase)
                .Replace(home.Replace('\\', '/'), "<test-home>", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(Environment.UserName))
                message = message.Replace(Environment.UserName, "<user>", StringComparison.OrdinalIgnoreCase);
            Assert.Fail(message);
        }
    }

    /** A selector as a "some element matches" expression. */
    private static string Query(string selector) => $"document.querySelector({JsonSerializer.Serialize(selector)})";

    /** Long paths and session lists must wrap or truncate, never widen the page. */
    private static async Task AssertNoHorizontalScrollAsync(HeadlessChrome chrome)
        => Assert.Equal("true", await chrome.EvaluateAsync("String(document.documentElement.scrollWidth <= window.innerWidth)", Ct));
}
