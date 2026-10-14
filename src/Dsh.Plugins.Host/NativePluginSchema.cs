using System.Text.Json.Nodes;

namespace Dsh.Plugins;

/**
 * 原生插件的配置面: 插件在握手后由 dsh_plugin_settings_schema 导出 JSON, 宿主在这里转成托管侧 schema 登记,
 * 于是原生插件与托管插件在"参数文档/校验"上同标准(生成模板与 dsharp config schema 一视同仁)。
 */
public static class NativePluginSchema
{
    /** 解析 schema 导出文本并登记; 空文本(插件没声明)或格式非法时只记日志、不影响装载。 */
    public static void Register(string package, string schemaJson, Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
            return;
        try
        {
            var root = JsonNode.Parse(schemaJson);
            if (root?["settings"] is not JsonArray settings)
                return;
            var specs = new List<PluginSettingSpec>();
            foreach (var node in settings)
            {
                if (Parse(node) is { } spec)
                    specs.Add(spec);
            }
            if (specs.Count == 0)
                return;
            DshPluginSettingsRegistry.Register(new NativeSchema(package, specs));
        }
        catch (Exception error)
        {
            warn?.Invoke($"native plugin \"{package}\" settings schema is invalid: {error.Message}");
        }
    }

    private static PluginSettingSpec? Parse(JsonNode? node)
    {
        if (node?["key"]?.GetValue<string>() is not { Length: > 0 } key)
            return null;
        var kind = node["kind"]?.GetValue<string>() switch
        {
            "Text" => PluginSettingKind.Text,
            "Boolean" => PluginSettingKind.Boolean,
            "Integer" => PluginSettingKind.Integer,
            "Number" => PluginSettingKind.Number,
            "List" => PluginSettingKind.List,
            "Mapping" => PluginSettingKind.Mapping,
            _ => PluginSettingKind.Unknown,
        };
        var description = node["description"]?.GetValue<string>();
        return new PluginSettingSpec(key, kind, null, description);
    }

    private sealed record NativeSchema(string Package, IReadOnlyList<PluginSettingSpec> Settings) : ISettingsSchemaProvider;
}
