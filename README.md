# DeepSeek-Harness-Sharp

一个用 C# 重写的 DeepSeek Harness 实现,初衷是解决原 DeepSeek Harness 在高并发下性能减退和崩溃的问题(TUI 单进程内存占用约 10MB，GUI 单进程内存占用约50-100MB)。

## 安装

从 GitHub Release 下载对应平台的 Release 构建,装到用户目录并注册 `dsharp` 命令:

```bash
# Linux / macOS
curl -fsSL https://raw.githubusercontent.com/forestbat/DeepSeek-Harness-Sharp/master/scripts/install.sh | bash

# Windows (PowerShell)
irm https://raw.githubusercontent.com/forestbat/DeepSeek-Harness-Sharp/master/scripts/install.ps1 | iex
```

安装后新开终端,在任意目录运行 `dsharp` 即可启动 TUI。可用环境变量覆盖: `DSHARP_VERSION`(版本号或 latest)、`DSHARP_HOME`(安装目录)、`DSHARP_BASE_URL`(镜像基址)、`DSHARP_PROXY`(下载代理)。Release 资产命名为 `dsharp-<rid>.tar.gz`(Linux/macOS)与 `dsharp-win-<arch>.zip`(Windows)。

## 运行

1. 配置文件:`~/.dsh/settings.yaml`(Linux)/`%USERPROFILE%\.dsh\settings.yaml`(Windows);首次运行会生成带注释的模板,插件开关与参数写在 `plugins:` 段。
2. 构建与运行:
   ```bash
   dotnet build # 只构建不运行
   dotnet run --project DeepSeek-Harness-Sharp/DeepSeek-Harness-Sharp.csproj  # 执行构建并启动
   DeepSeek-Harness-Sharp/bin/Debug(Release)/net10.0/dsharp 启动构建结果
   ```
   主程序叫做dsharp/dsharp.exe；`DSH_HOME`(或 settings.yaml 的 `storage.root`)可以指定独立的配置/会话目录,便于用不同配置试跑构建产物。

   ```bash
   # 默认入口: 交互式 TUI
   dsharp --gpu # 使用GPU加速渲染，会启动独立窗口
   # 无头单次任务
   dsharp -- headless "跑一遍测试"
   # 从磁盘恢复已退出的对话
   dsharp tui --session <session-id>
   dsharp --session <session-id>  # 默认进入 TUI
   dsharp tui --gpu --session <session-id>  # 在独立 GPU 窗口恢复
   # 后台 PTY 会话管理(守护进程按需自动拉起, 无需手动启动)
   dsharp tui list  # 列出 daemon 管理的 PTY 会话
   dsharp tui attach <pty-id>  # 接回仍在运行的后台会话
   dsharp tui attach <pty-id> --gpu  # 用独立 GPU 窗口接回
   ```
   `session-id` 标识磁盘里的对话, `pty-id` 标识 daemon 管理的进程; `tui attach <session-id>` 也可以从磁盘恢复对话。
   `--session` 可以放在 `tui` 之前, 如 `dsharp --session <session-id> tui`。
   恢复会显示原有历史并继续使用原会话; ID 不存在或日志损坏时报告错误并以非零状态退出, 不新建替代会话。

   TUI 的 GPU 渲染后端由 `plugins."@deepseek-ai/dsh-tui"` 的 `gpuBackend` 配置项决定(`auto|vk|gl|cpu`,缺省 `auto`，优先以 `vk` 后端启动)，`vsync` 即垂直同步默认为 `true`。

   Linux 上 `vk` 后端要求 glibc >= 2.33(ppy.SDL3-CS 要求), 老系统请用 `gl` 或升级 glibc; 后端不可用时 dsharp 会输出原因并自动回退(`gl` -> CPU)。

   终端 TUI 的后台 Git 面板、检查点和命令工具不继承交互终端输入,刷新或执行命令期间仍可逐键输入。普通命令的标准输入立即收到 EOF;需要持续输入的管道协议(如 `bash_persistent`)单独保持输入管道,交互式 shell 仍使用独立 PTY。窗口调整和子进程退出信号不会被误当作输入结束。正常 `/exit` 会恢复终端行模式、回显和主屏幕。

   Unix 下可用 `python3 scripts/check-tui-raw-mode.py --runs 8 --llm-smoke` 对已构建的 Debug TUI 做真实 PTY 回归(需 Python 3 和可用的模型配置)。它只操作独立 PTY,验证跨后台刷新和窗口调整的输入与正常退出的完整 termios 恢复;Linux 可额外加 `--strace` 诊断,但跟踪会改变信号时序,不能替代无跟踪回归。隔离配置、原始跟踪和结果保存在 `artifacts/raw/regression/`,其中配置与跟踪可能含私密信息,不要上传该目录。
3. 插件运维:TUI 内用 `/plugins list|add <包|dll 路径>|remove <包> [--force]|disable <包>|enable <包>|ban_write <包>|enable_write <包>`(输入 `/plugins` 后按 Tab 可从浮层选单选子命令与包名);改动写回 `settings.yaml` 的 `plugins:` 段并保留注释。

    `ban_write`/`enable_write` 切换该插件的写权限(`plugins."<包>".permissions.write`),`enable`/`disable` 保留权限设置,`remove` 连同参数与权限一并清除。权限目前只记录在配置里,由后续版本的插件沙箱强制执行。
    `disable` 只停用效果、保留插件(`enable` 可复原);`remove` 是卸载:摘除效果并删除 `plugins/<包名>/` 目录(内置或手工平铺的插件没有该目录,只做摘除)。更新插件:直接 `remove` 旧版(安装目录会被一并删除),再 `add` 新版;`add` 遇到同名但内容不同的文件会拒绝,以免覆盖手工放置的同名插件。
4. 日志:`<home>/logs/dsh-YYYYMMDD.log`(默认开启);`logging:` 段可配置级别、内存缓冲、是否输出到控制台(TUI 下强制关闭)。
5. 后台会话:TUI 内不带命令的 `/detach`(Ctrl+X D)保留当前对话和未发送草稿, 交由 PTY 守护进程继续运行。
   `/detach <命令>` 托管指定命令;通过 `tui list` 查找 PTY ID, 再用 `tui attach <pty-id>` 接回。
6. 插件目录:把插件放进 `<安装目录>/plugins/`,启动时自动加载并启用(可用 `/plugins disable <包>` 关闭)。托管插件是构建时引入 `Dsh.Plugins.Generator` 的 dll(生成清单提供入口),仅在默认 JIT 发布档可用;原生插件是 `.so`/`.dylib`/`.dll` 共享库(实现 `IDshNativePlugin` 声明包名与工具,导出握手与 ABI 胶水由生成器产出),JIT 与 AOT 发布档均可用,示例见 `tests/Dsh.NativePluginSample`。
7. 看图:模型可调用 `read_image` 工具读取本地图片(PNG/JPEG/GIF/WebP/BMP/TIFF 等常见格式,无扩展名的文件按内容识别),图片本身随工具结果交给模型;路径解析与 `read` 相同(相对路径按会话工作目录)。
   - 长边超过 2000px 的图片自动降采样,结果里写明原始尺寸与坐标换算倍数;源文件上限 20 MiB,存入附件库的图片不超过 5 MiB(超出时改用 JPEG 压缩)。
   - 需要接收图片输入的模型:DeepSeek 以内置模型目录为准(如 `deepseek-v4-flash-vision-exp`),Anthropic 与 OpenAI 兼容提供方以 models.dev 标注的输入模态为准;当前模型明确不支持图片时 `read_image` 直接报错,未收录的模型交由提供方自行判定。
8. TUI 快捷键:默认是 `Ctrl+X` 前缀序列(如 `Ctrl+X W` 总览、`Ctrl+X N` 新会话、`Ctrl+X +`/`Ctrl+X -` 分屏/关窗格;按下 `Ctrl+X` 后状态行会列出可用的后续键),TUI 内 `/keys` 列出全部动作与当前绑定。改键写在 `settings.yaml` 的 `plugins."@deepseek-ai/dsh-tui".keys` 下,格式为 `动作名: 按键串`,修饰键(`Ctrl`/`Alt`)用 `+` 连接,多键序列用空格分隔,未写的动作保持默认:

   ```yaml
   plugins:
     "@deepseek-ai/dsh-tui":
       keys:
         overview: F5                  # 单键打开总览
         newSession: "Ctrl+X Ctrl+N"
         splitPane: "Ctrl+X |"
       keySequenceTimeoutMs: 1500      # 前缀等待超时(毫秒),缺省 0 = 一直等到下一个键
   ```

   动作名:`newSession`、`sessionMenu`、`detach`、`deleteSessionMenu`、`agentList`、`overview`、`openShell`、`quit`、`splitPane`、`closePane`、`prevSession`、`nextSession`、`focusLeft`/`focusRight`/`focusUp`/`focusDown`、`focusNextPane`、`interrupt`(默认 `Ctrl+C`,连按两次退出)、`promptCommandMenu`(默认 `Ctrl+P`)。按键串的首键必须带 `Ctrl`/`Alt` 或是 F 键(否则会吞掉正常输入);同一按键串绑了多个动作、或一个按键串是另一个的前缀时,启动时记 WARN 日志并让冲突的自定义项回退默认。按下前缀后再按 `Esc` 或未绑定的键即取消。焦点在 shell 窗格时,`interrupt` 与 `promptCommandMenu` 的按键直达 shell,其余动作仍由 DSH 处理。
9. MCP 资源:`settings.yaml` 的 `mcp:` 段配置了 MCP 服务器时,模型可用 `list_mcp_resources`、`list_mcp_resource_templates`、`read_mcp_resource` 三个工具读取服务器暴露的资源,`server` 参数填 `mcp:` 段里的服务器名。
   - 文本资源直接返回;超过工具结果落盘阈值(`storage.tool_results.max_bytes`,缺省 64 KiB)时只保留头尾,全文写入 `<home>/tool-results/` 下的文件并在结果里给出路径,模型可用 `read` 分段读取。
   - 二进制资源存入内容寻址附件库(`<home>/attachments/`),结果给出附件 id、本地路径与字节数;图片可再用 `read_image` 查看。
   - 列表工具不带 `cursor` 时取回全部分页,带 `cursor` 时只取该页并返回 `nextCursor`;只有不带 `cursor` 的完整列举才清除对应列表的变更提醒,资源列表与模板列表分别记账,列举其中一个不会清除另一个的提醒。资源模板(如 `notes://{name}`)需由模型填好变量后交给 `read_mcp_resource`。
   - 服务器未声明 resources 能力时三个工具直接报错说明;服务器推送资源列表变更后,模型会在下一次请求时收到提示并重新列举。旧协议接收会话通知,`2026-07-28` 起仅监听列表变更,不订阅单个资源内容。监听在活连接中结束或失败会显示错误,下一次 MCP 调用沿现有单次重连路径恢复,不会静默丢失后续通知。`/mcp` 会标出声明了 resources 的服务器。
10. 会话内定时提醒:直接让模型提醒你即可,例如「30 分钟后提醒我看 CI」「工作日每天 9 点提醒我写日报」。模型用 `schedule_create` / `schedule_list` / `schedule_update` / `schedule_delete` 管理当前会话的提醒;到点后提醒内容作为一条计划消息回到本会话:会话空闲时直接开始新一轮,正在回复时排在当前这一轮之后。
   - 时间规格(每个提醒选一种):`after_seconds`(正安全整数秒)、`at`(带偏移的绝对时刻,或本地日期 + 时间 + 时区)、`every_seconds`(安全整数秒,至少 60 秒)、`daily`(每天的本地时间)、`weekly`(每周的 ISO 星期几,周一 1 至周日 7,不可重复)、`cron`(按给定时区计算的五段 Vixie cron)。本地日期严格写 `YYYY-MM-DD`,时间必须包含秒:`HH:mm:ss`,可带 1–3 位小数秒,返回值统一为 `HH:mm:ss.fff`。绝对时刻写 `YYYY-MM-DDTHH:mm:ss[.fff]Z` 或数字偏移 `±HH:mm`,必须使用大写 `T`/`Z`,偏移小时范围 00–23、分钟 00–59,不接受 `-00:00`。日期、时间和时区不自动去除首尾空白;cron 允许首尾和字段之间的空白。
   - 时区只接受 `UTC` 或有效的多段 IANA 名称(如 `Asia/Shanghai`),不接受 Windows id、固定偏移或空白;系统提示给出的本机建议也会映射为 IANA/UTC,无法映射则建议 `UTC`。`daily`/`weekly`/`cron` 跳过夏令时中不存在的本地时刻,重复时刻只在较早的瞬间触发一次;`at` 指向不存在的本地时刻会被拒绝,重复时刻取较早的一次。
   - 提醒属于会话:会话关闭后不再触发;恢复该会话时提醒随之恢复,期间错过的一次性提醒补发一次,循环提醒只补发最近错过的那一次。fork 出的会话不继承提醒,子代理不能使用这些工具。`headless` 单次任务在回复结束后即退出,其中设置的提醒不会触发。
   - 提醒不是 goal:提醒消息不算用户输入,不会创建、恢复或完成 goal,也不改变 goal 的自动续跑。
   - 派发会先推进提醒状态,再排入会话收件箱。删除已派发的提醒不撤回排队消息;用户中断会清空收件箱,已派发的一次性提醒不会重投。禁用定时提醒插件后仍可读取带 `ignorable` 标记的会话事件,重新启用后恢复提醒状态;禁用期间不运行计时器。事件注册支持与其他会话的日志读写并发进行。
   - cron 严格限定五段:`分钟 小时 日期 月份 星期`。只接受数字、`*`、升序范围 `a-b`、步进 `*/n` / `a-b/n` 及其逗号列表;步长须为正安全整数,星期日可写 0 或 7。不接受名称、宏、`?`、`L`、`W`、`#`、倒序范围、单值步进 `a/n` 或秒字段。Vixie 的日期/星期 star 标记取决于该字段原文是否以 `*` 开头(包括 `*/n`):任一带 star 标记时两段都须匹配,两段均无 star 标记时任一匹配即可;完整数字范围不等同于 `*`,列表中 `*` 的位置也会影响标记。
   - 所有平台(包括 Windows invariant-globalization 发布档)均使用内置 TZDB 规则与时区别名映射,不依赖系统 tzdata 或 ICU,规则随应用版本更新。别名使用与 Intl 一致的稳定规范名(如 `Asia/Kolkata` 规范化为 `Asia/Calcutta`)。UTC 目标限定为 0001–9999 年且保留毫秒;规则没有可表示的未来目标时返回 `time_out_of_range`。
11. 网页搜索引擎:`web_search` 工具的 `engine` 参数选择搜索引擎,同一次调用里的所有查询都发给这个引擎;不填 `engine` 时走通用网页搜索。系统提示词会列出当前可用的引擎,每条搜索结果都标明回答它的引擎及其局限(只搜了哪类对象、限流额度)。

   | 引擎 | 插件包 | 搜索范围 | 凭据参数 | 未配置凭据时的限制 |
   | --- | --- | --- | --- | --- |
   | `deepseek-official`(默认) | `@deepseek-ai/dsh-web-search-deepseek` | 全网网页 | `apiKey`(必填) | 不可用 |
   | `github-repos` | `@deepseek-ai/dsh-web-search-github` | GitHub 仓库的名称、描述与 topic(查询里加 `in:readme` 可搜 README) | `token`(可选) | 每 IP 每分钟 10 次(配置后 30 次) |
   | `github-code` | `@deepseek-ai/dsh-web-search-github` | GitHub 默认分支上的文件内容 | `token`(必填) | 不可用(配置后每分钟 10 次) |
   | `stackoverflow` | `@deepseek-ai/dsh-web-search-stackoverflow` | Stack Overflow 的问题(标题、正文、标签) | `key`(可选,在 stackapps.com 注册应用获得) | 每 IP 每天 300 次(配置后 10000 次) |
   | `baidu` | `@deepseek-ai/dsh-web-search-baidu` | 百度全网网页引用(千帆/AppBuilder `baidu_search_v2`) | `apiKey`(AppBuilder API Key,需开通搜索) | 不可用;额度和计费以控制台为准 |
   | `google` | `@deepseek-ai/dsh-web-search-google` | Google Programmable Search Engine 配置的语料 | `apiKey` 与 `searchEngineId`(cx),均必填 | 不可用;仅既有 API 客户可用,2027-01-01 停服 |
   | `bing` | `@deepseek-ai/dsh-web-search-bing` | Bing 公共 RSS 入口的首屏结果 | 无;可配 `market`(默认不传,由站点选择地区) | 无公开自动调用额度/SLA,可能限流或封禁 |
   | `duckduckgo` | `@deepseek-ai/dsh-web-search-duckduckgo` | DuckDuckGo 公共 HTML 入口的首屏链接与标题 | 无;可配 `region`(默认 `wt-wt`) | 无公开自动调用额度/SLA,可能要求人工验证 |

   各引擎插件独立发现并启用,各自在 `plugins:` 段写 `false` 即单独关闭;缺凭据的引擎不进入可用列表,显式调用时给出配置项提示。
   新增的四个引擎都是 named-only,不会抢占默认 DeepSeek 搜索;也可在 `plugins."@deepseek-ai/dsh-web".searchProvider` 显式设为默认。
   凭据只从 `settings.yaml` 读取,错误只展示不含查询串的端点;请求、凭据与原始响应不会写入搜索日志:

   ```yaml
   plugins:
     "@deepseek-ai/dsh-web-search-github":
       token: <GitHub token>          # 可选; 开启 github-code 并提高限流, 只需公开仓库只读权限
     "@deepseek-ai/dsh-web-search-stackoverflow":
       key: <Stack Exchange key>      # 可选; 提高每日配额
     # "@deepseek-ai/dsh-web-search-stackoverflow": false   # 单独关闭 Stack Overflow 引擎
     "@deepseek-ai/dsh-web-search-baidu":
       apiKey: <AppBuilder API Key>
     "@deepseek-ai/dsh-web-search-google":
       apiKey: <Google API key>       # 仅既有 Custom Search JSON API 客户
       searchEngineId: <cx>
     "@deepseek-ai/dsh-web-search-bing":
       market: zh-CN
     "@deepseek-ai/dsh-web-search-duckduckgo":
       region: cn-zh
     # "@deepseek-ai/dsh-web-search-duckduckgo": false
   ```

   传输、代理与错误语义由所有站点引擎共享:统一读取 `network.outbound_proxy.url`/`bypass`,不另建绕过代理的 handler;代理在当前进程共享,修改后后续请求使用新路由。
   HTTP 429/配额错误返回 `WEB_PROVIDER_ERROR`,保留 `Retry-After` 提示;人工验证、重定向及无法识别的响应也报错,不伪装成“没有结果”,不自动换引擎。
   百度最多取 50 条网页引用;Google 每次最多 10 条、只能访问前 100 条,默认每天 100 次免费请求,开通计费后最高每天 10000 次,具体范围由 cx 决定。
   Bing RSS 不是已于 2025-08-11 退役的 Azure Bing Search API,没有总数或翻页保证,结果相关性可能与交互网页不同;其 RSS 条款限制个人非商业 RSS 阅读用途,其他用途需 Microsoft 许可。
   DuckDuckGo 不提供完整网页搜索的公开 JSON API,这里使用非 JavaScript HTML 入口而不是只有百科摘要的 Instant Answer API;遇到站点验证需人工处理或由用户选择其他引擎,不会自动解验证码。
   入口与限制依据:[百度搜索 API](https://ai.baidu.com/ai-doc/AppBuilder/pmaxd1hvy)、[Google 当前可用范围与停服说明](https://developers.google.com/custom-search/v1/overview)、[Bing API 退役公告](https://learn.microsoft.com/en-us/lifecycle/announcements/bing-search-api-retirement)、[Bing 公共 RSS](https://www.bing.com/search?q=dotnet&format=rss)、[DuckDuckGo HTML](https://html.duckduckgo.com/html/?q=dotnet)。
   注意 `/plugins disable|enable` 会丢弃该插件的参数,重新启用后需要再写一次凭据与地区配置。

12. 工作目录与 worktree:模型可用 `working_directory` 查看或切换本会话的工作目录(`cd`),用 `create_worktree` 从本地 commit/branch/tag 新建 Git 分支与 worktree(位于 `<当前检出根>/.agents/worktrees/<名字>`)并切换进去。
   - 切换只影响之后的相对路径解析,以及新启动的 shell/终端的默认目录;已经存在的 `bash_persistent` shell 与终端仍停在各自的目录。
   - 写权限根固定为会话的初始目录,不随切换移动:写入权限根之外的文件(`write`/`edit`/`str_replace_editor`)、在权限根之外启动 shell/终端或建 worktree,都会先弹审批,提示「目标路径在权限根之外」;审批策略为 never 时直接拒绝。
   - 两者分别对应插件 `@deepseek-ai/dsh-tool-working-directory` 与 `@deepseek-ai/dsh-tool-worktree`,可用 `/plugins disable` 关闭。

## 图形界面

GUI 是内置插件 `@deepseek-ai/dsh-gui`,与 TUI 共用同一份配置、会话与命令实现(`/` 命令、`@` 引用、审批、`ask_user_question` 都走 harness 的同一条链路)。

```bash
# 构建 GUI 启动器(独立于 CLI/TUI 宿主;产物在 DshGuiHost/bin/<Debug|Release>/net10.0/)
dotnet build DshGuiHost/DshGuiHost.csproj
# 根目录的 dotnet build 会经 DeepSeek-Harness-Sharp.slnx 一并构建它

# Windows: 双击 dsh-gui.exe(Windows 子系统,不会出现控制台黑框),或从命令行启动
DshGuiHost\bin\Debug\net10.0\dsh-gui.exe
# Linux/macOS: 运行同一工程的产物
DshGuiHost/bin/Debug/net10.0/dsh-gui
# 从控制台宿主进入 GUI(会占用当前控制台)
DeepSeek-Harness-Sharp\bin\Debug\net10.0\dsharp.exe gui
# 直接打开某个历史会话
DeepSeek-Harness-Sharp\bin\Debug\net10.0\dsharp.exe gui --session <会话 id>
```

- 挂到桌面环境:Windows 用 `pwsh -File scripts/install-desktop.ps1`(在桌面创建快捷方式),Linux 用 `bash scripts/install-desktop.sh`(写入 `~/.local/share/applications/dsh-gui.desktop` 并复制图标,GNOME/KDE 等桌面可直接固定到 dock/面板)。
- 快捷键:`Enter` 发送、`Shift+Enter` 换行、`Esc` 回到对话、`Ctrl+N` 新会话、`Ctrl+B` 折叠左栏、`Ctrl+1`/`Ctrl+2` 对话/轨迹、`Ctrl+,` 设置、`Ctrl+W` 关闭(按关闭行为)、`Ctrl+Q` 直接退出。
- 插件市场页:安装框旁的文件夹按钮可浏览选择插件库文件(`.dll`/`.so`/`.dylib`);每个插件有启用/禁用、"移除"按钮(与 `/plugins remove` 相同,托管插件移除后照常校验加载上下文回收)以及读/写/网络三个权限勾选框,写入与 `/plugins ban_write|enable_write` 相同的 `settings.yaml` 键,两端看到同一份状态。
- 设置页改的是 GUI 插件自己的参数段(写在 `settings.yaml` 的 `plugins:` 里,不会新建 `ui:` 顶层段):

  ```yaml
  plugins:
    "@deepseek-ai/dsh-gui":
      theme: dark           # dark|light|system
      fontSize: 13.5        # 11~18
      closeAction: tray     # tray|quit|ask
      workspaceView: solution   # solution|filesystem
      sidebarVisible: true
      gpu: { enabled: true, adapter: auto, backend: auto }   # adapter 选显卡(设置页会列出本机识别到的卡), backend 是高级项: auto|opengl|vulkan|software,改动重启生效
      window: { rememberBounds: true }
  ```

- 内容折叠:思考、工具调用与结果、上下文注入默认折叠,点「▸ 展开 / ▾ 折叠」切换;轨迹条目用右侧箭头展开详情;助手正文中的代码块超过 12 行先折叠。
- 需要用户决定:工具审批与 `ask_user_question` 共用同一个窗口。审批提供「仅本次允许(y)/总是允许(a,本会话内该工具不再询问)/拒绝(n)/取消(c 或 Esc)」,并展示工具的主参数与影响提示。
- 会话标题:首条用户消息写入会话标题(会话 id 不变),侧栏会话项的 `⋯` 菜单可重命名、导出日志、删除。
- 消息操作:助手消息下方有复制、有帮助/没帮助、重新生成(把最后一条用户消息重新入队)。
- 交付物:模型用 `present` 声明的文件列在会话顶栏「交付物」下拉里,可「打开」(系统默认程序)或复制路径;远端会话只列出、不在本机打开。
- 单实例:同一份 home 只会有一个 GUI,重复启动(双击、快捷方式、`dsh-gui.exe`)只会把已有窗口调到前台,不会开第二个窗口。同一个 home 里同时跑 GUI 与 CLI/TUI 也是允许的,日志会自动共用同一个文件。
- 显卡选择:设置页「图形与加速」列出本机识别到的显卡(Windows 读显示类驱动注册表,Linux 读 `/sys/class/drm`),默认「自动」。Windows 上通过 Avalonia 的显卡选择回调按名字匹配;Linux 上用 PRIME 选择器(`DRI_PRIME` / `__NV_PRIME_RENDER_OFFLOAD`)表达偏好,部分驱动或容器里可能不生效。保存后重启生效;想确认实际用了哪张卡,Windows 上可在任务管理器或 `nvidia-smi` 里看 `dsh-gui.exe`。
- 关闭行为:默认「最小化到托盘」,设置页「图形与加速」可改成直接退出或每次询问。Windows 用通知区域图标;Linux 走 StatusNotifier,KDE 自带托盘宿主,GNOME 需要 AppIndicator 扩展(Ubuntu 桌面默认带)。启动时若探测不到托盘宿主(例如 GNOME 没装扩展),会按「直接退出」处理,避免窗口藏进看不见的地方。
- 平台说明:自绘标题栏、显卡选择与托盘在 Windows 上支持最完整。在 Linux(含 WSL) 上后台验证界面可用 `bash scripts/verify-gui-linux.sh`(Xvfb 虚拟显示 + 截图,不打扰当前桌面);托盘行为可用同样方式起 plasmashell / gnome-shell 后验证(见 AGENTS.md 规则 55)。

## 发布形态

- 默认 Release:JIT + 裁剪 + 自包含。
  ```bash
  dotnet publish DeepSeek-Harness-Sharp/DeepSeek-Harness-Sharp.csproj -c Release -r linux-x64
  ```
  支持运行时插件装载:`<安装目录>/plugins/` 启动加载,以及 `/plugins add <dll>` 热装载(可回收 AssemblyLoadContext,移除后校验回收)。
- NativeAOT(可选档):
  ```bash
  dotnet publish DeepSeek-Harness-Sharp/DeepSeek-Harness-Sharp.csproj -c Release -r linux-x64 -p:PublishAot=true
  ```
  - 启动更快、体积更小,覆盖镜像内的全部插件与 LLM 适配器;
  - 原生插件可在启动时从 `<安装目录>/plugins/` 加载并注册工具(ABI v1:日志与工具;插件需自带 NativeAOT 编译的共享库);
  - NativeAOT 不支持在运行期装载托管 dll:`plugins/` 中的托管插件启动时逐文件 WARN 并跳过(被跳过的插件不会出现在 `/plugins list`),其余插件照常启用、程序正常启动;要使用托管插件就把它编译进镜像;
  - 会话持久化(JSONL + Zstd)在 JIT 与 AOT 下均可用。

## 各版本新增功能

0.1.1 起的发布说明未单独张贴,这里按版本折叠列出新增能力(0.1.0 的完整发布说明见[讨论区](https://github.com/deepseek-ai/deepseek-harness/discussions/8952))。

<details>
<summary><b>0.1.1</b> — SSH 远程工作区、macOS 支持、TUI 增强</summary>

- **SSH 远程工作区**:新增中立传输层(长度分帧 + JSON-RPC 2.0)与远端宿主,GUI 可保存/打开远端工作区,自动在远端部署 `dsharp` 并测试连接,远端会话/消息/工具/文件/终端(PTY)/审批走同一条通道。
- **命令改名**:`dsh` → `dsharp`。
- **macOS**:加入 macOS 的 CI 测试与 JIT 发布,修复 Unix PTY 绑定与 GUI 主线程启动。
- **TUI**:24 位/256 色真彩渲染;attach 或调整尺寸后整帧重绘;Pty→Session→Pane 总览;`Ctrl+X ,` / `Ctrl+X .` 切换同一 Pty 下的会话;`/plugins remove` 变为真正卸载。
- **时间线/回滚**:`/timestamp` 预览与跳转、会话截断、检查点回滚。
- **其它**:提示词按路径引用的图片可随请求发送;过大的工具结果自动落盘;原生 AOT 插件加载带故障遏制;看板新增 `board_claim`/`board_release` 软行锁。
</details>

<details>
<summary><b>0.1.2</b> — 远程工作区打磨</summary>

- 远端会话跟随客户端选择的模型与提供方。
- 远端部署按发布版本取键,过期守护进程自动重启。
- 修复 Avalonia 12.1.3+ 上托盘图标不显示;Avalonia 升到 12.1.4。
- GUI 远端会话列表放入独立滚动区。
</details>

<details>
<summary><b>0.2.0</b> — Vulkan 渲染后端、可配置主题、设置面统一</summary>

- **Vulkan 渲染后端**:TUI 新增可插拔 GPU 宿主抽象与 Vulkan 后端,按 DRM 卡的 PCI 槽位选设备;裸控制台可经 SDL KMS/DRM 直接驱动;无 root 即可安装 Vulkan loader;OpenGL 后端拆分到独立模块。
- **界面**:TUI 颜色可配置;会话选择显示标题与时间戳;IDE 历史并入 `/timestamp` 时间线(可开关);同一守护进程下的 Pty 可直接进入。
- **设置**:插件配置面在编译期生成 schema;GUI 列出提供方生效后的模型;`settings.yaml` 只保留 models.dev 未收录的模型;存储路径统一到一个根。
- **会话**:`fork` 改为按事件种子继承,修复派生会话内容缺失。
- 发布 macOS JIT 构建。
</details>

## settings.yaml 结构

配置文件为 `~/.dsh/settings.yaml`;首次运行按 `src/Dsh.Boot/Profiles/Templates/settings.yaml` 生成带注释的模板(可用 `$DSH_HOME` 或 `storage.root` 指定其它 harness home 做隔离测试)。
`/plugins` 命令写回 `plugins:` 段时采用文本级修改(保留注释)。

```yaml
# 全局默认模型 / 压缩模型 / 子代理默认模型,格式均为 provider/model
global_default_model: deepseek-official/deepseek-v4-flash
compaction_model: deepseek-official/deepseek-v4-flash
subagent:
  default_model: deepseek-official/deepseek-v4-flash

# LLM 提供方:options 为连接参数,models 为该提供方可用模型
# type 缺省即 openai-compatible;DeepSeek 专属行为(缓存 token 统计等)需显式 type: deepseek
# 适配器由插件提供(dsh-llm-openai / dsh-llm-anthropic / dsh-llm-deepseek),可禁用或换用第三方插件;
# 未配置 key 的 provider 会被跳过并记 WARN;完全不配 provider 也能启动,首次发起请求时才报错
providers:
  deepseek-official:
    type: openai-compatible
    options:
      baseUrl: https://api.deepseek.com
      apiKeyEnv: DEEPSEEK_API_KEY
    models:
      deepseek-v4-flash: { name: DeepSeek V4 Flash, reasoning: true, tool_call: true }
      deepseek-v4-pro:   { name: DeepSeek V4 Pro,   reasoning: true, tool_call: true }

skills:                 # 技能目录与 URL
  paths: []
  urls: []
rules: []               # 全局规则
sandbox:                # 插件沙箱开关,默认 false(关闭时插件装载与运行方式不变)
  enabled: false
plugins: {}             # 插件开关,见下
mcp: {}                 # MCP 服务器:transport: stdio|sse|streamable-http,配 command/args/url/enabled
memory:                 # 项目记忆(/memory on|off;file 或 mongo 后端)
  enabled: false
  # backend: mongo      # 可选:mongo 时用数据库存整段 markdown
  # mongo: { connectionString: mongodb://localhost:27017, database: dsh_memory, collection: memory, key: project }
compaction:             # 自动压缩
  auto: true
  prune: true
checkpoints:            # shadow git 恢复点
  enabled: false
  max_points: 256
  keep_days: 15
logging:                # 日志:trace|debug|info|warn|error|off
  level: info
  buffer_size: 1000
  console: false
  file: true
  file_max_mb: 32
  keep_days: 15
safety:
  autoApprove: false
  blacklist: []
```

本机出口由 GUI 设置「网络」与 TUI 共用:

```yaml
network:
  outbound_proxy:
    url: socks5://user:pass@127.0.0.1:1080
    bypass: [api.deepseek.com]
    probe_url: https://www.gstatic.com/generate_204
```

`url` 支持 HTTP、HTTPS、SOCKS4/4a/5,留空跟随系统代理;日志与自检脱敏代理凭据。此配置统一本机 HTTP、LLM、web_search/web_fetch、目录查询与发布包下载,不配置远程服务器自己的出口。GUI 保存立即应用到已有客户端的后续请求;TUI `/proxy set <URL>` 写回并热更新,`/proxy clear` 恢复系统代理(保留直连名单与自检地址),手改 settings.yaml 后用 `/proxy reload` 热更新。`/proxy test [URL]` 自检并区分代理连接、认证与目标访问失败。

`web_fetch` 不因配置代理而放宽公网限制:每次请求及重定向均在本机解析并拒绝非公网或混合公网/私网答案;直连钉住已校验地址,经代理则以已校验的 IP 作为 HTTP 绝对 URI、CONNECT 或 SOCKS 的实际目标,同时保留原始 Host、TLS SNI 与正常证书域名校验。代理不再替 `web_fetch` 解析目标域名,因此本机 DNS 不可用时请求明确失败。代理必须支持 IP 目标及原始 Host;拒绝这种路由的代理会报真实访问失败,不会回退到未校验的域名。代理服务器本身是可信传输边界,恶意代理仍可无视所请求的地址(HTTP 无端到端加密,HTTPS 仍校验证书);本机不声称能控制远端 DNS。其他 HTTP 客户端沿用各自的目标策略。

- `plugins:` 支持两种写法:`"@deepseek-ai/dsh-tool-todo": false`(禁用),或带参数的 `"@deepseek-ai/dsh-tool-todo": { enabled: true, ... }`(保存插件参数);插件被发现即启用,这里的禁用项与 `/plugins` 命令是仅有的两个开关来源。
- 插件权限:`permissions` 是与 `enabled` 并列的保留键(不会作为参数交给插件),含 `read`/`write`/`network` 三项布尔值;未写 `permissions` 或未写某一项时均为允许。由 `/plugins ban_write|enable_write` 与 GUI 插件市场的勾选框写入,也可手写;拼错的键或非布尔值会在启动时报 `INVALID_PLUGIN_SETTING`。目前只记录配置,强制执行由后续版本的插件沙箱(`sandbox.enabled: true`)提供:

  ```yaml
  sandbox:
    enabled: false
  plugins:
    "@acme/foo":
      enabled: true
      permissions: { read: true, write: false, network: true }
  ```
- 运行期数据:`<home>/logs/dsh-YYYYMMDD.log`(按天 + `file_max_mb` 切分、`keep_days` 清理),`<home>/sessions/<工作目录转写>/<会话 id>/session.jsonl.zstd`,`<home>/telemetry.jsonl`(agent 生命周期与错误事件,可用 `plugins: {"@deepseek-ai/dsh-telemetry": false}` 关闭)。
- 子代理模型:模型可调用 `list_subagent_models` 查看子代理能用的模型路由,即已配置 key 的 provider 及其生效模型(`settings.yaml` 声明的模型 ∪ models.dev 目录),模型公布了推理强度时一并列出;`subagent` 工具的 `provider`/`model`/`reasoning_effort` 参数直接取用其中一行,不填则沿用该工具插件参数里的 `agentOptions`,其次是 `settings.yaml` 的 `subagent.default_model`,再缺省则跟随当前会话的模型。未配置 key 的 provider 不会出现在列表中。可用 `plugins: {"@deepseek-ai/dsh-tool-subagent/list-models": false}` 关闭该工具。
- 会话检索:TUI/CLI 内 `/session-query <关键词>` 搜索历史会话(命中用 `/session <id>` 打开);模型侧对应 `session_search` 工具。检索覆盖内存中的活会话与 `sessions/` 下的历史日志。
- 交付物:模型生成报告、Office 文档、表格、幻灯片等文件后,用 `present` 工具把已存在的文件声明为最终交付物(只记录绝对路径,不复制、不转换内容;路径不存在或是目录会报错)。清单随会话日志保存,恢复会话后仍在,回退/截断会话时一并回退。TUI 会话流里显示交付物卡片(绝对路径可直接复制),`/deliverables` 列出本会话交付物,`/deliverables open <编号>` 用系统默认程序打开;GUI 见「图形界面」一节。不需要时用 `plugins: {"@deepseek-ai/dsh-tool-present": false}` 关闭。
