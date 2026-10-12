using OpenTK.Graphics.OpenGL;
// 本命名空间末段就叫 GL, 裸名 `GL` 会被它遮蔽; 用别名 GlApi 引用 OpenTK 的静态绑定类(与 Vk 侧 VulkanApi 同形)。
using GlApi = OpenTK.Graphics.OpenGL.GL;

namespace Dsh.Tui.GL;

/**
 * GL 后端适配器: 把既有 GpuRenderCore 的 GL 调用映射到 ITuiSurfaceBackend, GpuRenderCore 本身不改。
 * 行为与重构前逐调用等价(视口/脏行上传/整帧重绘/读回), 保证 GL 输出不变。
 */
internal sealed class GlSurfaceBackend(GpuRenderCore core) : ITuiSurfaceBackend
{
    public string DeviceInfo => $"gl: {GlApi.GetString(StringName.Renderer)} ({GlApi.GetString(StringName.Version)})";

    public bool ContextLost => (All)GlApi.GetError() == All.ContextLost;

    public void Initialize(GlyphAtlas atlas) => core.Initialize(atlas);

    public void EnsureCellCapacity(int cellCount) => core.EnsureCellCapacity(cellCount);

    public void UploadCells(uint[] packed, int firstCell, int count) => core.UploadCells(packed, firstCell, count);

    public void RequestCapture()
    {
    }

    public void RenderFrame(GlyphAtlas atlas, int gridWidth, int gridHeight) => core.RenderFrame(atlas, gridWidth, gridHeight);

    public void Resize(int pixelWidth, int pixelHeight) => GlApi.Viewport(0, 0, pixelWidth, pixelHeight);

    public bool TryReadPixels(Span<byte> destination, int width, int height)
    {
        GlApi.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, destination);
        return true;
    }

    public void Dispose() => core.Dispose();
}
