using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.IdeHistory.Plugin.IdeHistory)]

namespace Dsh.IdeHistory;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string IdeHistory = "@deepseek-ai/dsh-ide-history";

    public string[] Inject => packageName switch
    {
        IdeHistory => [ToolRuntime.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config)
    {
        var registry = IdeHistoryRegistry.CreateDefault();
        // 服务自己向 ctx 注册(IIdeHistoryService): TUI/GUI 用它查历史, 与 checkpoints 无依赖。
        _ = new IdeHistoryService(ctx, registry, IdeHistoryOptions.From(config));
        return IdeHistoryTool.Register(ctx, registry);
    }
}
