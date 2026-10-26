using System.Collections;
using Dsh.Core;
using Dsh.Ide.History;
using Dsh.Ide.Catalog;
using Dsh.Ide.Bridge;

namespace Dsh.Ide;

[global::Dsh.Plugins.DshPluginSettings]
public sealed record EditorOptions
{
    public IReadOnlyList<EditorDescriptor> Editors { get; init; } = BuiltinEditors.Create();
    [global::Dsh.Plugins.DshPluginSettings(Name = "default_editor")]
    public string DefaultEditor { get; init; } = "auto";
    [global::Dsh.Plugins.DshPluginSettings(Name = "strict_match")]
    public bool StrictMatch { get; init; }
    [global::Dsh.Plugins.DshPluginSettings(Name = "reuse_window")]
    public EditorReuseWindow ReuseWindow { get; init; } = EditorReuseWindow.Auto;
    public EditorFocus Focus { get; init; } = EditorFocus.Never;
    public EditorFallback Fallback { get; init; }
    [global::Dsh.Plugins.DshPluginSettings(Name = "allow_outside_workspace")]
    public bool AllowOutsideWorkspace { get; init; }
    public IdeHistoryOptions History { get; init; } = new();
    public IReadOnlyList<Dsh.Ide.Triggers.EditorTrigger> Triggers { get; init; } = [];
    [global::Dsh.Plugins.DshPluginSettings(Name = "client_mappings")]
    public IReadOnlyList<EditorPathMapping> ClientMappings { get; init; } = [];
    public static EditorOptions From(object? config)
    {
        var map = ConfigMap.Read(config);
        var builtins = BuiltinEditors.Create();
        var editors = map.TryGetValue("editors", out var raw) ? ConfigMap.Items(raw).Where(IsEnabled).Select(item => ReadEditor(item, builtins)).ToArray() : builtins;
        return new EditorOptions
        {
            Editors = editors,
            DefaultEditor = ConfigMap.Text(map, "default_editor") ?? "auto",
            StrictMatch = ConfigMap.Boolean(map, "strict_match"),
            AllowOutsideWorkspace = ConfigMap.Boolean(map, "allow_outside_workspace"),
            ReuseWindow = ConfigMap.EnumValue(map, "reuse_window", EditorReuseWindow.Auto),
            Focus = ConfigMap.EnumValue(map, "focus", EditorFocus.Never),
            Fallback = ConfigMap.EnumValue(map, "fallback", EditorFallback.None),
            History = IdeHistoryOptions.From(config),
            Triggers = Dsh.Ide.Triggers.EditorTrigger.Parse(map.GetValueOrDefault("triggers")),
            ClientMappings = EditorPathMapper.From(config).Mappings,
        };
    }

    private static bool IsEnabled(object? value)
    {
        var map = ConfigMap.Read(value);
        if (!map.TryGetValue("enabled", out var enabled)) return true;
        if (enabled is bool flag) return flag;
        if (enabled is string text && bool.TryParse(text, out var parsed)) return parsed;
        throw new ArgumentException("Editor enabled must be a boolean.");
    }

    private static EditorDescriptor ReadEditor(object? value, IReadOnlyList<EditorDescriptor> builtins)
    {
        if (value is string id)
            return builtins.FirstOrDefault(d => d.Id.Value == id) ?? throw new ArgumentException($"Unknown editor ID '{id}'.");
        var map = ConfigMap.Read(value);
        var name = ConfigMap.Text(map, "id") ?? throw new ArgumentException("Editor requires an ID.");
        var basis = builtins.FirstOrDefault(d => d.Id.Value == name);
        var command = ConfigMap.Text(map, "command") ?? basis?.Command ?? throw new ArgumentException($"Editor '{name}' requires a global command.");
        var actions = basis?.Actions.ToDictionary(p => p.Key, p => p.Value) ?? new Dictionary<EditorAction, string[]>();
        if (map.TryGetValue("actions", out var rawActions))
        {
            var supplied = ConfigMap.Read(rawActions);
            foreach (var (key, action) in supplied)
            {
                if (!Enum.TryParse<EditorAction>(key.Replace("_", ""), true, out var parsed) || !Enum.IsDefined(parsed))
                    throw new ArgumentException($"Unknown editor action '{key}'.");
                actions[parsed] = ConfigMap.Strings(action);
            }
            if (supplied.ContainsKey("open_file") && !supplied.ContainsKey("open_at_position"))
            {
                if (actions[EditorAction.OpenFile].Any(a => a.Contains("{line}", StringComparison.Ordinal)))
                    actions[EditorAction.OpenAtPosition] = actions[EditorAction.OpenFile];
                else actions.Remove(EditorAction.OpenAtPosition);
            }
        }
        var kind = ConfigMap.EnumValue(map, "kind", basis?.Kind ?? EditorKind.Gui);
        var conditions = map.TryGetValue("when", out var when) ? ConfigMap.Read(when) : null;
        var environment = map.TryGetValue("env", out var env) ? ConfigMap.Read(env).ToDictionary(p => p.Key, p => p.Value?.ToString() ?? "") : basis?.Environment ?? new Dictionary<string, string>();
        return (basis ?? new EditorDescriptor { Id = name, Command = command }) with
        {
            Command = command, Kind = kind, Actions = actions,
            ExecutableCandidates = ConfigMap.Text(map, "command") is not null ? [] : basis?.ExecutableCandidates ?? [],
            BundleNames = ConfigMap.Text(map, "command") is not null ? [] : basis?.BundleNames ?? [],
            Environment = environment, WorkingDirectory = ConfigMap.Text(map, "cwd") ?? basis?.WorkingDirectory,
            ReuseArguments = map.TryGetValue("reuse_args", out var reuse) ? ConfigMap.Strings(reuse) : basis?.ReuseArguments ?? [],
            NewWindowArguments = map.TryGetValue("new_window_args", out var newWindow) ? ConfigMap.Strings(newWindow) : basis?.NewWindowArguments ?? [],
            BackgroundArguments = map.TryGetValue("background_args", out var background) ? ConfigMap.Strings(background) : basis?.BackgroundArguments ?? [],
            FocusArguments = map.TryGetValue("focus_args", out var focus) ? ConfigMap.Strings(focus) : basis?.FocusArguments ?? [],
            Capabilities = actions.Keys.Aggregate(EditorCapability.None, (caps, action) => caps | EditorActionCapabilities.For(action)) | (kind != EditorKind.Gui ? EditorCapability.Terminal : EditorCapability.None) | (basis?.Capabilities & (EditorCapability.History | EditorCapability.Remote) ?? EditorCapability.None),
            When = conditions is null ? basis?.When ?? new() : new EditorConditions
            {
                Workspace = conditions.TryGetValue("workspace", out var markers) ? ConfigMap.Strings(markers) : [],
                Extensions = conditions.TryGetValue("extensions", out var extensions) ? ConfigMap.Strings(extensions) : [],
                Globs = conditions.TryGetValue("globs", out var globs) ? ConfigMap.Strings(globs) : [],
            },
        };
    }
}

internal static class ConfigMap
{
    public static Dictionary<string, object?> Read(object? value)
    {
        if (value is IDictionary<string, object?> typed)
            return new(typed, StringComparer.Ordinal);
        if (value is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
                if (entry.Key is string key) result[key] = entry.Value;
            return result;
        }
        return new(StringComparer.Ordinal);
    }
    public static IEnumerable<object?> Items(object? value) => value is IEnumerable list && value is not string ? list.Cast<object?>() : [];
    public static string[] Strings(object? value) => value is string text ? [text] : Items(value).OfType<string>().ToArray();
    public static string? Text(Dictionary<string, object?> map, string key) => map.TryGetValue(key, out var value) ? value as string : null;
    public static bool Boolean(Dictionary<string, object?> map, string key) => map.TryGetValue(key, out var value) && (value is true || value is string text && bool.TryParse(text, out var parsed) && parsed);
    public static T EnumValue<T>(Dictionary<string, object?> map, string key, T fallback) where T : struct, Enum
    {
        if (!map.ContainsKey(key)) return fallback;
        if (Text(map, key) is { } text && Enum.TryParse<T>(text.Replace("_", "").Replace("-", ""), true, out var value) && Enum.IsDefined(value)) return value;
        throw new ArgumentException($"Invalid editor configuration value for '{key}'.");
    }
}

public static class EditorActionCapabilities
{
    public static EditorCapability For(EditorAction action) => action switch
    {
        EditorAction.OpenWorkspace => EditorCapability.OpenWorkspace,
        EditorAction.OpenFile => EditorCapability.OpenFile,
        EditorAction.OpenAtPosition => EditorCapability.OpenAtPosition,
        EditorAction.OpenDiff => EditorCapability.Diff,
        EditorAction.Reveal => EditorCapability.Reveal,
        EditorAction.Focus => EditorCapability.Focus,
        EditorAction.OpenTerminal => EditorCapability.Terminal,
        EditorAction.Restore => EditorCapability.Restore,
        _ => EditorCapability.None,
    };
}
