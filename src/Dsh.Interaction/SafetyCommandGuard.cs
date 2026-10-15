using System.Text.Json;
using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;

namespace Dsh.Interaction;

/**
 * Pre-execution guard for safety.blacklist.
 * Shell rules are matched against the tokenized simple commands of a shell tool call; this is a best-effort tripwire, not a sandbox.
 */
public static class SafetyCommandGuard
{
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
            var segments = command is null ? [] : ShellCommandSegments.Split(command, ShellToolDialects.ByToolName[exec.Name]);
            var matched = rules.FirstOrDefault(rule => rule.Matches(exec.Name, segments));
            return matched is null ? null : $"blocked by safety.blacklist rule \"{matched.Text}\"";
        });
    }

    private static string? ExtractCommand(ToolExecution exec)
    {
        if (!ShellToolDialects.ByToolName.ContainsKey(exec.Name))
            return null;
        if (exec.Arguments.ValueKind != JsonValueKind.Object)
            return null;
        if (exec.Arguments.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String)
            return command.GetString();
        return null;
    }

    /**
     * "tool:<name>" blocks a tool by name.
     * "<shell tool>:<pattern>" (e.g. "bash:", "pwsh:") blocks matching commands in every shell tool of that tool's dialect, so a POSIX rule also covers bash_persistent and wsl.
     */
    private sealed record SafetyRule(string Text, string? ToolName, ShellDialect? Dialect, ShellCommandPattern? Pattern)
    {
        public static SafetyRule Parse(string text)
        {
            if (text.StartsWith(ToolPrefix, StringComparison.OrdinalIgnoreCase))
                return new SafetyRule(text, text[ToolPrefix.Length..], null, null);
            foreach (var (tool, dialect) in ShellToolDialects.ByToolName)
            {
                if (text.StartsWith($"{tool}:", StringComparison.OrdinalIgnoreCase))
                    return new SafetyRule(text, null, dialect, ShellCommandPattern.Parse(text[(tool.Length + 1)..], dialect));
            }
            return new SafetyRule(text, null, null, null);
        }

        public bool Matches(string toolName, IReadOnlyList<IReadOnlyList<string>> segments)
            => Pattern is null
                ? ToolName == toolName
                : ShellToolDialects.ByToolName.TryGetValue(toolName, out var dialect) && dialect == Dialect && Pattern.Matches(segments);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
