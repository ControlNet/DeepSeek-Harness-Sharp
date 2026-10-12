using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Dsh.Tui.Vk;

/**
 * 无头有 GPU(无显示服务器)的 Vulkan 宿主: 不开窗口/不建 VkSurface, 直接离屏渲染到 image,
 * 每帧把帧缓冲经终端图像协议(kitty/sixel)贴回终端, 并按终端格尺寸映射键鼠事件 —— 与 GL 的
 * TerminalGraphicsHost 同形, 只是渲染后端换成 VkOffscreenTarget。终端两种协议都不支持时:
 * 仅截图/捕获形态可用(不呈现), 否则 TryCreate 失败由调用方给出原因。
 */
internal sealed class VkOffscreenHost : IGlSurfaceHostRunner, IGpuHostWaker
{
    private readonly int _cellWidth;
    private readonly int _cellHeight;
    private readonly TerminalGraphicsProtocol _protocol;
    private VkGpuDevice? _gpu;
    private VkRenderCore? _core;
    private VkOffscreenTarget? _target;
    private ITerminalInputSource? _input;
    private byte[]? _previous;
    private byte[]? _frame;
    private readonly TerminalGraphics.KittyRowPresenter _kitty = new();
    private bool[] _dirtyCellRows = [];
    private (int FirstRow, int RowCount)[] _captureRegions = [];

    /** 类型化引用: 脏段记录/回读范围只在 Vk 核心上提供(不在 ITuiSurfaceBackend 上暴露)。 */
    private VkRenderCore? VkCore => _core as VkRenderCore;
    private int _columns;
    private int _rows;
    private bool _forceFull = true;
    private bool _closed;
    private bool _disposed;

    private VkOffscreenHost(GlyphAtlas atlas, VkGpuDevice gpu, int columns, int rows, TerminalGraphicsProtocol protocol)
    {
        _gpu = gpu;
        _columns = columns;
        _rows = rows;
        _cellWidth = atlas.GlyphWidth;
        _cellHeight = atlas.GlyphHeight;
        _protocol = protocol;
    }

    private bool Presenting => _protocol != TerminalGraphicsProtocol.None;

    public (int Width, int Height) Size => (_columns * _cellWidth, _rows * _cellHeight);

    public void MakeCurrent()
    {
    }

    /**
     * headlessCapture(截图/捕获)不要求终端支持图像协议; 交互形态则需要 kitty/sixel。
     * Vulkan 设备不可用/终端尺寸不可用时返回 false, 由调用方给出原因并回退。
     */
    public static bool TryCreate(GlyphAtlas atlas, string? deviceNameFilter, bool headlessCapture, [NotNullWhen(true)] out VkOffscreenHost? host, out string reason, string? devicePciSlot = null)
    {
        host = null;
        var protocol = TerminalGraphics.Detect(out var protocolReason);
        if (protocol == TerminalGraphicsProtocol.None && !headlessCapture)
        {
            reason = protocolReason;
            return false;
        }

        VkGpuDevice gpu;
        try
        {
            gpu = VkGpuDevice.Create(deviceNameFilter: deviceNameFilter, devicePciSlot: devicePciSlot);
        }
        catch (Exception error)
        {
            reason = $"无可用 Vulkan 设备: {error.Message}";
            return false;
        }

        host = new VkOffscreenHost(atlas, gpu, SafeWindowWidth(), SafeWindowHeight(), protocol);
        reason = "";
        return true;
    }

    /** 创建离屏帧目标与渲染核心(VkRenderCore 复用窗口路径的录制逻辑)。 */
    public ITuiSurfaceBackend CreateRenderCore(GlyphAtlas atlas)
    {
        var gpu = _gpu ?? throw new InvalidOperationException("宿主已释放");
        var core = new VkRenderCore(gpu, atlas);
        _target = VkOffscreenTarget.Create(gpu, core, _columns * _cellWidth, _rows * _cellHeight);
        core.Target = _target;
        _core = core;
        return core;
    }

    public void Present()
    {
        if (!Presenting || _closed || _core is null)
            return;
        var size = Size;
        if (size.Width <= 0 || size.Height <= 0)
            return;
        // 双缓冲复用: 读回进 _frame, 与 _previous 比较后交替换位(不再每帧 new byte[] + Clone)。
        var length = size.Width * size.Height * 4;
        if (_frame is null || _previous is null || _frame.Length != length)
        {
            _frame = new byte[length];
            _previous = new byte[length];
            _forceFull = true;
        }

        // 只回读本帧脏段覆盖的像素行(无脏段/覆盖过半/强制整帧时为空 = 整帧): 省掉整帧 PCIe 传输与后续比较。
        // 注意: 即便无变化也必须走完 TryReadPixels(它会触发目标 Present 结束命令缓冲); 产品侧由 GpuRenderer
        // 的"内容未变则不 Present"早退来省掉整帧, 这里不做 skip。
        var rows = _forceFull ? default : CollectDirtyRows(size.Width, size.Height);
        _core.SetCaptureRows(rows);
        VkCore?.ClearFrameUploads();
        var pixels = _frame;
        if (!_core.TryReadPixels(pixels, size.Width, size.Height, rows))
            return;
        if (_protocol == TerminalGraphicsProtocol.Sixel)
        {
            Console.Out.Write(TerminalGraphics.EncodeSixel(pixels, size.Width, size.Height));
            Console.Out.Flush();
            return;
        }

        PresentKittyRows(pixels, size.Width, size.Height, rows);
        if (rows.IsEmpty)
            (_frame, _previous) = (_previous, pixels);              // 整帧: 换位即可
        else
            CopyRows(pixels, _previous, size.Width, size.Height, rows);   // 部分: 只把读到的行同步给上一帧
    }

    /** 由核心记录的脏段(cell 单位)算出需回读的像素行 ranges; 无脏段或覆盖过半时返回空(= 整帧)。 */
    private ReadOnlySpan<(int FirstRow, int RowCount)> CollectDirtyRows(int pixelWidth, int pixelHeight)
    {
        if (VkCore is null || _columns <= 0)
            return default;
        var uploads = VkCore.FrameUploads;
        if (uploads.Count == 0)
            return default;
        var rows = Math.Max(1, pixelHeight / _cellHeight);
        if (_dirtyCellRows.Length < rows)
            _dirtyCellRows = new bool[rows];
        Array.Clear(_dirtyCellRows, 0, rows);
        var dirty = 0;
        foreach (var (firstCell, count) in uploads)
        {
            var first = Math.Clamp(firstCell / _columns, 0, rows - 1);
            var last = Math.Clamp((firstCell + count - 1) / _columns, 0, rows - 1);
            for (var row = first; row <= last; row++)
            {
                if (_dirtyCellRows[row])
                    continue;
                _dirtyCellRows[row] = true;
                dirty++;
            }
        }

        if (dirty == 0 || dirty * 2 >= rows)
            return default;
        if (_captureRegions.Length < dirty)
            _captureRegions = new (int, int)[Math.Max(4, dirty)];
        var regions = 0;
        for (var row = 0; row < rows; row++)
        {
            if (!_dirtyCellRows[row])
                continue;
            var start = row;
            while (row + 1 < rows && _dirtyCellRows[row + 1])
                row++;
            _captureRegions[regions++] = (start * _cellHeight, ((row - start) + 1) * _cellHeight);
        }

        return _captureRegions.AsSpan(0, regions);
    }

    /** 把 source 里给定像素行的内容同步到 destination(缓冲自下而上, 行号取 height-1-r)。 */
    private static void CopyRows(byte[] source, byte[] destination, int width, int height, ReadOnlySpan<(int FirstRow, int RowCount)> rows)
    {
        foreach (var (first, count) in rows)
        {
            for (var r = first; r < first + count && r < height; r++)
            {
                var offset = (height - 1 - r) * width * 4;
                source.AsSpan(offset, width * 4).CopyTo(destination.AsSpan(offset, width * 4));
            }
        }
    }

    /** kitty: 逐终端行贴图; 首帧/尺寸变化全量, 之后只重传内容变化的行。 */
    private void PresentKittyRows(byte[] pixels, int width, int height, ReadOnlySpan<(int FirstRow, int RowCount)> rows)
    {
        if (_kitty.Present(pixels, _previous, width, height, _cellHeight, _columns, _forceFull, TerminalGraphics.SupportsPlacementReuse(), Console.Out, rows))
            Console.Out.Flush();
        _forceFull = false;
    }

    public void Run(IGpuHostClient client)
    {
        var interactive = !Console.IsInputRedirected;
        using var raw = interactive ? TerminalRawMode.TryEnable(enableMouse: true) : null;
        _input = interactive ? CreateInputSource() : null;
        EnterTerminal();
        try
        {
            client.OnLoaded();
            while (!_closed)
            {
                // 环形缓冲: 上传前先等当前槽在途 fence(离屏为同步单槽, 通常立即返回)。
                _target!.WaitSlotIdle();
                if (client.OnFrame())
                    break;
                var inputEvent = _input?.Read(8);
                if (inputEvent is null)
                {
                    if (_input is { EndOfStream: true })
                        break;
                    if (_input is null)
                        Thread.Sleep(8);
                }
                else
                {
                    Dispatch(client, inputEvent.Value);
                }

                MaybeResize(client);
            }
        }
        finally
        {
            _input?.Dispose();
            _input = null;
            LeaveTerminal();
        }
    }

    public void RequestClose()
    {
        _closed = true;
        _input?.Wake();
    }

    /** 无窗口无表面: 流式输出靠宿主每 8ms 轮询, 无需额外唤醒。 */
    public void Wake()
    {
    }

    public string? ReadClipboard() => null;

    public void Resize(int pixelWidth, int pixelHeight)
    {
        var columns = Math.Max(1, pixelWidth / _cellWidth);
        var rows = Math.Max(1, pixelHeight / _cellHeight);
        ApplySize(columns, rows, notify: null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _target?.Dispose();
        _core?.Dispose();
        _gpu?.Dispose();
        _gpu = null;
    }

    /** 终端尺寸变化: 重建离屏目标尺寸并通知客户端重排(与 TerminalGraphicsHost.MaybeResize 同形)。 */
    private void MaybeResize(IGpuHostClient client)
    {
        var columns = SafeWindowWidth();
        var rows = SafeWindowHeight();
        if (columns == _columns && rows == _rows)
            return;
        ApplySize(columns, rows, client);
    }

    private void ApplySize(int columns, int rows, IGpuHostClient? notify)
    {
        _columns = Math.Max(1, columns);
        _rows = Math.Max(1, rows);
        _target?.SetSize(_columns * _cellWidth, _rows * _cellHeight);
        _forceFull = true;
        _previous = null;
        if (notify is not null)
            notify.OnResize(Size.Width, Size.Height);
    }

    private void Dispatch(IGpuHostClient client, TerminalInputEvent inputEvent)
    {
        if (inputEvent.Mouse is not { } mouse)
        {
            client.OnKey(inputEvent.Key);
            return;
        }

        var x = mouse.X * _cellWidth;
        var y = mouse.Y * _cellHeight;
        if (mouse.IsWheel)
        {
            client.OnMouseWheel(mouse.WheelDelta);
            return;
        }

        if (mouse.IsMove)
        {
            client.OnMouseMove(x, y);
            return;
        }

        client.OnMouseButton(GpuMouseButton.Left, mouse.Pressed, x, y);
    }

    private static ITerminalInputSource? CreateInputSource()
        => OperatingSystem.IsWindows()
            ? WindowsConsoleInputReader.TryCreate()
            : new TerminalInputReader();

    private void EnterTerminal()
    {
        if (!Presenting)
            return;
        Console.Out.Write("\x1b[?1049h\x1b[?25l\x1b[2J\x1b[H");
        Console.Out.Flush();
    }

    private void LeaveTerminal()
    {
        if (!Presenting)
            return;
        if (_protocol == TerminalGraphicsProtocol.Kitty)
            _kitty.DeleteAll(Console.Out);
        Console.Out.Write("\x1b[?25h\x1b[?1049l");
        Console.Out.Flush();
    }

    private static int SafeWindowWidth()
    {
        try
        {
            return Math.Max(1, Console.WindowWidth);
        }
        catch (IOException)
        {
            return 80;
        }
        catch (ArgumentOutOfRangeException)
        {
            return 80;
        }
    }

    private static int SafeWindowHeight()
    {
        try
        {
            return Math.Max(1, Console.WindowHeight);
        }
        catch (IOException)
        {
            return 25;
        }
        catch (ArgumentOutOfRangeException)
        {
            return 25;
        }
    }
}
