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
   # TUI 的会话管理(PTY 守护进程按需自动拉起,无需手动启动)
   dsharp -- tui list   # 列出所有会话id
   dsharp -- tui attach <id>  #进入某个会话
   dsharp -- tui attach <pty-id> --gpu  #用独立 GPU 窗口接回该会话
   ```
   TUI 的 GPU 渲染后端由 `plugins."@deepseek-ai/dsh-tui"` 的 `gpuBackend` 配置项决定(`auto|vk|gl|cpu`,缺省 `auto`，优先以 `vk` 后端启动)，`vsync` 即垂直同步默认为 `true`。

   Linux 上 `vk` 后端要求 glibc >= 2.33(ppy.SDL3-CS 要求), 老系统请用 `gl` 或升级 glibc; 后端不可用时 dsharp 会输出原因并自动回退(`gl` -> CPU)。
3. 插件运维:TUI 内用 `/plugins list|add <包|dll 路径>|remove <包> [--force]|disable <包>|enable <包>`;改动写回 `settings.yaml` 的 `plugins:` 段并保留注释。

    `disable` 只停用效果、保留插件(`enable` 可复原);`remove` 是卸载:摘除效果并删除 `plugins/<包名>/` 目录(内置或手工平铺的插件没有该目录,只做摘除)。更新插件:直接 `remove` 旧版(安装目录会被一并删除),再 `add` 新版;`add` 遇到同名但内容不同的文件会拒绝,以免覆盖手工放置的同名插件。
4. 日志:`<home>/logs/dsh-YYYYMMDD.log`(默认开启);`logging:` 段可配置级别、内存缓冲、是否输出到控制台(TUI 下强制关闭)。
5. 后台会话:TUI 内 `/detach [命令]`(Ctrl+X D)把命令交给 PTY 守护进程托管;`tui list`/`tui attach` 会自动拉起守护进程。
6. 插件目录:把插件放进 `<安装目录>/plugins/`,启动时自动加载并启用(可用 `/plugins disable <包>` 关闭)。托管插件是构建时引入 `Dsh.Plugins.Generator` 的 dll(生成清单提供入口),仅在默认 JIT 发布档可用;原生插件是 `.so`/`.dylib`/`.dll` 共享库(实现 `IDshNativePlugin` 声明包名与工具,导出握手与 ABI 胶水由生成器产出),JIT 与 AOT 发布档均可用,示例见 `tests/Dsh.NativePluginSample`。

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

- `plugins:` 支持两种写法:`"@deepseek-ai/dsh-tool-todo": false`(禁用),或带参数的 `"@deepseek-ai/dsh-tool-todo": { enabled: true, ... }`(保存插件参数);插件被发现即启用,这里的禁用项与 `/plugins` 命令是仅有的两个开关来源。
- 运行期数据:`<home>/logs/dsh-YYYYMMDD.log`(按天 + `file_max_mb` 切分、`keep_days` 清理),`<home>/sessions/<工作目录转写>/<会话 id>/session.jsonl.zstd`,`<home>/telemetry.jsonl`(agent 生命周期与错误事件,可用 `plugins: {"@deepseek-ai/dsh-telemetry": false}` 关闭)。
- 会话检索:TUI/CLI 内 `/session-query <关键词>` 搜索历史会话(命中用 `/session <id>` 打开);模型侧对应 `session_search` 工具。检索覆盖内存中的活会话与 `sessions/` 下的历史日志。
- 交付物:模型生成报告、Office 文档、表格、幻灯片等文件后,用 `present` 工具把已存在的文件声明为最终交付物(只记录绝对路径,不复制、不转换内容;路径不存在或是目录会报错)。清单随会话日志保存,恢复会话后仍在,回退/截断会话时一并回退。TUI 会话流里显示交付物卡片(绝对路径可直接复制),`/deliverables` 列出本会话交付物,`/deliverables open <编号>` 用系统默认程序打开;GUI 见「图形界面」一节。不需要时用 `plugins: {"@deepseek-ai/dsh-tool-present": false}` 关闭。
