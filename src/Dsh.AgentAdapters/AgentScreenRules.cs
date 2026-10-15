using System.Text.RegularExpressions;

namespace Dsh.AgentAdapters;

/** Interaction state read off a terminal screen; Unknown means no rule matched (always the case for raw terminal sessions). */
public enum AgentScreenState
{
    Unknown,
    Busy,
    WaitingForInput,
    WaitingForApproval,
}

/**
 * Regex rules over the last non-blank screen lines, checked in the order approval, busy, idle.
 * Approval dialogs reuse the idle prompt glyph for their menu cursor, and busy agents keep their input box visible,
 * so the order is what makes the plain idle patterns safe.
 */
public sealed record AgentScreenRules(IReadOnlyList<Regex> Approval, IReadOnlyList<Regex> Busy, IReadOnlyList<Regex> Idle)
{
    /** Enough to cover the tallest approval dialog observed (Claude Code, about 16 lines) plus the footer. */
    public const int TailLineCount = 30;

    public static AgentScreenRules None { get; } = new([], [], []);

    public static AgentScreenRules FromPatterns(
        IEnumerable<string> approval,
        IEnumerable<string> busy,
        IEnumerable<string> idle)
        => new(Compile(approval), Compile(busy), Compile(idle));

    public AgentScreenState Classify(IReadOnlyList<string> screenLines)
    {
        var tail = screenLines.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(TailLineCount).ToList();
        if (AnyMatch(Approval, tail))
            return AgentScreenState.WaitingForApproval;
        if (AnyMatch(Busy, tail))
            return AgentScreenState.Busy;
        return AnyMatch(Idle, tail) ? AgentScreenState.WaitingForInput : AgentScreenState.Unknown;
    }

    private static bool AnyMatch(IReadOnlyList<Regex> rules, List<string> lines)
        => rules.Any(rule => lines.Exists(rule.IsMatch));

    private static Regex[] Compile(IEnumerable<string> patterns)
        => patterns.Select(pattern => new Regex(pattern, RegexOptions.CultureInvariant)).ToArray();
}
