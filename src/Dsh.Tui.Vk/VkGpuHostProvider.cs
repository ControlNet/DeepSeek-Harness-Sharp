namespace Dsh.Tui.Vk;

/**
 * Dsh.Tui 通过反射按约定名(Dsh.Tui.Vk.VkGpuHostProvider)发现的 Vulkan 宿主工厂。
 * 放在本程序集而非 Dsh.Tui, 是为了不产生 Dsh.Tui → Dsh.Tui.Vk 的循环项目引用。
 */
public sealed class VkGpuHostProvider : ITuiGpuHostProvider
{
    public IGlSurfaceHostRunner CreateHost(int pixelWidth, int pixelHeight, bool hidden, bool vsync, string? gpuCard, string? preferredCard)
        => VkSurfaceHost.Create(
            "dsharp --gpu (vk)",
            Math.Max(1, pixelWidth),
            Math.Max(1, pixelHeight),
            deviceNameFilter: null,
            hidden: hidden,
            vsync: vsync,
            devicePciSlot: ResolvePciSlot(gpuCard, preferredCard));

    public (IGlSurfaceHostRunner? Host, string Reason) CreateHeadlessHost(GlyphAtlas atlas, bool headlessCapture, string? gpuCard, string? preferredCard)
    {
        var pciSlot = ResolvePciSlot(gpuCard, preferredCard);
        // 无显示服务器时: 若本进程就坐在真实控制台上, 先试 DRM 直连屏(SDL 的 kmsdrm 后端, 经 VK_KHR_display)。
        // 裸控制台没有终端图像协议, 这是它唯一能出 GPU 画面的途径; 经 pts 进来时(见 ConsoleTty)不接管控制台。
        var directConsole = ConsoleTty.IsDirectConsole;
        if (!headlessCapture && directConsole && TryCreateKmsWindow(atlas, pciSlot) is { } kmsHost)
        {
            Console.Error.WriteLine("vk kmsdrm window created (console scanout)");
            return (kmsHost, "");
        }

        // 没走直连屏(截图形态/终端不是控制台/窗口建不起来)时留下判据, 便于解释为什么退到离屏贴回。
        Console.Error.WriteLine($"vk kmsdrm not used: directConsole={directConsole}, headlessCapture={headlessCapture}");

        return VkOffscreenHost.TryCreate(
                atlas,
                deviceNameFilter: null,
                headlessCapture,
                out var host,
                out var reason,
                devicePciSlot: pciSlot)
            ? (host, "")
            : (null, reason);
    }

    /** KMS/DRM 直连屏窗口; 失败(无权限拿 DRM master / 无已连接显示器 / SDL 不支持)返回 null 交给调用方回退离屏贴回。 */
    private static IGlSurfaceHostRunner? TryCreateKmsWindow(GlyphAtlas atlas, string? pciSlot)
    {
        try
        {
            return VkSurfaceHost.Create(
                "dsharp --gpu (vk/kmsdrm)",
                Math.Max(1, Console.WindowWidth) * atlas.GlyphWidth,
                Math.Max(1, Console.WindowHeight) * atlas.GlyphHeight,
                deviceNameFilter: null,
                hidden: false,
                vsync: true,
                devicePciSlot: pciSlot,
                videoDriver: VkSurfaceHost.KmsDrmVideoDriver,
                fullscreenDesktop: true);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"vk kmsdrm unavailable: {error.Message}");
            return null;
        }
    }

    /**
     * 与 GL 侧同一套选卡: CLI 的 --gpu-card 优先, 其次设置里选的卡; 两者都先解析成 DRM 卡节点再读 PCI 地址。
     * 解析不出(选的是 auto / 非 Linux / 链接缺失)时返回 null, 由 Vk 自动选设备。
     */
    private static string? ResolvePciSlot(string? gpuCard, string? preferredCard)
    {
        if (!OperatingSystem.IsLinux())
            return null;
        var cardPath = gpuCard is { Length: > 0 }
            ? GpuCatalog.ResolveDrmCardPath(gpuCard) ?? gpuCard
            : preferredCard;
        return GpuCatalog.ResolveCardPciSlot(cardPath);
    }
}
