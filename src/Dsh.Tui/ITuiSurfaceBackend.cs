namespace Dsh.Tui;

/**
 * 渲染核心后端抽象: GpuRenderer 只经此接口驱动绘制/上传, 让 GL(GpuRenderCore)与 Vulkan(VkRenderCore) 可互换, 而格栅/布局/脏行/光标逻辑只有一份。
 * 窗口/上下文/交换链由 IGlSurfaceHostRunner 抽象; 本接口只负责"把一整帧画到当前目标"。
 * 像素读回约定: RGBA 4 字节/像素, 行序自下而上(与 glReadPixels 一致), 由调用方负责翻转成图片行序。
 */
public interface ITuiSurfaceBackend : IDisposable
{
    /** 上下文就绪: 创建 GPU 资源并上传字形图集(调用前当前上下文/设备必须可渲染)。 */
    void Initialize(GlyphAtlas atlas);

    /** 保证 cells 缓冲至少容纳 cellCount 个格(只在增长时重建)。 */
    void EnsureCellCapacity(int cellCount);

    /** 把打包好的格(源取 packed[0..count])写到 cells 缓冲的 firstCell 偏移处(脏行增量上传)。 */
    void UploadCells(uint[] packed, int firstCell, int count);

    /** 请求本帧把像素准备好, 供随后的 TryReadPixels 读取; 能即时读回的后端可忽略。 */
    void RequestCapture();

    /** 提示本帧只需回读这些像素行(自上而下, 像素行); 空 = 整帧。默认忽略(能即时读回/不需要读回的后端)。 */
    void SetCaptureRows(ReadOnlySpan<(int FirstRow, int RowCount)> pixelRows)
    {
    }

    /** 绘制一帧(恒全帧重绘, 不做脏行重放)。 */
    void RenderFrame(GlyphAtlas atlas, int gridWidth, int gridHeight);

    /** 帧缓冲像素尺寸变化。 */
    void Resize(int pixelWidth, int pixelHeight);

    /** 读回当前帧像素; 不支持读回时返回 false。destination 至少 width*height*4 字节。 */
    bool TryReadPixels(Span<byte> destination, int width, int height);

    /** 只读回指定像素行(其余行保持 destination 原值); rows 为空等价于整帧。默认退化为整帧。 */
    bool TryReadPixels(Span<byte> destination, int width, int height, ReadOnlySpan<(int FirstRow, int RowCount)> rows)
        => TryReadPixels(destination, width, height);

    /** 设备/驱动描述(冷路径, 仅用于日志与后端取证)。 */
    string DeviceInfo { get; }

    /** 上下文是否丢失(仅 GL robust 上下文的 TDR 会置真); Vk 恒为 false。 */
    bool ContextLost { get; }
}
