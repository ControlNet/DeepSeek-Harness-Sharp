namespace Dsh.Tui;

/**
 * 转录行的着色提示: diff 卡片(diff 标题/新增行/删除行)与错误行(失败的工具结果/回合失败), 其余行为 None。
 * 具体颜色由 TuiTheme 决定, 这里只标记语义。
 */
internal enum TranscriptTint
{
    None,
    DiffTitle,
    DiffAdded,
    DiffRemoved,
    Error,
}
