# WebBridge（浏览器到 host 的 JSON-RPC 桥）

## 使用方式
- 启动：`dsharp host web [--port N]`，默认端口 `WebBridgeEndpoint.DefaultPort`（8780），`--port 0` 取随机端口。
- 本机 host 守护进程不可达时，入口会先以脱离会话的方式起 `dsharp host serve`（与 `--stdio` 分支同一个 `StartHostDaemon`）。
- 只监听 `127.0.0.1`，没有对外监听的选项；跨设备用 `ssh -N -L 8780:127.0.0.1:8780 user@host`，然后在本地浏览器打开 `http://127.0.0.1:8780/`。
- 令牌：每次启动生成新令牌，写在 `<harness home>/run/web.token`（0600，`LocalEndpointToken`），端口写在 `run/web.port`；正常退出（Ctrl+C）时两个文件都会删除。
- `dsharp host web --print-token`：第一行只输出令牌（便于脚本），第二行输出一次性登录链接 `http://127.0.0.1:<port>/#token=<令牌>`；桥没在运行时返回 1。
- 反向代理：桥只接受 Host 为 `127.0.0.1`/`localhost`/`[::1]`（端口任意，兼容 ssh -L 改端口）且 Origin 与 Host 同源的请求，所以代理必须把 `Host` 改写为回环地址，并把 `Origin` 改写为 `http://<同一个 Host>`（nginx：`proxy_set_header Host 127.0.0.1:8780; proxy_set_header Origin http://127.0.0.1:8780;`，并转发 `Upgrade`/`Connection` 头）。

## 架构（src/Dsh.WebBridge，只引用 Dsh.Transport 与 Dsh.RemoteHost）
- `WebBridgeServer`：TcpListener + 手写 HTTP/1.1（每连接一个请求，`Connection: close`）+ `WebSocket.CreateFromStream`；不依赖 ASP.NET Core 共享框架，也没有 HttpListener 在 Windows 上的 URL ACL 问题，裁剪/AOT 安全。
- 路由：`GET /ws`（WebSocket）、`POST /api/login`、`GET /api/session`（204 已登录 / 401 未登录）、其余 `GET` 走嵌入的静态资源。
- `HostRelay`：每个 WebSocket 新建一条 host.sock 连接（`RemoteHostListener.ConnectAsync` 代为出示 host 令牌），一条文本消息对应一帧 `FrameCodec` 帧，反向同理；不解析 JSON-RPC，host 增加方法不需要改桥。
- 任何一侧结束都会结束另一侧：浏览器关闭即释放 host 连接；host 断开时桥以 1001 关闭 WebSocket；host 不可达时以 1011 + "host daemon not reachable" 关闭；二进制消息以 1003 关闭；超过 `FrameCodec.DefaultMaxFrameBytes` 以 1009 关闭。
- `WebAccessGate`：令牌常量时间比较后换成随机会话 id 的 cookie（`dsh_web_session`，`HttpOnly; SameSite=Strict; Path=/`，令牌本身不进 cookie），会话只在内存中，桥重启需要重新登录；最多保留 32 个会话。
- 校验顺序：所有请求先校验 Host（防 DNS 重绑定）；登录和 WebSocket 升级再校验 Origin 同源（防跨站），升级还要求会话 cookie。
- 所有响应带 `X-Content-Type-Options: nosniff`、`Referrer-Policy: no-referrer`、`X-Frame-Options: DENY` 和 `Content-Security-Policy: default-src 'self'; connect-src 'self'; ...`，所以页面里不能有内联脚本和内联样式。

## 前端约定（src/Dsh.WebBridge/wwwroot）
- 纯 HTML/CSS/JS（ES module），没有 Node 构建链；csproj 用 `<EmbeddedResource Include="wwwroot\**\*" LogicalName="wwwroot/%(RecursiveDir)%(Filename)%(Extension)" />` 打进程序集，新增文件放进 wwwroot 即被服务，子目录按相对路径访问。
- 内容类型按扩展名映射（`StaticAssets.ContentTypes`），新增文件类型要在那里登记，否则以 `application/octet-stream` 返回。
- 当前文件：`index.html`（骨架）、`app.css`、`rpc.js`（JSON-RPC 客户端，后续前端应复用）、`app.js`（登录、连接、显示 host.info 的最小页面）。
- 登录：页面读取 `location.hash` 中的 `#token=` 后立刻 `history.replaceState` 清掉，再 `POST /api/login`（`application/x-www-form-urlencoded`，字段 `token`），成功返回 204 并设置 cookie；也可在登录表单里粘贴令牌。
- 连接：`new WebSocket(ws(s)://<location.host>/ws)`，cookie 自动携带；`rpc.js` 的 `HostRpc.connect()` 打开连接，`request(method, params)` 返回 Promise，`on(method, handler)` 订阅通知（`host.event`、`host.approval`、`host.question`、`host.pty.output` 等），`onClose(handler)` 读取关闭码和原因。
- 第一条调用必须是 `host.hello`，参数 `{ ProtocolVersion: 1 }`（与 `HostProtocol.Version` 一致），检查返回的 `Ok` 与 `Mismatch`；守护进程以 `--token` 启动时 hello 还需要 `Token`，否则其他方法返回 -32001。
- host 协议的参数和结果属性名是 PascalCase 且区分大小写（如 `{"SessionId":"…","FromSeq":0}`），信封字段是小写的 `jsonrpc/id/method/params/result/error`。
- 每个 WebSocket 是一条独立的 host 连接，订阅（`host.session.subscribe`）只对这条连接生效，断线重连后需要重新订阅。
- 用 `textContent`/`createElement` 渲染 host 数据，不要拼 `innerHTML`。
- 作者 CSS 里的 `display` 会覆盖 `hidden` 属性，`app.css` 里有 `[hidden] { display: none !important; }`，新样式不要去掉它。

## 测试
- `tests/Dsh.Tests/WebBridgeTests.cs`：进程内 `RemoteHostServer` + `RemoteHostListener`（后端是 `RemoteHostCapabilityTests.FakeBackend`，其 `OpenApprovalStreams` 等于存活的 host 连接数）+ 真实 `ClientWebSocket`/`HttpClient`。
- `ClientWebSocket` 默认不发 Origin，测试需要 `Options.SetRequestHeader("Origin", …)`；`Options.CollectHttpResponseDetails = true` 后可读拒绝时的状态码。
- 无头 Chrome 的 `--dump-dom`/`--screenshot` 配 `--virtual-time-budget` 不会等待 WebSocket 往返，页面会停在 connecting；测试改用 `--remote-debugging-port=0` 走 DevTools 协议轮询状态后再截图，截图在 `artifacts020/web-bridge/host-info.png`。
