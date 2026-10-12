namespace Dsh.Tui.GL;

/**
 * Dsh.Tui 通过反射按约定名(Dsh.Tui.GL.GlGpuHostProvider)发现的 OpenGL 宿主工厂。
 * 放在本程序集而非 Dsh.Tui, 是为了不产生 Dsh.Tui → Dsh.Tui.GL 的循环项目引用,
 * 也让 GLFW/OpenGL/EGL/DRM 特化的代码(窗口 / 裸 TTY GBM-KMS / 离屏 EGL 贴回 / evdev / 裸 TTY 控制台)集中在一处。
 */
public sealed class GlGpuHostProvider : ITuiGpuHostProvider
{
    /** 有显示服务器(或 Windows)走 GLFW 窗口。 */
    public IGlSurfaceHostRunner CreateHost(int pixelWidth, int pixelHeight, bool hidden, bool vsync, string? gpuCard, string? preferredCard)
    {
        if (!OperatingSystem.IsWindows() && !GpuRenderer.TryDetectDisplay(out var displayReason))
            throw new InvalidOperationException($"gl window host unavailable: {displayReason}");
        return new GlfwWindowHost(Math.Max(1, pixelWidth), Math.Max(1, pixelHeight), visible: !hidden, vsync: vsync);
    }

    /**
     * 无显示服务器时的演进顺序与原先 GL 路径一致: 先试 GBM/KMS 裸屏直连, 再试离屏 EGL + 终端图像协议贴回。
     * 都不行时返回 (null, 原因), 由 Dsh.Tui 决定回退(CPU)或报错。
     */
    public (IGlSurfaceHostRunner? Host, string Reason) CreateHeadlessHost(GlyphAtlas atlas, bool headlessCapture, string? gpuCard, string? preferredCard)
    {
        if (EglGbmKmsHost.TryCreate(gpuCard, preferredCard, out var gbmHost, out var drmReason))
            return (gbmHost, "");
        if (TerminalGraphicsHost.TryCreate(atlas, headlessCapture, out var terminalHost, out var terminalReason))
            return (terminalHost, "");
        return (null, $"{drmReason}; {terminalReason}");
    }
}
