using System.Text;

namespace Dsh.Plugins.Native.Host;

/**
 * 把 settings.yaml 里的插件参数段(经 YAML 解析出的原始值: null/bool/long/double/string/list/map)写成 JSON,
 * 交给原生插件自己解析。手写不用 System.Text.Json: 宿主是 AOT 产物, 保持零反射。
 */
public static class SettingsJson
{
    /** null 或空映射返回空串(插件据此判断"没有配置")。 */
    public static string Write(object? value)
    {
        if (value is not IReadOnlyDictionary<string, object?> map || map.Count == 0)
            return "";
        var builder = new StringBuilder();
        WriteObject(builder, map);
        return builder.ToString();
    }

    private static void WriteObject(StringBuilder builder, IReadOnlyDictionary<string, object?> map)
    {
        builder.Append('{');
        var first = true;
        foreach (var (key, item) in map)
        {
            if (!first)
                builder.Append(',');
            first = false;
            builder.Append(Quote(key)).Append(':');
            WriteValue(builder, item);
        }
        builder.Append('}');
    }

    private static void WriteValue(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case bool flag:
                builder.Append(flag ? "true" : "false");
                break;
            case string text:
                builder.Append(Quote(text));
                break;
            case IDictionary<string, object?> nested:
                WriteObject(builder, new Dictionary<string, object?>(nested, StringComparer.Ordinal));
                break;
            case IEnumerable<object?> list:
                builder.Append('[');
                var first = true;
                foreach (var item in list)
                {
                    if (!first)
                        builder.Append(',');
                    first = false;
                    WriteValue(builder, item);
                }
                builder.Append(']');
                break;
            default:
                AppendNumber(builder, value);
                break;
        }
    }

    private static void AppendNumber(StringBuilder builder, object value)
        => builder.Append(value is IFormattable formattable
            ? formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
            : Quote(value.ToString() ?? ""));

    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }
        return builder.Append('"').ToString();
    }
}
