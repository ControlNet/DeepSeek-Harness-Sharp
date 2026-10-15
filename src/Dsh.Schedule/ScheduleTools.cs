using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Schedule;

/** Model-facing `schedule_create`, `schedule_list`, `schedule_update` and `schedule_delete`; every call acts on the calling agent's session. */
public static class ScheduleTools
{
    public const string SectionName = "tool:schedule";

    private const string GoalSectionName = "tool:goal";
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private const string AfterSeconds = "after_seconds";
    private const string At = "at";
    private const string EverySeconds = "every_seconds";
    private const string Daily = "daily";
    private const string Weekly = "weekly";
    private const string Cron = "cron";

    private const string CreateDescription =
        "Create a reminder in the current session that delivers prompt back to this session when it becomes due. "
        + "Supply exactly one timing parameter: after_seconds, at, every_seconds, daily, weekly, or cron. "
        + "Daily, weekly, and cron times follow their time zone: a local time skipped by a daylight-saving jump fires at the end of the jump, "
        + "and a repeated local time fires once. When the session is resumed after being closed, an overdue one-shot is delivered once "
        + "and a recurring reminder delivers only its latest missed occurrence.";

    private const string ListDescription = "List the active reminders in the current session.";

    private const string DeleteDescription =
        "Delete a reminder in the current session, active or ended. Deletion does not retract a reminder message that is already queued.";

    private const string UpdateDescription =
        "Change a reminder in place, keeping its id. Supply a new title, prompt, or at most one timing parameter; "
        + "omitted fields keep their stored values. To change a relative delay, create a new reminder.";

    private const string TitleDescription =
        "Concise task name in the user's language, at most 120 characters. "
        + "Name only the core action and subject; keep essential target identifiers such as PR numbers and branch names. "
        + "Do not include execution frequency, scheduled dates or times, time zones, task status, or execution details. "
        + "Put scheduling information in the timing parameters and execution instructions in prompt.";

    private static readonly string[] CreateSelectors = [AfterSeconds, At, EverySeconds, Daily, Weekly, Cron];
    private static readonly string[] UpdateSelectors = [At, EverySeconds, Daily, Weekly, Cron];

    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const string SelectorProperties = """
        "every_seconds": {
          "type": "integer",
          "description": "Fixed-rate interval in whole seconds, at least 60, aligned to the time it is saved."
        },
        "daily": {
          "type": "object",
          "additionalProperties": false,
          "required": ["time", "time_zone"],
          "description": "Every day at a local time.",
          "properties": {
            "time": { "type": "string", "description": "HH:mm:ss, for example 23:00:00." },
            "time_zone": { "type": "string", "description": "UTC, an IANA Area/Location name such as Asia/Shanghai, or the host zone named in the system prompt." }
          }
        },
        "weekly": {
          "type": "object",
          "additionalProperties": false,
          "required": ["time", "time_zone", "weekdays"],
          "description": "On the given weekdays at a local time.",
          "properties": {
            "time": { "type": "string", "description": "HH:mm:ss, for example 09:00:00." },
            "time_zone": { "type": "string", "description": "UTC, an IANA Area/Location name such as Asia/Shanghai, or the host zone named in the system prompt." },
            "weekdays": { "type": "array", "items": { "type": "integer" }, "description": "ISO weekdays, Monday 1 through Sunday 7, without repetitions." }
          }
        },
        "cron": {
          "type": "object",
          "additionalProperties": false,
          "required": ["expression", "time_zone"],
          "description": "Five-field cron expression in a time zone.",
          "properties": {
            "expression": {
              "type": "string",
              "description": "minute hour day-of-month month day-of-week, for example \"*/15 9-17 * * 1-5\". Day and month names, L, W and # are accepted. When both day fields are restricted, a date must match both."
            },
            "time_zone": { "type": "string", "description": "UTC, an IANA Area/Location name such as Asia/Shanghai, or the host zone named in the system prompt." }
          }
        },
        "at": {
          "description": "Absolute one-shot target: an RFC 3339 date-time with offset, or a local date, time, and time_zone. A local time that does not exist in the zone is rejected; a repeated local time uses the earlier instant.",
          "oneOf": [
            { "type": "string" },
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["date", "time", "time_zone"],
              "properties": {
                "date": { "type": "string", "description": "yyyy-MM-dd" },
                "time": { "type": "string", "description": "HH:mm:ss" },
                "time_zone": { "type": "string" }
              }
            }
          ]
        }
        """;

    private const string ViewSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["id", "title", "prompt", "kind", "scheduledAt", "state"],
          "properties": {
            "id": { "type": "string" },
            "title": { "type": "string" },
            "prompt": { "type": "string" },
            "kind": { "type": "string", "enum": ["after", "at", "every", "daily", "weekly", "cron"] },
            "scheduledAt": { "type": "string" },
            "state": { "type": "string", "enum": ["scheduled", "overdue"] },
            "afterSeconds": { "type": "integer" },
            "everySeconds": { "type": "integer" },
            "time": { "type": "string" },
            "timeZone": { "type": "string" },
            "weekdays": { "type": "array", "items": { "type": "integer" } },
            "expression": { "type": "string" }
          }
        }
        """;

    public static IDisposable Apply(Context ctx)
    {
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)
            ?? throw new InvalidOperationException("tool-schedule requires the systemPrompt service");
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("tool-schedule requires the tools service");
        var schedules = ctx.Get<ScheduleService>(ScheduleService.ServiceName)
            ?? throw new InvalidOperationException("tool-schedule requires the schedules service");
        return new RegistrationBundle(
            systemPrompt.Section(new PromptSection(SectionName, _ => Guidance(TimeZoneInfo.Local.Id), After: [GoalSectionName])),
            tools.Register(CreateTool(schedules)),
            tools.Register(ListTool(schedules)),
            tools.Register(UpdateTool(schedules)),
            tools.Register(DeleteTool(schedules)));
    }

    private static string Guidance(string hostZone)
        => "Use the schedule tools to set reminders in the current session. When a reminder is due, its prompt comes back to this "
            + "session as a scheduled message: it starts a new turn when the session is idle, and otherwise waits until the current turn ends. "
            + "Reminders belong to this session: they run only while the session is open, come back when it is resumed, and are not "
            + "copied into forks or subagents. Use after_seconds for relative delays. at, daily, weekly, and cron need an explicit time "
            + $"zone; the host's local time zone is \"{hostZone}\". A reminder is not a goal: it never creates, resumes, or completes one.";

    private static ToolDefinition CreateTool(ScheduleService schedules)
        => new()
        {
            Name = "schedule_create",
            Description = CreateDescription,
            Parameters = Schema($$"""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["prompt", "title"],
                  "properties": {
                    "prompt": { "type": "string", "description": "Reminder content to deliver when the target becomes due." },
                    "title": { "type": "string", "description": {{JsonValue.Create(TitleDescription).ToJsonString()}} },
                    "after_seconds": { "type": "integer", "description": "Delay in whole seconds." },
                    {{SelectorProperties}}
                  }
                }
                """),
            Output = Output(Schema(ViewSchema)),
            Execute = (args, exec) =>
            {
                var agent = Caller(exec);
                var timing = Timing(args, CreateSelectors)
                    ?? throw Selector("schedule_create accepts exactly one of after_seconds, at, every_seconds, daily, weekly, or cron");
                var record = schedules.Create(agent, StringArg(args, "title") ?? "", StringArg(args, "prompt") ?? "", timing);
                return Task.FromResult<object?>(View(record, schedules.Now));
            },
        };

    private static ToolDefinition ListTool(ScheduleService schedules)
        => new()
        {
            Name = "schedule_list",
            Description = ListDescription,
            Parameters = Schema("""{ "type": "object", "additionalProperties": false, "properties": {} }"""),
            Output = Output(Schema($$"""{ "type": "array", "items": {{ViewSchema}} }""")),
            Execute = (_, exec) =>
            {
                var agent = Caller(exec);
                var now = schedules.Now;
                var views = new JsonArray([.. schedules.List(agent).Select(record => ViewNode(record, now))]);
                return Task.FromResult<object?>(Element(views));
            },
        };

    private static ToolDefinition UpdateTool(ScheduleService schedules)
        => new()
        {
            Name = "schedule_update",
            Description = UpdateDescription,
            Parameters = Schema($$"""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["id"],
                  "properties": {
                    "id": { "type": "string", "description": "Schedule id returned by schedule_list." },
                    "title": { "type": "string", "description": "New task name; follow the schedule_create title rules." },
                    "prompt": { "type": "string", "description": "New reminder content." },
                    {{SelectorProperties}}
                  }
                }
                """),
            Output = Output(Schema($$"""
                {
                  "oneOf": [
                    {{ViewSchema}},
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["id", "updated", "code"],
                      "properties": {
                        "id": { "type": "string" },
                        "updated": { "const": false },
                        "code": { "type": "string", "enum": ["schedule_not_found", "schedule_ended"] }
                      }
                    }
                  ]
                }
                """)),
            Execute = (args, exec) => Task.FromResult<object?>(Update(schedules, args, Caller(exec))),
        };

    private static ToolDefinition DeleteTool(ScheduleService schedules)
        => new()
        {
            Name = "schedule_delete",
            Description = DeleteDescription,
            Parameters = Schema("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["id"],
                  "properties": { "id": { "type": "string", "description": "Exact schedule id." } }
                }
                """),
            Output = Output(Schema("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["id", "deleted"],
                  "properties": {
                    "id": { "type": "string" },
                    "deleted": { "type": "boolean" },
                    "code": { "const": "schedule_not_found" }
                  }
                }
                """)),
            Execute = (args, exec) =>
            {
                var agent = Caller(exec);
                var id = Id(args);
                var deleted = schedules.Delete(agent, id);
                var result = new JsonObject { ["id"] = id, ["deleted"] = deleted };
                if (!deleted)
                    result["code"] = ScheduleErrorCodes.NotFound;
                return Task.FromResult<object?>(Element(result));
            },
        };

    private static JsonElement Update(ScheduleService schedules, JsonElement args, IAgent agent)
    {
        var id = Id(args);
        if (args.TryGetProperty(AfterSeconds, out _))
            throw Selector("schedule_update cannot change a relative delay; create a new reminder instead");
        var timing = Timing(args, UpdateSelectors);
        var title = StringArg(args, "title");
        var prompt = StringArg(args, "prompt");
        if (timing is null && title is null && prompt is null)
            throw Selector("schedule_update needs a new title, prompt, or one of at, every_seconds, daily, weekly, or cron");
        var (record, missCode) = schedules.Update(agent, id, title, prompt, timing);
        return record is not null
            ? View(record, schedules.Now)
            : Element(new JsonObject { ["id"] = id, ["updated"] = false, ["code"] = missCode });
    }

    /** A delegated subagent's reminders could never be delivered once its run ends, so every tool refuses it. */
    private static IAgent Caller(ToolRunContext exec)
    {
        var agent = exec.Agent
            ?? throw new HarnessException("the schedule operation failed: no calling agent", ScheduleErrorCodes.InternalError);
        if (agent.Session.Header.DelegationDepth is > 0)
            throw new HarnessException("A delegated subagent cannot use reminders.", ScheduleErrorCodes.SubagentSession);
        return agent;
    }

    private static ScheduleTiming? Timing(JsonElement args, string[] selectors)
    {
        var present = selectors
            .Where(name => args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
            .ToList();
        if (present.Count > 1)
            throw Selector($"supply at most one timing parameter, got {string.Join(", ", present)}");
        return present is [var name] ? ParseTiming(name, args.GetProperty(name)) : null;
    }

    private static ScheduleTiming ParseTiming(string name, JsonElement value) => name switch
    {
        AfterSeconds => new ScheduleTiming.After(Seconds(value, name)),
        EverySeconds => new ScheduleTiming.Every(Seconds(value, name)),
        At when value.ValueKind == JsonValueKind.String => new ScheduleTiming.AtInstant(value.GetString()!),
        At => new ScheduleTiming.AtLocal(Field(value, name, "date"), Field(value, name, "time"), Field(value, name, "time_zone")),
        Daily => new ScheduleTiming.Daily(Field(value, name, "time"), Field(value, name, "time_zone")),
        Weekly => new ScheduleTiming.Weekly(Field(value, name, "time"), Field(value, name, "time_zone"), Weekdays(value)),
        _ => new ScheduleTiming.Cron(Field(value, name, "expression"), Field(value, name, "time_zone")),
    };

    private static long Seconds(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            && double.IsInteger(number) && Math.Abs(number) <= MaxSafeInteger
            ? (long)number
            : throw new HarnessException($"{name} must be a whole number of seconds", ScheduleErrorCodes.InvalidRule);

    private static string Field(JsonElement value, string selector, string field)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new HarnessException($"{selector}.{field} must be a string", ScheduleErrorCodes.InvalidRule);

    private static IReadOnlyList<int> Weekdays(JsonElement value)
    {
        if (!value.TryGetProperty("weekdays", out var days) || days.ValueKind != JsonValueKind.Array
            || days.EnumerateArray().Any(day => !day.TryGetInt32(out _)))
            throw new HarnessException("weekly.weekdays must be an array of integers", ScheduleErrorCodes.InvalidRule);
        return [.. days.EnumerateArray().Select(day => day.GetInt32())];
    }

    private static string Id(JsonElement args)
    {
        var id = StringArg(args, "id");
        return id is { Length: > 0 } && id.Trim() == id
            ? id
            : throw new HarnessException("id must be non-empty without surrounding whitespace", ScheduleErrorCodes.InvalidRule);
    }

    private static string? StringArg(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static HarnessException Selector(string message) => new(message, ScheduleErrorCodes.InvalidSelector);

    private static JsonElement View(ScheduleRecord record, DateTimeOffset now) => Element(ViewNode(record, now));

    private static JsonObject ViewNode(ScheduleRecord record, DateTimeOffset now)
    {
        var rule = record.Rule;
        var view = new JsonObject
        {
            ["id"] = record.Id,
            ["title"] = record.Title,
            ["prompt"] = record.Prompt,
            ["kind"] = rule.Kind.ToString().ToLowerInvariant(),
            ["scheduledAt"] = ScheduleFraming.Instant(rule.ScheduledAt),
            ["state"] = rule.ScheduledAt > now ? "scheduled" : "overdue",
        };
        if (rule.AfterSeconds is { } afterSeconds)
            view["afterSeconds"] = afterSeconds;
        if (rule.EverySeconds is { } everySeconds)
            view["everySeconds"] = everySeconds;
        if (rule.Time is { } time)
            view["time"] = time;
        if (rule.TimeZone is { } timeZone)
            view["timeZone"] = timeZone;
        if (rule.Weekdays is { } weekdays)
            view["weekdays"] = new JsonArray([.. weekdays.Select(day => (JsonNode)day)]);
        if (rule.Expression is { } expression)
            view["expression"] = expression;
        return view;
    }

    private static ToolOutputDefinition Output(JsonObject schema) => new(schema, (_, value) => [new TextBlock(value.GetRawText())]);

    private static JsonElement Element(JsonNode node) => JsonDocument.Parse(node.ToJsonString(RelaxedJson)).RootElement;

    private static JsonObject Schema(string json) => JsonNode.Parse(json)!.AsObject();

    private sealed class RegistrationBundle(params IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}
