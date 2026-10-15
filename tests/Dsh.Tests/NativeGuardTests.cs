using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dsh.Plugins.Native;

namespace Dsh.Tests;

/**
 * §9/D12 信号级遏制回归: 通过 dsh_plugin_guard(SEH/sigsetjmp) 调用一个真实的、会访问违例的原生函数
 * (dsh_fault_probe_boom), 断言故障被接住、进程存活、正常调用不受影响。探针/guard 缺失(无 C 工具链)时跳过。
 */
public sealed unsafe class NativeGuardTests
{
    [Fact]
    public void Guard_TrapsNativeAccessViolation_AndProcessSurvives()
    {
        var probe = LoadProbe();
        var ok = NativeLibrary.GetExport(probe, "dsh_fault_probe_ok");
        var boom = NativeLibrary.GetExport(probe, "dsh_fault_probe_boom");
        try
        {
            Assert.True(DshNativeGuard.TryInvokeTool(ok, 0, 0, null, null, 0, out var value));
            Assert.Equal(42, value);

            Assert.False(DshNativeGuard.TryInvokeTool(boom, 0, 0, null, null, 0, out _));

            // 故障被接住后进程仍在: 正常调用继续可用。
            Assert.True(DshNativeGuard.TryInvokeTool(ok, 0, 0, null, null, 0, out var after));
            Assert.Equal(42, after);
        }
        finally
        {
            NativeLibrary.Free(probe);
        }
    }

    /** guard 接管故障信号后, 受护调用之外的托管空引用仍要交回运行时变成 NullReferenceException, 而不是让进程退出。 */
    [Fact]
    public void Guard_LeavesManagedNullReferenceToRuntime_AfterGuardedCalls()
    {
        var probe = LoadProbe();
        try
        {
            Assert.True(DshNativeGuard.TryInvokeTool(NativeLibrary.GetExport(probe, "dsh_fault_probe_ok"), 0, 0, null, null, 0, out _));
            Assert.False(DshNativeGuard.TryInvokeTool(NativeLibrary.GetExport(probe, "dsh_fault_probe_boom"), 0, 0, null, null, 0, out _));

            Assert.Throws<NullReferenceException>(() => LengthOf(null));
            Assert.Throws<NullReferenceException>(() => LengthOf(null));
        }
        finally
        {
            NativeLibrary.Free(probe);
        }
    }

    /** 受护调用里回调进宿主托管代码时, 托管故障仍由运行时转成异常, guard 不会跨托管帧跳走。 */
    [Fact]
    public void Guard_HostCallbackInsideGuardedCall_KeepsManagedExceptions()
    {
        var probe = LoadProbe();
        try
        {
            var callback = (nint)(delegate* unmanaged[Cdecl]<int, int>)&CatchNullReference;
            Assert.True(DshNativeGuard.TryInvokeTool(NativeLibrary.GetExport(probe, "dsh_fault_probe_callback"), callback, 7, null, null, 0, out var value));
            Assert.Equal(7, value);
        }
        finally
        {
            NativeLibrary.Free(probe);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CatchNullReference(int handle)
    {
        using var scope = DshNativeGuard.EnterHostCallback();
        try
        {
            return LengthOf(null) + handle;
        }
        catch (NullReferenceException)
        {
            return handle;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int LengthOf(string? text) => text!.Length;

    private static nint LoadProbe()
    {
        if (!NativeLibrary.TryLoad("dsh_fault_probe", typeof(NativeGuardTests).Assembly, DllImportSearchPath.AssemblyDirectory, out var probe))
            Assert.Skip("dsh_fault_probe 未构建(缺 C 工具链), 跳过原生遏制回归");
        return probe;
    }
}
