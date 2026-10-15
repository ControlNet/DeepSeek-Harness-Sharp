using System.Runtime.InteropServices;
using System.Text;

namespace Dsh.Plugins.Native;

/**
 * 原生插件 ABI v1:宿主与插件用 C 调用约定交换函数表;字符串统一为 UTF-8。
 * 以指针传入的字符串(工具输入、日志、工具登记)以 NUL 结尾;两段式缓冲输出不带终止符,长度由返回值给出。
 */
public static class DshNativePluginAbi
{
    public const int Version = 1;
    public const string EntryPoint = "dsh_plugin_entry";
    public const string PackageExport = "dsh_plugin_package";
    /** 可选的独立版本导出:插件声明自身 ABI 版本,使宿主在握手前就能精确区分“版本不符”与“其它拒绝”。 */
    public const string VersionExport = "dsh_plugin_abi_version";
    /** 可选的配置面导出:插件声明自己认哪些参数(JSON),宿主据此把参数传下去并纳入文档。 */
    public const string SettingsSchemaExport = "dsh_plugin_settings_schema";
    /** 配置回传缓冲上限:超出即视为插件不认这份配置,宿主只记 WARN(避免无界重试)。 */
    public const int ConfigCapacityHint = 64 * 1024;
    /** 宿主首次调用工具时提供的输出缓冲:多数结果一次放下,超出时按插件报的长度再取一次。 */
    public const int InvokeCapacityHint = 64 * 1024;

    public const int Ok = 0;
    public const int Error = -1;
    public const int LogDebug = 0;
    public const int LogInfo = 1;
    public const int LogWarn = 2;
    public const int LogError = 3;
}

/** 宿主侧握手探测结果:非静默诊断「缺导出符号 / ABI 版本不符 / 被拒」。 */
public enum DshNativeHandshake
{
    Ok = 0,
    MissingExports = 1,
    VersionMismatch = 2,
    Rejected = 3,
}

/** 标注函数表字段在协议中的角色:两侧胶水由生成器按角色产出,不靠字段名约定。 */
[AttributeUsage(AttributeTargets.Field)]
public sealed class DshAbiRoleAttribute(string role) : Attribute
{
    public string Role { get; } = role;
}

/** ABI v1 的字段角色名。 */
public static class DshAbiRoles
{
    public const string Package = "package";
    public const string Activate = "activate";
    public const string Deactivate = "deactivate";
    public const string InvokeTool = "invoke_tool";
    public const string Log = "log";
    public const string RegisterTool = "register_tool";
    /** 运行期反注册(ABI v1 的可选回调):插件按句柄撤销已登记的工具。 */
    public const string UnregisterTool = "unregister_tool";
    /** 参数回传(可选回调):插件按"两段式缓冲"取自己那份 plugins.<包名> 配置(JSON)。 */
    public const string GetConfig = "get_config";
}

/** 宿主提供给插件的函数表;Context 是不透明句柄,插件只负责原样回传。 */
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DshHostApi
{
    public int AbiVersion;
    public void* Context;
    [DshAbiRole(DshAbiRoles.Log)]
    public delegate* unmanaged[Cdecl]<void*, int, byte*, void> Log;
    [DshAbiRole(DshAbiRoles.RegisterTool)]
    public delegate* unmanaged[Cdecl]<void*, byte*, byte*, byte*, int, int> RegisterTool;
    /** 可选回调:旧宿主此指针为 null,插件据此判断能否在运行期反注册。 */
    [DshAbiRole(DshAbiRoles.UnregisterTool)]
    public delegate* unmanaged[Cdecl]<void*, int, int> UnregisterTool;
    /**
     * 可选回调(追加在末尾,旧插件只读前缀所以不受影响):读取该插件在 settings.yaml 里的参数(JSON)。
     * 两段式缓冲:output 为 null 或容量不足时只返回所需字节数,否则写出内容并返回写入长度;无配置返回 0。
     */
    [DshAbiRole(DshAbiRoles.GetConfig)]
    public delegate* unmanaged[Cdecl]<void*, byte*, int, int> GetConfig;
}

/** 插件提供给宿主的函数表;Package 指向插件内静态 UTF-8 缓冲区,插件卸载前一直有效。 */
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DshPluginApi
{
    public int AbiVersion;
    [DshAbiRole(DshAbiRoles.Package)]
    public byte* Package;
    [DshAbiRole(DshAbiRoles.Activate)]
    public delegate* unmanaged[Cdecl]<void*, int> Activate;
    [DshAbiRole(DshAbiRoles.Deactivate)]
    public delegate* unmanaged[Cdecl]<void*, void> Deactivate;
    [DshAbiRole(DshAbiRoles.InvokeTool)]
    public delegate* unmanaged[Cdecl]<void*, int, byte*, byte*, int, int> InvokeTool;
}

/** 插件侧运行期工具登记入口:生成器在激活时挂上,作者可随时增删工具。 */
public interface INativeToolRegistry
{
    /** 登记一个工具,返回宿主句柄(供之后 UnregisterTool);失败返回 0。 */
    int RegisterTool(string name, string description, string parametersJson, Func<string, string?> invoke);

    /** 撤销一个已登记工具;返回 DshNativePluginAbi.Ok / Error。 */
    int UnregisterTool(int handle);
}

/** 插件侧工具注册与调度的便利包装:插件在 Activate 里注册工具,由 Dispatch 派发调用。 */
public sealed unsafe class NativePluginContext(DshHostApi* host, void* context) : INativeToolRegistry
{
    private readonly Dictionary<int, Func<string, string?>> _handlers = [];
    private int _nextHandle;

    [ThreadStatic]
    private static RetainedResult? _retained;

    public void Log(int level, string message)
    {
        fixed (byte* pointer = NativeUtf8.Terminated(message))
            host->Log(context, level, pointer);
    }

    public int RegisterTool(string name, string description, string parametersJson, Func<string, string?> invoke)
    {
        var handle = Interlocked.Increment(ref _nextHandle);
        _handlers[handle] = invoke;
        int result;
        fixed (byte* namePointer = NativeUtf8.Terminated(name))
        fixed (byte* descriptionPointer = NativeUtf8.Terminated(description))
        fixed (byte* parametersPointer = NativeUtf8.Terminated(parametersJson))
            result = host->RegisterTool(context, namePointer, descriptionPointer, parametersPointer, handle);
        if (result != DshNativePluginAbi.Ok)
        {
            _handlers.Remove(handle);
            return 0;
        }
        return handle;
    }

    public int UnregisterTool(int handle)
    {
        if (host->UnregisterTool is null)
            return DshNativePluginAbi.Error;
        var result = host->UnregisterTool(context, handle);
        if (result == DshNativePluginAbi.Ok)
            _handlers.Remove(handle);
        return result;
    }

    public string? Dispatch(int handle, string inputJson)
        => _handlers.TryGetValue(handle, out var invoke) ? invoke(inputJson) : null;

    /**
     * 两段式工具调用:结果放不下时只返回所需字节数,并在本线程保留结果。
     * 宿主随即带同样的 handle/input 与够大的缓冲再调一次时直接交付保留结果,工具不重复执行。
     */
    public int InvokeTool(int handle, byte* input, byte* output, int capacity)
    {
        var inputJson = NativeUtf8.Read(input);
        var retained = _retained;
        _retained = null;
        var bytes = retained is { } kept && kept.Handle == handle && kept.Input == inputJson
            ? kept.Bytes
            : Encoding.UTF8.GetBytes(Dispatch(handle, inputJson) ?? "");
        if (output is not null && capacity >= bytes.Length)
        {
            bytes.CopyTo(new Span<byte>(output, capacity));
            return bytes.Length;
        }
        _retained = new RetainedResult(handle, inputJson, bytes);
        return bytes.Length;
    }

    private readonly record struct RetainedResult(int Handle, string Input, byte[] Bytes);

    /** 该插件在 settings.yaml 里的参数(JSON 文本);宿主不支持或没有配置时返回空串。 */
    public string ConfigJson()
    {
        if (host->GetConfig is null)
            return "";
        var required = host->GetConfig(context, null, 0);
        if (required <= 0 || required > DshNativePluginAbi.ConfigCapacityHint)
            return "";
        var buffer = new byte[required];
        fixed (byte* pointer = buffer)
        {
            var written = host->GetConfig(context, pointer, required);
            return written <= 0 ? "" : Encoding.UTF8.GetString(buffer, 0, written);
        }
    }
}

/** 插件侧 UTF-8 互操作助手。 */
public static unsafe class NativeUtf8
{
    public static string Read(byte* pointer)
    {
        if (pointer is null)
            return "";
        var length = 0;
        while (pointer[length] != 0)
            length++;
        return Encoding.UTF8.GetString(pointer, length);
    }

    /** 以指针传给对端的字符串:UTF-8 并补 NUL 终止符,对端按终止符读取。 */
    public static byte[] Terminated(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    /** 常驻字符串(如包名):分配一次并固定,供导出函数返回稳定指针。 */
    public static byte* Persistent(string value)
        => (byte*)Marshal.StringToCoTaskMemUTF8(value);

    /** 两段式缓冲协议:output 为 null 或容量不足时只返回所需字节数,否则写出内容并返回写入长度。 */
    public static int Write(string? value, byte* output, int capacity)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        if (output is null || capacity < bytes.Length)
            return bytes.Length;
        for (var index = 0; index < bytes.Length; index++)
            output[index] = bytes[index];
        return bytes.Length;
    }
}
