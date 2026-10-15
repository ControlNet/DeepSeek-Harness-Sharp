using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dsh.AgentAdapters.Acp;
using Dsh.Core;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Host;

/**
 * The conversation of one ACP session as harness session events, so clients render it like a harness session (host.session.subscribe).
 * Mapping: the prompt becomes turn/start + user/message; message and thought chunks become assistant/chunk deltas and the text is closed
 * into one assistant/message before each tool call and at the end of the turn; tool_call becomes tool/call, a completed or failed
 * tool_call_update becomes tool/result; plan entries become a system/message; the stop reason becomes turn/end.
 * Events are numbered from 0 like a harness session and appended to a JSONL file, so the history outlives the agent process and the host.
 */
internal sealed class AcpTranscript : IAsyncDisposable
{
    /** Source plugin name on the system messages that carry an agent's plan. */
    private const string PlanSource = "acp-plan";

    private readonly Lock _gate = new();
    private readonly List<SessionEvent> _events;
    private readonly List<Channel<RemoteEventInfo>> _subscribers = [];
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly StringBuilder _text = new();
    private readonly string _sessionId;
    private readonly string _agentKind;
    private readonly string _path;
    private readonly Task _writer;
    private int _turn;
    private int _step;

    private AcpTranscript(string path, string sessionId, string agentKind, List<SessionEvent> events)
    {
        _path = path;
        _sessionId = sessionId;
        _agentKind = agentKind;
        _events = events;
        _turn = events.Count(sessionEvent => sessionEvent.Data is TurnStartPayload);
        _writer = Task.Run(WriteLoopAsync);
    }

    /** Loads the history written so far; lines that do not parse (a torn last line) are skipped. */
    public static async Task<AcpTranscript> OpenAsync(string path, string sessionId, string agentKind, CancellationToken cancellationToken)
    {
        var lines = File.Exists(path) ? await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false) : [];
        var events = lines.Select(TryParse).OfType<SessionEvent>().ToList();
        return new AcpTranscript(path, sessionId, agentKind, events);
    }

    public void BeginTurn(string text)
    {
        lock (_gate)
        {
            _step = 0;
            Append(new TurnStartPayload(++_turn));
            Append(new UserMessagePayload(MessageFactory.CreateUserText(text)), new SurfaceOp.Append());
        }
    }

    public void Apply(AcpSessionUpdate update)
    {
        lock (_gate)
        {
            switch (update.Kind)
            {
                case AcpProtocol.UpdateAgentMessageChunk when update.Text is { Length: > 0 } text:
                    _text.Append(text);
                    Append(new AssistantChunkPayload(_turn, _step, new StreamChunk.TextDelta(0, text)));
                    break;
                case AcpProtocol.UpdateAgentThoughtChunk when update.Text is { Length: > 0 } thought:
                    Append(new AssistantChunkPayload(_turn, _step, new StreamChunk.ReasoningDelta(0, thought)));
                    break;
                case AcpProtocol.UpdateToolCall:
                    CloseText();
                    _step++;
                    Append(new ToolCallPayload(_turn, _step, ToolCallId.Create(update.ToolCallId ?? ""), ToolName(update), RawInput(update.Update)));
                    break;
                case AcpProtocol.UpdateToolCallUpdate when update.Status is "completed" or "failed":
                    AppendToolResult(update);
                    break;
                case AcpProtocol.UpdatePlan:
                    Append(new SystemMessagePayload(_turn, _step, MessageFactory.CreateSystemMessage([new TextBlock(PlanText(update.Update))], new PluginMessageSource(PlanSource))), new SurfaceOp.Append());
                    break;
            }
        }
    }

    /** Maps the ACP stop reason to the harness turn end reasons. */
    public void EndTurn(string stopReason)
    {
        TurnEndReason reason = stopReason switch
        {
            AcpProtocol.StopEndTurn => new TurnEndReason.Completed(),
            AcpProtocol.StopCancelled => new TurnEndReason.Interrupted(),
            AcpProtocol.StopMaxTokens => new TurnEndReason.MaxTokens(),
            AcpProtocol.StopRefusal => new TurnEndReason.Blocked(),
            _ => new TurnEndReason.Error(new LlmFailure($"the agent stopped: {stopReason}", stopReason)),
        };
        Finish(reason);
    }

    public void FailTurn(string message, string code) => Finish(new TurnEndReason.Error(new LlmFailure(message, code)));

    /** History from FromSeq, then live events; the listener is attached before the history is copied, so nothing falls in between. */
    public async IAsyncEnumerable<RemoteEventInfo> SubscribeAsync(long fromSeq, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<RemoteEventInfo>();
        List<SessionEvent> history;
        lock (_gate)
        {
            history = [.. _events.Where(sessionEvent => sessionEvent.Seq >= fromSeq)];
            _subscribers.Add(channel);
        }
        try
        {
            foreach (var sessionEvent in history)
                yield return new RemoteEventInfo(_sessionId, DshJson.Serialize(sessionEvent));
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            lock (_gate)
                _subscribers.Remove(channel);
        }
    }

    private void Finish(TurnEndReason reason)
    {
        lock (_gate)
        {
            CloseText();
            Append(new TurnEndPayload(_turn, reason));
        }
    }

    private void AppendToolResult(AcpSessionUpdate update)
    {
        var failed = update.Status == "failed";
        var content = ToolContentText(update.Update);
        var message = MessageFactory.CreateToolResultMessage(ToolCallId.Create(update.ToolCallId ?? ""), [new TextBlock(content)], failed);
        Append(new ToolResultPayload(_turn, _step, message, failed ? new ToolResultErrorInfo(ToolName(update), update.Status!) : null), new SurfaceOp.Append());
    }

    /** Called under the lock: the streamed text so far becomes one assistant message. */
    private void CloseText()
    {
        if (_text.Length == 0)
            return;
        var message = MessageFactory.CreateAssistantMessage([new TextBlock(_text.ToString())], _agentKind, _agentKind);
        _text.Clear();
        Append(new AssistantMessagePayload(_turn, _step, message), new SurfaceOp.Append());
    }

    /** Called under the lock. */
    private void Append(SessionEventPayload payload, SurfaceOp? surfaceOp = null)
    {
        var sessionEvent = new SessionEvent
        {
            Type = payload.Type,
            Seq = _events.Count,
            Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Data = payload,
            SurfaceOp = surfaceOp,
        };
        _events.Add(sessionEvent);
        var json = DshJson.Serialize(sessionEvent);
        _lines.Writer.TryWrite(json);
        foreach (var subscriber in _subscribers)
            subscriber.Writer.TryWrite(new RemoteEventInfo(_sessionId, json));
    }

    private static string ToolName(AcpSessionUpdate update) => update.Title ?? StringOf(update.Update, "kind") ?? "tool";

    private static string? StringOf(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RawInput(JsonElement update)
        => update.TryGetProperty("rawInput", out var input) ? input.GetRawText() : "{}";

    /** Text blocks of the tool call content (`[{type:"content", content:{type:"text", text}}]`), else the raw output. */
    private static string ToolContentText(JsonElement update)
    {
        var texts = update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray()
                .Select(item => item.TryGetProperty("content", out var inner) ? inner : item)
                .Select(inner => StringOf(inner, "text"))
                .OfType<string>()
                .ToList()
            : [];
        if (texts.Count > 0)
            return string.Join('\n', texts);
        return update.TryGetProperty("rawOutput", out var output) ? output.GetRawText() : "";
    }

    /** One line per plan entry: `[status] content`. */
    private static string PlanText(JsonElement update)
        => update.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array
            ? string.Join('\n', entries.EnumerateArray().Select(entry => $"[{StringOf(entry, "status")}] {StringOf(entry, "content")}"))
            : "";

    private static SessionEvent? TryParse(string line)
    {
        try
        {
            return string.IsNullOrWhiteSpace(line) ? null : DshJson.Deserialize<SessionEvent>(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task WriteLoopAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await foreach (var line in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
            await File.AppendAllTextAsync(_path, $"{line}\n").ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _lines.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryComplete();
        }
    }
}
