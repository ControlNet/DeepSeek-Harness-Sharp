using Dsh.Tui;

namespace Dsh.Tests;

/** 快捷键表: 按键规范化、按键串解析、配置覆盖与冲突校验、多键序列的前缀状态机。 */
public sealed class KeyBindingTableTests
{
    private static readonly KeyChord CtrlX = new(ConsoleModifiers.Control, "X");

    public static TheoryData<char, ConsoleKey, bool, bool, string> HostKeys => new()
    {
        { '\0', ConsoleKey.X, false, true, "Ctrl+X" },
        { '\u0018', ConsoleKey.X, false, true, "Ctrl+X" },
        { 'w', ConsoleKey.NoName, false, false, "W" },
        { 'W', ConsoleKey.W, false, false, "W" },
        { 'w', ConsoleKey.NoName, true, false, "Alt+W" },
        { '+', ConsoleKey.NoName, false, false, "+" },
        { '=', ConsoleKey.OemPlus, false, false, "+" },
        { '+', ConsoleKey.Add, false, false, "+" },
        { '-', ConsoleKey.Subtract, false, false, "-" },
        { '.', ConsoleKey.Decimal, false, false, "." },
        { '\u001c', ConsoleKey.NoName, false, true, "Ctrl+\\" },
        { '\0', ConsoleKey.UpArrow, false, false, "Up" },
        { '\u001b', ConsoleKey.Escape, false, false, "Esc" },
        { '\0', ConsoleKey.F5, false, false, "F5" },
        { '\0', ConsoleKey.D1, false, true, "Ctrl+1" },
    };

    [Theory]
    [MemberData(nameof(HostKeys))]
    public void Chord_From_Host_Key_Is_Canonical(char character, ConsoleKey key, bool alt, bool control, string expected)
    {
        var chord = KeyChord.From(new ConsoleKeyInfo(character, key, false, alt, control));

        Assert.Equal(expected, chord?.ToString());
    }

    [Fact]
    public void Chord_From_Bare_Modifier_Is_Null()
        => Assert.Null(KeyChord.From(new ConsoleKeyInfo('\0', ConsoleKey.NoName, false, false, true)));

    [Theory]
    [InlineData("Ctrl+X D", "Ctrl+X D")]
    [InlineData("ctrl+x   d", "Ctrl+X D")]
    [InlineData("Ctrl+X +", "Ctrl+X +")]
    [InlineData("Ctrl++", "Ctrl++")]
    [InlineData("Control+Alt+Escape", "Ctrl+Alt+Esc")]
    [InlineData("Alt+, Alt+Plus", "Alt+, Alt++")]
    [InlineData("F12", "F12")]
    [InlineData("Ctrl+X pgdn", "Ctrl+X PageDown")]
    public void Sequence_Parses_To_Canonical_Form(string text, string expected)
    {
        var sequence = KeyBindingTable.ParseSequence(text);

        Assert.NotNull(sequence);
        Assert.Equal(expected, KeyBindingTable.Format(sequence));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Shift+A")]
    [InlineData("Ctrl+Foo")]
    [InlineData("Ctrl+X Hyper+D")]
    public void Sequence_With_Unknown_Parts_Is_Rejected(string text)
        => Assert.Null(KeyBindingTable.ParseSequence(text));

    [Fact]
    public void Default_Table_Binds_Every_Action_And_Renders_Grouped_Keys()
    {
        var table = KeyBindingTable.Load(null).Table;

        Assert.All(Enum.GetValues<TuiAction>(), action => Assert.NotEmpty(table.Sequence(action)));
        Assert.Equal("Ctrl+X ,/.", table.Keys(TuiAction.PrevSession, TuiAction.NextSession));
        Assert.Equal("Ctrl+X 方向键/O", table.Keys(TuiAction.FocusLeft, TuiAction.FocusRight, TuiAction.FocusUp, TuiAction.FocusDown, TuiAction.FocusNextPane));
        Assert.Equal("Ctrl+X", table.Leader(TuiAction.SessionMenu));
        Assert.Equal("Ctrl+C", table.Leader(TuiAction.Interrupt));
        Assert.Equal("Ctrl+X W 总览 - + 分屏 - - 关闭", table.Line(new("总览", TuiAction.Overview), new("分屏", TuiAction.SplitPane), new("关闭", TuiAction.ClosePane)));
    }

    [Fact]
    public void Configured_Keys_Override_Defaults_And_Missing_Actions_Keep_Defaults()
    {
        var (table, warnings) = KeyBindingTable.Load(Keys(("Overview", "F5"), ("splitPane", "Ctrl+X Ctrl+S")));

        Assert.Empty(warnings);
        Assert.Equal("F5", KeyBindingTable.Format(table.Sequence(TuiAction.Overview)));
        Assert.Equal("Ctrl+X Ctrl+S", KeyBindingTable.Format(table.Sequence(TuiAction.SplitPane)));
        Assert.Equal("Ctrl+X N", KeyBindingTable.Format(table.Sequence(TuiAction.NewSession)));
        Assert.Equal("F5 总览 - Ctrl+X Ctrl+S 分屏 - Ctrl+X - 关闭", table.Line(new("总览", TuiAction.Overview), new("分屏", TuiAction.SplitPane), new("关闭", TuiAction.ClosePane)));
        Assert.Contains(table.Describe(), line => line.StartsWith("overview", StringComparison.Ordinal) && line.Contains("F5") && line.Contains("(自定义)"));
    }

    [Theory]
    [InlineData("nosuchAction", "Ctrl+X Z", "未知动作")]
    [InlineData("overview", "Ctrl+X Bogus", "无法解析")]
    [InlineData("overview", "G G", "吞掉正常输入")]
    public void Invalid_Entries_Warn_And_Keep_Defaults(string name, string keys, string reason)
    {
        var (table, warnings) = KeyBindingTable.Load(Keys((name, keys)));

        Assert.Contains(reason, Assert.Single(warnings));
        Assert.Equal("Ctrl+X W", KeyBindingTable.Format(table.Sequence(TuiAction.Overview)));
    }

    [Fact]
    public void Non_String_Value_Warns_And_Keeps_Default()
    {
        var (table, warnings) = KeyBindingTable.Load(new Dictionary<string, object?> { ["quit"] = 5L });

        Assert.Single(warnings);
        Assert.Equal("Ctrl+X Q", KeyBindingTable.Format(table.Sequence(TuiAction.Quit)));
    }

    [Fact]
    public void Same_Sequence_For_Two_Actions_Warns_And_Keeps_Default()
    {
        var (table, warnings) = KeyBindingTable.Load(Keys(("overview", "Ctrl+X N")));

        Assert.Contains("同时绑定了 newSession 与 overview", Assert.Single(warnings));
        Assert.Equal("Ctrl+X W", KeyBindingTable.Format(table.Sequence(TuiAction.Overview)));
        Assert.Equal("Ctrl+X N", KeyBindingTable.Format(table.Sequence(TuiAction.NewSession)));
    }

    [Theory]
    [InlineData("promptCommandMenu", "Ctrl+X", "Ctrl+P")]
    [InlineData("quit", "Ctrl+C X", "Ctrl+X Q")]
    public void Prefix_Conflict_Warns_And_Keeps_Default(string name, string keys, string expectedDefault)
    {
        var (table, warnings) = KeyBindingTable.Load(Keys((name, keys)));

        Assert.Contains("前缀", Assert.Single(warnings));
        var action = Enum.Parse<TuiAction>(name, ignoreCase: true);
        Assert.Equal(expectedDefault, KeyBindingTable.Format(table.Sequence(action)));
    }

    [Fact]
    public void Swapped_Keys_Are_Not_A_Conflict()
    {
        var (table, warnings) = KeyBindingTable.Load(Keys(("overview", "Ctrl+X N"), ("newSession", "Ctrl+X W")));

        Assert.Empty(warnings);
        Assert.Equal("Ctrl+X N", KeyBindingTable.Format(table.Sequence(TuiAction.Overview)));
        Assert.Equal("Ctrl+X W", KeyBindingTable.Format(table.Sequence(TuiAction.NewSession)));
    }

    /** 回退一项可能让它的默认键与另一项自定义冲突: 连锁回退直到没有冲突。 */
    [Fact]
    public void Reverting_A_Conflict_Cascades_Until_Consistent()
    {
        var (table, warnings) = KeyBindingTable.Load(Keys(("overview", "Ctrl+X N"), ("newSession", "Ctrl+X W"), ("agentList", "Ctrl+X W")));

        Assert.Equal(2, warnings.Count);
        Assert.Equal("Ctrl+X W", KeyBindingTable.Format(table.Sequence(TuiAction.Overview)));
        Assert.Equal("Ctrl+X N", KeyBindingTable.Format(table.Sequence(TuiAction.NewSession)));
        Assert.Equal("Ctrl+X A", KeyBindingTable.Format(table.Sequence(TuiAction.AgentList)));
    }

    [Fact]
    public void Matcher_Waits_On_Prefix_Then_Runs_The_Action()
    {
        var matcher = new KeySequenceMatcher(KeyBindingTable.Default, 0);

        var first = matcher.Feed(CtrlX, KeyContext.Chat, 0);
        var second = matcher.Feed(Chord("W"), KeyContext.Chat, 0);

        Assert.Equal(KeyMatchKind.Pending, first.Kind);
        Assert.Equal((KeyMatchKind.Action, TuiAction.Overview, true), (second.Kind, second.Action, second.IsSequence));
        Assert.False(matcher.IsPending);
    }

    [Fact]
    public void Matcher_Runs_Single_Key_Action_Without_Waiting()
    {
        var matcher = new KeySequenceMatcher(KeyBindingTable.Default, 0);

        var match = matcher.Feed(new KeyChord(ConsoleModifiers.Control, "C"), KeyContext.Chat, 0);

        Assert.Equal((KeyMatchKind.Action, TuiAction.Interrupt, false), (match.Kind, match.Action, match.IsSequence));
    }

    [Fact]
    public void Matcher_Ignores_Modifiers_Held_On_Continuation_Keys()
    {
        var matcher = new KeySequenceMatcher(KeyBindingTable.Default, 0);

        matcher.Feed(CtrlX, KeyContext.Chat, 0);
        var match = matcher.Feed(new KeyChord(ConsoleModifiers.Control, "W"), KeyContext.Chat, 0);

        Assert.Equal((KeyMatchKind.Action, TuiAction.Overview), (match.Kind, match.Action));
    }

    [Fact]
    public void Matcher_Cancels_On_Unbound_Continuation_And_Then_Passes_Keys_Through()
    {
        var matcher = new KeySequenceMatcher(KeyBindingTable.Default, 0);

        matcher.Feed(CtrlX, KeyContext.Chat, 0);
        var cancelled = matcher.Feed(Chord("Z"), KeyContext.Chat, 0);
        var next = matcher.Feed(Chord("Z"), KeyContext.Chat, 0);
        matcher.Feed(CtrlX, KeyContext.Chat, 0);
        var unknown = matcher.Feed(null, KeyContext.Chat, 0);

        Assert.Equal(KeyMatchKind.Cancelled, cancelled.Kind);
        Assert.Equal(KeyMatchKind.None, next.Kind);
        Assert.Equal(KeyMatchKind.Cancelled, unknown.Kind);
    }

    /** shell 焦点: 聊天专属动作(中断/命令选单)不拦, 窗口级序列照样拦。 */
    [Fact]
    public void Matcher_In_Shell_Context_Only_Sees_Window_Actions()
    {
        var matcher = new KeySequenceMatcher(KeyBindingTable.Default, 0);

        var interrupt = matcher.Feed(new KeyChord(ConsoleModifiers.Control, "C"), KeyContext.Shell, 0);
        var prefix = matcher.Feed(CtrlX, KeyContext.Shell, 0);

        Assert.Equal(KeyMatchKind.None, interrupt.Kind);
        Assert.Equal(KeyMatchKind.Pending, prefix.Kind);
    }

    [Fact]
    public void Matcher_Drops_Expired_Prefix_And_Treats_Next_Key_As_Fresh()
    {
        const int timeout = 1000;
        var matcher = new KeySequenceMatcher(KeyBindingTable.Default, timeout);

        matcher.Feed(CtrlX, KeyContext.Chat, 0);
        Assert.False(matcher.Expire(timeout - 1));
        var late = matcher.Feed(Chord("W"), KeyContext.Chat, timeout + 1);

        Assert.Equal(KeyMatchKind.None, late.Kind);
        matcher.Feed(CtrlX, KeyContext.Chat, 0);
        Assert.True(matcher.Expire(timeout));
        Assert.False(matcher.IsPending);
    }

    [Fact]
    public void Matcher_Without_Timeout_Waits_Indefinitely()
    {
        var matcher = new KeySequenceMatcher(KeyBindingTable.Default, 0);

        matcher.Feed(CtrlX, KeyContext.Chat, 0);

        Assert.False(matcher.Expire(long.MaxValue));
        Assert.Equal(KeyMatchKind.Action, matcher.Feed(Chord("W"), KeyContext.Chat, long.MaxValue).Kind);
    }

    [Fact]
    public void Matcher_Handles_Three_Key_Sequences()
    {
        var table = KeyBindingTable.Load(Keys(("quit", "Ctrl+X G Q"))).Table;
        var matcher = new KeySequenceMatcher(table, 0);

        var kinds = new[] { CtrlX, Chord("G"), Chord("Q") }.Select(chord => matcher.Feed(chord, KeyContext.Chat, 0)).ToList();
        matcher.Feed(CtrlX, KeyContext.Chat, 0);
        var oldQuit = matcher.Feed(Chord("Q"), KeyContext.Chat, 0);

        Assert.Equal([KeyMatchKind.Pending, KeyMatchKind.Pending, KeyMatchKind.Action], kinds.Select(match => match.Kind));
        Assert.Equal(TuiAction.Quit, kinds[^1].Action);
        Assert.Equal(KeyMatchKind.Cancelled, oldQuit.Kind);
        Assert.Equal("Ctrl+X: G Q 退出", table.PrefixHint([CtrlX], [new KeyHint("退出", TuiAction.Quit)]));
    }

    private static KeyChord Chord(string key) => new(ConsoleModifiers.None, key);

    private static Dictionary<string, object?> Keys(params (string Name, string Keys)[] entries)
        => entries.ToDictionary(entry => entry.Name, entry => (object?)entry.Keys);
}
