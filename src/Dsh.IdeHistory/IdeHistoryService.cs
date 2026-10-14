using Dsh.Runtime;

namespace Dsh.IdeHistory;

/** `@deepseek-ai/dsh-ide-history` 的插件参数: 各 provider 开关、时间线开关与上限。默认只开 provider、时间线关。 */
[global::Dsh.Plugins.DshPluginSettings]
public sealed record IdeHistoryOptions
{
    public const int DefaultTimelineLimit = 50;

    public static readonly IReadOnlyList<string> AllProviders = ["vscode", "jetbrains", "zed"];

    public IReadOnlyList<string> Providers { get; init; } = AllProviders;

    /** 是否把 IDE 历史并进 `/timestamp` 时间线(供 jump/revert/fork)。默认 false: IDE 自动保存很密, 会盖过用户消息。 */
    public bool Timeline { get; init; }

    public int TimelineLimit { get; init; } = DefaultTimelineLimit;

    public static IdeHistoryOptions From(object? config)
    {
        if (config is not IDictionary<string, object?> map)
            return new IdeHistoryOptions();
        var providers = map.TryGetValue("providers", out var configuredProviders)
            && ReadProviders(configuredProviders) is { Count: > 0 } configured
                ? configured
                : AllProviders;
        var limit = map.TryGetValue("timeline_limit", out var configuredLimit)
            ? ReadLimit(configuredLimit)
            : DefaultTimelineLimit;
        return new IdeHistoryOptions
        {
            Providers = providers,
            Timeline = map.TryGetValue("timeline", out var timeline) && timeline is true,
            TimelineLimit = Math.Clamp(limit, 1, 500),
        };
    }

    private static IReadOnlyList<string> ReadProviders(object? value) => value switch
    {
        IEnumerable<object?> list => [.. list.OfType<string>()],
        string single => [single],
        _ => [],
    };

    private static int ReadLimit(object? value) => value switch
    {
        int number => number,
        long number => (int)number,
        double number => (int)number,
        _ => DefaultTimelineLimit,
    };
}

/**
 * 供 TUI/GUI 复用的 IDE 历史服务(§5): 与 checkpoints 无依赖, 关掉检查点也照常可用。
 * 查询按工作区门控(§3): 只有工作区带对应 IDE 标记时才查该 provider; 无标记返回空(不做全局扫描)。
 */
public interface IIdeHistoryService
{
    public const string ServiceName = "ideHistory";

    /** 当前工作区命中的 IDE 标记(无标记即 None)。 */
    IdeWorkspaceKind WorkspaceKind(string? workspaceRoot);

    /** 是否把 IDE 历史并进 `/timestamp` 时间线(插件参数 `timeline`, 默认 false)。 */
    bool TimelineEnabled { get; }

    /** 工作区内的历史条目(时间倒序, 上限见配置); 只查被工作区启用且配置允许的 provider。 */
    IReadOnlyList<IdeHistoryEntry> Query(string? workspaceRoot, long? from, long? to, string? path = null);

    /** 读某个条目的内容(供 revert 写回工作区)。 */
    IdeHistoryContent? Read(IdeHistoryEntry entry);
}

public sealed class IdeHistoryService(Context ctx, IdeHistoryRegistry registry, IdeHistoryOptions options)
    : Service(ctx, IIdeHistoryService.ServiceName), IIdeHistoryService
{
    /** 工作区标记 → 提供该历史的 provider 名关键字。 */
    private static readonly IReadOnlyList<(IdeWorkspaceKind Kind, string Keyword)> Sources =
    [
        (IdeWorkspaceKind.VsCode, "vscode"),
        (IdeWorkspaceKind.JetBrains, "jetbrains"),
        (IdeWorkspaceKind.Zed, "zed"),
    ];

    public bool TimelineEnabled => options.Timeline;

    public IdeWorkspaceKind WorkspaceKind(string? workspaceRoot) => WorkspaceIdeDetector.Detect(workspaceRoot);

    public IReadOnlyList<IdeHistoryEntry> Query(string? workspaceRoot, long? from, long? to, string? path = null)
    {
        var kind = WorkspaceKind(workspaceRoot);
        if (kind == IdeWorkspaceKind.None)
            return [];
        // 先按上限放大取一批(provider 过滤与时间窗都还要筛), 再截到 timeline_limit。
        var candidates = registry.Query(path ?? workspaceRoot, providerFilter: null, limit: Math.Max(options.TimelineLimit * 4, options.TimelineLimit));
        var enabled = candidates.Where(item => IsEnabled(kind, item.Provider.Name));
        return [.. enabled
            .Select(item => item.Entry)
            .Where(entry => (from is null || entry.Timestamp >= from) && (to is null || entry.Timestamp <= to))
            .OrderByDescending(entry => entry.Timestamp)
            .Take(options.TimelineLimit)];
    }

    public IdeHistoryContent? Read(IdeHistoryEntry entry)
    {
        var match = registry.Find(entry.Path, entry.Timestamp, entry.Provider);
        return match is { } found ? found.Provider.Read(found.Store, found.Entry) : null;
    }

    private bool IsEnabled(IdeWorkspaceKind kind, string providerName)
        => Sources.Any(source =>
            kind.HasFlag(source.Kind)
            && providerName.Contains(source.Keyword, StringComparison.OrdinalIgnoreCase)
            && options.Providers.Any(name => name.Contains(source.Keyword, StringComparison.OrdinalIgnoreCase)));
}
