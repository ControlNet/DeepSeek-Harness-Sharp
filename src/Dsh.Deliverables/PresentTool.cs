using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Deliverables;

public sealed record PresentResult(int Turn, IReadOnlyList<PresentedFile> Files);

/**
 * present: declares existing files as final deliverables of the calling session.
 * Files are validated during execution and recorded only when the final tool result succeeds, so blocked or failed results publish nothing.
 */
public static class PresentTool
{
    public const string ToolName = "present";

    /** Enforced per-call ceiling; the parameter description recommends at most four. */
    public const int MaxFiles = 8;

    private const string Description =
        "Declare existing files as final deliverables for the user. "
        + "Use it when the user needs a separate file, especially Office documents, spreadsheets, and slide decks; "
        + "prefer your final response when that suffices. The user opens the current files; their contents are not copied.";

    private const string SectionText =
        "Use present after the files the user should receive are final, such as reports, Office documents, spreadsheets, slide decks or images. "
        + "It records existing files for the user interface to list and open with the default application; it never copies or converts them. "
        + "Present only the most important files and mention them briefly in your reply instead of pasting their contents. "
        + "Deliverables belong to the calling session: a subagent's presented files are not shown to the user, so the top-level session presents them.";

    private sealed record Staged(Session Session, int Turn, IReadOnlyList<PresentedFile> Files);

    public static IDisposable Register(Context ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var projections = ctx.Get<SessionProjectionRegistry>(SessionProjectionRegistry.ServiceName)!;
        var staged = new ConditionalWeakTable<ToolExecution, Staged>();
        var section = systemPrompt.Section(PromptSection.Literal("tool:present", SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = Description,
            Parameters = Parameters,
            Output = new ToolOutputDefinition(OutputSchema, static (_, value) => [new TextBlock(Rendered(value))]),
            Execute = (args, exec) => Execute(args, exec, projections, staged),
        });
        var unsubscribe = ctx.On<ToolResultNotification>(
            notification => Publish(notification, staged),
            new EventOptions { Global = true });
        return new Registration(section, registration, unsubscribe);
    }

    private static Task<object?> Execute(
        JsonElement args,
        ToolRunContext exec,
        SessionProjectionRegistry projections,
        ConditionalWeakTable<ToolExecution, Staged> staged)
    {
        var session = exec.Agent?.Session ?? throw new InvalidOperationException("present requires an agent session");
        var boundary = projections.StateOf<TurnBoundaryProjection>(session, TurnBoundaryProjectionDefinition.Key);
        if (boundary?.OpenTurnStartSeq is null)
            throw new InvalidOperationException("present requires an open turn");
        List<JsonElement> requested = args.TryGetProperty("files", out var filesElement) && filesElement.ValueKind == JsonValueKind.Array
            ? [.. filesElement.EnumerateArray()]
            : [];
        if (requested.Count is 0 or > MaxFiles)
            throw new ArgumentException($"present accepts 1 to {MaxFiles} files");
        var cwd = session.Header.Cwd ?? Environment.CurrentDirectory;
        List<PresentedFile> files = [.. requested.Select(item => Resolve(item, cwd))];
        exec.Signal.ThrowIfCancellationRequested();
        staged.AddOrUpdate(exec, new Staged(session, boundary.LastTurn, files));
        return Task.FromResult<object?>(new PresentResult(boundary.LastTurn, files));
    }

    private static PresentedFile Resolve(JsonElement item, string cwd)
    {
        var path = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("path", out var pathElement)
            && pathElement.ValueKind == JsonValueKind.String
                ? pathElement.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("present requires a non-empty file path");
        var target = Path.GetFullPath(path, cwd);
        var info = new FileInfo(target);
        if (Directory.Exists(target) || info.LinkTarget is not null)
            throw new ArgumentException($"Cannot present {path}: not a regular file");
        if (!info.Exists)
            throw new FileNotFoundException($"Cannot present {path}: file not found. Check the path, create the file if needed, and retry.");
        var description = item.TryGetProperty("description", out var descriptionElement)
            && descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString()?.Trim()
                : null;
        return new PresentedFile(target, string.IsNullOrEmpty(description) ? null : description);
    }

    private static void Publish(ToolResultNotification notification, ConditionalWeakTable<ToolExecution, Staged> staged)
    {
        if (!staged.TryGetValue(notification.Run, out var delivery))
            return;
        staged.Remove(notification.Run);
        if (!notification.Result.IsError)
            delivery.Session.Append(new DeliverablesPresentedPayload(delivery.Turn, notification.Run.CallId, delivery.Files));
    }

    private static string Rendered(JsonElement value)
        => string.Join('\n', value.GetProperty("files").EnumerateArray()
            .Select(file => $"Presented {file.GetProperty("path").GetString()}"));

    private static readonly JsonObject Parameters = JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["files"],
          "properties": {
            "files": {
              "type": "array",
              "description": "Usually the 1-2 most important deliverables; at most 4 per call.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["path"],
                "properties": {
                  "path": { "type": "string", "description": "Path of an existing regular file. Relative paths resolve against the session working directory." },
                  "description": { "type": "string", "description": "Brief description for the user." }
                }
              }
            }
          }
        }
        """)!.AsObject();

    private static readonly JsonObject OutputSchema = JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["turn", "files"],
          "properties": {
            "turn": { "type": "integer" },
            "files": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["path"],
                "properties": {
                  "path": { "type": "string" },
                  "description": { "type": "string" }
                }
              }
            }
          }
        }
        """)!.AsObject();

    private sealed class Registration(IDisposable section, IDisposable registration, Func<bool> unsubscribe) : IDisposable
    {
        public void Dispose()
        {
            unsubscribe();
            registration.Dispose();
            section.Dispose();
        }
    }
}
