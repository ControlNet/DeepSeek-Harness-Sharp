using System.Runtime.InteropServices;

namespace Dsh.Plugins.Native;

/**
 * 原生插件故障遏制入口:包装 native/plugin_guard/guard.c 提供的 SEH(Windows)/sigsetjmp(POSIX)原语,
 * 让一次插件调用引发 AV/ILL/FPE 时被接住而不是终结进程。库名 dsh_plugin_guard(Windows 为 .dll, Unix 为 lib*.so)。
 */
public static unsafe class DshNativeGuard
{
    private const string Library = "dsh_plugin_guard";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dsh_guard_invoke_tool(nint fn, nint context, int handle, byte* input, byte* output, int capacity, out int result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dsh_guard_call_void_int(nint fn, nint context, out int result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int dsh_guard_call_void(nint fn, nint context);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint dsh_guard_suspend();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dsh_guard_resume(nint saved);

    /** 受护地调用工具函数; 返回 false 表示插件内发生故障(未写 result), 调用方应标记 Faulted 并卸载。 */
    public static bool TryInvokeTool(nint fn, nint context, int handle, byte* input, byte* output, int capacity, out int result)
        => dsh_guard_invoke_tool(fn, context, handle, input, output, capacity, out result) == 0;

    /** 受护地调用 int(void*) 函数(如 Activate); 返回 false 表示故障。 */
    public static bool TryCallInt(nint fn, nint context, out int result)
        => dsh_guard_call_void_int(fn, context, out result) == 0;

    /** 受护地调用 void(void*) 函数(如 Deactivate); 返回 false 表示故障。 */
    public static bool TryCallVoid(nint fn, nint context)
        => dsh_guard_call_void(fn, context) == 0;

    /**
     * 插件在受护调用中回调宿主时进入: 暂停本线程的故障跳回点, 让托管代码里的故障交还运行时变成异常。
     * 不暂停的话 siglongjmp 会越过托管帧跳回 guard, 破坏运行时状态。
     */
    public static HostCallbackScope EnterHostCallback() => new(dsh_guard_suspend());

    /** 宿主回调作用域: 释放时恢复进入前的跳回点。 */
    public readonly struct HostCallbackScope(nint saved) : IDisposable
    {
        public void Dispose() => dsh_guard_resume(saved);
    }
}
