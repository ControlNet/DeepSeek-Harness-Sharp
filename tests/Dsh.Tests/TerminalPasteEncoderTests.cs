using System.Text;

namespace Dsh.Tests;

/** TerminalPasteEncoder: 换行归一与括号粘贴包裹。 */
public sealed class TerminalPasteEncoderTests
{
    [Fact]
    public void Plain_Paste_Normalizes_Line_Endings_To_Carriage_Return()
        => Assert.Equal("a\rb\rc", Encoding.UTF8.GetString(TerminalPasteEncoder.Encode("a\r\nb\nc", bracketed: false)));

    [Fact]
    public void Bracketed_Paste_Wraps_Content()
        => Assert.Equal("\e[200~中文\rx\e[201~", Encoding.UTF8.GetString(TerminalPasteEncoder.Encode("中文\nx", bracketed: true)));

    [Fact]
    public void Bracketed_Paste_Strips_Embedded_End_Marker()
        => Assert.Equal("\e[200~rm -rf\e[201~", Encoding.UTF8.GetString(TerminalPasteEncoder.Encode("rm\e[201~ -rf", bracketed: true)));
}
