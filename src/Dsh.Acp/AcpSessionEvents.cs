using System.Text.Json;

namespace Dsh.Acp;

/**
 * One `session/update` notification; Kind is the `sessionUpdate` discriminator (AcpMethods.Update*) and Update the raw object.
 * The helpers read the fields shared by the common kinds and return null when the kind does not carry them.
 */
public sealed record AcpSessionUpdate(string SessionId, string Kind, JsonElement Update)
{
    /** Text of a message or thought chunk whose content block is text. */
    public string? Text => Update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
        ? StringOf(content, "text")
        : null;

    public string? ToolCallId => StringOf(Update, "toolCallId");

    public string? Title => StringOf(Update, "title");

    /** Tool call status: pending, in_progress, completed or failed. */
    public string? Status => StringOf(Update, "status");

    internal static string? StringOf(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/**
 * A pending `session/request_permission` from the agent, waiting for whoever answers first.
 * RequestId is unique per process so hosts can expose it in pending lists; the agent's JSON-RPC id stays internal.
 */
public sealed class AcpPermissionRequest
{
    private readonly TaskCompletionSource<AcpPermissionOutcome> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal AcpPermissionRequest(string sessionId, AcpToolCallInfo toolCall, IReadOnlyList<AcpPermissionOption> options)
    {
        SessionId = sessionId;
        ToolCall = toolCall;
        Options = options;
    }

    public string RequestId { get; } = Guid.NewGuid().ToString("N");

    public string SessionId { get; }

    public AcpToolCallInfo ToolCall { get; }

    public IReadOnlyList<AcpPermissionOption> Options { get; }

    /** Completes once answered; it is already cancelled when the turn was cancelled or the agent went away. */
    public Task<AcpPermissionOutcome> Outcome => _outcome.Task;

    public bool IsResolved => _outcome.Task.IsCompleted;

    /** Answers with one of Options; false when OptionId is unknown or someone answered first. */
    public bool TrySelect(string optionId)
        => Options.Any(option => option.OptionId == optionId) && _outcome.TrySetResult(AcpPermissionOutcome.Selected(optionId));

    public bool TryCancel() => _outcome.TrySetResult(AcpPermissionOutcome.Cancelled);

    internal static AcpToolCallInfo ToolCallOf(JsonElement toolCall)
        => new(
            AcpSessionUpdate.StringOf(toolCall, "toolCallId") ?? "",
            AcpSessionUpdate.StringOf(toolCall, "title"),
            AcpSessionUpdate.StringOf(toolCall, "kind"),
            AcpSessionUpdate.StringOf(toolCall, "status"),
            toolCall.Clone());
}
