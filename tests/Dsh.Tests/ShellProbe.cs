using System.Diagnostics;

namespace Dsh.Tests;

/** 外部 shell 可用性探测：WSL/Git Bash/pwsh 缺失或无响应时让依赖它的用例跳过而不是挂死。结果进程级缓存。 */
internal static class ShellProbe
{
    private const int ProbeTimeoutMs = 5000;

    private static readonly Lazy<bool> BashAvailable = new(() => Probe("bash", "-c \"exit 0\""));
    private static readonly Lazy<bool> PwshAvailable = new(() => Probe("pwsh", "-NoLogo -NoProfile -NonInteractive -Command exit 0"));

    public static bool Bash => BashAvailable.Value;

    public static bool Pwsh => PwshAvailable.Value;

    private static bool Probe(string executable, string arguments)
    {
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return process is not null && process.WaitForExit(ProbeTimeoutMs) && process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            // 探测超时(如本机 WSL 挂死)时子进程仍在运行, 必须连子进程树杀掉, 否则会一直占着句柄、拖住整个测试进程。
            try
            {
                if (process is { HasExited: false })
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 进程可能已退出或无权终止: 探测结果已定, 这里不再抛出。
            }
            process?.Dispose();
        }
    }
}
