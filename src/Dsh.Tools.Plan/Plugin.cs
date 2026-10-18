using Dsh.Runtime;
using Dsh.Core;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Tools.Plan.Plugin.ToolTodo)]

namespace Dsh.Tools.Plan;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string ToolTodo = "@deepseek-ai/dsh-tool-todo.plan";

    public string[] Inject => packageName == ToolTodo
        ? [ToolRuntime.ServiceName]
        : throw new InvalidOperationException($"Unknown DSH package '{packageName}'.");

    public IDisposable Apply(Context ctx, object? config) => packageName == ToolTodo
        ? TodoWriteTool.Register(ctx, (config as IReadOnlyDictionary<string, object?>)?.GetValueOrDefault("allowParallelInProgress") as bool? ?? true)
        : throw new InvalidOperationException($"Unknown DSH package '{packageName}'.");
}
