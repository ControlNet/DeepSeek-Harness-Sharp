namespace Dsh.Tui.Vk;

/**
 * 进程级一次性入口: 在首次创建 Vulkan 实例之前调用(见 VkGpuDevice)。
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
            var repair = new VkLinuxEnvironmentRepair(new VkRepairRealIo());
            _outcome = repair.EnsureReady();
            _repair = repair;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _repair?.Dispose();
            return _outcome.Value;
        }
    }
}
