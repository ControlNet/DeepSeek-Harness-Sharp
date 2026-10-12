namespace Dsh.Tui;

/**
 * GPU 宿主选择(与后端无关的调度): 按 tui.gpu.backend 选后端插件, 再由插件自己在"窗口形态"与"无头形态"之间选择。
 * GL / Vk 各自在自己的程序集里(经反射按约定名发现), Dsh.Tui 不引用它们; 两者都不可用时抛异常携带原因,
 * 由 TuiRunner 提示并回退 CPU 渲染。
 */
internal static class GpuHostFactory
{
    /**
     * 按选择的后端创建宿主。vk: 反射发现 Dsh.Tui.Vk 的 provider(显式请求而不可用时抛异常, 不静默);
     * auto: 先试 Vk, 失败打印可读原因并回退 GL; gl: 走 Dsh.Tui.GL 的 provider。
     * 有显示服务器要窗口; 没显示时交给后端自己的无头路径(GBM/KMS 裸屏 或 离屏 EGL/Vk + 终端图像协议贴回)。
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
            var host = TryCreateVkHost(backend, atlas, gpuCard, preferredCard, hidden, vsync, headlessCapture);
            if (host is not null)
                return host;
        }

        return CreateGlHost(atlas, gpuCard, preferredCard, hidden, vsync, headlessCapture);
    }

    /** Vk 可用则返回宿主; auto 下不可用返回 null(打印原因, 交给 GL 回退), 显式 vk 下直接抛异常。 */
    private static IGlSurfaceHostRunner? TryCreateVkHost(
        TuiGpuBackend backend,
        GlyphAtlas atlas,
        string? gpuCard,
        string? preferredCard,
        bool hidden,
        bool vsync,
        bool headlessCapture)
    {
        var provider = GpuHostProviderLocator.ResolveVk();
        if (provider is null)
        {
            if (backend == TuiGpuBackend.Vk)
                throw new InvalidOperationException("vk backend requested but Dsh.Tui.Vk is not available in this build");
            return null;
        }

        if (OperatingSystem.IsWindows() || GpuRenderer.TryDetectDisplay(out _))
        {
            try
            {
                return provider.CreateHost(80 * atlas.GlyphWidth, 25 * atlas.GlyphHeight, hidden, vsync, gpuCard, preferredCard);
            }
            catch (Exception error) when (backend == TuiGpuBackend.Auto)
            {
                Console.Error.WriteLine($"vk backend unavailable: {error.Message}; falling back to gl");
                return null;
            }
        }

        // 无显示服务器: 窗口/交换链形态不可用, 走无头离屏 + 终端图像协议贴回。
        var (headless, reason) = provider.CreateHeadlessHost(atlas, headlessCapture, gpuCard, preferredCard);
        if (headless is not null)
            return headless;
        if (backend == TuiGpuBackend.Vk)
            throw new InvalidOperationException($"vk headless unavailable: {reason}");
        Console.Error.WriteLine($"vk backend unavailable: {reason}; falling back to gl");
        return null;
    }

    /** GL 后端(Dsh.Tui.GL): 有显示走 GLFW 窗口, 否则由 GL 插件在 GBM/KMS 裸屏与离屏 EGL 贴回之间自己选。 */
    private static IGlSurfaceHostRunner CreateGlHost(
        GlyphAtlas atlas,
        string? gpuCard,
        string? preferredCard,
        bool hidden,
        bool vsync,
        bool headlessCapture)
    {
        var provider = GpuHostProviderLocator.ResolveGl();
        if (provider is null)
            throw new InvalidOperationException("gl backend requested but Dsh.Tui.GL is not available in this build");

        if (OperatingSystem.IsWindows() || GpuRenderer.TryDetectDisplay(out _))
            return provider.CreateHost(80 * atlas.GlyphWidth, 25 * atlas.GlyphHeight, hidden, vsync, gpuCard, preferredCard);

        var (headless, reason) = provider.CreateHeadlessHost(atlas, headlessCapture, gpuCard, preferredCard);
        return headless ?? throw new InvalidOperationException($"gl headless unavailable: {reason}");
    }
}
