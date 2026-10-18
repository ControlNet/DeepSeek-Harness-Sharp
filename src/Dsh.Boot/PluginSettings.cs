using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Composition;

namespace Dsh.Boot;

/** settings.yaml 的 plugins 条目:布尔开关或 {enabled, ...参数} 形式。 */
public sealed record PluginSetting
{
    public const string EnabledKey = "enabled";
    public const string PermissionsKey = "permissions";

    public bool Enabled { get; init; } = true;
    public Dictionary<string, object?> Parameters { get; init; } = [];

    /** Reserved key beside enabled with read/write/network booleans; null (key omitted) allows everything. */
    public PluginPermissions? Permissions { get; init; }

    public static PluginSetting FromRaw(object? raw) => raw switch
    {
        null => new PluginSetting(),
        bool enabled => new PluginSetting { Enabled = enabled },
        IDictionary<string, object?> map => FromMap(map),
        _ => throw new RuntimeException("INVALID_PLUGIN_SETTING",
            $"plugin setting must be a boolean or a mapping, got {raw.GetType().Name}"),
    };

    private static PluginSetting FromMap(IDictionary<string, object?> map)
    {
        var enabled = true;
        if (map.TryGetValue(EnabledKey, out var value) && value is not null)
        {
            enabled = value is bool flag
                ? flag
                : throw new RuntimeException("INVALID_PLUGIN_SETTING", $"plugins.<name>.{EnabledKey} must be a boolean");
        }
        var parameters = new Dictionary<string, object?>(map, StringComparer.Ordinal);
        parameters.Remove(EnabledKey);
        parameters.Remove(PermissionsKey, out var permissions);
        return new PluginSetting
        {
            Enabled = enabled,
            Parameters = parameters,
            Permissions = permissions is null ? null : PluginPermissions.FromRaw(permissions),
        };
    }
}

/**
 * The plugins."<package>".permissions mapping: read, write and network flags, each allowed unless set to false.
 */
public sealed record PluginPermissions
{
    public static readonly PluginPermissions AllAllowed = new();

    public bool Read { get; init; } = true;
    public bool Write { get; init; } = true;
    public bool Network { get; init; } = true;

    public bool Allows(PluginPermission permission) => permission switch
    {
        PluginPermission.Read => Read,
        PluginPermission.Write => Write,
        _ => Network,
    };

    public PluginPermissions With(PluginPermission permission, bool allowed) => permission switch
    {
        PluginPermission.Read => this with { Read = allowed },
        PluginPermission.Write => this with { Write = allowed },
        _ => this with { Network = allowed },
    };

    /** The settings.yaml key of a permission, e.g. "write". */
    public static string KeyOf(PluginPermission permission) => permission switch
    {
        PluginPermission.Read => "read",
        PluginPermission.Write => "write",
        _ => "network",
    };

    private static readonly Dictionary<string, PluginPermission> ByKey =
        Enum.GetValues<PluginPermission>().ToDictionary(KeyOf, StringComparer.Ordinal);

    /** Strict parse: a typo such as "wirte: false" must fail loudly instead of leaving the permission allowed. */
    public static PluginPermissions FromRaw(object raw)
    {
        if (raw is not IDictionary<string, object?> map)
            throw Invalid($"plugins.<name>.{PluginSetting.PermissionsKey} must be a mapping of read/write/network booleans");
        var permissions = AllAllowed;
        foreach (var (key, value) in map)
        {
            if (!ByKey.TryGetValue(key, out var permission))
                throw Invalid($"unknown plugin permission \"{key}\" (expected read, write or network)");
            if (value is not bool allowed)
                throw Invalid($"plugins.<name>.{PluginSetting.PermissionsKey}.{key} must be a boolean");
            permissions = permissions.With(permission, allowed);
        }
        return permissions;
    }

    private static RuntimeException Invalid(string message) => new("INVALID_PLUGIN_SETTING", message);
}

/** settings.yaml 的 plugins 段:用户在此禁用插件或保存参数;未列出的插件发现即启用。 */
public static class PluginSettingsSection
{
    public const string PluginsKey = "plugins";

    private static readonly IReadOnlyDictionary<string, string> RemovedToolPackages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["@deepseek-ai/dsh-tool-bash"] = "@deepseek-ai/dsh-tool-bash.shell",
        ["@deepseek-ai/dsh-subprocess"] = "@deepseek-ai/dsh-subprocess.shell",
        ["@deepseek-ai/dsh-tool-pwsh"] = "@deepseek-ai/dsh-tool-pwsh.shell",
        ["@deepseek-ai/dsh-tool-fs"] = "@deepseek-ai/dsh-tool-fs.files",
        ["@deepseek-ai/dsh-tool-fs-search"] = "@deepseek-ai/dsh-tool-fs.search",
        ["@deepseek-ai/dsh-tool-todo"] = "@deepseek-ai/dsh-tool-todo.plan",
        ["@deepseek-ai/dsh-tool-str-replace-editor"] = "@deepseek-ai/dsh-tool-str-replace-editor.fs",
        ["@deepseek-ai/dsh-fs-local"] = "@deepseek-ai/dsh-fs-local.fs",
        ["@deepseek-ai/dsh-tool-powershell"] = "@deepseek-ai/dsh-tool-powershell.shell",
        ["@deepseek-ai/dsh-tool-wsl"] = "@deepseek-ai/dsh-tool-wsl.shell",
        ["@deepseek-ai/dsh-tool-working-directory"] = "@deepseek-ai/dsh-tool-working-directory.fs",
        ["@deepseek-ai/dsh-tool-worktree"] = "@deepseek-ai/dsh-tool-worktree.fs",
        ["@deepseek-ai/dsh-tool-bash-persistent"] = "@deepseek-ai/dsh-tool-bash.shell-persistent",
        ["@deepseek-ai/dsh-tool-pwsh-persistent"] = "@deepseek-ai/dsh-tool-pwsh.shell-persistent",
    };

    public static Dictionary<string, PluginSetting> ParseDocument(string yaml)
    {
        var root = YamlConfig.LoadMapping(yaml);
        return root.TryGetValue(PluginsKey, out var raw) ? Parse(raw) : new Dictionary<string, PluginSetting>(StringComparer.Ordinal);
    }

    private static Dictionary<string, PluginSetting> Parse(object? raw)
    {
        if (raw is null)
            return new Dictionary<string, PluginSetting>(StringComparer.Ordinal);
        if (raw is not IDictionary<string, object?> map)
            throw new RuntimeException("INVALID_PLUGIN_SETTING", "plugins must be a mapping from package IDs to booleans or parameter mappings");
        var plugins = new Dictionary<string, PluginSetting>(StringComparer.Ordinal);
        foreach (var (name, value) in map)
        {
            if (RemovedToolPackages.TryGetValue(name, out var replacement))
                throw new RuntimeException("TOOL_PACKAGE_MIGRATION_REQUIRED",
                    $"Plugin package '{name}' moved to '{replacement}'. Rename its settings.yaml/profile key, preserving enabled, permissions and all parameters, and remove the old key even when the new key is also present. See README: 工具能力包迁移.");
            plugins[name] = PluginSetting.FromRaw(value);
        }
        return plugins;
    }
}
