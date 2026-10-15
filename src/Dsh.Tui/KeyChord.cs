namespace Dsh.Tui;

/**
 * 一次按键的规范形态: 修饰键(只认 Ctrl/Alt) + 键名。
 * 键名统一成配置里写的样子: 字母大写、数字与标点取字符本身、功能键用名字(Up/Esc/F5 ...)。
 * Shift 不单独建模: 它已体现在字符里(大写字母、`+` 等), 各宿主上报 Shift 的方式也不一致。
 */
internal readonly record struct KeyChord(ConsoleModifiers Modifiers, string Key)
{
    public const ConsoleModifiers SupportedModifiers = ConsoleModifiers.Control | ConsoleModifiers.Alt;

    private const string CtrlName = "Ctrl";
    private const string AltName = "Alt";
    private const char ModifierSeparator = '+';
    /** Ctrl+\ .. Ctrl+_ 在 Unix 原始输入里是 0x1c..0x1f 的裸字符(没有 ConsoleKey), 加回该偏移得到可读字符。 */
    private const int ControlCharacterOffset = 0x40;
    private const char FirstUnnamedControlCharacter = '\u001c';

    /** 功能键: ConsoleKey → 键名; 解析配置时键名大小写不敏感。 */
    private static readonly Dictionary<ConsoleKey, string> NamedKeys = new()
    {
        [ConsoleKey.UpArrow] = "Up",
        [ConsoleKey.DownArrow] = "Down",
        [ConsoleKey.LeftArrow] = "Left",
        [ConsoleKey.RightArrow] = "Right",
        [ConsoleKey.Home] = "Home",
        [ConsoleKey.End] = "End",
        [ConsoleKey.PageUp] = "PageUp",
        [ConsoleKey.PageDown] = "PageDown",
        [ConsoleKey.Insert] = "Insert",
        [ConsoleKey.Delete] = "Delete",
        [ConsoleKey.Enter] = "Enter",
        [ConsoleKey.Escape] = "Esc",
        [ConsoleKey.Tab] = "Tab",
        [ConsoleKey.Backspace] = "Backspace",
        [ConsoleKey.Spacebar] = "Space",
    };

    /**
     * 这四个 ConsoleKey 一律按键位认, 不看字符: Windows 控制台不按 Shift 时给的是 OemPlus + '=' 这类组合,
     * 小键盘的 Add/Subtract 也要和主键盘同义。
     */
    private static readonly Dictionary<ConsoleKey, string> PunctuationKeys = new()
    {
        [ConsoleKey.OemPlus] = "+",
        [ConsoleKey.Add] = "+",
        [ConsoleKey.OemMinus] = "-",
        [ConsoleKey.Subtract] = "-",
        [ConsoleKey.OemComma] = ",",
        [ConsoleKey.OemPeriod] = ".",
    };

    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Escape"] = "Esc",
        ["Return"] = "Enter",
        ["Del"] = "Delete",
        ["Ins"] = "Insert",
        ["PgUp"] = "PageUp",
        ["PgDn"] = "PageDown",
        ["Spacebar"] = "Space",
        ["Plus"] = "+",
        ["Minus"] = "-",
        ["Comma"] = ",",
        ["Period"] = ".",
    };

    private static readonly HashSet<string> CanonicalNames = new(
        [.. NamedKeys.Values, .. Enumerable.Range(1, ConsoleKey.F24 - ConsoleKey.F1 + 1).Select(index => $"F{index}")],
        StringComparer.OrdinalIgnoreCase);

    public bool IsFunctionKey => Key.Length > 1 && Key[0] == 'F' && int.TryParse(Key.AsSpan(1), out _);

    public bool HasCommandModifier => (Modifiers & SupportedModifiers) != 0;

    public KeyChord WithoutModifiers => this with { Modifiers = ConsoleModifiers.None };

    /** 宿主上报的按键 → 规范形态; 认不出键名(纯修饰键、未知控制字符)返回 null。 */
    public static KeyChord? From(ConsoleKeyInfo info)
    {
        var modifiers = info.Modifiers & SupportedModifiers;
        if (info.Key == ConsoleKey.NoName && info.KeyChar is >= FirstUnnamedControlCharacter and < ' ')
            return new KeyChord(modifiers | ConsoleModifiers.Control, ((char)(info.KeyChar + ControlCharacterOffset)).ToString());
        return NameOf(info) is { } name ? new KeyChord(modifiers, name) : null;
    }

    /** 解析一个按键(如 `Ctrl+X`、`Alt+,`、`Ctrl++`、`F5`); 语法不对返回 null。 */
    public static KeyChord? Parse(string text)
    {
        var token = text.Trim();
        if (token.Length == 0)
            return null;
        // 末尾的 '+' 是"加号键"本身(`+`、`Ctrl++`), 不是分隔符。
        var plusKey = token[^1] == ModifierSeparator;
        var body = plusKey ? token[..^1].TrimEnd(ModifierSeparator) : token;
        var parts = body.Split(ModifierSeparator);
        var keyName = plusKey ? "+" : parts[^1];
        var modifierParts = plusKey ? parts.Where(part => part.Length > 0) : parts[..^1];
        var modifiers = ConsoleModifiers.None;
        foreach (var part in modifierParts)
        {
            if (ParseModifier(part) is not { } modifier)
                return null;
            modifiers |= modifier;
        }
        return CanonicalKey(keyName) is { } key ? new KeyChord(modifiers, key) : null;
    }

    public override string ToString()
    {
        var prefix = (Modifiers & ConsoleModifiers.Control) != 0 ? $"{CtrlName}+" : "";
        prefix += (Modifiers & ConsoleModifiers.Alt) != 0 ? $"{AltName}+" : "";
        return $"{prefix}{Key}";
    }

    private static ConsoleModifiers? ParseModifier(string text)
        => text.Trim().ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => ConsoleModifiers.Control,
            "ALT" => ConsoleModifiers.Alt,
            _ => null,
        };

    private static string? CanonicalKey(string text)
    {
        var name = text.Trim();
        if (KeyAliases.TryGetValue(name, out var alias))
            return alias;
        if (name.Length == 1)
            return char.IsWhiteSpace(name[0]) ? null : char.ToUpperInvariant(name[0]).ToString();
        return CanonicalNames.TryGetValue(name, out var canonical) ? canonical : null;
    }

    private static string? NameOf(ConsoleKeyInfo info)
    {
        if (info.Key is >= ConsoleKey.A and <= ConsoleKey.Z)
            return ((char)info.Key).ToString();
        if (info.Key is >= ConsoleKey.F1 and <= ConsoleKey.F24)
            return $"F{info.Key - ConsoleKey.F1 + 1}";
        if (PunctuationKeys.TryGetValue(info.Key, out var punctuation))
            return punctuation;
        if (NamedKeys.TryGetValue(info.Key, out var named))
            return named;
        if (info.KeyChar > ' ' && !char.IsControl(info.KeyChar))
            return char.ToUpperInvariant(info.KeyChar).ToString();
        // Ctrl+数字在 Windows 控制台不带字符, 只剩键位。
        return info.Key is >= ConsoleKey.D0 and <= ConsoleKey.D9 ? ((char)('0' + (info.Key - ConsoleKey.D0))).ToString() : null;
    }
}
