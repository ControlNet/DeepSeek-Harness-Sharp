using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dsh.Llm;

namespace Dsh.Schedule;

/**
 * Model-visible text of a delivered reminder.
 * Each dynamic field is a JSON string or a canonical UTC instant on its own line, so a prompt cannot forge framing lines.
 */
public static class ScheduleFraming
{
    public const string ScheduledMessageLine = "This is a scheduled message from the user";

    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static UserMessage Reminder(ScheduleRecord record, DateTimeOffset occurrenceAt)
    {
        var text = string.Join('\n',
            "[SCHEDULE REMINDER]",
            ScheduledMessageLine,
            $"schedule_id_json: {Json(writer => writer.WriteStringValue(record.Id))}",
            $"occurrence_at: {Instant(occurrenceAt)}",
            $"reminder_prompt_json: {Json(writer => writer.WriteStringValue(record.Prompt))}");
        return Message(text, $"reminder: {record.Title}");
    }

    public static UserMessage RecurringBatch(IReadOnlyList<(ScheduleRecord Record, DateTimeOffset OccurrenceAt)> reminders)
    {
        var payload = Json(writer =>
        {
            writer.WriteStartArray();
            foreach (var (record, occurrenceAt) in reminders)
            {
                writer.WriteStartObject();
                writer.WriteString("schedule_id", record.Id);
                writer.WriteString("occurrence_at", Instant(occurrenceAt));
                writer.WriteString("reminder_prompt", record.Prompt);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
        var text = string.Join('\n', "[SCHEDULE REMINDER BATCH]", ScheduledMessageLine, $"reminders_json: {payload}");
        return Message(text, $"reminders: {string.Join(", ", reminders.Select(reminder => reminder.Record.Title))}");
    }

    public static string Instant(DateTimeOffset instant) => instant.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture);

    private static UserMessage Message(string text, string summary)
        => MessageFactory.CreateUserMessage(
            [new TextBlock(text)],
            new PluginMessageSource(ScheduleService.MessagePlugin, ContextForms.Notice, Summary: MessageFactory.BoundContextSummary(summary)));

    private static string Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            write(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
