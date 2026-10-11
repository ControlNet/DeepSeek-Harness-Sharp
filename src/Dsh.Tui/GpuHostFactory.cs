namespace Dsh.Tui;

/**
 * GPU 形态的宿主选择: 有显示服务器>GLFW 窗口; 无显示服务器且有 DRM>GBM/KMS 直连屏;
 * 否则>离屏 EGL + kitty graphics 贴回终端(无 DRM/无显示也能交互渲染 GPU 画面; 仅截图/捕获时不要求终端支持图像协议)。
 * 按环境选择只在创建时做一次; 宿主选择失败以异常携带原因, 由 TuiRunner 提示并回退。
 */
internal static class GpuHostFactory
{
    public static IGlSurfaceHostRunner CreateWindowHost(
        GlyphAtlas atlas,
        string? gpuCard = null,
        string? preferredCard = null,
        bool hidden = false,
        bool vsync = true,
        bool headlessCapture = false)
    {
        if (OperatingSystem.IsWindows() || GpuRenderer.TryDetectDisplay(out var displayReason))
            return new GlfwWindowHost(80 * atlas.GlyphWidth, 25 * atlas.GlyphHeight, visible: !hidden, vsync: vsync);
        if (EglGbmKmsHost.TryCreate(gpuCard, preferredCard, out var gbmHost, out var drmReason))
            return gbmHost;
        if (TerminalGraphicsHost.TryCreate(atlas, headlessCapture, out var terminalHost, out var terminalReason))
            return terminalHost;
        throw new InvalidOperationException($"{displayReason}; {drmReason}; {terminalReason}");
    }

    /**
     * 按选择的后端创建宿主。Vk: 反射发现 Dsh.Tui.Vk 的 provider(不可用时抛异常, 不静默);
     * auto: 先试 Vk, 失败打印可读原因并回退 GL; gl: 走既有 GL/GBM/Terminal 选择。
     */
    public static IGlSurfaceHostRunner CreateWindowHostForBackend(
        TuiGpuBackend backend,
        GlyphAtlas atlas,
        string? gpuCard,
        string? preferredCard,
        bool hidden,
        bool vsync,
        bool headlessCapture)
    {
        if (backend is TuiGpuBackend.Vk or TuiGpuBackend.Auto)
        {
            var provider = VkGpuHostProviderLocator.Resolve();
            if (provider is null)
            {
                if (backend == TuiGpuBackend.Vk)
                    throw new InvalidOperationException("vk backend requested but Dsh.Tui.Vk is not available in this build");
            }
            else if (OperatingSystem.IsWindows() || GpuRenderer.TryDetectDisplay(out _))
            {
                try
                {
                    return provider.CreateHost(80 * atlas.GlyphWidth, 25 * atlas.GlyphHeight, hidden, vsync);
                }
                catch (Exception error) when (backend == TuiGpuBackend.Auto)
                {
                    Console.Error.WriteLine($"vk backend unavailable: {error.Message}; falling back to gl");
                }
            }
            else
            {
                // 无显示服务器: 窗口/交换链形态不可用, 走无头离屏 + 终端图像协议贴回。
                var headless = provider.CreateHeadlessHost(atlas, headlessCapture, out var headlessReason);
                if (headless is not null)
                    return headless;
                if (backend == TuiGpuBackend.Vk)
                    throw new InvalidOperationException($"vk headless unavailable: {headlessReason}");
                Console.Error.WriteLine($"vk backend unavailable: {headlessReason}; falling back to gl");
            }
        }

        return CreateWindowHost(atlas, gpuCard, preferredCard, hidden, vsync, headlessCapture);
    }
}
