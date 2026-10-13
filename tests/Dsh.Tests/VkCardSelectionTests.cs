using Dsh.Tui;
using Dsh.Tui.Vk;

namespace Dsh.Tests;

/**
 * Vk 侧选卡: GpuCatalog 的选卡值 → PCI 槽位 → VK_EXT_pci_bus_info 匹配(同型号多卡只能靠 PCI 区分)。
 * 需要真实 Vulkan 设备; 驱动不提供 PCI 扩展时按环境跳过。
 */
public sealed class VkCardSelectionTests
{
    [Fact]
    public void FormatPciSlot_Follows_Sysfs_Layout()
    {
        Assert.Equal("0000:11:00.0", VkGpuDevice.FormatPciSlot(0, 0x11, 0, 0));
        Assert.Equal("0000:03:1f.1", VkGpuDevice.FormatPciSlot(0, 0x03, 0x1f, 1));
        Assert.Equal("0001:ab:cd.e", VkGpuDevice.FormatPciSlot(1, 0xab, 0xcd, 0x0e));
    }

    /** 按 PCI 槽位选卡必须选到"就是那块卡"; 认不出的槽位要回退而不是抛异常。 */
    [Fact]
    [Trait("Category", "OnlyGpu")]
    public void SelectByPciSlot_PicksThatDevice_AndFallsBackWhenUnknown()
    {
        string? slot;
        using (var plain = VkGpuDevice.Create())
        {
            slot = plain.PciSlot;
        }

        if (slot is null)
        {
            Assert.Skip("本机 Vulkan 驱动未提供 VK_EXT_pci_bus_info, 无法按 PCI 选卡");
            return;
        }

        // 用同一个槽位再选一次: 必须命中同一个 PCI 地址。
        using (var picked = VkGpuDevice.Create(devicePciSlot: slot))
            Assert.Equal(slot, picked.PciSlot);

        // 不存在的槽位: 回退到实际设备, 不抛异常(与 GL 侧"选卡不可用就回退扫描"一致)。
        using var unknown = VkGpuDevice.Create(devicePciSlot: "ffff:ff:ff.f");
        Assert.NotNull(unknown.PciSlot);
    }

    /** Vk 报出的 PCI 地址必须与 sysfs(/sys/class/drm/cardN/device)同一套写法, 否则选卡永远匹配不上。 */
    [Fact]
    public void DevicePciSlot_Matches_SysfsConvention()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("只有 Linux 用 sysfs 选卡");
            return;
        }

        using var gpu = VkGpuDevice.Create();
        if (gpu.PciSlot is null)
        {
            Assert.Skip("本机 Vulkan 驱动未提供 VK_EXT_pci_bus_info");
            return;
        }

        var sysfsSlots = Directory.EnumerateDirectories("/sys/class/drm", "card[0-9]*")
            .Select(GpuCatalog.ResolveCardPciSlot)
            .Where(slot => slot is not null)
            .ToList();
        Assert.Contains(gpu.PciSlot, sysfsSlots);
    }
}
