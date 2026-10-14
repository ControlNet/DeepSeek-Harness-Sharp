using System.Text;

namespace Dsh.Plugins.Native;

/**
 * 原生插件配置面的 JSON 编解码(ABI 侧只走 JSON)。
 * 手写拼接不用 System.Text.Json: 原生插件是 AOT 产物, 保持零反射、零源生成上下文依赖。
 */
public static class NativePluginSettingsJson
{
    /** 配置项类型族 → wire 名(与托管侧 PluginSettingKind 同名, 宿主直接映射)。 */
    public const string KindText = "Text";
    public const string KindBoolean = "Boolean";
    public const string KindInteger = "Integer";
    public const string KindNumber = "Number";
    public const string KindList = "List";
    public const string KindMapping = "Mapping";
    public const string KindUnknown = "Unknown";

    /** schema 导出文本: {"settings":[{key,kind,default?,description?}]}; 没有声明时返回 {"settings":[]}。 */
    public static string Render(IReadOnlyList<NativePluginSetting>? settings)
    {
        var builder = new StringBuilder("{\"settings\":[");
        var first = true;
        foreach (var setting in settings ?? [])
        {
            if (string.IsNullOrWhiteSpace(setting.Key))
                continue;
            if (!first)
                builder.Append(',');
            first = false;
            builder.Append("{\"key\":").Append(Quote(setting.Key));
            builder.Append(",\"kind\":").Append(Quote(string.IsNullOrWhiteSpace(setting.Kind) ? KindUnknown : setting.Kind));
            if (!string.IsNullOrWhiteSpace(setting.DefaultJson))
                builder.Append(",\"default\":").Append(setting.DefaultJson);
            if (!string.IsNullOrWhiteSpace(setting.Description))
                builder.Append(",\"description\":").Append(Quote(setting.Description));
            builder.Append('}');
        }
        return builder.Append("]}").ToString();
    }

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
