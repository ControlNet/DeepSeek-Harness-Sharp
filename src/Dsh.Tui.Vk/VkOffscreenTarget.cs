using OpenTK.Graphics.Vulkan;
// 裸名 `Vk` 会被外层命名空间 Dsh.Tui.Vk 遮蔽(编译期优先解析为命名空间), 因此用编译单元别名指向静态绑定类。
using VulkanApi = OpenTK.Graphics.Vulkan.Vk;

namespace Dsh.Tui.Vk;

/**
 * 无头(VkOffscreenHost)的帧目标: 渲染到一张自有离屏 image 再拷回主机缓冲。
 * 与 VkWindowRenderer 一样实现 IVkFrameTarget, 因此 VkRenderCore 的两趟录制逻辑完全复用;
 * 区别只在"目标"是自持 image(不是交换链)且 Present 换成 image→buffer 拷贝。单槽(slot 0), 自己一条 fence/命令缓冲。
 */
internal sealed unsafe class VkOffscreenTarget : IVkFrameTarget, IDisposable
{
    private const int PixelBytes = 4;

    private readonly VkGpuDevice _gpu;
    private readonly VkRenderCore _core;

    private VkImage _image;
    private VkDeviceMemory _memory;
    private VkImageView _view;
    private VkFramebuffer _framebuffer;
    private VkBuffer _readbackBuffer;
    private VkDeviceMemory _readbackMemory;
    private nint _readbackMapped;
    private bool _readbackCoherent = true;
    private (int FirstRow, int RowCount)[] _captureRows = [];
    private int _captureRowCount;
    private readonly VkFence _fence;
    private readonly VkCommandBuffer _commandBuffer;

    private int _width;
    private int _height;
    private bool _frameBegun;
    private bool _rendered;
    private bool _captured;
    private bool _disposed;

    private VkOffscreenTarget(VkGpuDevice gpu, VkRenderCore core)
    {
        _gpu = gpu;
        _core = core;
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

    public static VkOffscreenTarget Create(VkGpuDevice gpu, VkRenderCore core, int width, int height)
    {
        var target = new VkOffscreenTarget(gpu, core);
        target.SetSize(width, height);
        return target;
    }

    public int Width => _width;

    public int Height => _height;

    public int CurrentSlot => 0;

    public VkCommandBuffer CommandBuffer => _commandBuffer;

    public VkFramebuffer CurrentFramebuffer => _framebuffer;

    public (uint Width, uint Height) CurrentExtent => ((uint)_width, (uint)_height);

    public void WaitSlotIdle()
    {
        var fence = _fence;
        VkGpuDevice.Throw(VulkanApi.WaitForFences(_gpu.Device, 1, &fence, 1, ulong.MaxValue), "vkWaitForFences");
    }

    public bool BeginFrame()
    {
        if (_width <= 0 || _height <= 0)
            return false;
        // 保证上一帧已结束(直连用法未走宿主 Run 循环时也需要), 否则命令缓冲仍 pending → begin 失败。
        WaitSlotIdle();
        var begin = new VkCommandBufferBeginInfo { sType = VkStructureType.StructureTypeCommandBufferBeginInfo };
        VkGpuDevice.Throw(VulkanApi.BeginCommandBuffer(_commandBuffer, &begin), "vkBeginCommandBuffer");
        _frameBegun = true;
        _rendered = false;
        _captured = false;
        return true;
    }

    public void MarkRendered() => _rendered = true;

    public void RequestResize()
    {
    }

    public void RequestCapture()
    {
    }

    /** 本帧只回读这些像素行(自上而下, image 行号); 空 = 整帧。 */
    public void SetCaptureRows(ReadOnlySpan<(int FirstRow, int RowCount)> pixelRows)
    {
        if (pixelRows.Length > _captureRows.Length)
            _captureRows = new (int, int)[Math.Max(4, pixelRows.Length)];
        _captureRowCount = pixelRows.Length;
        pixelRows.CopyTo(_captureRows);
    }

    /** 目标像素尺寸(宿主按终端格尺寸变化调用); 变化时重建 image/framebuffer/回读缓冲。 */
    public void SetSize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _width && height == _height && _framebuffer.Handle != 0)
            return;
        if (_gpu.Device.Handle != 0)
            VulkanApi.DeviceWaitIdle(_gpu.Device);

        DestroyTarget();
        _width = width;
        _height = height;

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
        _image = _gpu.CreateImage(imageInfo, out var memory);
        _memory = memory;
        _view = _gpu.CreateImageView(_image, VkFormat.FormatR8g8b8a8Unorm);
        _framebuffer = _core.CreateFramebuffer(_view, (uint)width, (uint)height);
        _readbackBuffer = _gpu.CreateMappedReadbackBuffer((ulong)width * (ulong)height * PixelBytes, out var readbackMemory, out var readbackMapped, out var coherent);
        _readbackMemory = readbackMemory;
        _readbackMapped = readbackMapped;
        _readbackCoherent = coherent;
    }

    /** 结束并提交当前帧: image→读回缓冲, 转回 Undefined(下一帧 render pass 以 Undefined 起步, 强制整帧重画)。 */
    public void Present()
    {
        if (!_frameBegun || !_rendered)
            return;

        VkRenderCore.Barrier(_commandBuffer, _image, VkImageLayout.ImageLayoutColorAttachmentOptimal, VkImageLayout.ImageLayoutTransferSrcOptimal,
            VkAccessFlagBits.AccessColorAttachmentWriteBit, VkAccessFlagBits.AccessTransferReadBit);
        if (_captureRowCount > 0)
        {
            // 只拷脏行 range(宿主按本帧脏段换算而来): 大幅减少 GPU→主机 的 PCIe 传输。
            var regions = stackalloc VkBufferImageCopy[_captureRowCount];
            for (var i = 0; i < _captureRowCount; i++)
            {
                var (first, count) = _captureRows[i];
                regions[i] = new VkBufferImageCopy
                {
                    bufferOffset = (ulong)first * (ulong)_width * PixelBytes,
                    imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlagBits.ImageAspectColorBit, 0, 0, 1),
                    imageOffset = new VkOffset3D(0, first, 0),
                    imageExtent = new VkExtent3D((uint)_width, (uint)count, 1),
                };
            }

            VulkanApi.CmdCopyImageToBuffer(_commandBuffer, _image, VkImageLayout.ImageLayoutTransferSrcOptimal, _readbackBuffer, (uint)_captureRowCount, regions);
        }
        else
        {
            var copy = new VkBufferImageCopy
            {
                bufferOffset = 0,
                imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlagBits.ImageAspectColorBit, 0, 0, 1),
                imageOffset = new VkOffset3D(0, 0, 0),
                imageExtent = new VkExtent3D((uint)_width, (uint)_height, 1),
            };
            VulkanApi.CmdCopyImageToBuffer(_commandBuffer, _image, VkImageLayout.ImageLayoutTransferSrcOptimal, _readbackBuffer, 1, &copy);
        }
        VkRenderCore.Barrier(_commandBuffer, _image, VkImageLayout.ImageLayoutTransferSrcOptimal, VkImageLayout.ImageLayoutUndefined, VkAccessFlagBits.AccessTransferReadBit, 0);
        VkGpuDevice.Throw(VulkanApi.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");

        var device = _gpu.Device;
        var fence = _fence;
        VkGpuDevice.Throw(VulkanApi.ResetFences(device, 1, &fence), "vkResetFences");
        var commandBuffer = _commandBuffer;
        var submit = new VkSubmitInfo
        {
            sType = VkStructureType.StructureTypeSubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &commandBuffer,
        };
        VkGpuDevice.Throw(VulkanApi.QueueSubmit(_gpu.GraphicsQueue, 1, &submit, _fence), "vkQueueSubmit");

        _frameBegun = false;
        _rendered = false;
        _captured = true;
    }

    /** 读回上一帧: RGBA 4 字节/像素, 行序自下而上(与 glReadPixels/VkWindowRenderer 一致), 供截图与终端贴回。 */
    public bool TryReadPixels(Span<byte> destination, int width, int height)
        => TryReadPixels(destination, width, height, default);

    /** rows 非空时只拷这些 image 行(自上而下), 其余行保持 destination 原值。 */
    public bool TryReadPixels(Span<byte> destination, int width, int height, ReadOnlySpan<(int FirstRow, int RowCount)> rows)
    {
        if (_frameBegun && _rendered)
            Present();
        if (!_captured || width != _width || height != _height)
            return false;

        var length = _width * _height * PixelBytes;
        if (destination.Length < length)
            throw new ArgumentException($"目标缓冲需要至少 {length} 字节", nameof(destination));

        WaitSlotIdle();
        if (!_readbackCoherent)
            _gpu.InvalidateMappedRange(_readbackMemory);
        var source = new ReadOnlySpan<byte>((void*)_readbackMapped, length);
        if (rows.IsEmpty)
        {
            for (var y = 0; y < height; y++)
            {
                var sourceRow = y * width * PixelBytes;
                var destinationRow = (height - 1 - y) * width * PixelBytes;
                source.Slice(sourceRow, width * PixelBytes).CopyTo(destination[destinationRow..]);
            }

            return true;
        }

        foreach (var (first, count) in rows)
        {
            for (var y = first; y < first + count && y < height; y++)
            {
                var sourceRow = y * width * PixelBytes;
                var destinationRow = (height - 1 - y) * width * PixelBytes;
                source.Slice(sourceRow, width * PixelBytes).CopyTo(destination[destinationRow..]);
            }
        }

        return true;
    }

    private void DestroyTarget()
    {
        if (_framebuffer.Handle != 0)
            VulkanApi.DestroyFramebuffer(_gpu.Device, _framebuffer, null);
        if (_view.Handle != 0)
            VulkanApi.DestroyImageView(_gpu.Device, _view, null);
        if (_image.Handle != 0)
            VulkanApi.DestroyImage(_gpu.Device, _image, null);
        if (_memory.Handle != 0)
            VulkanApi.FreeMemory(_gpu.Device, _memory, null);
        if (_readbackBuffer.Handle != 0)
        {
            VulkanApi.UnmapMemory(_gpu.Device, _readbackMemory);
            VulkanApi.DestroyBuffer(_gpu.Device, _readbackBuffer, null);
            VulkanApi.FreeMemory(_gpu.Device, _readbackMemory, null);
        }

        _framebuffer = default;
        _view = default;
        _image = default;
        _memory = default;
        _readbackBuffer = default;
        _readbackMemory = default;
        _readbackMapped = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_gpu.Device.Handle != 0)
            VulkanApi.DeviceWaitIdle(_gpu.Device);
        DestroyTarget();
        var commandBuffer = _commandBuffer;
        VulkanApi.FreeCommandBuffers(_gpu.Device, _gpu.CommandPool, 1, &commandBuffer);
        VulkanApi.DestroyFence(_gpu.Device, _fence, null);
    }
}
