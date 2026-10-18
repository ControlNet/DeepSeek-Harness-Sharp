using Dsh.Runtime;
using Dsh.Core;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Tools.Shell.Plugin.ToolBash)]
[assembly: DshPlugin(Dsh.Tools.Shell.Plugin.Subprocess)]
[assembly: DshPlugin(Dsh.Tools.Shell.Plugin.ToolPwsh)]
[assembly: DshPlugin(Dsh.Tools.Shell.Plugin.ToolPowershell)]
[assembly: DshPlugin(Dsh.Tools.Shell.Plugin.ToolWsl)]
[assembly: DshPlugin(Dsh.Tools.Shell.Plugin.ToolBashPersistent)]
[assembly: DshPlugin(Dsh.Tools.Shell.Plugin.ToolPwshPersistent)]

namespace Dsh.Tools.Shell;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string ToolBash = "@deepseek-ai/dsh-tool-bash.shell";
    internal const string Subprocess = "@deepseek-ai/dsh-subprocess.shell";
    internal const string ToolPwsh = "@deepseek-ai/dsh-tool-pwsh.shell";
    internal const string ToolPowershell = "@deepseek-ai/dsh-tool-powershell.shell";
    internal const string ToolWsl = "@deepseek-ai/dsh-tool-wsl.shell";
    internal const string ToolBashPersistent = "@deepseek-ai/dsh-tool-bash.shell-persistent";
    internal const string ToolPwshPersistent = "@deepseek-ai/dsh-tool-pwsh.shell-persistent";

    public string[] Inject => packageName switch
    {
        Subprocess => [],
        ToolBash or ToolPwsh or ToolPowershell or ToolWsl => [ToolRuntime.ServiceName, SystemPrompt.ServiceName, SubprocessService.ServiceName],
        ToolBashPersistent or ToolPwshPersistent => [ToolRuntime.ServiceName, SubprocessService.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Subprocess => RegisterSubprocess(ctx),
        ToolBash => BashTool.Register(ctx),
        ToolPwsh => PwshTool.Register(ctx),
        ToolPowershell => WindowsPowerShellTool.Register(ctx),
        ToolWsl => WslTool.Register(ctx),
        ToolBashPersistent => PersistentBashTool.Register(ctx, PersistentBashConfigFrom(config)),
        ToolPwshPersistent => PersistentPwshTool.Register(ctx, PersistentPwshConfigFrom(config)),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable RegisterSubprocess(Context ctx)
    {
        _ = new SubprocessService(ctx);
        return new CompositeDisposable();
    }

    private static IReadOnlyDictionary<string, object?>? ConfigOf(object? config)
        => config as IReadOnlyDictionary<string, object?>;

    private static PersistentBashConfig PersistentBashConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new PersistentBashConfig
        {
            BashPath = dict?.GetValueOrDefault("bashPath") as string,
            TimeoutMs = LongOf(dict, "timeoutMs") ?? new PersistentBashConfig().TimeoutMs,
            MaxOutputChars = IntOf(dict, "maxOutputChars") ?? new PersistentBashConfig().MaxOutputChars,
            Description = dict?.GetValueOrDefault("description") as string ?? PersistentBashConfig.DefaultDescription,
        };
    }

    private static PersistentPwshConfig PersistentPwshConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new PersistentPwshConfig
        {
            TimeoutMs = LongOf(dict, "timeoutMs") ?? new PersistentPwshConfig().TimeoutMs,
            MaxOutputChars = IntOf(dict, "maxOutputChars") ?? new PersistentPwshConfig().MaxOutputChars,
            Description = dict?.GetValueOrDefault("description") as string ?? PersistentPwshConfig.DefaultDescription,
        };
    }

    private static int? IntOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            long value => (int)value,
            int value => value,
            _ => null,
        };

    private static long? LongOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            long value => value,
            int value => value,
            _ => null,
        };
}
