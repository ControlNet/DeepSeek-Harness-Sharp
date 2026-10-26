using Dsh.Ide.History;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Interaction;
using Dsh.Ide.Catalog;
using Dsh.Ide.Launch;
using Dsh.Ide.Surfaces;
using Dsh.Ide.Triggers;
using Dsh.Ide.Bridge;

[assembly: DshPlugin(Dsh.Ide.Plugin.Ide)]

namespace Dsh.Ide;

[DshEntrypoint("ide-lsp")]
public sealed class Plugin(string packageName) : IDshPlugin, IDshEntrypoint
{
    internal const string Ide = "@deepseek-ai/dsh-ide";

    public string[] Inject => packageName switch
    {
        Ide => [ToolRuntime.ServiceName, CommandsService.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config)
    {
        var options = EditorOptions.From(config);
        var paths = DshPaths.FromContext(ctx) ?? throw new InvalidOperationException("IDE integration requires configured DSH storage paths.");
        var registry = IdeHistoryRegistry.CreateDefault();
        _ = new IdeHistoryService(ctx, registry, options.History);
        ConfigureEditors(ctx, options, paths);
        return IdeHistoryTool.Register(ctx, registry);
    }

    public Task<int> RunAsync(HarnessApp app, PluginEntrypointOptions options, CancellationToken cancellationToken)
        => EditorLspEntrypoint.RunAsync(cancellationToken);

    private static void ConfigureEditors(Context ctx, EditorOptions options, DshPaths paths)
    {
        var catalog = new EditorCatalog(options.Editors);
        var execution = new EditorExecutionContext();
        ctx.Provide(EditorExecutionContext.ServiceName, execution);
        var terminal = new DelegateTerminalHost("DSH-PTY",
            command => (execution.TerminalHost ?? ctx.Get<ITerminalHost>("editorTerminalHost", false))?.IsAvailable(command) == true,
            (command, signal) => (execution.TerminalHost ?? ctx.Get<ITerminalHost>("editorTerminalHost", false)) is { } host ? host.LaunchAsync(command, signal) : ValueTask.FromResult(new EditorExecutionResult(false, EditorErrorCode.NO_TERMINAL_HOST)));
        var reviews = new EditorReviewService(ctx, Path.Combine(paths.CachePath, "ide-review"));
        var launcher = new EditorLauncher(catalog, options, new EditorExecutor([terminal]), [reviews.SnapshotRoot],
            () => PluginSettingsSection.Resolve(HarnessSettings.Load(new(paths.Root)).Plugins, Ide)?.Permissions?.Write != false);
        var editors = RegisterBridge(ctx, launcher, options.ClientMappings, execution);
        if (options.Triggers.Count > 0)
        {
            var triggers = new EditorTriggerService(ctx, editors, options.Triggers);
            ctx.Provide(EditorTriggerService.ServiceName, triggers);
            ctx.Effect(() => (Func<Task>)(() => triggers.DisposeAsync().AsTask()));
            editors = new EditorInteractionService(ctx, editors, triggers);
        }
        ctx.Provide(IEditorCatalog.ServiceName, catalog);
        ctx.Provide(IEditorService.ServiceName, editors);
        ctx.Provide(IEditorLauncher.ServiceName, editors);
        ctx.Effect(() => IdeCommand.Register(ctx, editors, reviews));
        ctx.Effect(() => IdeTool.Register(ctx, editors));
    }

    private static IEditorService RegisterBridge(Context ctx, IEditorService launcher, IReadOnlyList<EditorPathMapping> mappings, EditorExecutionContext execution)
    {
        var clients = new EditorClientDispatcher();
        var mapper = new EditorPathMapper(mappings);
        ctx.Provide(IEditorClientDispatcher.ServiceName, clients);
        ctx.Provide("editorClientRequestMapper", (Func<string, EditorRequest, EditorRequest>)mapper.Map);
        ctx.Effect(() => clients);
        return new EditorBridgeService(launcher, clients, execution);
    }
}
