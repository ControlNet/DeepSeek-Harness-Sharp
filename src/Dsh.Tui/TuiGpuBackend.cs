using System.Reflection;

namespace Dsh.Tui;

/** GPU 后端选择: auto 优先 Vk, 不可用回退 GL(带可读原因), 再不可用回退 CPU。 */
public enum TuiGpuBackend
{
    Auto,
    Vk,
    Gl,
    Cpu,
}

/**
 * 后端宿主工厂的发现契约: 由 Dsh.Tui.Vk / Dsh.Tui.GL 实现。
 * Dsh.Tui 不能引用后端程序集(否则循环依赖: 后端依赖 Tui), 因此通过反射按约定名加载实现。
 */
public interface ITuiGpuHostProvider
{
    /** 创建"窗口 + 表面(+交换链)"宿主; 失败抛异常并携带可读原因(调用方据此回退或提示)。 */
    IGlSurfaceHostRunner CreateHost(int pixelWidth, int pixelHeight, bool hidden, bool vsync, string? gpuCard, string? preferredCard);

    /**
     * 无显示服务器时的无头宿主(离屏渲染 + 终端图像协议贴回)。
     * 后端不支持或设备不可用时返回 (null, 可读原因); headlessCapture 为真时不要求终端支持图像协议(仅截图/捕获形态)。
     */
    (IGlSurfaceHostRunner? Host, string Reason) CreateHeadlessHost(GlyphAtlas atlas, bool headlessCapture, string? gpuCard, string? preferredCard);
}

/** 解析 tui.gpu.backend 文本值; 未知值按 auto 处理(不因手改配置把界面卡住)。 */
public static class TuiGpuBackendParser
{
    public static TuiGpuBackend Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "vk" or "vulkan" => TuiGpuBackend.Vk,
        "gl" or "opengl" or "glfw" => TuiGpuBackend.Gl,
        "cpu" or "none" or "off" => TuiGpuBackend.Cpu,
        _ => TuiGpuBackend.Auto,
    };
}

/**
 * 后端 provider 的惰性发现: 按约定类型名反射加载; 对应程序集不在本构建里时返回 null, 由调用方回退。
 * 只在后端选择时解析一次(冷路径)。
 */
internal static class GpuHostProviderLocator
{
    private static readonly Lazy<ITuiGpuHostProvider?> Vk = new(() => Resolve("Dsh.Tui.Vk", "Dsh.Tui.Vk.VkGpuHostProvider"));
    private static readonly Lazy<ITuiGpuHostProvider?> Gl = new(() => Resolve("Dsh.Tui.GL", "Dsh.Tui.GL.GlGpuHostProvider"));

    public static ITuiGpuHostProvider? ResolveVk() => Vk.Value;

    public static ITuiGpuHostProvider? ResolveGl() => Gl.Value;

    private static ITuiGpuHostProvider? Resolve(string assemblyName, string typeName)
    {
        try
        {
            var type = Assembly.Load(assemblyName).GetType(typeName, throwOnError: false);
            return type is null ? null : (ITuiGpuHostProvider?)Activator.CreateInstance(type);
        }
        catch (Exception error) when (error is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }
}
