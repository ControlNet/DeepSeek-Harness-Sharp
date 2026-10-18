using Dsh.Runtime;
using Dsh.Core;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Tools.Fs.Plugin.ToolFs)]
[assembly: DshPlugin(Dsh.Tools.Fs.Plugin.ToolStrReplaceEditor)]
[assembly: DshPlugin(Dsh.Tools.Fs.Plugin.FsLocal)]
[assembly: DshPlugin(Dsh.Tools.Fs.Plugin.ToolWorkingDirectory)]
[assembly: DshPlugin(Dsh.Tools.Fs.Plugin.ToolWorktree)]

namespace Dsh.Tools.Fs;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string ToolFs = "@deepseek-ai/dsh-tool-fs.files";
    internal const string ToolStrReplaceEditor = "@deepseek-ai/dsh-tool-str-replace-editor.fs";
    internal const string FsLocal = "@deepseek-ai/dsh-fs-local.fs";
    internal const string ToolWorkingDirectory = "@deepseek-ai/dsh-tool-working-directory.fs";
    internal const string ToolWorktree = "@deepseek-ai/dsh-tool-worktree.fs";

    public string[] Inject => packageName switch
    {
        ToolFs => [ToolRuntime.ServiceName, SystemPrompt.ServiceName],
        ToolStrReplaceEditor => [ToolRuntime.ServiceName],
        FsLocal => [],
        ToolWorkingDirectory => [ToolRuntime.ServiceName, SystemPrompt.ServiceName],
        ToolWorktree => [ToolRuntime.ServiceName, SystemPrompt.ServiceName, SubprocessService.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        ToolFs => new CompositeDisposable(ReadTool.Register(ctx), ReadImageTool.Register(ctx), WriteTool.Register(ctx), EditTool.Register(ctx)),
        ToolStrReplaceEditor => StrReplaceEditorTool.Register(ctx, StrReplaceEditorConfigFrom(config)),
        FsLocal => LocalFsService.Register(ctx, ConfigOf(config)?.GetValueOrDefault("cwd") as string),
        ToolWorkingDirectory => WorkingDirectoryTool.Register(ctx),
        ToolWorktree => WorktreeTool.Register(ctx),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IReadOnlyDictionary<string, object?>? ConfigOf(object? config)
        => config as IReadOnlyDictionary<string, object?>;

    private static StrReplaceEditorConfig StrReplaceEditorConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new StrReplaceEditorConfig
        {
            MaxOutputChars = dict?.GetValueOrDefault("maxOutputChars") is long maxOutputChars
                ? (int)maxOutputChars
                : new StrReplaceEditorConfig().MaxOutputChars,
            Description = dict?.GetValueOrDefault("description") as string,
        };
    }
}
