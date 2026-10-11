using System.Runtime.Intrinsics;
using Dsh.Pty;
using OpenTK.Graphics.Vulkan;
// 裸名 `Vk` 会被外层命名空间 Dsh.Tui.Vk 遮蔽(编译期优先解析为命名空间), 因此用编译单元别名指向静态绑定类。
using VulkanApi = OpenTK.Graphics.Vulkan.Vk;

namespace Dsh.Tui.Vk;

/**
 * Vulkan 交换链 / 事件循环前端: 在 VkSurfaceHost(窗口+表面+设备)之上建交换链, 每帧
 * acquire → 整帧 render(VkRenderCore) → present, 并在 out-of-date/suboptimal 时重建交换链。
 * 与离屏共用 VkRenderCore, 遵守 §9 不变量: 每帧全屏重绘; 首帧/resize/交换链重建一律整帧重画
 * (render pass loadOp=Clear + initialLayout=Undefined, 不假定交换链 image 旧内容有效); 一帧一次提交一次 present。
 * 帧同步: N 个帧槽, 每槽各自 fence/semaphore, 且 cells/params/staging 也是每槽一套(环形缓冲,VkRenderCore)。
 * 每帧只等待"当前槽"的在途 fence(WaitSlotIdle) —— 不再每帧等全部在途帧, 从而允许 N 帧重叠(§2.6)。
 */
internal sealed unsafe class VkWindowRenderer : IVkFrameTarget, IDisposable
{
    private const int MaxFramesInFlight = VkRenderCore.FrameSlots;
    private const int PixelBytes = 4;

    private readonly VkSurfaceHost _host;
    private readonly VkRenderCore _core;

    private VkSwapchainKHR _swapchain;
    private readonly VkFormat _format;
    private readonly VkColorSpaceKHR _colorSpace;
    private VkExtent2D _extent;
    private VkImage[] _images = [];
    private VkImageView[] _views = [];
    private VkFramebuffer[] _framebuffers = [];
    private bool _captureSupported;

    private readonly VkCommandBuffer[] _commandBuffers = new VkCommandBuffer[MaxFramesInFlight];
    private readonly VkSemaphore[] _imageAvailable = new VkSemaphore[MaxFramesInFlight];
    private readonly VkSemaphore[] _renderFinished = new VkSemaphore[MaxFramesInFlight];
    private readonly VkFence[] _inFlight = new VkFence[MaxFramesInFlight];
    private int _frame;

    private VkBuffer _captureBuffer;
    private VkDeviceMemory _captureMemory;
    private nint _captureMapped;
    private bool _captureCoherent = true;
    private int _captureWidth;
    private int _captureHeight;
    private bool _captureRequested;
    private bool _captured;

    private uint _imageIndex;
    private bool _frameBegun;
    private bool _rendered;
    private bool _resizePending;
    private bool _disposed;

    public VkFormat Format => _format;

    public VkExtent2D Extent => _extent;

    public uint ImageCount => (uint)_images.Length;

    public VkPresentModeKHR PresentMode { get; private set; }

    /** 本帧是否真的提交并 present 了(未出帧时为 false, 宿主据此空闲让出 CPU)。 */
    public bool FramePresented { get; private set; }

    /** 环形 staging 取证: 帧槽数(在途帧上限)。 */
    public int FramesInFlight => MaxFramesInFlight;

    /** 环形 staging 取证: WaitSlotIdle 的调用次数。 */
    public long SlotWaitCalls { get; private set; }

    /** 环形 staging 取证: 累计等待的 fence 数(每次只等当前槽 → 应等于 SlotWaitCalls)。 */
    public long TotalFencesWaited { get; private set; }

    public bool CaptureSupported => _captureSupported;

    public string DeviceName => _host.DeviceName;

    public VkRenderCore Core => _core;

    /** 当前帧槽(0..FrameSlots-1): VkRenderCore 据此把 cells/params 写进对应环形缓冲。 */
    public int CurrentSlot => _frame;

    /** 当前帧命令缓冲(仅 _frameBegun 期间有效)。 */
    public VkCommandBuffer CommandBuffer => _commandBuffers[_frame];

    /** 当前交换链 image 的 framebuffer(仅 _frameBegun 期间有效)。 */
    public VkFramebuffer CurrentFramebuffer => _framebuffers[_imageIndex];

    public (uint Width, uint Height) CurrentExtent => (_extent.width, _extent.height);

    private VkWindowRenderer(VkSurfaceHost host, GlyphAtlas atlas, bool vsync)
    {
        _host = host;
        var (format, colorSpace) = ChooseSurfaceFormat();
        _format = format;
        _colorSpace = colorSpace;
        PresentMode = ChoosePresentMode(vsync);
        _core = new VkRenderCore(host.Gpu, atlas, _format);
        _core.Target = this;
        BuildSwapchain(VkSwapchainKHR.Zero);

        var cmdInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.StructureTypeCommandBufferAllocateInfo,
            commandPool = host.Gpu.CommandPool,
            level = VkCommandBufferLevel.CommandBufferLevelPrimary,
            commandBufferCount = MaxFramesInFlight,
        };
        fixed (VkCommandBuffer* p = _commandBuffers)
            VkGpuDevice.Throw(VulkanApi.AllocateCommandBuffers(host.Gpu.Device, &cmdInfo, p), "vkAllocateCommandBuffers");

        var fenceInfo = new VkFenceCreateInfo { sType = VkStructureType.StructureTypeFenceCreateInfo, flags = VkFenceCreateFlagBits.FenceCreateSignaledBit };
        var semaphoreInfo = new VkSemaphoreCreateInfo { sType = VkStructureType.StructureTypeSemaphoreCreateInfo };
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            fixed (VkFence* p = &_inFlight[i])
                VkGpuDevice.Throw(VulkanApi.CreateFence(host.Gpu.Device, &fenceInfo, null, p), "vkCreateFence");
            fixed (VkSemaphore* p = &_imageAvailable[i])
                VkGpuDevice.Throw(VulkanApi.CreateSemaphore(host.Gpu.Device, &semaphoreInfo, null, p), "vkCreateSemaphore");
            fixed (VkSemaphore* p = &_renderFinished[i])
                VkGpuDevice.Throw(VulkanApi.CreateSemaphore(host.Gpu.Device, &semaphoreInfo, null, p), "vkCreateSemaphore");
        }
    }

    public static VkWindowRenderer Create(VkSurfaceHost host, GlyphAtlas atlas, bool vsync = true)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(atlas);
        return new VkWindowRenderer(host, atlas, vsync);
    }

    /** 泵一次 SDL 事件; 返回 false 表示应退出(Quit / 关闭窗口)。键鼠事件暂未映射(见报告未解决项)。 */
    public bool PumpEvents()
    {
        SDL.SDL_Event @event;
        while (SDL.SDL3.SDL_PollEvent(&@event))
        {
            switch ((SDL.SDL_EventType)@event.type)
            {
                case SDL.SDL_EventType.SDL_EVENT_QUIT:
                case SDL.SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                    return false;
                case SDL.SDL_EventType.SDL_EVENT_WINDOW_RESIZED:
                case SDL.SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                    _resizePending = true;
                    break;
            }
        }

        return true;
    }

    /** 渲染并 present 一帧真实 CellGrid(离屏对拍/冒烟用; 走内部 BeginFrame→RecordFrame→Present 三步)。 */
    public void DrawFrame(CellGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        // Prepare 会把整屏写进当前槽的环形缓冲, 必须先等该槽的在途 fence(否则与 GPU 读竞争)。
        WaitSlotIdle();
        _core.Prepare(grid);
        if (!BeginFrame())
            return;
        _core.RecordFrame(CommandBuffer, CurrentFramebuffer, _extent.width, _extent.height, grid.Width, grid.Height);
        _rendered = true;
        Present();
    }

    /**
     * 等"当前槽"的在途 fence: 上上帧(同槽)完成后本槽的环形缓冲才可被 CPU 重写。
     * 只等这一槽 —— 其余槽允许继续在途(§2.6 环形 staging)。可在上传前与 BeginFrame 前重复调用(幂等, 不 reset)。
     */
    public void WaitSlotIdle()
    {
        fixed (VkFence* p = &_inFlight[_frame])
            VkGpuDevice.Throw(VulkanApi.WaitForFences(_host.Gpu.Device, 1, p, 1, ulong.MaxValue), "vkWaitForFences");
        SlotWaitCalls++;
        TotalFencesWaited++;
    }

    /**
     * 开启一帧: 处理挂起的 resize, acquire 交换链 image, 开命令缓冲并把目标 image 从 Undefined 转到
     * ColorAttachmentOptimal(不假定旧内容有效)。返回 false 表示本帧无法开始(out-of-date/零尺寸), 调用方跳过本帧。
     * 环形缓冲的 fence 等待在 WaitSlotIdle(RenderFrame 前的上传阶段), 这里不再等全部在途帧。
     */
    public bool BeginFrame()
    {
        if (_frameBegun)
            return true;
        if (_resizePending)
            RecreateSwapchain();
        if (_extent.width == 0 || _extent.height == 0)
            return false;

        var device = _host.Gpu.Device;
        var imageIndex = 0u;
        var acquire = VulkanApi.AcquireNextImageKHR(device, _swapchain, ulong.MaxValue, _imageAvailable[_frame], default, &imageIndex);
        if (acquire == VkResult.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            return false;
        }

        if (acquire != VkResult.Success && acquire != VkResult.SuboptimalKhr)
            VkGpuDevice.Throw(acquire, "vkAcquireNextImageKHR");

        var commandBuffer = _commandBuffers[_frame];
        var begin = new VkCommandBufferBeginInfo { sType = VkStructureType.StructureTypeCommandBufferBeginInfo };
        VkGpuDevice.Throw(VulkanApi.BeginCommandBuffer(commandBuffer, &begin), "vkBeginCommandBuffer");
        // 不假定旧内容有效: Undefined → ColorAttachmentOptimal, render pass 全帧 clear。
        VkRenderCore.Barrier(commandBuffer, _images[imageIndex], VkImageLayout.ImageLayoutUndefined, VkImageLayout.ImageLayoutColorAttachmentOptimal, 0, VkAccessFlagBits.AccessColorAttachmentWriteBit);

        _imageIndex = imageIndex;
        _frameBegun = true;
        _rendered = false;
        FramePresented = false;
        return true;
    }

    /** 标记本帧已录制内容(决定 Present 是否真的有帧可提交)。 */
    public void MarkRendered() => _rendered = true;

    /** 结束并提交当前帧: 需要时把交换链 image 拷回可读缓冲, 转 PresentSrc, 提交并 present。 */
    public void Present()
    {
        if (!_frameBegun || !_rendered)
            return;

        var commandBuffer = _commandBuffers[_frame];
        var image = _images[_imageIndex];
        var captureThisFrame = _captureRequested && _captureSupported;
        if (captureThisFrame)
        {
            EnsureCaptureBuffer((int)_extent.width, (int)_extent.height);
            VkRenderCore.Barrier(commandBuffer, image, VkImageLayout.ImageLayoutColorAttachmentOptimal, VkImageLayout.ImageLayoutTransferSrcOptimal, VkAccessFlagBits.AccessColorAttachmentWriteBit, VkAccessFlagBits.AccessTransferReadBit);
            var copy = new VkBufferImageCopy
            {
                bufferOffset = 0,
                imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlagBits.ImageAspectColorBit, 0, 0, 1),
                imageOffset = new VkOffset3D(0, 0, 0),
                imageExtent = new VkExtent3D(_extent.width, _extent.height, 1),
            };
            VulkanApi.CmdCopyImageToBuffer(commandBuffer, image, VkImageLayout.ImageLayoutTransferSrcOptimal, _captureBuffer, 1, &copy);
            VkRenderCore.Barrier(commandBuffer, image, VkImageLayout.ImageLayoutTransferSrcOptimal, VkImageLayout.ImageLayoutPresentSrcKhr, VkAccessFlagBits.AccessTransferReadBit, 0);
        }
        else
        {
            VkRenderCore.Barrier(commandBuffer, image, VkImageLayout.ImageLayoutColorAttachmentOptimal, VkImageLayout.ImageLayoutPresentSrcKhr, VkAccessFlagBits.AccessColorAttachmentWriteBit, 0);
        }

        VkGpuDevice.Throw(VulkanApi.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

        // 该槽的 fence 已在 WaitSlotIdle 等到; 现在(确定要提交了)才 reset, 交给本次 QueueSubmit 重新置位。
        var device = _host.Gpu.Device;
        fixed (VkFence* fence = &_inFlight[_frame])
            VkGpuDevice.Throw(VulkanApi.ResetFences(device, 1, fence), "vkResetFences");

        var waitSemaphore = _imageAvailable[_frame];
        var signalSemaphore = _renderFinished[_frame];
        var waitStage = VkPipelineStageFlagBits.PipelineStageColorAttachmentOutputBit;
        var submit = new VkSubmitInfo
        {
            sType = VkStructureType.StructureTypeSubmitInfo,
            waitSemaphoreCount = 1,
            pWaitSemaphores = &waitSemaphore,
            pWaitDstStageMask = &waitStage,
            commandBufferCount = 1,
            pCommandBuffers = &commandBuffer,
            signalSemaphoreCount = 1,
            pSignalSemaphores = &signalSemaphore,
        };
        VkGpuDevice.Throw(VulkanApi.QueueSubmit(_host.Gpu.GraphicsQueue, 1, &submit, _inFlight[_frame]), "vkQueueSubmit");

        var imageIndex = _imageIndex;
        var presentSwapchain = _swapchain;
        var present = new VkPresentInfoKHR
        {
            waitSemaphoreCount = 1,
            pWaitSemaphores = &signalSemaphore,
            swapchainCount = 1,
            pSwapchains = &presentSwapchain,
            pImageIndices = &imageIndex,
        };
        var presentResult = VulkanApi.QueuePresentKHR(_host.Gpu.GraphicsQueue, &present);
        if (presentResult is VkResult.ErrorOutOfDateKhr or VkResult.SuboptimalKhr)
            RecreateSwapchain();
        else
            VkGpuDevice.Throw(presentResult, "vkQueuePresentKHR");

        if (captureThisFrame)
            _captured = true;
        _captureRequested = false;
        _frameBegun = false;
        _rendered = false;
        FramePresented = true;
        _frame = (_frame + 1) % MaxFramesInFlight;
    }

    /** 请求下一帧重建交换链(窗口尺寸变化/显式 resize)。 */
    public void RequestResize() => _resizePending = true;

    /** 读回最近一次捕获的交换链 image, 转成 RGBA 且行序自下而上(与 glReadPixels 一致)。 */
    public bool TryReadPixels(Span<byte> destination, int width, int height)
    {
        if (_frameBegun && _rendered)
            Present();
        if (!_captured || width != _captureWidth || height != _captureHeight)
            return false;

        var length = _captureWidth * _captureHeight * PixelBytes;
        if (destination.Length < length)
            throw new ArgumentException($"目标缓冲需要至少 {length} 字节", nameof(destination));
        // 拷回缓冲由 GPU 异步写入: 读前必须等所有在途提交完成, 否则读到未完成/未定义内容。
        var fences = stackalloc VkFence[MaxFramesInFlight];
        for (var i = 0; i < MaxFramesInFlight; i++)
            fences[i] = _inFlight[i];
        VkGpuDevice.Throw(VulkanApi.WaitForFences(_host.Gpu.Device, MaxFramesInFlight, fences, 1, ulong.MaxValue), "vkWaitForFences");
        if (!_captureCoherent)
            _host.Gpu.InvalidateMappedRange(_captureMemory);

        var source = new Span<byte>((void*)_captureMapped, length);
        var bgra = _format is VkFormat.FormatB8g8r8a8Unorm or VkFormat.FormatB8g8r8a8Srgb;
        var rowBytes = width * PixelBytes;
        for (var y = 0; y < height; y++)
        {
            // 行序自下而上翻转; 每行整体拷贝(BGRA 时顺带 R/B 交换), 不再逐像素。
            CopyRow(source.Slice(y * rowBytes, rowBytes), destination.Slice((height - 1 - y) * rowBytes, rowBytes), bgra);
        }

        return true;
    }

    /** 整行拷回并按需交换 R/B: 16 字节一次(SSSE3 pshufb / ARM TBL), 尾部按 4 字节标量。 */
    private static void CopyRow(Span<byte> source, Span<byte> destination, bool swapRedBlue)
    {
        if (!swapRedBlue)
        {
            source.CopyTo(destination);
            return;
        }

        var index = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            var shuffle = Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
            for (; index + 16 <= destination.Length; index += 16)
                Vector128.Shuffle(Vector128.LoadUnsafe(ref source[index]), shuffle).StoreUnsafe(ref destination[index]);
        }

        for (; index + PixelBytes <= destination.Length; index += PixelBytes)
        {
            destination[index] = source[index + 2];
            destination[index + 1] = source[index + 1];
            destination[index + 2] = source[index];
            destination[index + 3] = source[index + 3];
        }
    }

    /** 请求在下一帧 present 前把交换链 image 拷回可读缓冲(用于取证/对拍)。 */
    public void RequestCapture()
    {
        _captureRequested = true;
        _captured = false;
    }

    /** 取回最近一次请求的交换链 image 像素(行主序, 4 字节/像素; 通道序见 Format)。 */
    public bool TryGetCapture(Span<byte> destination)
    {
        if (!_captured)
            return false;
        var length = _captureWidth * _captureHeight * PixelBytes;
        if (destination.Length < length)
            throw new ArgumentException($"目标缓冲需要至少 {length} 字节", nameof(destination));
        // 拷回缓冲由 GPU 异步写入: 读前必须等所有在途提交完成, 否则读到未完成/未定义内容。
        var fences = stackalloc VkFence[MaxFramesInFlight];
        for (var i = 0; i < MaxFramesInFlight; i++)
            fences[i] = _inFlight[i];
        VkGpuDevice.Throw(VulkanApi.WaitForFences(_host.Gpu.Device, MaxFramesInFlight, fences, 1, ulong.MaxValue), "vkWaitForFences");
        new ReadOnlySpan<byte>((void*)_captureMapped, length).CopyTo(destination);
        return true;
    }

    private (VkFormat Format, VkColorSpaceKHR ColorSpace) ChooseSurfaceFormat()
    {
        var physicalDevice = _host.Gpu.PhysicalDevice;
        uint count = 0;
        VkGpuDevice.Throw(VulkanApi.GetPhysicalDeviceSurfaceFormatsKHR(physicalDevice, _host.Surface, &count, null), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        var formats = new VkSurfaceFormatKHR[count];
        fixed (VkSurfaceFormatKHR* p = formats)
            VkGpuDevice.Throw(VulkanApi.GetPhysicalDeviceSurfaceFormatsKHR(physicalDevice, _host.Surface, &count, p), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        if (formats.Length == 0)
            throw new InvalidOperationException("表面没有任何可用格式");
        if (formats.Length == 1 && formats[0].format == VkFormat.FormatUndefined)
            return (VkFormat.FormatR8g8b8a8Unorm, VkColorSpaceKHR.ColorSpaceSrgbNonlinearKhr);

        // 优先与离屏一致的 R8G8B8A8_UNORM(读回通道序一致); 否则退 B8G8R8A8_UNORM; 再否则第一个。
        foreach (var candidate in formats)
        {
            if (candidate.format == VkFormat.FormatR8g8b8a8Unorm)
                return (candidate.format, candidate.colorSpace);
        }

        foreach (var candidate in formats)
        {
            if (candidate.format == VkFormat.FormatB8g8r8a8Unorm)
                return (candidate.format, candidate.colorSpace);
        }

        return (formats[0].format, formats[0].colorSpace);
    }

    /** vsync 开: FIFO; 关: 优先 IMMEDIATE, 再 MAILBOX, 都没有则退回 FIFO(不静默丢帧)。 */
    private VkPresentModeKHR ChoosePresentMode(bool vsync)
    {
        if (vsync)
            return VkPresentModeKHR.PresentModeFifoKhr;
        var physicalDevice = _host.Gpu.PhysicalDevice;
        uint count = 0;
        VkGpuDevice.Throw(VulkanApi.GetPhysicalDeviceSurfacePresentModesKHR(physicalDevice, _host.Surface, &count, null), "vkGetPhysicalDeviceSurfacePresentModesKHR");
        var modes = new VkPresentModeKHR[count];
        fixed (VkPresentModeKHR* p = modes)
            VkGpuDevice.Throw(VulkanApi.GetPhysicalDeviceSurfacePresentModesKHR(physicalDevice, _host.Surface, &count, p), "vkGetPhysicalDeviceSurfacePresentModesKHR");
        if (Array.IndexOf(modes, VkPresentModeKHR.PresentModeImmediateKhr) >= 0)
            return VkPresentModeKHR.PresentModeImmediateKhr;
        if (Array.IndexOf(modes, VkPresentModeKHR.PresentModeMailboxKhr) >= 0)
            return VkPresentModeKHR.PresentModeMailboxKhr;
        return VkPresentModeKHR.PresentModeFifoKhr;
    }

    private VkSurfaceCapabilitiesKHR QueryCapabilities()
    {
        VkSurfaceCapabilitiesKHR capabilities;
        VkGpuDevice.Throw(VulkanApi.GetPhysicalDeviceSurfaceCapabilitiesKHR(_host.Gpu.PhysicalDevice, _host.Surface, &capabilities), "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
        return capabilities;
    }

    private VkExtent2D ChooseExtent(VkSurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.currentExtent.width != uint.MaxValue)
            return capabilities.currentExtent;
        var (width, height) = _host.PixelSize();
        return new VkExtent2D(
            Math.Clamp((uint)width, capabilities.minImageExtent.width, capabilities.maxImageExtent.width),
            Math.Clamp((uint)height, capabilities.minImageExtent.height, capabilities.maxImageExtent.height));
    }

    private void BuildSwapchain(VkSwapchainKHR oldSwapchain)
    {
        var capabilities = QueryCapabilities();
        _extent = ChooseExtent(capabilities);

        var usage = VkImageUsageFlagBits.ImageUsageColorAttachmentBit;
        _captureSupported = (capabilities.supportedUsageFlags & VkImageUsageFlagBits.ImageUsageTransferSrcBit) != 0;
        if (_captureSupported)
            usage |= VkImageUsageFlagBits.ImageUsageTransferSrcBit;

        var imageCount = capabilities.minImageCount + 1;
        if (capabilities.maxImageCount > 0 && imageCount > capabilities.maxImageCount)
            imageCount = capabilities.maxImageCount;

        var createInfo = new VkSwapchainCreateInfoKHR
        {
            surface = _host.Surface,
            minImageCount = imageCount,
            imageFormat = _format,
            imageColorSpace = _colorSpace,
            imageExtent = _extent,
            imageArrayLayers = 1,
            imageUsage = usage,
            imageSharingMode = VkSharingMode.SharingModeExclusive,
            preTransform = capabilities.currentTransform,
            compositeAlpha = PickCompositeAlpha(capabilities.supportedCompositeAlpha),
            presentMode = PresentMode,
            clipped = 1,
            oldSwapchain = oldSwapchain,
        };
        VkSwapchainKHR swapchain;
        VkGpuDevice.Throw(VulkanApi.CreateSwapchainKHR(_host.Gpu.Device, &createInfo, null, &swapchain), "vkCreateSwapchainKHR");

        DestroySwapchainResources(oldSwapchain);
        _swapchain = swapchain;
        FetchImages();
    }

    private void FetchImages()
    {
        var device = _host.Gpu.Device;
        uint count = 0;
        VkGpuDevice.Throw(VulkanApi.GetSwapchainImagesKHR(device, _swapchain, &count, null), "vkGetSwapchainImagesKHR");
        var images = new VkImage[count];
        fixed (VkImage* p = images)
            VkGpuDevice.Throw(VulkanApi.GetSwapchainImagesKHR(device, _swapchain, &count, p), "vkGetSwapchainImagesKHR");

        _images = images;
        _views = new VkImageView[count];
        _framebuffers = new VkFramebuffer[count];
        for (var i = 0; i < count; i++)
        {
            _views[i] = _host.Gpu.CreateImageView(images[i], _format);
            _framebuffers[i] = _core.CreateFramebuffer(_views[i], _extent.width, _extent.height);
        }
    }

    private void RecreateSwapchain()
    {
        _resizePending = false;
        var device = _host.Gpu.Device;
        if (device.Handle != 0)
            VulkanApi.DeviceWaitIdle(device);
        var old = _swapchain;
        BuildSwapchain(old);
    }

    private void DestroySwapchainResources(VkSwapchainKHR swapchain)
    {
        var device = _host.Gpu.Device;
        foreach (var framebuffer in _framebuffers)
        {
            if (framebuffer.Handle != 0)
                VulkanApi.DestroyFramebuffer(device, framebuffer, null);
        }

        foreach (var view in _views)
        {
            if (view.Handle != 0)
                VulkanApi.DestroyImageView(device, view, null);
        }

        _framebuffers = [];
        _views = [];
        _images = [];

        if (swapchain.Handle != 0)
            VulkanApi.DestroySwapchainKHR(device, swapchain, null);
    }

    private void EnsureCaptureBuffer(int width, int height)
    {
        if (_captureBuffer.Handle != 0 && width == _captureWidth && height == _captureHeight)
            return;
        if (_captureBuffer.Handle != 0)
        {
            VulkanApi.UnmapMemory(_host.Gpu.Device, _captureMemory);
            VulkanApi.DestroyBuffer(_host.Gpu.Device, _captureBuffer, null);
            VulkanApi.FreeMemory(_host.Gpu.Device, _captureMemory, null);
        }

        _captureWidth = width;
        _captureHeight = height;
        _captureBuffer = _host.Gpu.CreateMappedReadbackBuffer((ulong)width * (ulong)height * PixelBytes, out _captureMemory, out _captureMapped, out _captureCoherent);
    }

    private static VkCompositeAlphaFlagBitsKHR PickCompositeAlpha(VkCompositeAlphaFlagBitsKHR supported)
    {
        if ((supported & VkCompositeAlphaFlagBitsKHR.CompositeAlphaOpaqueBitKhr) != 0)
            return VkCompositeAlphaFlagBitsKHR.CompositeAlphaOpaqueBitKhr;
        if ((supported & VkCompositeAlphaFlagBitsKHR.CompositeAlphaPreMultipliedBitKhr) != 0)
            return VkCompositeAlphaFlagBitsKHR.CompositeAlphaPreMultipliedBitKhr;
        if ((supported & VkCompositeAlphaFlagBitsKHR.CompositeAlphaPostMultipliedBitKhr) != 0)
            return VkCompositeAlphaFlagBitsKHR.CompositeAlphaPostMultipliedBitKhr;
        return VkCompositeAlphaFlagBitsKHR.CompositeAlphaInheritBitKhr;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var device = _host.Gpu.Device;
        if (device.Handle != 0)
            VulkanApi.DeviceWaitIdle(device);

        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            if (_imageAvailable[i].Handle != 0)
                VulkanApi.DestroySemaphore(device, _imageAvailable[i], null);
            if (_renderFinished[i].Handle != 0)
                VulkanApi.DestroySemaphore(device, _renderFinished[i], null);
            if (_inFlight[i].Handle != 0)
                VulkanApi.DestroyFence(device, _inFlight[i], null);
        }

        fixed (VkCommandBuffer* p = _commandBuffers)
            VulkanApi.FreeCommandBuffers(device, _host.Gpu.CommandPool, MaxFramesInFlight, p);

        if (_captureBuffer.Handle != 0)
        {
            VulkanApi.UnmapMemory(device, _captureMemory);
            VulkanApi.DestroyBuffer(device, _captureBuffer, null);
            VulkanApi.FreeMemory(device, _captureMemory, null);
        }

        DestroySwapchainResources(_swapchain);
        _swapchain = VkSwapchainKHR.Zero;

        _core.Dispose();
    }
}
