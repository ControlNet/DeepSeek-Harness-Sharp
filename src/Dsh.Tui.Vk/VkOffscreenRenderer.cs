using OpenTK.Graphics.Vulkan;
using Dsh.Pty;
// 裸名 `Vk` 会被外层命名空间 Dsh.Tui.Vk 遮蔽(编译期优先解析为命名空间), 因此用编译单元别名指向静态绑定类。
using VulkanApi = OpenTK.Graphics.Vulkan.Vk;

namespace Dsh.Tui.Vk;

/**
 * Vulkan 离屏文本渲染前端: 自有一张离屏 image + framebuffer + 回读缓冲, 把 CellGrid 全帧渲染并读回像素
 * (供截图/对拍/无头验证)。录制逻辑在 VkRenderCore; 本类只负责"目标 + 回读"。
 * 语义(恒全帧重绘/续格不画字形/宽字形背景×2)见 VkRenderCore 与 plans/DSH-Sharp-§4-Vulkan与cellgrid方案.md §9。
 */
internal sealed unsafe class VkOffscreenRenderer : IDisposable
{
    private readonly VkRenderCore _core;
    private readonly VkFence _fence;
    private readonly VkCommandBuffer _commandBuffer;

    private VkImage _targetImage;
    private VkDeviceMemory _targetMemory;
    private VkImageView _targetView;
    private VkFramebuffer _framebuffer;
    private VkBuffer _readbackBuffer;
    private VkDeviceMemory _readbackMemory;
    private nint _readbackMapped;
    private bool _readbackCoherent = true;
    private bool _disposed;

    public string DeviceName => _core.Gpu.DeviceName;

    /** 诊断/回归入口: 暴露核心以便测试直接读"已上传的 params"与回读 GPU 图集纹理。 */
    internal VkRenderCore Core => _core;

    public int Width { get; private set; }

    public int Height { get; private set; }

    private VkOffscreenRenderer(GlyphAtlas atlas, VkGpuDevice gpu)
    {
        _core = new VkRenderCore(gpu, atlas);

        var fenceInfo = new VkFenceCreateInfo { sType = VkStructureType.StructureTypeFenceCreateInfo, flags = VkFenceCreateFlagBits.FenceCreateSignaledBit };
        VkFence fenceOut;
        VkGpuDevice.Throw(VulkanApi.CreateFence(gpu.Device, &fenceInfo, null, &fenceOut), "vkCreateFence");
        _fence = fenceOut;

        var cmdInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.StructureTypeCommandBufferAllocateInfo,
            commandPool = gpu.CommandPool,
            level = VkCommandBufferLevel.CommandBufferLevelPrimary,
            commandBufferCount = 1,
        };
        VkCommandBuffer commandBufferOut;
        VkGpuDevice.Throw(VulkanApi.AllocateCommandBuffers(gpu.Device, &cmdInfo, &commandBufferOut), "vkAllocateCommandBuffers");
        _commandBuffer = commandBufferOut;
    }

    public static VkOffscreenRenderer Create(GlyphAtlas atlas, string? deviceNameFilter = null)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        return new VkOffscreenRenderer(atlas, VkGpuDevice.Create(deviceNameFilter: deviceNameFilter));
    }

    /** 全帧渲染给定格栅; 结果像素在 ReadPixels 中读回。 */
    public void Render(CellGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        EnsureTarget(grid.Width * _core.GlyphWidth, grid.Height * _core.GlyphHeight);

        // 先等在途帧结束再 Prepare: Prepare 会写 per-slot staging 并提交 atlas/glyph map 的 CmdCopyBufferToImage,
        // 若上一帧的 GPU 拷贝仍在读同一 staging/同一张 atlas image, 新旧数据会在纹理里混合(字形下缘被"混掉")。
        var fence = _fence;
        VkGpuDevice.Throw(VulkanApi.WaitForFences(_core.Gpu.Device, 1, &fence, 1, ulong.MaxValue), "vkWaitForFences");
        VkGpuDevice.Throw(VulkanApi.ResetFences(_core.Gpu.Device, 1, &fence), "vkResetFences");

        _core.Prepare(grid);

        var begin = new VkCommandBufferBeginInfo { sType = VkStructureType.StructureTypeCommandBufferBeginInfo };
        VkGpuDevice.Throw(VulkanApi.BeginCommandBuffer(_commandBuffer, &begin), "vkBeginCommandBuffer");
        _core.RecordFrame(_commandBuffer, _framebuffer, (uint)Width, (uint)Height, grid.Width, grid.Height);

        // 呈现目标 → 传输源 → 拷回主机缓冲 → 复位为 Undefined(下一帧 render pass 以 Undefined 起步, 强制整帧重画)。
        VkRenderCore.Barrier(_commandBuffer, _targetImage, VkImageLayout.ImageLayoutColorAttachmentOptimal, VkImageLayout.ImageLayoutTransferSrcOptimal,
            VkAccessFlagBits.AccessColorAttachmentWriteBit, VkAccessFlagBits.AccessTransferReadBit);
        var copy = new VkBufferImageCopy
        {
            bufferOffset = 0,
            imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlagBits.ImageAspectColorBit, 0, 0, 1),
            imageOffset = new VkOffset3D(0, 0, 0),
            imageExtent = new VkExtent3D((uint)Width, (uint)Height, 1),
        };
        VulkanApi.CmdCopyImageToBuffer(_commandBuffer, _targetImage, VkImageLayout.ImageLayoutTransferSrcOptimal, _readbackBuffer, 1, &copy);
        VkRenderCore.Barrier(_commandBuffer, _targetImage, VkImageLayout.ImageLayoutTransferSrcOptimal, VkImageLayout.ImageLayoutUndefined, VkAccessFlagBits.AccessTransferReadBit, 0);

        VkGpuDevice.Throw(VulkanApi.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");

        var commandBuffer = _commandBuffer;
        var submit = new VkSubmitInfo
        {
            sType = VkStructureType.StructureTypeSubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &commandBuffer,
        };
        VkGpuDevice.Throw(VulkanApi.QueueSubmit(_core.Gpu.GraphicsQueue, 1, &submit, _fence), "vkQueueSubmit");
        VkGpuDevice.Throw(VulkanApi.WaitForFences(_core.Gpu.Device, 1, &fence, 1, ulong.MaxValue), "vkWaitForFences");
    }

    /** 读回上一帧: R8G8B8A8, 行主序, 长度 Width*Height*4。 */
    public void ReadPixels(Span<byte> destination)
    {
        var length = Width * Height * 4;
        if (destination.Length < length)
            throw new ArgumentException($"目标缓冲需要至少 {length} 字节", nameof(destination));
        if (!_readbackCoherent)
            _core.Gpu.InvalidateMappedRange(_readbackMemory);
        new ReadOnlySpan<byte>((void*)_readbackMapped, length).CopyTo(destination);
    }

    private void EnsureTarget(int width, int height)
    {
        if (width == Width && height == Height && _framebuffer.Handle != 0)
            return;

        if (_framebuffer.Handle != 0)
            VulkanApi.DestroyFramebuffer(_core.Gpu.Device, _framebuffer, null);
        if (_targetImage.Handle != 0)
        {
            VulkanApi.DestroyImageView(_core.Gpu.Device, _targetView, null);
            VulkanApi.DestroyImage(_core.Gpu.Device, _targetImage, null);
            VulkanApi.FreeMemory(_core.Gpu.Device, _targetMemory, null);
        }

        if (_readbackBuffer.Handle != 0)
        {
            VulkanApi.UnmapMemory(_core.Gpu.Device, _readbackMemory);
            VulkanApi.DestroyBuffer(_core.Gpu.Device, _readbackBuffer, null);
            VulkanApi.FreeMemory(_core.Gpu.Device, _readbackMemory, null);
        }

        Width = width;
        Height = height;

        var imageInfo = new VkImageCreateInfo
        {
            sType = VkStructureType.StructureTypeImageCreateInfo,
            imageType = VkImageType.ImageType2D,
            format = VkFormat.FormatR8g8b8a8Unorm,
            extent = new VkExtent3D((uint)width, (uint)height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlagBits.SampleCount1Bit,
            tiling = VkImageTiling.ImageTilingOptimal,
            usage = VkImageUsageFlagBits.ImageUsageColorAttachmentBit | VkImageUsageFlagBits.ImageUsageTransferSrcBit,
            sharingMode = VkSharingMode.SharingModeExclusive,
            initialLayout = VkImageLayout.ImageLayoutUndefined,
        };
        _targetImage = _core.Gpu.CreateImage(imageInfo, out _targetMemory);
        _targetView = _core.Gpu.CreateImageView(_targetImage, VkFormat.FormatR8g8b8a8Unorm);
        _framebuffer = _core.CreateFramebuffer(_targetView, (uint)width, (uint)height);

        _readbackBuffer = _core.Gpu.CreateMappedReadbackBuffer((ulong)width * (ulong)height * 4, out _readbackMemory, out _readbackMapped, out _readbackCoherent);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_core.Gpu.Device.Handle != 0)
            VulkanApi.DeviceWaitIdle(_core.Gpu.Device);

        if (_framebuffer.Handle != 0)
            VulkanApi.DestroyFramebuffer(_core.Gpu.Device, _framebuffer, null);
        if (_readbackBuffer.Handle != 0)
        {
            VulkanApi.UnmapMemory(_core.Gpu.Device, _readbackMemory);
            VulkanApi.DestroyBuffer(_core.Gpu.Device, _readbackBuffer, null);
            VulkanApi.FreeMemory(_core.Gpu.Device, _readbackMemory, null);
        }

        if (_targetView.Handle != 0)
            VulkanApi.DestroyImageView(_core.Gpu.Device, _targetView, null);
        if (_targetImage.Handle != 0)
            VulkanApi.DestroyImage(_core.Gpu.Device, _targetImage, null);
        if (_targetMemory.Handle != 0)
            VulkanApi.FreeMemory(_core.Gpu.Device, _targetMemory, null);

        var commandBuffer = _commandBuffer;
        VulkanApi.FreeCommandBuffers(_core.Gpu.Device, _core.Gpu.CommandPool, 1, &commandBuffer);
        VulkanApi.DestroyFence(_core.Gpu.Device, _fence, null);

        _core.Dispose();
        _core.Gpu.Dispose();
    }
}
