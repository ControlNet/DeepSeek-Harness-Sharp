namespace Dsh.Tui.Vk;

/**
 * Dsh.Tui 通过反射按约定名(Dsh.Tui.Vk.VkGpuHostProvider)发现的 Vulkan 宿主工厂。
 * 放在本程序集而非 Dsh.Tui, 是为了不产生 Dsh.Tui → Dsh.Tui.Vk 的循环项目引用。
 */
public sealed class VkGpuHostProvider : ITuiGpuHostProvider
{
    public IGlSurfaceHostRunner CreateHost(int pixelWidth, int pixelHeight, bool hidden, bool vsync)
        => VkSurfaceHost.Create(
            "dsharp --gpu (vk)",
            Math.Max(1, pixelWidth),
            Math.Max(1, pixelHeight),
            deviceNameFilter: null,
            hidden: hidden,
            vsync: vsync);

    public IGlSurfaceHostRunner? CreateHeadlessHost(GlyphAtlas atlas, bool headlessCapture, out string reason)
        => VkOffscreenHost.TryCreate(atlas, deviceNameFilter: null, headlessCapture, out var host, out reason) ? host : null;
}
