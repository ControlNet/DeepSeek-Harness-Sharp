using System.IO.Compression;
using System.Text;
using Dsh.Tui;
using Xunit;

namespace Dsh.Tests;

public class TerminalGraphicsTests
{
    [Fact]
    public void EncodeKittyRow_TransmitsAndPlaces()
    {
        var pixels = Pixels(2, 2);
        var text = EncodeRow(pixels, 2, 2, row: 0, rowHeight: 2, columns: 2);
        Assert.StartsWith("\x1b[1;1H", text, StringComparison.Ordinal);
        Assert.Contains("a=t", text, StringComparison.Ordinal);
        Assert.Contains("c=2", text, StringComparison.Ordinal);
        Assert.Contains("a=p", text, StringComparison.Ordinal);
        // kitty 协议里 t= 是"传输介质", 图像高度必须写成 v=(真 kitty 0.31 会因 t=<高> 报 Malformed GraphicsCommand)。
        Assert.Contains(",v=2", text, StringComparison.Ordinal);
        Assert.DoesNotContain(",t=", text, StringComparison.Ordinal);
        Assert.Equal(FlipRows(pixels, 2, 2), Decode(text));
    }

    [Fact]
    public void EncodeKittyRow_OnlyCoversItsOwnBand()
    {
        var pixels = Pixels(2, 4);
        var text = EncodeRow(pixels, 2, 4, row: 1, rowHeight: 2, columns: 2);
        Assert.StartsWith("\x1b[2;1H", text, StringComparison.Ordinal);
        Assert.Equal(Band(pixels, 2, 4, topY: 2, bandHeight: 2), Decode(text));
    }

    /** 载荷超过 4096 字符时必须分多个 ESC_G 块(m=1 … m=0), 且拼接后仍是同一条压缩流。 */
    [Fact]
    public void EncodeKittyRow_SplitsLongPayloadIntoChunks()
    {
        var rgba = new byte[400 * 16 * 4];
        new Random(20260916).NextBytes(rgba); // 随机 → 几乎不可压 → 载荷远超 4096 字符
        var text = EncodeRow(rgba, 400, 16, row: 0, rowHeight: 16, columns: 40);
        Assert.Contains("m=1", text, StringComparison.Ordinal);
        Assert.Contains("m=0", text, StringComparison.Ordinal);
        Assert.True(text.Split("\x1b_G", StringSplitOptions.None).Length - 1 > 2, "应有多块 ESC_G");
        Assert.Equal(FlipRows(rgba, 400, 16), Decode(text));
    }

    [Fact]
    public void Presenter_UnchangedFrame_WritesNothing()
    {
        var frame = Frame(8, 4, 2, [1, 2]);
        var writer = new StringWriter();
        var presenter = new TerminalGraphics.KittyRowPresenter();
        Assert.True(presenter.Present(frame, null, 8, 8, 4, 2, forceFull: true, reuse: true, writer));
        writer.GetStringBuilder().Clear();
        Assert.False(presenter.Present(frame, frame, 8, 8, 4, 2, forceFull: false, reuse: true, writer));
        Assert.Equal("", writer.ToString());
    }

    /** 滚动(两行内容对调): 内容已在终端缓存 → 只发 a=p 重放置, 完全不重传载荷。 */
    [Fact]
    public void Presenter_MovedContent_ReusesPlacementWithoutPayload()
    {
        var first = Frame(8, 4, 2, [1, 2]);
        var second = Frame(8, 4, 2, [2, 1]);
        var writer = new StringWriter();
        var presenter = new TerminalGraphics.KittyRowPresenter();
        presenter.Present(first, null, 8, 8, 4, 2, forceFull: true, reuse: true, writer);
        writer.GetStringBuilder().Clear();
        Assert.True(presenter.Present(second, first, 8, 8, 4, 2, forceFull: false, reuse: true, writer));
        var text = writer.ToString();
        Assert.DoesNotContain("a=t", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split("a=p", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Presenter_ReuseDisabled_AlwaysTransmits()
    {
        var first = Frame(8, 4, 2, [1, 2]);
        var second = Frame(8, 4, 2, [2, 1]);
        var writer = new StringWriter();
        var presenter = new TerminalGraphics.KittyRowPresenter();
        presenter.Present(first, null, 8, 8, 4, 2, forceFull: true, reuse: false, writer);
        writer.GetStringBuilder().Clear();
        presenter.Present(second, first, 8, 8, 4, 2, forceFull: false, reuse: false, writer);
        Assert.Equal(2, writer.ToString().Split("a=t", StringSplitOptions.None).Length - 1);
    }

    /**
     * 非复用终端(wezterm 等): "同 placement 换另一张图"会被终端忽略(真机验证), 所以行内容变化时
     * 必须复用该行自己的 imageId 同 id 重传载荷, placement id 保持不变(row+1), 且不新增 placement。
     */
    [Fact]
    public void Presenter_ReuseDisabled_KeepsRowImageIdOnContentChange()
    {
        var writer = new StringWriter();
        var presenter = new TerminalGraphics.KittyRowPresenter();
        presenter.Present(Frame(8, 4, 2, [1, 2]), null, 8, 8, 4, 2, forceFull: true, reuse: false, writer);
        var first = RowImageIds(writer.ToString());
        Assert.Equal([(1, first[0].Id), (2, first[1].Id)], first);
        writer.GetStringBuilder().Clear();

        presenter.Present(Frame(8, 4, 2, [3, 4]), null, 8, 8, 4, 2, forceFull: true, reuse: false, writer);
        var second = RowImageIds(writer.ToString());
        Assert.Equal(first, second);                                   // 同一行始终复用同一个 imageId
        Assert.Equal(1, writer.ToString().Split(",p=1,").Length - 1);
        Assert.Equal(1, writer.ToString().Split(",p=2,").Length - 1);
    }

    /** 终端行数变化(resize): 旧几何下的 placement 不会被新画面覆盖, 必须先整批删除(a=d,d=i)再重画。 */
    [Fact]
    public void Presenter_Resize_DeletesOldIds()
    {
        var writer = new StringWriter();
        var presenter = new TerminalGraphics.KittyRowPresenter();
        presenter.Present(Frame(8, 4, 2, [1, 2]), null, 8, 8, 4, 2, forceFull: true, reuse: true, writer);
        writer.GetStringBuilder().Clear();
        presenter.Present(Frame(8, 4, 3, [1, 2, 3]), null, 8, 12, 4, 2, forceFull: true, reuse: true, writer);
        Assert.Contains("a=d,d=i", writer.ToString(), StringComparison.Ordinal);
    }

    /** 从转义流里取出 (终端行, imageId) 对(AppendRow 会先移光标到该行, 随即发 a=t 带该 imageId)。 */
    private static List<(int Row, int Id)> RowImageIds(string text)
    {
        var result = new List<(int, int)>();
        var matches = System.Text.RegularExpressions.Regex.Matches(text, "\x1b\\[(\\d+);1H\x1b_Ga=t,i=(\\d+)");
        foreach (System.Text.RegularExpressions.Match match in matches)
            result.Add((int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)));
        return result;
    }

    [Fact]
    public void Presenter_NewContent_Transmits()
    {
        var first = Frame(8, 4, 2, [1, 2]);
        var second = Frame(8, 4, 2, [1, 3]);
        var writer = new StringWriter();
        var presenter = new TerminalGraphics.KittyRowPresenter();
        presenter.Present(first, null, 8, 8, 4, 2, forceFull: true, reuse: true, writer);
        writer.GetStringBuilder().Clear();
        presenter.Present(second, first, 8, 8, 4, 2, forceFull: false, reuse: true, writer);
        var text = writer.ToString();
        Assert.Equal(1, text.Split("a=t", StringSplitOptions.None).Length - 1);   // 只有新内容那行
        Assert.Contains("a=p", text, StringComparison.Ordinal);
    }

    /** 缓存满(行数+64)且新内容持续到来时, 应淘汰"未被任何行放置"的 id(发 a=d,d=i)。 */
    [Fact]
    public void Presenter_EvictsUnplacedId()
    {
        var writer = new StringWriter();
        var presenter = new TerminalGraphics.KittyRowPresenter();
        for (var i = 0; i < 70; i++)
        {
            var frame = Frame(8, 4, 1, [(byte)(i + 1)]);
            presenter.Present(frame, null, 8, 4, 4, 2, forceFull: true, reuse: true, writer);
        }

        Assert.Contains("a=d,d=i", writer.ToString(), StringComparison.Ordinal);
    }

    /** 造一帧: rows 个终端行, 每行整带填 bandValues[row]。缓冲自下而上, 故终端行 0 在缓冲最高处。 */
    private static byte[] Frame(int width, int cellHeight, int rows, byte[] bandValues)
    {
        var height = cellHeight * rows;
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < rows; row++)
        {
            var top = height - 1 - (row * cellHeight);
            var bottom = height - (row * cellHeight) - cellHeight;
            for (var y = bottom; y <= top; y++)
            {
                for (var x = 0; x < width * 4; x++)
                    pixels[(y * width * 4) + x] = bandValues[row];
            }
        }

        return pixels;
    }

    private static string EncodeRow(byte[] pixels, int width, int height, int row, int rowHeight, int columns)
    {
        var encoder = new TerminalGraphics.KittyRowEncoder();
        encoder.AppendRow(pixels, width, height, row, rowHeight, columns, TerminalGraphics.KittyRowImageBase + row);
        var writer = new StringWriter();
        encoder.WriteTo(writer);
        return writer.ToString();
    }

    [Fact]
    public void ParseKittyReply_OnlyOwnOkReply()
    {
        Assert.True(TerminalGraphics.ParseKittyReply("\x1b_Gi=7;OK\x1b\\", 7));
        Assert.False(TerminalGraphics.ParseKittyReply("\x1b_Gi=8;OK\x1b\\", 7));
        Assert.False(TerminalGraphics.ParseKittyReply("noise", 7));
        Assert.False(TerminalGraphics.ParseKittyReply("\x1b_Gi=7;ENOENT\x1b\\", 7));
    }

    [Fact]
    public void ParseSixelSupport_RequiresAttribute4()
    {
        Assert.True(TerminalGraphics.ParseSixelSupport("\x1b[?1;2;4c"));
        Assert.False(TerminalGraphics.ParseSixelSupport("\x1b[?1;2c"));
        Assert.False(TerminalGraphics.ParseSixelSupport("noise"));
    }

    [Fact]
    public void EncodeSixel_HasHeaderPaletteAndTerminator()
    {
        var text = TerminalGraphics.EncodeSixel(Pixels(2, 2), 2, 2);
        Assert.Contains("\x1bPq", text, StringComparison.Ordinal);
        Assert.Contains("\"1;1;2;2", text, StringComparison.Ordinal);
        Assert.Contains("#0;2;", text, StringComparison.Ordinal);
        Assert.EndsWith("\x1b\\", text, StringComparison.Ordinal);
    }

    private static byte[] Pixels(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 4;
                pixels[offset] = (byte)(y * 16 + x);
                pixels[offset + 1] = (byte)(0x40 + x);
                pixels[offset + 2] = (byte)(0x80 + y);
                pixels[offset + 3] = 0xFF;
            }
        }
        return pixels;
    }

    private static byte[] Band(byte[] pixels, int width, int height, int topY, int bandHeight)
    {
        var band = new byte[width * bandHeight * 4];
        for (var ty = 0; ty < bandHeight; ty++)
            Array.Copy(pixels, ((height - 1 - (topY + ty)) * width) * 4, band, ty * width * 4, width * 4);
        return band;
    }

    private static byte[] FlipRows(byte[] pixels, int width, int height)
    {
        var flipped = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
            Array.Copy(pixels, y * width * 4, flipped, ((height - 1 - y) * width) * 4, width * 4);
        return flipped;
    }

    /** 取出所有带载荷的 ESC_G 转义, 拼接 base64 后 zlib 解压。 */
    private static byte[] Decode(string text)
    {
        var payload = new StringBuilder();
        var cursor = 0;
        while (true)
        {
            var start = text.IndexOf("\x1b_G", cursor, StringComparison.Ordinal);
            if (start < 0)
                break;
            var end = text.IndexOf("\x1b\\", start, StringComparison.Ordinal);
            Assert.True(end > start, "kitty 转义未闭合");
            var semicolon = text.IndexOf(';', start);
            if (semicolon > start && semicolon < end)
                payload.Append(text, semicolon + 1, end - semicolon - 1);
            cursor = end + 2;
        }
        using var input = new MemoryStream(Convert.FromBase64String(payload.ToString()));
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

}
