using System.Text;

namespace Dsh.Gui.Services;

/**
 * 远端文件字节与可编辑文本之间的往返: 识别 BOM 决定编码, 无 BOM 时按严格 UTF-8 解码。
 * 无 BOM 且前 SniffBytes 字节里有 NUL、或不是合法 UTF-8 的内容视为二进制(与 git 判断二进制的办法一致), 不进编辑器。
 * 保存时写回同一编码与同样的 BOM; 换行原样保留在文本里, 所以 CRLF 文件保存后仍是 CRLF。
 */
public sealed record TextFileContent(string Text, Encoding Encoding, bool HasBom)
{
    /** git 的 buffer_is_binary 只看前 8000 字节。 */
    public const int SniffBytes = 8000;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Encoding[] BomEncodings =
    [
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
        new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true),
        new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true),
    ];

    /** 不是文本(含 NUL 或不能按其编码解码)时返回 null。 */
    public static TextFileContent? TryDecode(ReadOnlySpan<byte> bytes)
    {
        foreach (var encoding in BomEncodings)
        {
            var bom = encoding.Preamble;
            if (bytes.StartsWith(bom))
                return Decode(bytes[bom.Length..], encoding, hasBom: true);
        }

        return bytes[..Math.Min(bytes.Length, SniffBytes)].Contains((byte)0) ? null : Decode(bytes, StrictUtf8, hasBom: false);
    }

    public byte[] Encode(string text)
    {
        var preamble = HasBom ? Encoding.Preamble : ReadOnlySpan<byte>.Empty;
        var bytes = new byte[preamble.Length + Encoding.GetByteCount(text)];
        preamble.CopyTo(bytes);
        Encoding.GetBytes(text, bytes.AsSpan(preamble.Length));
        return bytes;
    }

    private static TextFileContent? Decode(ReadOnlySpan<byte> body, Encoding encoding, bool hasBom)
    {
        try
        {
            return new TextFileContent(encoding.GetString(body), encoding, hasBom);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
