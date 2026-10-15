using System.Text.Json;
using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;

namespace Dsh.Interaction;

/**
 * Pre-execution guard for safety.blacklist.
 * Shell rules are matched against the tokenized simple commands of a bash/pwsh call; this is a best-effort tripwire, not a sandbox.
 */
public static class SafetyCommandGuard
{
    private const string BashTool = "bash";
    private const string PwshTool = "pwsh";
    private const string ToolPrefix = "tool:";

    public static IDisposable Register(Context ctx, SafetySettings? safety)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        if (safety?.Blacklist is not { Count: > 0 })
            return new NoopDisposable();
        var rules = safety.Blacklist.OrderBy(rule => rule.Length).Select(SafetyRule.Parse).ToList();
        return tools.Guard(exec =>
        {
            var command = ExtractCommand(exec);
            var segments = command is null ? [] : ShellCommandSegments.Split(command, DialectOf(exec.Name));
            var matched = rules.FirstOrDefault(rule => rule.Matches(exec.Name, segments));
            return matched is null ? null : $"blocked by safety.blacklist rule \"{matched.Text}\"";
        });
    }

    private static string? ExtractCommand(ToolExecution exec)
    {
        if (exec.Name is not (BashTool or PwshTool))
            return null;
        if (exec.Arguments.ValueKind != JsonValueKind.Object)
            return null;
        if (exec.Arguments.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String)
            return command.GetString();
        return null;
    }

    private static ShellDialect DialectOf(string toolName)
        => toolName == PwshTool ? ShellDialect.PowerShell : ShellDialect.Posix;

    /** "tool:<name>" blocks a tool by name; "bash:<pattern>" and "pwsh:<pattern>" block matching shell commands of that tool. */
    private sealed record SafetyRule(string Text, string? ToolName, ShellCommandPattern? Pattern)
    {
        public static SafetyRule Parse(string text)
        {
            if (text.StartsWith(ToolPrefix, StringComparison.OrdinalIgnoreCase))
                return new SafetyRule(text, text[ToolPrefix.Length..], null);
            foreach (var tool in (string[])[BashTool, PwshTool])
            {
                if (text.StartsWith($"{tool}:", StringComparison.OrdinalIgnoreCase))
                    return new SafetyRule(text, tool, ShellCommandPattern.Parse(text[(tool.Length + 1)..], DialectOf(tool)));
            }
            return new SafetyRule(text, null, null);
        }

        public bool Matches(string toolName, IReadOnlyList<IReadOnlyList<string>> segments)
            => ToolName == toolName && (Pattern is null || Pattern.Matches(segments));
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
