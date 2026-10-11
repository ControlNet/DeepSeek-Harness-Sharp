using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Dsh.Tui;

/** 终端图像协议: kitty graphics 优先, sixel 兜底; 都不支持时调用方回退 CPU(ANSI)渲染。 */
internal enum TerminalGraphicsProtocol
{
    None,
    Kitty,
    Sixel,
}

/**
 * 终端图像协议的编码与能力探测: 把 GL 读回的 RGBA 帧贴到终端。
 * kitty 按终端行分块(只重传变化的行, 高帧率); sixel 整帧重发(无局部更新语义)。
 */
internal static class TerminalGraphics
{
    private const int QueryId = 0x6473683F;
    private const int KittyTimeoutMilliseconds = 300;

    /** 环境变量能直接判定的 kitty 支持情况(免查询): kitty / wezterm / ghostty。 */
    public static bool EnvSupportsKitty()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KITTY_WINDOW_ID")))
            return true;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEZTERM_PANE")))
            return true;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GHOSTTY_RESOURCES_DIR")))
            return true;
        var term = Environment.GetEnvironmentVariable("TERM") ?? "";
        return term.Contains("kitty", StringComparison.OrdinalIgnoreCase)
            || term.Contains("wezterm", StringComparison.OrdinalIgnoreCase);
    }

    /**
     * 该终端是否已确认支持"同一图像多 placement + 重放置即移动"(#2 行图复用的前提)。
     * 目前只在 kitty 0.31 真机验证过; wezterm 未验证 → 保守返回 false(退化为每变化行重传)。
     */
    public static bool SupportsPlacementReuse()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEZTERM_PANE")))
            return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KITTY_WINDOW_ID")))
            return true;
        var term = Environment.GetEnvironmentVariable("TERM") ?? "";
        return term.Contains("kitty", StringComparison.OrdinalIgnoreCase);
    }

    /**
     * 探测协议: 环境命中直接判 kitty; 否则同时发 kitty 查询与 DA1(设备属性)查询并等应答。
     */
    public static TerminalGraphicsProtocol Detect(out string reason)
    {
        if (EnvSupportsKitty())
        {
            reason = "";
            return TerminalGraphicsProtocol.Kitty;
        }
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            reason = "终端输入/输出被重定向, 无法探测图像协议";
            return TerminalGraphicsProtocol.None;
        }
        try
        {
            using var raw = TerminalRawMode.TryEnable();
            Console.Out.Write($"\x1b_Gi={QueryId},s=1,v=1,a=q,t=d,f=24;AAAA\x1b\\\x1b[c");
            Console.Out.Flush();
            var reply = ReadReply();
            if (ParseKittyReply(reply, QueryId))
            {
                reason = "";
                return TerminalGraphicsProtocol.Kitty;
            }
            if (ParseSixelSupport(reply))
            {
                reason = "";
                return TerminalGraphicsProtocol.Sixel;
            }
            reason = $"终端不支持 kitty graphics / sixel(终端应答: {Escape(reply)})";
            return TerminalGraphicsProtocol.None;
        }
        catch (IOException error)
        {
            reason = $"图像协议探测失败: {error.Message}";
            return TerminalGraphicsProtocol.None;
        }
    }

    /** kitty 应答形如 ESC _ G i=<id> ; OK ESC \; 只认自身 id。 */
    internal static bool ParseKittyReply(string reply, int id)
    {
        var start = reply.IndexOf("\x1b_G", StringComparison.Ordinal);
        if (start < 0)
            return false;
        var end = reply.IndexOf("\x1b\\", start, StringComparison.Ordinal);
        if (end < 0)
            return false;
        var body = reply[(start + 3)..end];
        return body.Contains($"i={id}", StringComparison.Ordinal)
            && body.Contains(";OK", StringComparison.Ordinal);
    }

    /** DA1 应答形如 ESC [ ? 1 ; 2 ; 4 c; 参数含 4 表示 sixel。 */
    internal static bool ParseSixelSupport(string reply)
    {
        var start = reply.IndexOf("\x1b[?", StringComparison.Ordinal);
        if (start < 0)
            return false;
        var end = reply.IndexOf('c', start);
        if (end < 0)
            return false;
        var parameters = reply[(start + 3)..end].Split(';');
        return parameters.Contains("4", StringComparer.Ordinal);
    }

    /**
     * kitty 行图编码器(可复用, 零分配): 把一行带 zlib 压缩 → base64 → 按 4096 字符分块转义,
     * 直接写进复用的 char 缓冲。输出与原先 `EncodeKittyRow` 逐字符一致, 但不再产生
     * base64 string / StringBuilder / byte[](每行只余一个 ZLibStream 对象)。
     */
    internal sealed class KittyRowEncoder
    {
        private const int PayloadChunk = 4096;
        private const int EscapeSlack = 256;

        private char[] _output = new char[128 * 1024];
        private int _length;
        private char[] _payload = new char[8 * 1024];
        private readonly MemoryStream _sink = new(256 * 1024);

        /** 已累积的待输出字符数。 */
        public int Length => _length;

        public void Reset() => _length = 0;

        /** 追加一行的完整转义(传输+放置; imageId 由呈现器分配, 同一内容跨行复用时相同)。 */
        public void AppendRow(byte[] rgba, int width, int height, int row, int rowHeight, int columns, int imageId)
        {
            CompressBand(rgba, width, height, row * rowHeight, rowHeight);
            var compressed = _sink.GetBuffer();
            var compressedLength = (int)_sink.Length;
            var maxPayload = ((compressedLength + 2) / 3) * 4;
            if (_payload.Length < maxPayload)
                _payload = new char[maxPayload];
            var payloadLength = Convert.ToBase64CharArray(compressed, 0, compressedLength, _payload, 0);

            var chunks = Math.Max(1, (payloadLength + PayloadChunk - 1) / PayloadChunk);
            EnsureCapacity(payloadLength + (chunks * 16) + EscapeSlack);
            Append("\x1b[");
            AppendNumber(row + 1);
            Append(";1H");
            for (var index = 0; index < chunks; index++)
            {
                var offset = index * PayloadChunk;
                var count = Math.Min(PayloadChunk, payloadLength - offset);
                var more = index == chunks - 1 ? 0 : 1;
                if (index == 0)
                {
                    // 目前用 a=t(传输) + 单独的 a=p(放置): 已在 kitty 0.31 与 wezterm 20240203 真机验证(都是分块发送,
                    // 单条载荷必须 ≤4096 字符, 否则终端报 payload too large 并丢弃)。a=T(传输并显示) 两家也都支持,
                    // 但 #2 的行图复用需要独立的 placement, 故保持 a=t+a=p。
                    Append("\x1b_Ga=t,i=");
                    AppendNumber(imageId);
                    Append(",f=32,o=z,s=");
                    AppendNumber(width);
                    Append(",v=");
                    AppendNumber(rowHeight);
                    Append(",q=2,m=");
                }
                else
                {
                    Append("\x1b_Gm=");
                }
                AppendNumber(more);
                Append(";");
                Append(_payload.AsSpan(offset, count));
                Append("\x1b\\");
            }
            Append("\x1b_Ga=p,i=");
            AppendNumber(imageId);
            Append(",p=");
            AppendNumber(row + 1);
            Append(",c=");
            AppendNumber(columns);
            Append(",r=1,q=2,C=1\x1b\\");
        }

        /** 只发"重放置"(不重传载荷): 把已存在于终端缓存里的 imageId 放到该行(每行独立 placement id = row+1)。 */
        public void AppendPlacement(int row, int columns, int imageId)
        {
            EnsureCapacity(EscapeSlack + 64);
            Append("\x1b[");
            AppendNumber(row + 1);
            Append(";1H\x1b_Ga=p,i=");
            AppendNumber(imageId);
            Append(",p=");
            AppendNumber(row + 1);
            Append(",c=");
            AppendNumber(columns);
            Append(",r=1,q=2,C=1\x1b\\");
        }

        /** 从终端删除某个 imageId(淘汰时用, 只对本呈现器分配过的 id)。 */
        public void AppendDelete(int imageId)
        {
            EnsureCapacity(EscapeSlack + 32);
            Append("\x1b_Ga=d,d=i,i=");
            AppendNumber(imageId);
            Append(",q=2\x1b\\");
        }

        /** 一次性写出并清空(调用方负责 Flush)。 */
        public void WriteTo(TextWriter writer)
        {
            if (_length == 0)
                return;
            writer.Write(_output.AsSpan(0, _length));
            _length = 0;
        }

        /** 取自上而下的第 topY 行起、高 bandHeight 的像素带并压缩(GL/Vk 缓冲自下而上, 需换算并按上>下写入)。 */
        private void CompressBand(byte[] rgba, int width, int height, int topY, int bandHeight)
        {
            _sink.SetLength(0);
            using var zlib = new ZLibStream(_sink, CompressionLevel.Fastest, leaveOpen: true);
            var top = height - 1 - topY;
            var bottom = Math.Max(0, height - topY - bandHeight);
            for (var y = top; y >= bottom; y--)
                zlib.Write(rgba.AsSpan(y * width * 4, width * 4));
        }

        private void Append(string text) => Append(text.AsSpan());

        private void Append(ReadOnlySpan<char> text)
        {
            text.CopyTo(_output.AsSpan(_length));
            _length += text.Length;
        }

        private void AppendNumber(int value)
        {
            if (!value.TryFormat(_output.AsSpan(_length), out var written))
                throw new InvalidOperationException("整型格式化失败");
            _length += written;
        }

        private void EnsureCapacity(int extra)
        {
            if (_length + extra <= _output.Length)
                return;
            var grown = new char[Math.Max(_output.Length * 2, _length + extra)];
            _output.AsSpan(0, _length).CopyTo(grown);
            _output = grown;
        }
    }

    /**
     * kitty 行图呈现器(状态化): 维护"内容哈希 → imageId"缓存与"每行当前 imageId/内容哈希"。
     * 变化行若其内容已在终端缓存里(滚动/重复内容最常见), 只发 a=p 重放置而**不重传载荷**;
     * 淘汰只删"当前未被任何行放置"的 id, 避免把屏上内容删空;
     * reuse=false 时退化为"每变化行都重传+zlib"(与旧行为一致, 保守)。
     */
    internal sealed class KittyRowPresenter
    {
        private readonly Dictionary<ulong, int> _idByHash = [];
        private readonly Queue<(ulong Hash, int Id)> _order = [];
        private readonly KittyRowEncoder _encoder = new();
        private int[] _rowImageId = [];
        private int _rowCount;
        private int _nextId = 1;

        /** 呈现一帧。pixelRows 非空时只处理这些像素行(其余行本次未回读, 必须跳过); 返回是否写了内容。 */
        public bool Present(byte[] pixels, byte[]? previous, int width, int height, int cellHeight, int columns, bool forceFull, bool reuse, TextWriter writer, ReadOnlySpan<(int FirstRow, int RowCount)> pixelRows = default)
        {
            cellHeight = Math.Max(1, cellHeight);
            var rows = Math.Max(1, height / cellHeight);
            if (rows != _rowCount)
            {
                // 行数变化(终端 resize): 旧几何下的 placement 不会被新画面覆盖, 先整批删除再重画。
                DeleteAllIds();
                _rowCount = rows;
                _rowImageId = new int[rows];
            }

            // 注意: _rowImageId 跨帧保留"该行当前在屏上显示的 imageId", 不随帧清空。
            // 它是淘汰判定(IsPlaced)与"同 id 原地重传"的依据; 清空会导致仍在屏上的图被误删。
            for (var row = 0; row < _rowCount; row++)
            {
                if (!pixelRows.IsEmpty && !RowInRanges(row, cellHeight, pixelRows))
                    continue;
                if (!forceFull && previous is not null && BandEquals(pixels, previous, width, height, row, cellHeight))
                    continue;

                var hash = HashBand(pixels, width, height, row, cellHeight);
                var current = _rowImageId[row];
                if (reuse && _idByHash.TryGetValue(hash, out var shared))
                {
                    if (shared != current)
                    {
                        // 复用: 只重放置已缓存图像。依赖"同 placement 换另一张图 = 替换", 仅对已验证该语义的终端开启。
                        _encoder.AppendPlacement(row, columns, shared);
                        _rowImageId[row] = shared;
                    }

                    continue;
                }

                if (!reuse && current != 0)
                {
                    // 未验证"同 placement 换图"的终端(wezterm 会忽略这种放置): 该行固定复用它自己的 id,
                    // 内容变化时同 id 重传载荷 → 终端原地刷新该行(真机验证: kitty/wezterm 都生效)。
                    _encoder.AppendRow(pixels, width, height, row, cellHeight, columns, current);
                    _idByHash[hash] = current;
                    continue;
                }

                var id = Allocate(hash, _rowCount);
                _encoder.AppendRow(pixels, width, height, row, cellHeight, columns, id);
                _rowImageId[row] = id;
            }

            if (_encoder.Length == 0)
                return false;
            _encoder.WriteTo(writer);
            return true;
        }

        /** 退出/重开场: 删除本呈现器分配过的全部 imageId。 */
        public void DeleteAll(TextWriter writer)
        {
            _encoder.Reset();
            DeleteAllIds();
            _rowCount = 0;
            _encoder.WriteTo(writer);
        }

        /** 发 a=d,d=i 删掉本呈现器分配(缓存)或放置(各行当前显示)过的全部 imageId, 并清空状态。 */
        private void DeleteAllIds()
        {
            foreach (var id in _idByHash.Values)
                _encoder.AppendDelete(id);
            foreach (var id in _rowImageId)
            {
                if (id != 0)
                    _encoder.AppendDelete(id);
            }

            _idByHash.Clear();
            _order.Clear();
            _nextId = 1;
            Array.Clear(_rowImageId);
        }

        private int Allocate(ulong hash, int rowCount)
        {
            var cap = rowCount + 64;
            if (_idByHash.Count >= cap)
                Evict();
            var id = KittyRowImageBase + _nextId++;
            _idByHash[hash] = id;
            _order.Enqueue((hash, id));
            return id;
        }

        /** 淘汰一个"未被任何行放置"的最老 id(单趟扫描队列; 全被引用则本帧不淘汰)。 */
        private void Evict()
        {
            var scans = _order.Count;
            while (scans-- > 0 && _order.Count > 0)
            {
                var (hash, id) = _order.Dequeue();
                if (!_idByHash.TryGetValue(hash, out var current) || current != id)
                    continue;
                if (IsPlaced(id))
                {
                    _order.Enqueue((hash, id));
                    continue;
                }

                _idByHash.Remove(hash);
                _encoder.AppendDelete(id);
                return;
            }
        }

        private bool IsPlaced(int id)
        {
            foreach (var placed in _rowImageId)
            {
                if (placed == id)
                    return true;
            }

            return false;
        }

        /** 终端行 row 的像素带是否落在给定像素行 ranges 内。 */
        private static bool RowInRanges(int row, int cellHeight, ReadOnlySpan<(int FirstRow, int RowCount)> ranges)
        {
            var first = row * cellHeight;
            var last = first + cellHeight - 1;
            foreach (var (rangeFirst, rangeCount) in ranges)
            {
                if (first < rangeFirst + rangeCount && last >= rangeFirst)
                    return true;
            }

            return false;
        }

        /** 终端行 row(自上而下) 在缓冲(自下而上)里的像素带是否逐字节相同。 */
        private static bool BandEquals(byte[] current, byte[] previous, int width, int height, int row, int cellHeight)
        {
            var (start, count) = BandRange(width, height, row, cellHeight);
            return count <= 0 || current.AsSpan(start, count).SequenceEqual(previous.AsSpan(start, count));
        }

        /** 终端行 row 的像素带哈希(FNV-1a 64, 按 8 字节整块); 带在内存里是连续一段。 */
        private static ulong HashBand(byte[] pixels, int width, int height, int row, int cellHeight)
        {
            var (start, count) = BandRange(width, height, row, cellHeight);
            var span = pixels.AsSpan(start, count);
            var words = MemoryMarshal.Cast<byte, ulong>(span);
            var hash = 14695981039346656037UL;
            foreach (var word in words)
            {
                hash ^= word;
                hash *= 1099511628211UL;
            }

            for (var i = words.Length * sizeof(ulong); i < span.Length; i++)
            {
                hash ^= span[i];
                hash *= 1099511628211UL;
            }

            return hash;
        }

        /** 终端行 row 对应缓冲里的字节区间(缓冲自下而上; 与编码器 CompressBand 同一映射)。 */
        private static (int Start, int Count) BandRange(int width, int height, int row, int cellHeight)
        {
            var top = height - 1 - (row * cellHeight);
            var bottom = Math.Max(0, height - (row * cellHeight) - cellHeight);
            if (top < bottom)
                return (0, 0);
            var start = bottom * width * 4;
            return (start, ((top - bottom) + 1) * width * 4);
        }
    }

    public const int KittyRowImageBase = 0x64736800;

    /**
     * sixel 整帧编码: 精确调色板(≤255 色), 超限退化为 RGB332 量化; 6 行一带, 逐色 RLE。
     * 输出前把光标移到左上角, 图像按自身像素尺寸铺满(与终端格对齐)。
     */
    public static string EncodeSixel(byte[] rgba, int width, int height)
    {
        var (indices, palette) = Quantize(rgba, width, height);
        var bandCount = (height + 5) / 6;
        var bandColors = new List<int>[bandCount];
        for (var band = 0; band < bandCount; band++)
            bandColors[band] = [];
        var seen = new HashSet<int>[bandCount];
        for (var band = 0; band < bandCount; band++)
            seen[band] = [];
        for (var y = 0; y < height; y++)
        {
            var band = y / 6;
            for (var x = 0; x < width; x++)
            {
                var color = indices[(y * width) + x];
                if (seen[band].Add(color))
                    bandColors[band].Add(color);
            }
        }

        var builder = new StringBuilder();
        builder.Append("\x1b[H\x1bPq");
        builder.Append($"\"1;1;{width};{height}");
        foreach (var (color, index) in palette)
            builder.Append($"#{index};2;{(color >> 16) & 0xFF};{(color >> 8) & 0xFF};{color & 0xFF}");
        for (var band = 0; band < bandCount; band++)
        {
            for (var colorPosition = 0; colorPosition < bandColors[band].Count; colorPosition++)
            {
                if (colorPosition > 0)
                    builder.Append('$');
                var color = bandColors[band][colorPosition];
                builder.Append($"#{color}");
                AppendSixelRun(builder, indices, width, height, band, color);
            }
            if (band < bandCount - 1)
                builder.Append('-');
        }
        builder.Append("\x1b\\");
        return builder.ToString();
    }

    private static void AppendSixelRun(StringBuilder builder, byte[] indices, int width, int height, int band, int color)
    {
        var current = -1;
        var repeat = 0;
        for (var x = 0; x < width; x++)
        {
            var mask = 0;
            for (var bit = 0; bit < 6; bit++)
            {
                var y = (band * 6) + bit;
                if (y < height && indices[(y * width) + x] == color)
                    mask |= 1 << bit;
            }
            if (mask == current)
            {
                repeat++;
                continue;
            }
            if (current >= 0)
                AppendSixelValue(builder, current, repeat);
            current = mask;
            repeat = 1;
        }
        if (current >= 0)
            AppendSixelValue(builder, current, repeat);
    }

    private static void AppendSixelValue(StringBuilder builder, int mask, int repeat)
    {
        var character = (char)(63 + mask);
        if (repeat >= 4)
            builder.Append('!').Append(repeat).Append(character);
        else
            builder.Append(character, repeat);
    }

    /** 精确调色板优先; 超过 255 色退化为 RGB332 量化。返回每像素调色板索引与索引>RGB 表。 */
    private static (byte[] Indices, List<(int Color, int Index)> Palette) Quantize(byte[] rgba, int width, int height)
    {
        var indices = new byte[width * height];
        var exact = new Dictionary<int, int>();
        var ordered = new List<(int Color, int Index)>();
        var overflow = false;
        for (var i = 0; i < width * height; i++)
        {
            var offset = i * 4;
            var color = (rgba[offset] << 16) | (rgba[offset + 1] << 8) | rgba[offset + 2];
            if (!exact.TryGetValue(color, out var index))
            {
                if (exact.Count >= 255)
                {
                    overflow = true;
                    break;
                }
                index = exact.Count;
                exact[color] = index;
                ordered.Add((color, index));
            }
            indices[i] = (byte)index;
        }
        if (!overflow)
            return (indices, ordered);
        var quantized = new (int Color, int Index)[256];
        for (var i = 0; i < 256; i++)
        {
            var red = ((i >> 5) & 0x7) * 255 / 7;
            var green = ((i >> 2) & 0x7) * 255 / 7;
            var blue = (i & 0x3) * 255 / 3;
            quantized[i] = ((red << 16) | (green << 8) | blue, i);
        }
        for (var i = 0; i < width * height; i++)
        {
            var offset = i * 4;
            indices[i] = (byte)(((rgba[offset] >> 5) << 5) | ((rgba[offset + 1] >> 5) << 2) | (rgba[offset + 2] >> 6));
        }
        return (indices, [.. quantized]);
    }

    /** 把终端应答转成可读短串(控制字符转义, 截断), 便于诊断报告。 */
    private static string Escape(string text)
    {
        var builder = new StringBuilder();
        foreach (var character in text)
        {
            if (builder.Length >= 80)
            {
                builder.Append('~');
                break;
            }
            switch (character)
            {
                case '\x1b':
                    builder.Append("\\e");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(character))
                        builder.Append($"\\x{(int)character:X2}");
                    else
                        builder.Append(character);
                    break;
            }
        }
        return builder.Length == 0 ? "(空)" : builder.ToString();
    }

    private static string ReadReply()
    {
        var builder = new StringBuilder();
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < KittyTimeoutMilliseconds)
        {
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(5);
                continue;
            }
            var key = Console.ReadKey(intercept: true);
            builder.Append(key.KeyChar);
            var text = builder.ToString();
            if (text.Contains("\x1b\\", StringComparison.Ordinal) && text.Contains('c', StringComparison.Ordinal))
                break;
        }
        return builder.ToString();
    }
}
