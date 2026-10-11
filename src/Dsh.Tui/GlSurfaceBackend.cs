using OpenTK.Graphics.OpenGL;

namespace Dsh.Tui;

/**
 * GL 后端适配器: 把既有 GpuRenderCore 的 GL 调用映射到 ITuiSurfaceBackend, GpuRenderCore 本身不改。
 * 行为与重构前逐调用等价(视口/脏行上传/整帧重绘/读回), 保证 GL 输出不变。
 */
internal sealed class GlSurfaceBackend(GpuRenderCore core) : ITuiSurfaceBackend
{
    public string DeviceInfo => $"gl: {GL.GetString(StringName.Renderer)} ({GL.GetString(StringName.Version)})";

    public bool ContextLost => (All)GL.GetError() == All.ContextLost;

    public void Initialize(GlyphAtlas atlas) => core.Initialize(atlas);

    public void EnsureCellCapacity(int cellCount) => core.EnsureCellCapacity(cellCount);

    public void UploadCells(uint[] packed, int firstCell, int count) => core.UploadCells(packed, firstCell, count);

    public void RequestCapture()
    {
    }

    public void RenderFrame(GlyphAtlas atlas, int gridWidth, int gridHeight) => core.RenderFrame(atlas, gridWidth, gridHeight);

    public void Resize(int pixelWidth, int pixelHeight) => GL.Viewport(0, 0, pixelWidth, pixelHeight);

    public bool TryReadPixels(Span<byte> destination, int width, int height)
    {
        GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, destination);
        return true;
    }

    public void Dispose() => core.Dispose();
}
