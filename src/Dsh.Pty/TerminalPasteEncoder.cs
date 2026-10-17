using System.Text;

namespace Dsh.Pty;

/** 把剪贴板文本编码成送给子进程的粘贴字节。 */
public static class TerminalPasteEncoder
{
    private const string PasteStart = "\u001b[200~";
    private const string PasteEnd = "\u001b[201~";

    /** 换行统一成回车(终端里 Enter 发的就是 CR); 括号粘贴模式下包上起止标记, 并剔除内容里伪造的标记, 防止粘贴内容提前结束粘贴后被当成按键执行。 */
    public static byte[] Encode(string text, bool bracketed)
    {
        var normalized = text.ReplaceLineEndings("\r");
        if (!bracketed)
            return Encoding.UTF8.GetBytes(normalized);
        var sanitized = normalized.Replace(PasteStart, "", StringComparison.Ordinal).Replace(PasteEnd, "", StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes($"{PasteStart}{sanitized}{PasteEnd}");
    }
}
