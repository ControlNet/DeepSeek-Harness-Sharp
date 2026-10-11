using OpenTK.Graphics.Vulkan;
using System.Runtime.InteropServices;
// 裸名 `Vk` 会被外层命名空间 Dsh.Tui.Vk 遮蔽(编译期优先解析为命名空间), 因此用编译单元别名指向静态绑定类。
using VulkanApi = OpenTK.Graphics.Vulkan.Vk;

namespace Dsh.Tui.Vk;

/**
 * SDL3 窗口 / Vulkan 表面宿主: 窗口 + 实例/设备 + VkSurfaceKHR 这一层, 并作为 IGlSurfaceHostRunner
 * 驱动 SDL3 事件循环: 把键/文本/鼠标事件映射成 IGpuHostClient 的回调, Present 提交交换链帧。
 * - SDL.SDL3.SDL_Init(VIDEO) 全进程只做一次;
 * - 窗口默认 HIDDEN(AGENTS 55: 不得抢前台焦点);
 * - 实例扩展取自 SDL.SDL3.SDL_Vulkan_GetInstanceExtensions; 设备启用 VK_KHR_swapchain;
 * - 队列族用 SDL.SDL3.SDL_Vulkan_GetPresentationSupport 挑"图形+呈现";
 * - 渲染核心 VkRenderCore 由 CreateRenderCore 创建并与本宿主的交换链(VkWindowRenderer)共享。
 */
internal sealed unsafe class VkSurfaceHost : IGlSurfaceHostRunner, IGpuHostWaker
{
    private static readonly object SdlGate = new();
    private static bool _sdlInitialized;

    /** SDL_BUTTON_* 索引: 左/中/右分别映射到 GpuMouseButton. */
    private const byte SdlButtonLeft = 1;
    private const byte SdlButtonMiddle = 2;
    private const byte SdlButtonRight = 3;

    /** leading/trailing 合并窗口(B16): 距上帧 ≥10ms 立即渲, 否则合并到 10ms 后渲最新态。 */
    private const int FrameIntervalMs = 10;

    private readonly SDL.SDL_Window* _window;
    private readonly VkGpuDevice _gpu;
    private readonly VkSurfaceKHR _surface;
    private readonly bool _vsync;
    private VkWindowRenderer? _renderer;
    private volatile uint _wakeType;
    private bool _closeRequested;
    private bool _disposed;

    public nint Window => (nint)_window;

    public VkGpuDevice Gpu => _gpu;

    public VkSurfaceKHR Surface => _surface;

    /** 交换链窗口渲染器(CreateRenderCore 之后非空); 供取证/测试读取帧槽诊断。 */
    internal VkWindowRenderer? Renderer => _renderer;

    public string DeviceName => _gpu.DeviceName;

    private VkSurfaceHost(SDL.SDL_Window* window, VkGpuDevice gpu, VkSurfaceKHR surface, bool vsync)
    {
        _window = window;
        _gpu = gpu;
        _surface = surface;
        _vsync = vsync;
    }

    public static VkSurfaceHost Create(string title, int width, int height, string? deviceNameFilter = null, bool hidden = true, bool vsync = true)
    {
        EnsureSdl();
        if (!SDL.SDL3.SDL_SetAppMetadata("dsh-tui-vk", "0.1", "dsh"))
            throw new InvalidOperationException($"SDL_SetAppMetadata 失败: {SDL.SDL3.SDL_GetError()}");

        // SDL 窗口默认不可缩放, 要显式加 SDL_WINDOW_RESIZABLE 才能让标题栏最大化/双击/拖边生效
        // (GLFW 侧 GLFW_RESIZABLE 默认就是 true, 所以 GL 窗口天生可缩放)。缩放事件由下面 PumpEvents 的
        // SDL_EVENT_WINDOW_RESIZED / SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED 分支驱动交换链重建。
        var flags = SDL.SDL_WindowFlags.SDL_WINDOW_VULKAN | SDL.SDL_WindowFlags.SDL_WINDOW_RESIZABLE;
        if (hidden)
            flags |= SDL.SDL_WindowFlags.SDL_WINDOW_HIDDEN;
        var window = SDL.SDL3.SDL_CreateWindow(title, width, height, flags);
        if (window is null)
            throw new InvalidOperationException($"SDL_CreateWindow 失败: {SDL.SDL3.SDL_GetError()}");

        var extensions = GetInstanceExtensions();
        if (extensions.Length == 0)
        {
            SDL.SDL3.SDL_DestroyWindow(window);
            throw new InvalidOperationException($"SDL_Vulkan_GetInstanceExtensions 失败: {SDL.SDL3.SDL_GetError()}");
        }

        VkGpuDevice gpu;
        try
        {
            gpu = VkGpuDevice.Create(
                extensions,
                deviceNameFilter,
                ["VK_KHR_swapchain"],
                (instance, device, family) => SDL.SDL3.SDL_Vulkan_GetPresentationSupport((SDL.VkInstance_T*)instance.Handle, (SDL.VkPhysicalDevice_T*)device.Handle, family));
        }
        catch
        {
            SDL.SDL3.SDL_DestroyWindow(window);
            throw;
        }

        SDL.VkSurfaceKHR_T* surfacePtr;
        if (!SDL.SDL3.SDL_Vulkan_CreateSurface(window, (SDL.VkInstance_T*)gpu.Instance.Handle, null, &surfacePtr))
        {
            gpu.Dispose();
            SDL.SDL3.SDL_DestroyWindow(window);
            throw new InvalidOperationException($"SDL_Vulkan_CreateSurface 失败: {SDL.SDL3.SDL_GetError()}");
        }

        return new VkSurfaceHost(window, gpu, new VkSurfaceKHR((ulong)surfacePtr), vsync);
    }

    /** SDL_Vulkan_GetInstanceExtensions 返回 byte**(UTF-8 名字数组); 冷路径(建窗口一次)编组成 string[]。 */
    private static string[] GetInstanceExtensions()
    {
        uint count = 0;
        var names = SDL.SDL3.SDL_Vulkan_GetInstanceExtensions(&count);
        if (names is null || count == 0)
            return [];
        var result = new string[count];
        for (var i = 0u; i < count; i++)
            result[i] = Marshal.PtrToStringUTF8((nint)names[i]) ?? "";
        return result;
    }

    /** 当前窗口标志(用于核对未抢焦点: 不应含 INPUT_FOCUS / MOUSE_FOCUS, 应含 HIDDEN)。 */
    public SDL.SDL_WindowFlags WindowFlags => SDL.SDL3.SDL_GetWindowFlags(_window);

    public bool HasInputFocus => (WindowFlags & SDL.SDL_WindowFlags.SDL_WINDOW_INPUT_FOCUS) != 0;

    /** 窗口像素尺寸(交换链 extent 为 undefined 时用它兜底)。 */
    public (int Width, int Height) PixelSize()
    {
        int width = 0;
        int height = 0;
        return SDL.SDL3.SDL_GetWindowSizeInPixels(_window, &width, &height) ? (width, height) : (0, 0);
    }

    private static void EnsureSdl()
    {
        lock (SdlGate)
        {
            if (_sdlInitialized)
                return;
            if (!SDL.SDL3.SDL_Init(SDL.SDL_InitFlags.SDL_INIT_VIDEO))
                throw new InvalidOperationException($"SDL_Init(Video) 失败: {SDL.SDL3.SDL_GetError()}");
            _sdlInitialized = true;
        }
    }

    /** Vk 没有"当前上下文"概念(设备常驻), MakeCurrent 为空实现。 */
    public void MakeCurrent()
    {
    }

    /** 呈现一帧: 把 VkRenderCore 录好的当前帧提交到交换链并 present(未录帧时为空操作)。 */
    public void Present() => _renderer?.Present();

    public (int Width, int Height) Size => PixelSize();

    /** 创建与宿主交换链共享的渲染核心(同时懒建交换链窗口渲染器)。 */
    public ITuiSurfaceBackend CreateRenderCore(GlyphAtlas atlas)
    {
        _renderer = VkWindowRenderer.Create(this, atlas, _vsync);
        return _renderer.Core;
    }

    /**
     * 驱动 SDL3 事件循环(B16): 输入/唤醒事件驱动 + leading/trailing 10ms 合并; 空闲时阻塞等待事件(不忙等)。
     * - leading: 距上帧至少 10ms(或首帧)收到事件即渲;
     * - trailing: 距上帧不足 10ms 时置 dirty, 由 WaitEventTimeout 在 10ms 到点后渲最新态;
     * - 空闲: 阻塞在 WaitEventTimeout 直到事件/超时(超时周期=光标闪烁半周期, 让光标能闪);
     * - 每帧渲前先 WaitSlotIdle(只等当前帧槽的在途 fence)。
     */
    public void Run(IGpuHostClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (_renderer is null)
            throw new InvalidOperationException("创建渲染核心(CreateRenderCore)之后才能 Run");
        SDL.SDL3.SDL_StartTextInput(_window);
        _wakeType = SDL.SDL3.SDL_RegisterEvents(1);
        client.OnLoaded();
        var nextFrameAt = 0L;
        var dirty = true;
        try
        {
            while (!_closeRequested)
            {
                var now = Environment.TickCount64;
                if (dirty && now >= nextFrameAt)
                {
                    if (!PumpEvents(client))
                        break;
                    // 环形缓冲: 上传前先等当前槽的在途 fence(只等这一槽, 允许其余槽继续在途)。
                    _renderer.WaitSlotIdle();
                    if (client.OnFrame())
                        break;
                    nextFrameAt = Environment.TickCount64 + FrameIntervalMs;
                    dirty = false;
                    continue;
                }

                var timeout = dirty ? (int)Math.Max(0, nextFrameAt - now) : GpuRenderer.CursorBlinkMs;
                SDL.SDL_Event @event;
                if (!SDL.SDL3.SDL_WaitEventTimeout(&@event, timeout))
                {
                    // 到点: trailing 合并窗口已过(或空闲光标闪烁该换相), 下一轮重绘。
                    dirty = true;
                    continue;
                }

                if (!DispatchEvent(client, in @event))
                    break;
                dirty = true;
            }
        }
        finally
        {
            _wakeType = 0;
        }
    }

    /** 跨线程唤醒(agent 流式输出/隧道画面更新): 推一个自定义事件解除 WaitEventTimeout 的阻塞。 */
    public void Wake()
    {
        var type = _wakeType;
        if (type == 0)
            return;
        var @event = new SDL.SDL_Event { type = type };
        SDL.SDL3.SDL_PushEvent(&@event);
    }

    public void RequestClose() => _closeRequested = true;

    public string? ReadClipboard() => SDL.SDL3.SDL_GetClipboardText();

    /** 调整窗口像素尺寸并触发交换链重建(捕获计划的 resize 指令走这里)。 */
    public void Resize(int pixelWidth, int pixelHeight)
    {
        SDL.SDL3.SDL_SetWindowSize(_window, Math.Max(1, pixelWidth), Math.Max(1, pixelHeight));
        _renderer?.RequestResize();
    }

    /** 泵一轮排队的 SDL 事件; 返回 false 表示应退出(Quit/关闭窗口)。 */
    private bool PumpEvents(IGpuHostClient client)
    {
        SDL.SDL_Event @event;
        while (SDL.SDL3.SDL_PollEvent(&@event))
        {
            if (!DispatchEvent(client, in @event))
                return false;
        }

        return true;
    }

    /** 派发单个 SDL 事件; 返回 false 表示应退出。尺寸变化按像素尺寸回调 client.OnResize。 */
    private bool DispatchEvent(IGpuHostClient client, in SDL.SDL_Event @event)
    {
        switch ((SDL.SDL_EventType)@event.type)
        {
            case SDL.SDL_EventType.SDL_EVENT_QUIT:
            case SDL.SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                return false;
            case SDL.SDL_EventType.SDL_EVENT_WINDOW_RESIZED:
            case SDL.SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                _renderer?.RequestResize();
                var (width, height) = PixelSize();
                client.OnResize(width, height);
                break;
            case SDL.SDL_EventType.SDL_EVENT_KEY_DOWN:
                DispatchKey(client, @event.key);
                break;
            case SDL.SDL_EventType.SDL_EVENT_TEXT_INPUT:
                DispatchText(client, @event.text);
                break;
            case SDL.SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                client.OnMouseMove(@event.motion.x, @event.motion.y);
                break;
            case SDL.SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
            case SDL.SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                DispatchMouseButton(client, @event.button);
                break;
            case SDL.SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                if (@event.wheel.y != 0)
                    client.OnMouseWheel(@event.wheel.y);
                break;
        }

        return true;
    }

    /** 左/中/右键按下与释放; 其它鼠标键(X1/X2 等)不产事件。 */
    private static void DispatchMouseButton(IGpuHostClient client, SDL.SDL_MouseButtonEvent button)
    {
        GpuMouseButton kind;
        switch (button.button)
        {
            case SdlButtonLeft:
                kind = GpuMouseButton.Left;
                break;
            case SdlButtonMiddle:
                kind = GpuMouseButton.Middle;
                break;
            case SdlButtonRight:
                kind = GpuMouseButton.Right;
                break;
            default:
                return;
        }

        client.OnMouseButton(kind, button.down, button.x, button.y);
    }

    /** SDL 键盘事件 → ConsoleKeyInfo; 修饰键按 SDL_Keymod 拆分, 与 GLFW 宿主同形。 */
    private static void DispatchKey(IGpuHostClient client, SDL.SDL_KeyboardEvent key)
    {
        if (!TryMapKey(key.key, out var consoleKey))
            return;
        var modifiers = key.mod;
        client.OnKey(new ConsoleKeyInfo(
            '\0',
            consoleKey,
            (modifiers & SDL.SDL_Keymod.SDL_KMOD_SHIFT) != 0,
            (modifiers & SDL.SDL_Keymod.SDL_KMOD_ALT) != 0,
            (modifiers & SDL.SDL_Keymod.SDL_KMOD_CTRL) != 0));
    }

    /** SDL 文本输入(UTF-8) → 逐码元回调(中文/组合键走这里; 与 GLFW TextInput 语义一致)。 */
    private static void DispatchText(IGpuHostClient client, SDL.SDL_TextInputEvent text)
    {
        if (text.text is null)
            return;
        var value = Marshal.PtrToStringUTF8((nint)text.text);
        if (string.IsNullOrEmpty(value))
            return;
        foreach (var character in value)
            client.OnText(character);
    }

    private static bool TryMapKey(SDL.SDL_Keycode key, out ConsoleKey consoleKey)
    {
        var code = (uint)key;
        if (code is >= 0x61 and <= 0x7a)
        {
            consoleKey = ConsoleKey.A + (int)(code - 0x61);
            return true;
        }

        if (code is >= 0x30 and <= 0x39)
        {
            consoleKey = ConsoleKey.D0 + (int)(code - 0x30);
            return true;
        }

        switch (key)
        {
            case SDL.SDL_Keycode.SDLK_RETURN or SDL.SDL_Keycode.SDLK_KP_ENTER:
                consoleKey = ConsoleKey.Enter;
                return true;
            case SDL.SDL_Keycode.SDLK_ESCAPE:
                consoleKey = ConsoleKey.Escape;
                return true;
            case SDL.SDL_Keycode.SDLK_TAB:
                consoleKey = ConsoleKey.Tab;
                return true;
            case SDL.SDL_Keycode.SDLK_BACKSPACE:
                consoleKey = ConsoleKey.Backspace;
                return true;
            case SDL.SDL_Keycode.SDLK_DELETE:
                consoleKey = ConsoleKey.Delete;
                return true;
            case SDL.SDL_Keycode.SDLK_INSERT:
                consoleKey = ConsoleKey.Insert;
                return true;
            case SDL.SDL_Keycode.SDLK_LEFT:
                consoleKey = ConsoleKey.LeftArrow;
                return true;
            case SDL.SDL_Keycode.SDLK_RIGHT:
                consoleKey = ConsoleKey.RightArrow;
                return true;
            case SDL.SDL_Keycode.SDLK_UP:
                consoleKey = ConsoleKey.UpArrow;
                return true;
            case SDL.SDL_Keycode.SDLK_DOWN:
                consoleKey = ConsoleKey.DownArrow;
                return true;
            case SDL.SDL_Keycode.SDLK_HOME:
                consoleKey = ConsoleKey.Home;
                return true;
            case SDL.SDL_Keycode.SDLK_END:
                consoleKey = ConsoleKey.End;
                return true;
            case SDL.SDL_Keycode.SDLK_PAGEUP:
                consoleKey = ConsoleKey.PageUp;
                return true;
            case SDL.SDL_Keycode.SDLK_PAGEDOWN:
                consoleKey = ConsoleKey.PageDown;
                return true;
            case SDL.SDL_Keycode.SDLK_SPACE:
                consoleKey = ConsoleKey.Spacebar;
                return true;
            case SDL.SDL_Keycode.SDLK_F1:
                consoleKey = ConsoleKey.F1;
                return true;
            case SDL.SDL_Keycode.SDLK_F2:
                consoleKey = ConsoleKey.F2;
                return true;
            case SDL.SDL_Keycode.SDLK_F3:
                consoleKey = ConsoleKey.F3;
                return true;
            case SDL.SDL_Keycode.SDLK_F4:
                consoleKey = ConsoleKey.F4;
                return true;
            case SDL.SDL_Keycode.SDLK_F5:
                consoleKey = ConsoleKey.F5;
                return true;
            case SDL.SDL_Keycode.SDLK_F6:
                consoleKey = ConsoleKey.F6;
                return true;
            case SDL.SDL_Keycode.SDLK_F7:
                consoleKey = ConsoleKey.F7;
                return true;
            case SDL.SDL_Keycode.SDLK_F8:
                consoleKey = ConsoleKey.F8;
                return true;
            case SDL.SDL_Keycode.SDLK_F9:
                consoleKey = ConsoleKey.F9;
                return true;
            case SDL.SDL_Keycode.SDLK_F10:
                consoleKey = ConsoleKey.F10;
                return true;
            case SDL.SDL_Keycode.SDLK_F11:
                consoleKey = ConsoleKey.F11;
                return true;
            case SDL.SDL_Keycode.SDLK_F12:
                consoleKey = ConsoleKey.F12;
                return true;
            case SDL.SDL_Keycode.SDLK_F13:
                consoleKey = ConsoleKey.F13;
                return true;
            case SDL.SDL_Keycode.SDLK_F14:
                consoleKey = ConsoleKey.F14;
                return true;
            case SDL.SDL_Keycode.SDLK_F15:
                consoleKey = ConsoleKey.F15;
                return true;
            case SDL.SDL_Keycode.SDLK_F16:
                consoleKey = ConsoleKey.F16;
                return true;
            case SDL.SDL_Keycode.SDLK_F17:
                consoleKey = ConsoleKey.F17;
                return true;
            case SDL.SDL_Keycode.SDLK_F18:
                consoleKey = ConsoleKey.F18;
                return true;
            case SDL.SDL_Keycode.SDLK_F19:
                consoleKey = ConsoleKey.F19;
                return true;
            case SDL.SDL_Keycode.SDLK_F20:
                consoleKey = ConsoleKey.F20;
                return true;
            case SDL.SDL_Keycode.SDLK_F21:
                consoleKey = ConsoleKey.F21;
                return true;
            case SDL.SDL_Keycode.SDLK_F22:
                consoleKey = ConsoleKey.F22;
                return true;
            case SDL.SDL_Keycode.SDLK_F23:
                consoleKey = ConsoleKey.F23;
                return true;
            case SDL.SDL_Keycode.SDLK_F24:
                consoleKey = ConsoleKey.F24;
                return true;
            case SDL.SDL_Keycode.SDLK_KP_0:
                consoleKey = ConsoleKey.NumPad0;
                return true;
            case SDL.SDL_Keycode.SDLK_KP_1:
                consoleKey = ConsoleKey.NumPad1;
                return true;
            case SDL.SDL_Keycode.SDLK_KP_2:
                consoleKey = ConsoleKey.NumPad2;
                return true;
            case SDL.SDL_Keycode.SDLK_KP_3:
                consoleKey = ConsoleKey.NumPad3;
                return true;
            case SDL.SDL_Keycode.SDLK_KP_4:
                consoleKey = ConsoleKey.NumPad4;
                return true;
            case SDL.SDL_Keycode.SDLK_KP_5:
                consoleKey = ConsoleKey.NumPad5;
                return true;
            case SDL.SDL_Keycode.SDLK_KP_6:
                consoleKey = ConsoleKey.NumPad6;
                return true;
            case SDL.SDL_Keycode.SDLK_KP_7:
                consoleKey = ConsoleKey.NumPad7;
                return true;
            case SDL.SDL_Keycode.SDLK_KP_8:
                consoleKey = ConsoleKey.NumPad8;
                return true;
            case SDL.SDL_Keycode.SDLK_KP_9:
                consoleKey = ConsoleKey.NumPad9;
                return true;
            case SDL.SDL_Keycode.SDLK_KP_DECIMAL or SDL.SDL_Keycode.SDLK_KP_PERIOD:
                consoleKey = ConsoleKey.Decimal;
                return true;
            case SDL.SDL_Keycode.SDLK_KP_DIVIDE:
                consoleKey = ConsoleKey.Divide;
                return true;
            case SDL.SDL_Keycode.SDLK_KP_MULTIPLY:
                consoleKey = ConsoleKey.Multiply;
                return true;
            case SDL.SDL_Keycode.SDLK_KP_MINUS:
                consoleKey = ConsoleKey.Subtract;
                return true;
            case SDL.SDL_Keycode.SDLK_KP_PLUS:
                consoleKey = ConsoleKey.Add;
                return true;
            default:
                consoleKey = default;
                return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _renderer?.Dispose();
        if (_gpu.Device.Handle != 0)
            VulkanApi.DeviceWaitIdle(_gpu.Device);
        if (_surface.Handle != 0)
            SDL.SDL3.SDL_Vulkan_DestroySurface((SDL.VkInstance_T*)_gpu.Instance.Handle, (SDL.VkSurfaceKHR_T*)_surface.Handle, null);
        _gpu.Dispose();
        if (_window is not null)
            SDL.SDL3.SDL_DestroyWindow(_window);
    }
}
