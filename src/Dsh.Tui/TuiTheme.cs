namespace Dsh.Tui;

/**
 * TUI 主题色(运行期可变): 由 TuiSettings 载入, 供渲染时引用, 支持真彩("#rrggbb")与 16 色板。
 * 每个角色的缺省值等于改造前的硬编码外观; 设为 AnsiColor.Default 即"不着色"。
 */
public static class TuiTheme
{
    /** 高亮(聚焦窗格标题/浮层标题与选中项/右栏小节标题)。 */
    public static CellColor Highlight { get; set; } = CellColor.FromPalette(AnsiColor.BrightCyan);

    /** 聚焦窗格行首箭头("> ")。 */
    public static CellColor Arrow { get; set; } = CellColor.FromPalette(AnsiColor.BrightCyan);

    /** 分隔线(主区分隔/窗格分隔/右栏小节分隔)。 */
    public static CellColor Divider { get; set; } = CellColor.Default;

    /** 状态栏文本。 */
    public static CellColor Status { get; set; } = CellColor.Default;

    /** 错误文本(失败的工具结果卡片/回合失败)。 */
    public static CellColor Error { get; set; } = CellColor.FromPalette(AnsiColor.Red);

    public static void Load(Services.TuiSettings settings)
    {
        Highlight = settings.Highlight;
        Arrow = settings.Arrow;
        Divider = settings.Divider;
        Status = settings.Status;
        Error = settings.Error;
    }
}
