param(
    [Parameter(Mandatory = $true)][string]$WebPublishDir,
    [Parameter(Mandatory = $true)][string]$TuiPublishDir,
    [Parameter(Mandatory = $true)][string]$GuiPublishDir
)
$ErrorActionPreference = 'Stop'

# Inspect published dependency graphs and actual files, not project-source wiring.
foreach ($directory in @($TuiPublishDir, $GuiPublishDir)) {
    $directory = (Resolve-Path $directory).Path
    $forbidden = @(Get-ChildItem $directory -File -Filter 'Microsoft.AspNetCore*.dll') + @(Get-ChildItem $directory -File -Filter 'Dsh.WebBridge*.dll')
    if ($forbidden.Count -ne 0) { throw "Non-web publish contains web assemblies: $($forbidden.Name -join ', ')" }
    foreach ($graph in Get-ChildItem $directory -File | Where-Object { $_.Name.EndsWith('.runtimeconfig.json') -or $_.Name.EndsWith('.deps.json') }) {
        if ((Get-Content $graph.FullName -Raw) -match 'Microsoft\.AspNetCore|Dsh\.WebBridge') { throw "Non-web dependency graph contains WebHost dependencies: $($graph.Name)" }
    }
}
foreach ($directory in @($TuiPublishDir, $GuiPublishDir, $WebPublishDir)) {
    $bytes = (Get-ChildItem $directory -Recurse -File | Measure-Object -Property Length -Sum).Sum
    Write-Host "$directory : $bytes bytes"
}

$webDirectory = (Resolve-Path $WebPublishDir).Path
$exe = Join-Path $webDirectory $(if ($IsWindows) { 'dsharp-web.exe' } else { 'dsharp-web' })
# Keep Unix-domain socket paths short; every process and credential belongs to this smoke run.
$homeDirectory = Join-Path ([IO.Path]::GetTempPath()) ('dshw-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $homeDirectory | Out-Null
$oldHome = $env:DSH_HOME
$env:DSH_HOME = $homeDirectory
$hostProcess = $null
$webProcess = $null
$client = $null
$socket = $null
$deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(60))
try {
    $hostProcess = Start-Process $exe -ArgumentList @('host', 'serve') -PassThru -RedirectStandardOutput (Join-Path $homeDirectory 'host.out') -RedirectStandardError (Join-Path $homeDirectory 'host.err')
    $hostEndpoint = Join-Path $homeDirectory $(if ($IsWindows) { 'run/host.port' } else { 'run/host.sock' })
    while (-not (Test-Path $hostEndpoint)) {
        $deadline.Token.ThrowIfCancellationRequested()
        if ($hostProcess.HasExited) { throw 'Published WebHost could not serve the shared Host command' }
        Start-Sleep -Milliseconds 50
    }
    $webProcess = Start-Process $exe -ArgumentList @('--port', '0') -PassThru -RedirectStandardOutput (Join-Path $homeDirectory 'web.out') -RedirectStandardError (Join-Path $homeDirectory 'web.err')
    $portFile = Join-Path $homeDirectory 'run/web.port'
    while (-not (Test-Path $portFile)) {
        $deadline.Token.ThrowIfCancellationRequested()
        if ($webProcess.HasExited) { throw 'Published WebHost exited before announcing its endpoint' }
        Start-Sleep -Milliseconds 50
    }
    $port = (Get-Content $portFile -Raw).Trim()
    $origin = "http://127.0.0.1:$port"
    $token = (Get-Content (Join-Path $homeDirectory 'run/web.token') -Raw).Trim()
    $cookies = [Net.CookieContainer]::new()
    $handler = [Net.Http.SocketsHttpHandler]::new()
    $handler.CookieContainer = $cookies
    $client = [Net.Http.HttpClient]::new($handler)
    $unauthenticated = $client.GetAsync("$origin/api/session", $deadline.Token).GetAwaiter().GetResult()
    if ([int]$unauthenticated.StatusCode -ne 401) { throw 'Anonymous session endpoint did not refuse access' }
    $unauthenticated.Dispose()
    $foreign = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "$origin/api/login")
    $foreign.Headers.Add('Origin', 'http://foreign.invalid')
    $foreign.Content = [Net.Http.StringContent]::new("token=$token", [Text.Encoding]::UTF8, 'application/x-www-form-urlencoded')
    $refused = $client.SendAsync($foreign, $deadline.Token).GetAwaiter().GetResult()
    if ([int]$refused.StatusCode -ne 403) { throw 'Cross-origin login was not refused' }
    $refused.Dispose(); $foreign.Dispose()
    $login = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, "$origin/api/login")
    $login.Headers.Add('Origin', $origin)
    $login.Content = [Net.Http.StringContent]::new("token=$token", [Text.Encoding]::UTF8, 'application/x-www-form-urlencoded')
    $accepted = $client.SendAsync($login, $deadline.Token).GetAwaiter().GetResult()
    if ([int]$accepted.StatusCode -ne 204) { throw 'Token login failed in published WebHost' }
    $cookie = $accepted.Headers.GetValues('Set-Cookie') -join ';'
    if ($cookie -notmatch '(?i)httponly' -or $cookie -notmatch '(?i)samesite=strict' -or $cookie.Contains($token)) { throw 'Published cookie boundary is invalid' }
    $accepted.Dispose(); $login.Dispose()
    $page = $client.GetAsync("$origin/", $deadline.Token).GetAwaiter().GetResult()
    if ([int]$page.StatusCode -ne 200 -or -not $page.Content.ReadAsStringAsync($deadline.Token).GetAwaiter().GetResult().Contains('app.js')) { throw 'Published embedded front end is missing' }
    if (($page.Headers.GetValues('Content-Security-Policy') -join ';') -notmatch "frame-ancestors 'none'") { throw 'Published CSP is missing' }
    $page.Dispose()
    $socket = [Net.WebSockets.ClientWebSocket]::new()
    $socket.Options.Cookies = $cookies
    $socket.Options.SetRequestHeader('Origin', $origin)
    $socket.ConnectAsync([Uri]"ws://127.0.0.1:$port/ws", $deadline.Token).GetAwaiter().GetResult()
    $request = [Text.Encoding]::UTF8.GetBytes('{"jsonrpc":"2.0","id":1,"method":"host.info"}')
    $sent = $false
    do {
        $message = [IO.MemoryStream]::new()
        try {
            $buffer = [byte[]]::new(65536)
            do {
                $received = $socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), $deadline.Token).GetAwaiter().GetResult()
                if ($received.MessageType -ne [Net.WebSockets.WebSocketMessageType]::Text) { throw 'Published WebSocket closed before Host response' }
                $message.Write($buffer, 0, $received.Count)
            } while (-not $received.EndOfMessage)
            $reply = [Text.Encoding]::UTF8.GetString($message.ToArray()) | ConvertFrom-Json
        } finally { $message.Dispose() }
        if (-not $sent -and $reply.method -eq 'bridge.view' -and $reply.params.State -eq 'Connected') {
            $socket.SendAsync([ArraySegment[byte]]::new($request), [Net.WebSockets.WebSocketMessageType]::Text, $true, $deadline.Token).GetAwaiter().GetResult()
            $sent = $true
        }
    } while ($reply.id -ne 1)
    if ($null -ne $reply.error -or [string]::IsNullOrWhiteSpace($reply.result.HostVersion)) { throw 'Published Fleet forwarding returned no Host info' }
    $socket.CloseAsync([Net.WebSockets.WebSocketCloseStatus]::NormalClosure, '', $deadline.Token).GetAwaiter().GetResult()
    Write-Host 'Published WebHost: shared Host command, HTTP authentication/CSRF/CSP/assets and WebSocket Fleet forwarding passed.'
} finally {
    if ($socket) { $socket.Dispose() }
    if ($client) { $client.Dispose() }
    foreach ($process in @($webProcess, $hostProcess)) {
        if ($process -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        if ($process) { $process.Dispose() }
    }
    $deadline.Dispose()
    $env:DSH_HOME = $oldHome
    foreach ($name in @('web.err', 'host.err')) {
        $log = Join-Path $homeDirectory $name
        if ((Test-Path $log) -and (Get-Item $log).Length -gt 0) { Get-Content $log | Write-Host }
    }
    Remove-Item $homeDirectory -Recurse -Force
}
