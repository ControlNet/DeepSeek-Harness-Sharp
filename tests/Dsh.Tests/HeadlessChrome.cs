using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Dsh.AgentAdapters;

namespace Dsh.Tests;

/**
 * The machine's Google Chrome (or Chromium), headless with a throwaway profile inside artifacts020, driven over the DevTools protocol; skips the test when neither is installed.
 * Every instance has its own profile, so two instances are two independent browsers with their own cookies.
 * Pages record Content-Security-Policy violations and uncaught errors from the first script on, so tests can assert that the UI runs under the bridge's CSP.
 */
internal sealed class HeadlessChrome : IAsyncDisposable
{
    private const string ListeningPrefix = "DevTools listening on ";

    /** Installed before any page script runs; DevTools-injected scripts are exempt from the page's CSP. */
    private const string ProblemRecorder = """
        window.__problems = [];
        document.addEventListener('securitypolicyviolation', (event) => window.__problems.push(`csp ${event.violatedDirective} ${event.blockedURI}`));
        window.addEventListener('error', (event) => window.__problems.push(`error ${event.message}`));
        window.addEventListener('unhandledrejection', (event) => window.__problems.push(`rejection ${event.reason?.message ?? event.reason}`));
        """;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly Process _process;
    private readonly string _profile;
    private readonly ClientWebSocket _page = new();
    private int _nextId;

    private HeadlessChrome(Process process, string profile)
    {
        _process = process;
        _profile = profile;
    }

    /** The repository's artifacts020/<name> directory, created on demand. */
    public static string ArtifactDirectory(string name)
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020", name));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static async Task<HeadlessChrome> StartAsync(string artifacts, CancellationToken cancellationToken, int width = 1280, int height = 900)
    {
        var profile = Path.Combine(artifacts, $"chrome-profile-{Guid.NewGuid():N}");
        var executable = ChromeCandidates.Select(AgentExecutableLocator.Resolve).FirstOrDefault(path => path is not null);
        if (executable is null)
            Assert.Skip($"Chrome is not installed on this machine (looked for {string.Join(", ", ChromeCandidates)})");
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // The mock keychain and basic password store keep macOS Chrome from waiting on a keychain prompt.
        foreach (var argument in (string[])["--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--use-mock-keychain", "--password-store=basic", $"--user-data-dir={profile}", "--remote-debugging-port=0", $"--window-size={width},{height}", "about:blank"])
            start.ArgumentList.Add(argument);
        var chrome = new HeadlessChrome(Process.Start(start) ?? throw new InvalidOperationException($"{executable} did not start"), profile);
        var browserEndpoint = await chrome.ReadDevToolsEndpointAsync(cancellationToken);
        await chrome.AttachToPageAsync(browserEndpoint, cancellationToken);
        await chrome.SendAsync("Page.enable", "{}", cancellationToken);
        await chrome.SendAsync("Page.addScriptToEvaluateOnNewDocument", $$"""{"source":"{{JsonEncodedText.Encode(ProblemRecorder)}}"}""", cancellationToken);
        return chrome;
    }

    /** Sends one DevTools command to the page and returns its result; pushed DevTools events are skipped. */
    public async Task<JsonElement> SendAsync(string method, string parameters, CancellationToken cancellationToken)
    {
        var id = ++_nextId;
        await _page.SendAsync(Encoding.UTF8.GetBytes($$"""{"id":{{id}},"method":"{{method}}","params":{{parameters}}}"""), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        var reply = await WebSocketRpc.ReceiveUntilAsync(_page, message => message.TryGetProperty("id", out var replyId) && replyId.GetInt32() == id);
        Assert.False(reply.TryGetProperty("error", out var error), error.ToString());
        return reply.GetProperty("result");
    }

    public Task NavigateAsync(Uri url, CancellationToken cancellationToken)
        => SendAsync("Page.navigate", $$"""{"url":"{{JsonEncodedText.Encode(url.ToString())}}"}""", cancellationToken);

    /** Evaluates the expression (awaiting a returned promise); a thrown exception yields "". */
    public async Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken)
    {
        var result = await SendAsync("Runtime.evaluate", $$"""{"expression":"{{JsonEncodedText.Encode(expression)}}","returnByValue":true,"awaitPromise":true}""", cancellationToken);
        return result.GetProperty("result").TryGetProperty("value", out var value) ? value.ToString() : "";
    }

    public async Task WaitUntilAsync(string expression, string expected, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultWait);
        var actual = await EvaluateAsync(expression, cancellationToken);
        while (actual != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken);
            actual = await EvaluateAsync(expression, cancellationToken);
        }

        Assert.True(actual == expected, $"{expression} stayed \"{actual}\"; page status: {await EvaluateAsync("document.getElementById('status')?.textContent", cancellationToken)}; problems: {await ProblemsAsync(cancellationToken)}");
    }

    /** Waits until the expression is truthy. */
    public Task WaitForAsync(string expression, CancellationToken cancellationToken, TimeSpan? timeout = null)
        => WaitUntilAsync($"String(Boolean({expression}))", "true", cancellationToken, timeout);

    /** CSP violations and uncaught errors the page recorded, as a JSON array. */
    public Task<string> ProblemsAsync(CancellationToken cancellationToken)
        => EvaluateAsync("JSON.stringify(window.__problems ?? ['recorder missing'])", cancellationToken);

    /** A real mouse click in the middle of the first element matching the selector, after scrolling it into view. */
    public async Task ClickAsync(string selector, CancellationToken cancellationToken)
    {
        var (x, y) = await CenterOfAsync(selector, cancellationToken);
        foreach (var type in (string[])["mousePressed", "mouseReleased"])
            await SendAsync("Input.dispatchMouseEvent", $$"""{"type":"{{type}}","x":{{x}},"y":{{y}},"button":"left","clickCount":1}""", cancellationToken);
    }

    /** A finger tap in the middle of the first element matching the selector; needs touch emulation. */
    public async Task TapAsync(string selector, CancellationToken cancellationToken)
    {
        var (x, y) = await CenterOfAsync(selector, cancellationToken);
        await SendAsync("Input.dispatchTouchEvent", $$"""{"type":"touchStart","touchPoints":[{"x":{{x}},"y":{{y}}}]}""", cancellationToken);
        await SendAsync("Input.dispatchTouchEvent", """{"type":"touchEnd","touchPoints":[]}""", cancellationToken);
    }

    /** Types text into the focused element as an IME would. */
    public Task InsertTextAsync(string text, CancellationToken cancellationToken)
        => SendAsync("Input.insertText", $$"""{"text":"{{JsonEncodedText.Encode(text)}}"}""", cancellationToken);

    public async Task PressEnterAsync(CancellationToken cancellationToken)
    {
        await SendAsync("Input.dispatchKeyEvent", """{"type":"keyDown","key":"Enter","code":"Enter","windowsVirtualKeyCode":13,"text":"\r"}""", cancellationToken);
        await SendAsync("Input.dispatchKeyEvent", """{"type":"keyUp","key":"Enter","code":"Enter","windowsVirtualKeyCode":13}""", cancellationToken);
    }

    /** A phone: the viewport in CSS pixels at 3x density, mobile layout and touch input. */
    public async Task EmulatePhoneAsync(int width, int height, CancellationToken cancellationToken)
    {
        await SendAsync("Emulation.setDeviceMetricsOverride", $$"""{"width":{{width}},"height":{{height}},"deviceScaleFactor":3,"mobile":true}""", cancellationToken);
        await SendAsync("Emulation.setTouchEmulationEnabled", """{"enabled":true,"maxTouchPoints":5}""", cancellationToken);
    }

    public Task SetColorSchemeAsync(string scheme, CancellationToken cancellationToken)
        => SendAsync("Emulation.setEmulatedMedia", $$"""{"features":[{"name":"prefers-color-scheme","value":"{{scheme}}"}]}""", cancellationToken);

    public async Task ScreenshotAsync(string path, CancellationToken cancellationToken)
    {
        var screenshot = await SendAsync("Page.captureScreenshot", """{"format":"png"}""", cancellationToken);
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(screenshot.GetProperty("data").GetString()!), cancellationToken);
    }

    private async Task<(double X, double Y)> CenterOfAsync(string selector, CancellationToken cancellationToken)
    {
        var box = await EvaluateAsync($$"""
            (() => {
              const element = document.querySelector({{JsonSerializer.Serialize(selector)}});
              if (!element) return '';
              element.scrollIntoView({ block: 'center', inline: 'center' });
              const rect = element.getBoundingClientRect();
              return `${rect.left + rect.width / 2} ${rect.top + rect.height / 2}`;
            })()
            """, cancellationToken);
        Assert.False(box.Length == 0, $"no element matches {selector}");
        var parts = box.Split(' ');
        return (double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    /** Chrome prints the browser's DevTools WebSocket address on stderr once it listens; the rest of both streams is drained so Chrome never blocks on a full pipe. */
    private async Task<Uri> ReadDevToolsEndpointAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StartTimeout);
        var line = await _process.StandardError.ReadLineAsync(timeout.Token);
        while (line is not null && !line.StartsWith(ListeningPrefix, StringComparison.Ordinal))
            line = await _process.StandardError.ReadLineAsync(timeout.Token);
        Assert.NotNull(line);
        _ = _process.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = _process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        return new Uri(line[ListeningPrefix.Length..].Trim());
    }

    private async Task AttachToPageAsync(Uri browserEndpoint, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var targets = JsonDocument.Parse(await http.GetStringAsync(new Uri($"http://{browserEndpoint.Authority}/json/list"), cancellationToken)).RootElement;
        var page = targets.EnumerateArray().First(target => target.GetProperty("type").GetString() == "page");
        await _page.ConnectAsync(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!), cancellationToken);
    }

    /** Command names on PATH (Linux packages), then the macOS application bundle and the Windows per-machine and per-user installs. */
    private static readonly string[] ChromeCandidates =
    [
        "google-chrome",
        "google-chrome-stable",
        "chromium",
        "chromium-browser",
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        .. new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData }
            .Select(Environment.GetFolderPath)
            .Where(root => root.Length > 0)
            .Select(root => Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe")),
    ];

    public async ValueTask DisposeAsync()
    {
        _page.Dispose();
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        _process.Dispose();
        TempTree.Delete(_profile);
    }
}
