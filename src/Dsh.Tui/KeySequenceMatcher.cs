namespace Dsh.Tui;

internal enum KeyMatchKind
{
    /** 没有等待中的前缀, 这个键也不是任何绑定的开头: 调用方照常处理它。 */
    None,
    /** 这个键是某些多键序列的前缀: 已记住, 等下一个键。 */
    Pending,
    /** 序列完整命中一个动作。 */
    Action,
    /** 前缀等待中按了接不上的键: 前缀作废, 这个键也一并消耗。 */
    Cancelled,
}

/** Typed 是到目前为止按下的序列(Pending 时用来生成提示, Action 时用来区分单键/序列)。 */
internal readonly record struct KeyMatch(KeyMatchKind Kind, TuiAction Action, IReadOnlyList<KeyChord> Typed)
{
    public static readonly KeyMatch None = new(KeyMatchKind.None, default, []);

    public static readonly KeyMatch Cancelled = new(KeyMatchKind.Cancelled, default, []);

    public bool IsSequence => Typed.Count > 1;
}

/**
 * 多键序列的前缀状态机: 逐键喂入, 返回命中/等待/取消/无关。
 * 前缀等待中后续键带着修饰键没有精确绑定时, 去掉修饰键再匹配一次(按住 Ctrl 连按 `Ctrl+X Ctrl+W` 等同 `Ctrl+X W`)。
 * timeoutMilliseconds > 0 时前缀等待超时作废, 之后的键按全新输入处理; 0 表示一直等到下一个键。
 */
internal sealed class KeySequenceMatcher(KeyBindingTable table, int timeoutMilliseconds)
{
    private readonly List<KeyChord> _pending = [];
    private long _deadline;

    public bool IsPending => _pending.Count > 0;

    public KeyMatch Feed(KeyChord? chord, KeyContext context, long now)
    {
        Expire(now);
        if (chord is not { } typed)
            return Cancel();
        var match = Match(typed, context, now);
        if (match is null && _pending.Count > 0)
            match = Match(typed.WithoutModifiers, context, now);
        return match ?? Cancel();
    }

    /** 等待超时则丢弃前缀; 返回 true 表示刚刚丢弃(调用方据此收起提示)。 */
    public bool Expire(long now)
    {
        if (_pending.Count == 0 || timeoutMilliseconds <= 0 || now < _deadline)
            return false;
        _pending.Clear();
        return true;
    }

    private KeyMatch? Match(KeyChord chord, KeyContext context, long now)
    {
        List<KeyChord> typed = [.. _pending, chord];
        var (action, isPrefix) = table.Lookup(typed, context);
        if (action is { } found)
        {
            _pending.Clear();
            return new KeyMatch(KeyMatchKind.Action, found, typed);
        }
        if (!isPrefix)
            return null;
        _pending.Add(chord);
        _deadline = now + timeoutMilliseconds;
        return new KeyMatch(KeyMatchKind.Pending, default, typed);
    }

    private KeyMatch Cancel()
    {
        if (_pending.Count == 0)
            return KeyMatch.None;
        _pending.Clear();
        return KeyMatch.Cancelled;
    }
}
