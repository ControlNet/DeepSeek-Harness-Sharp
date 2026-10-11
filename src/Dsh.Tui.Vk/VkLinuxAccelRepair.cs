namespace Dsh.Tui.Vk;

/**
 * 用户可见的自愈开关。自愈是 best-effort: 硬件 Vulkan 已可用时它什么都不做; 只有"无硬件设备 + 存在 NVIDIA ICD"
 * 时才尝试补符号 shim / 起虚拟 X。若要在首次创建 Vulkan 设备之前彻底关闭, 把 Enabled 置 false。
 * 注意: 不使用环境变量作为配置项(项目约定)。
 */
public static class VkLinuxAccelRepair
{
    public static bool Enabled { get; set; } = true;
}

/**
 * 进程级一次性入口: 在首次创建 Vulkan 实例之前调用。
 * 非 Linux 直接返回(快路径 no-op); Linux 上只跑一次, 结果记入静态字段, 后续调用零成本。
 */
internal static class VkLinuxEnvironmentBootstrap
{
    private static readonly Lock Gate = new();
    private static VkRepairOutcome? _outcome;
    private static VkLinuxEnvironmentRepair? _repair;

    public static VkRepairOutcome EnsureReady()
    {
        if (!OperatingSystem.IsLinux())
            return new VkRepairOutcome(VkRepairStatus.NotApplicable, "非 Linux 平台, 环境自愈不适用");

        lock (Gate)
        {
            if (_outcome is { } settled)
                return settled;
            var repair = new VkLinuxEnvironmentRepair(new VkRepairRealIo(), VkLinuxAccelRepair.Enabled);
            _outcome = repair.EnsureReady();
            _repair = repair;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _repair?.Dispose();
            return _outcome.Value;
        }
    }
}
