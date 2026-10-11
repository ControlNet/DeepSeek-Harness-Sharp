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
 * 后端宿主工厂的发现契约: 由 Dsh.Tui.Vk 实现。
 * Dsh.Tui 不能引用 Dsh.Tui.Vk(否则循环依赖: Vk 依赖 Tui), 因此通过反射按约定名加载实现。
 */
public interface ITuiGpuHostProvider
{
    /** 创建"窗口 + 表面(+交换链)"宿主; 失败抛异常并携带可读原因(调用方据此回退或提示)。 */
    IGlSurfaceHostRunner CreateHost(int pixelWidth, int pixelHeight, bool hidden, bool vsync);

    /**
     * 创建无显示服务器时的无头宿主(离屏渲染 + 终端图像协议贴回); 后端不支持或设备不可用时返回 null 并给出原因。
     * headlessCapture 为真时不要求终端支持图像协议(仅截图/捕获形态)。
     */
    IGlSurfaceHostRunner? CreateHeadlessHost(GlyphAtlas atlas, bool headlessCapture, out string reason);
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
 * Dsh.Tui.Vk 的惰性发现: 按约定类型名反射加载 provider; 插件不在本构建里时返回 null, 由调用方回退 GL。
 * 只在后端选择时解析一次(冷路径)。
 */
internal static class VkGpuHostProviderLocator
{
    private const string AssemblyName = "Dsh.Tui.Vk";
    private const string ProviderTypeName = "Dsh.Tui.Vk.VkGpuHostProvider";

    private static ITuiGpuHostProvider? _provider;
    private static bool _resolved;

    public static ITuiGpuHostProvider? Resolve()
    {
        if (_resolved)
            return _provider;
        _resolved = true;
        try
        {
            var assembly = Assembly.Load(AssemblyName);
            var type = assembly.GetType(ProviderTypeName, throwOnError: false);
            _provider = type is null ? null : (ITuiGpuHostProvider?)Activator.CreateInstance(type);
        }
        catch (FileNotFoundException)
        {
            _provider = null;
        }
        catch (FileLoadException)
        {
            _provider = null;
        }
        catch (BadImageFormatException)
        {
            _provider = null;
        }

        return _provider;
    }
}
