using System.Text;
using Dsh.Plugins;

namespace Dsh.Boot;

/**
 * settings 配置面的单一出口: 数据来自各程序集生成器登记的 ISettingsSchemaProvider(零反射)。
 * 顶层文档(IsPlugin=false)与插件参数一起, 供 dsharp config schema、settings.yaml 模板生成块与 /inspect config 共用。
 */
public static class SettingsSchemaCatalog
{
    /** 模板生成块的边界标记: 块内由本文件渲染, 块外的手写说明不受影响。 */
    public const string GeneratedBegin = "# >>> generated: settings-schema (do not edit; run: dsharp config schema --write-template)";

    public const string GeneratedEnd = "# <<< generated";

    public static IReadOnlyList<ISettingsSchemaProvider> All() => DshPluginSettingsRegistry.All();

    public static ISettingsSchemaProvider? Document()
        => All().FirstOrDefault(provider => !provider.IsPlugin);

    public static IReadOnlyList<ISettingsSchemaProvider> Plugins()
        => [.. All().Where(provider => provider.IsPlugin)];

    /** 模板内容: 全部键以注释形式列出(不会替用户改默认行为), 顶层在前、插件在后。 */
    public static string RenderTemplateBlock()
    {
        var builder = new StringBuilder();
        builder.AppendLine(GeneratedBegin);
        if (Document() is { } document)
        {
            builder.AppendLine("# 顶层 settings(默认值来自 HarnessSettings, 取消注释即可覆盖):");
            Render(document.Settings, 0, builder);
        }
        var plugins = Plugins();
        if (plugins.Count > 0)
        {
            builder.AppendLine("#");
            builder.AppendLine("# 插件参数(plugins.\"<包名>\": {enabled, ...参数}; 未列出的插件发现即启用):");
        }
        foreach (var plugin in plugins)
        {
            builder.AppendLine("#");
            builder.AppendLine($"# {plugin.Package}:");
            if (plugin.Settings.Count == 0)
            {
                builder.AppendLine("#   (无参数)");
                continue;
            }
            Render(plugin.Settings, 2, builder);
        }
        builder.AppendLine(GeneratedEnd);
        return builder.ToString();
    }

    private static void Render(IReadOnlyList<PluginSettingSpec> settings, int indent, StringBuilder builder)
    {
        var pad = new string(' ', indent);
        foreach (var spec in settings)
        {
            builder.Append("# ").Append(pad).Append(spec.Key).Append(':');
            if (!string.IsNullOrWhiteSpace(spec.Description))
                builder.Append("  # ").Append(spec.Description);
            else if (spec.DefaultLiteral is { Length: > 0 } literal && IsLiteral(literal))
                builder.Append(' ').Append(literal.Replace("\n", " ").Replace("\r", ""));
            else if (spec.Kind == PluginSettingKind.List)
                builder.Append(" []");
            else if (spec.Kind == PluginSettingKind.Mapping && spec.Children is not { Count: > 0 })
                builder.Append(" {}");
            builder.AppendLine();
            if (spec.Children is { Count: > 0 } children)
                Render(children, indent + 2, builder);
        }
    }

    /** 只渲染字面量默认值: 初始化器写成表达式(如 LoggingOptions.DefaultBufferSize)时不猜。 */
    private static bool IsLiteral(string literal)
    {
        var text = literal.Trim();
        if (text is "true" or "false" or "null" or "[]" or "{}")
            return true;
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            return true;
        return text.Length > 1 && text[0] == '"' && text[^1] == '"';
    }

    /** 文本形态输出(CLI/文档共用): yaml 为模板块内容, json 为结构化清单, markdown 为表格。 */
    public static string Render(string format)
        => format switch
        {
            "json" => RenderJson(),
            "markdown" or "md" => RenderMarkdown(),
            _ => RenderTemplateBlock(),
        };

    private static string RenderJson()
    {
        var builder = new StringBuilder();
        builder.AppendLine("[");
        var providers = All();
        for (var index = 0; index < providers.Count; index++)
        {
            var provider = providers[index];
            builder.Append($"  {{\"package\":{Quote(provider.Package)},\"plugin\":{(provider.IsPlugin ? "true" : "false")},\"settings\":[");
            builder.Append(string.Join(",", provider.Settings.Select(SettingsJson)));
            builder.Append(']').AppendLine(index == providers.Count - 1 ? "}" : "},");
        }
        return builder.AppendLine("]").ToString();
    }

    private static string RenderMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| 作用域 | 键 | 类型 | 默认值 |");
        builder.AppendLine("| --- | --- | --- | --- |");
        foreach (var provider in All())
            AppendMarkdown(provider, provider.Settings, "", builder);
        return builder.ToString();
    }

    private static void AppendMarkdown(ISettingsSchemaProvider provider, IReadOnlyList<PluginSettingSpec> settings, string prefix, StringBuilder builder)
    {
        foreach (var spec in settings)
        {
            builder.Append("| ").Append(provider.Package).Append(" | ").Append(prefix).Append(spec.Key)
                .Append(" | ").Append(spec.Kind)
                .Append(" | ").Append(spec.DefaultLiteral ?? "").AppendLine(" |");
            if (spec.Children is { Count: > 0 } children)
                AppendMarkdown(provider, children, prefix + spec.Key + ".", builder);
        }
    }

    private static string SettingsJson(PluginSettingSpec spec)
    {
        var builder = new StringBuilder();
        builder.Append($"{{\"key\":{Quote(spec.Key)},\"kind\":\"{spec.Kind}\"");
        if (spec.DefaultLiteral is not null)
            builder.Append($",\"default\":{Quote(spec.DefaultLiteral)}");
        if (spec.Description is not null)
            builder.Append($",\"description\":{Quote(spec.Description)}");
        if (spec.Children is { Count: > 0 } children)
            builder.Append($",\"children\":[{string.Join(",", children.Select(SettingsJson))}]");
        return builder.Append('}').ToString();
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
