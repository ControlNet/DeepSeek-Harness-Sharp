using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Llm.Anthropic;

/**
 * thinking / redacted_thinking 块的回放状态: 签名只存在于原始块里, 通用的 ReasoningBlock 装不下。
 * 流结束时把原始块按出现顺序存进 assistant 消息的 replayState, 下次请求时按顺序换回 ReasoningBlock 的位置。
 */
internal static class AnthropicThinkingReplay
{
    private const string Key = "anthropicThinking";

    public static JsonElement Encode(IReadOnlyList<JsonObject> blocks)
    {
        var state = new JsonObject { [Key] = new JsonArray([.. blocks.Select(block => block.DeepClone())]) };
        using var document = JsonDocument.Parse(state.ToJsonString());
        return document.RootElement.Clone();
    }

    /** 只认本适配器写入的状态; 其他供应商的 replayState 或旧消息返回 null。 */
    public static IReadOnlyList<JsonNode>? Decode(Message message)
    {
        if (message.Source is not ModelMessageSource { ReplayState: { ValueKind: JsonValueKind.Object } state }
            || !state.TryGetProperty(Key, out var blocks)
            || blocks.ValueKind != JsonValueKind.Array)
            return null;
        return [.. blocks.EnumerateArray().Select(block => JsonNode.Parse(block.GetRawText())!)];
    }
}
