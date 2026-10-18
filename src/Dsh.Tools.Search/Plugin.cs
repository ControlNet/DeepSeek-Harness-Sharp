using Dsh.Runtime;
using Dsh.Core;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Tools.Search.Plugin.ToolFsSearch)]

namespace Dsh.Tools.Search;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string ToolFsSearch = "@deepseek-ai/dsh-tool-fs.search";

    public string[] Inject => packageName == ToolFsSearch
        ? [ToolRuntime.ServiceName, SystemPrompt.ServiceName]
        : throw new InvalidOperationException($"Unknown DSH package '{packageName}'.");

    public IDisposable Apply(Context ctx, object? config) => packageName == ToolFsSearch
        ? new CompositeDisposable(GlobTool.Register(ctx), GrepTool.Register(ctx))
        : throw new InvalidOperationException($"Unknown DSH package '{packageName}'.");
}
