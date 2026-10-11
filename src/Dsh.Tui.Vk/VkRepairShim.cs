using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Dsh.Tui.Vk;

/**
 * 内嵌的符号 shim C 源码与缓存命名。
 * 背景: 宿主驱动用户态(Ubuntu 20.04 / glibc 2.31 构建)被只读 bind-mount 进较新的 glibc 容器(如 22.04 / 2.35)后,
 * NVIDIA 图形库(libnvidia-glsi / libGLX_nvidia)在加载期引用了容器 userland 里不存在的 7 个符号, dlopen 失败。
 * 只提供这 7 个符号即可让整条驱动库链加载成功; 离屏 Vulkan 路径不会真正调用它们。
 * 库名带上源码+架构的哈希, 用于跨进程复用已编译产物、避免重复编译。
 */
internal static class VkRepairShim
{
    public const string SourceFileName = "shim.c";

    /** 4 个 glibc 2.34 已删除的 malloc 钩子(数据符号) + 3 个 X server 内部符号(函数存根)。 */
    public const string Source =
        """
        /* dsh vk-accel shim —— 由 Dsh.Tui.Vk 内嵌字符串生成, 请勿手改。
         * 目的: 让宿主(20.04/glibc2.31)构建、bind-mount 进较新 glibc 容器的 NVIDIA 图形用户态库完成加载期符号解析。
         * 4 个 malloc 钩子在 glibc 2.34 已被删除, 这里定义为数据符号; 3 个 X server 内部符号定义为返回 0 的存根。
         * 离屏 Vulkan 路径不会真正调用这些符号。
         */
        #include <stddef.h>

        typedef void *(*dsh_malloc_hook_t)(size_t, const void *);
        typedef void *(*dsh_realloc_hook_t)(void *, size_t, const void *);
        typedef void (*dsh_free_hook_t)(void *, const void *);
        typedef void *(*dsh_memalign_hook_t)(size_t, size_t, const void *);

        dsh_malloc_hook_t __malloc_hook = 0;
        dsh_realloc_hook_t __realloc_hook = 0;
        dsh_free_hook_t __free_hook = 0;
        dsh_memalign_hook_t __memalign_hook = 0;

        int xf86ProcessOptions(void) { return 0; }

        void ErrorF(const char *format, ...) { (void)format; }

        void *miCreateDefColormap(void) { return 0; }
        """;

    public static string LibraryFileName { get; } = $"shim-{ComputeHash()}.so";

    private static string ComputeHash()
    {
        var payload = $"{Source}|{RuntimeInformation.ProcessArchitecture}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(digest)[..16].ToLowerInvariant();
    }
}
