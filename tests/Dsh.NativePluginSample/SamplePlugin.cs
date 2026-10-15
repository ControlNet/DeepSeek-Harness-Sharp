using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Plugins.Native;

namespace Dsh.NativePluginSample;

/** 声明式原生插件样例:只声明包名与工具;
 *  导出握手、句柄表、两段式缓冲协议与异常拦截由生成器产出(见 DshNativePluginExports)。 */
public sealed class SamplePlugin : IDshNativePlugin
{
    private const string EchoSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["message"],
          "properties": { "message": { "type": "string", "description": "Text to echo" } }
        }
        """;

    private const string LengthSchema = """
        {
          "type": "object",
          "properties": { "message": { "type": "string", "description": "Text to measure" } }
        }
        """;

    private static readonly NativePluginTool[] RegisteredTools =
    [
        new("native_echo", "Echo the given message back (native ABI sample).", EchoSchema, Echo),
        new("native_length", "Return the length of the given message (native ABI sample).", LengthSchema, Length),
    ];

    public string Package => "sample.native-echo";

    public IReadOnlyList<NativePluginTool> Tools => RegisteredTools;

    private static string? Echo(string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        var message = document.RootElement.TryGetProperty("message", out var value) ? value.GetString() ?? "" : "";
        DshNativePluginRuntime.Log(DshNativePluginAbi.LogInfo, $"echo invoked: {message}");
        return new JsonObject { ["text"] = $"echo:{message}" }.ToJsonString();
    }

    /** 缺 message 时在插件代码里触发空引用: 运行时把它变成异常, 宿主只把这次调用记为工具错误, 插件继续可用。 */
    private static string? Length(string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        var message = document.RootElement.TryGetProperty("message", out var value) ? value.GetString() : null;
        return new JsonObject { ["text"] = $"length:{message!.Length}" }.ToJsonString();
    }
}
