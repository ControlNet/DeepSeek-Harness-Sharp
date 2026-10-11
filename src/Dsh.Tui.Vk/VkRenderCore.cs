using System.Runtime.InteropServices;
using Dsh.Pty;
using OpenTK.Graphics.Vulkan;
// 裸名 `Vk` 会被外层命名空间 Dsh.Tui.Vk 遮蔽(编译期优先解析为命名空间), 因此用编译单元别名指向静态绑定类。
using VulkanApi = OpenTK.Graphics.Vulkan.Vk;

namespace Dsh.Tui.Vk;

/**
 * VkRenderCore 的"帧目标": 交换链(VkWindowRenderer)或离屏 image(VkOffscreenTarget)。
 * 核心只经此接口驱动一帧的目标/命令缓冲/帧槽, 从而窗口与无头离屏复用同一套录制逻辑。
 */
internal interface IVkFrameTarget
{
    /** 当前帧槽(0..FrameSlots-1): 核心据此选择环形缓冲。 */
    int CurrentSlot { get; }

    /** 等当前槽的在途 fence(上传前调用, 幂等)。 */
    void WaitSlotIdle();

    /** 开始一帧(acquire/开命令缓冲/布局转换); false 表示本帧跳过。 */
    bool BeginFrame();

    VkCommandBuffer CommandBuffer { get; }

    VkFramebuffer CurrentFramebuffer { get; }

    (uint Width, uint Height) CurrentExtent { get; }

    /** 标记本帧已录制内容(Present 是否真有帧可提交)。 */
    void MarkRendered();

    void RequestResize();

    void RequestCapture();

    /** 本帧回读只需这些像素行(自上而下); 空 = 整帧。默认忽略(窗口目标不需要)。 */
    void SetCaptureRows(ReadOnlySpan<(int FirstRow, int RowCount)> pixelRows)
    {
    }

    bool TryReadPixels(Span<byte> destination, int width, int height);

    /** 只读回指定像素行(其余行保持调用方缓冲原值); 空 = 整帧。默认退化为整帧。 */
    bool TryReadPixels(Span<byte> destination, int width, int height, ReadOnlySpan<(int FirstRow, int RowCount)> rows)
        => TryReadPixels(destination, width, height);
}

/**
 * Vulkan 文本渲染共享核心: render pass / descriptor set / pipeline / 字形图集与脏槽上传 /
 * cells 打包与写入 / params(palette) 上传 / 一趟 CmdDraw 录制。
 * 离屏(VkOffscreenRenderer)与窗口/交换链(VkWindowRenderer)两个前端复用同一套录制逻辑,
 * 只各自持有自己的"目标"(离屏 image / 交换链 image 的 framebuffer)。
 * 语义严格对齐 GL 后端 GpuRenderCore(见 plans/DSH-Sharp-§4-Vulkan与cellgrid方案.md §9):
 *  - 恒全帧重绘(render pass loadOp=Clear, initialLayout=Undefined), 不做脏行重放;
 *  - 续格('\0')与空格不画字形; 宽字形背景横向 ×2、位图不缩放;
 *  - 每帧从 CellPacker 的打包结果全量提交到 cells 缓冲。
 */
internal sealed unsafe class VkRenderCore : ITuiSurfaceBackend
{
    private const int VertexCount = 6;                              // 三角形列表拼单位方块
    private const int ParamsSize = (4 * 16) + (64 * 16) + (2 * 16); // ivec4×4 + vec4[64] + 默认前/背景
    private const int HeaderFloats = 16;                            // 4×ivec4(网格/图集槽/图集像素/单格像素)
    private const int PaletteSlots = 64;
    private const int DefaultsFloats = HeaderFloats + (PaletteSlots * 4); // 默认前/背景起点(float 下标)
    private const int GlyphMapSide = 256;                           // 字形映射表 256×256(与 char 0..65535 一一对应)
    private const int MaxFlushSlots = 256;

    /**
     * 环形缓冲的帧槽数(N≥2 才谈得上重叠): 每槽各自一套 cells/params/staging 与 fence/semaphore,
     * 渲染时只等当前槽的在途 fence; 每帧仍是"全量上传 + 全屏重绘"(§2.6/§9 B4)。
     */
    internal const int FrameSlots = 3;

    private readonly GlyphAtlas _atlas;
    private readonly VkFormat _targetFormat;

    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _descriptorPool;
    private readonly VkDescriptorSet[] _descriptorSets = new VkDescriptorSet[FrameSlots];
    private readonly VkPipelineLayout _pipelineLayout;
    private readonly VkPipeline _pipeline;
    private readonly VkRenderPass _renderPass;
    private readonly VkSampler _sampler;

    private readonly VkImage _atlasImage;
    private readonly VkDeviceMemory _atlasMemory;
    private readonly VkImageView _atlasView;
    private readonly VkImage _glyphImage;
    private readonly VkDeviceMemory _glyphMemory;
    private readonly VkImageView _glyphView;

    private readonly VkBuffer[] _paramsBuffers = new VkBuffer[FrameSlots];
    private readonly VkDeviceMemory[] _paramsMemories = new VkDeviceMemory[FrameSlots];
    private readonly nint[] _paramsMapped = new nint[FrameSlots];
    private readonly VkBuffer[] _stagingBuffers = new VkBuffer[FrameSlots];
    private readonly VkDeviceMemory[] _stagingMemories = new VkDeviceMemory[FrameSlots];
    private readonly nint[] _stagingMapped = new nint[FrameSlots];

    private readonly VkBuffer[] _cellsBuffers = new VkBuffer[FrameSlots];
    private readonly VkDeviceMemory[] _cellsMemories = new VkDeviceMemory[FrameSlots];
    private readonly VkBufferView[] _cellsViews = new VkBufferView[FrameSlots];
    private readonly VkBuffer[] _cellsStagingBuffers = new VkBuffer[FrameSlots];
    private readonly VkDeviceMemory[] _cellsStagingMemories = new VkDeviceMemory[FrameSlots];
    private readonly nint[] _cellsStagingMapped = new nint[FrameSlots];
    private int _cellsCapacity;

    private uint[] _packed = [];
    private readonly List<(int FirstCell, int Count)> _frameUploads = [];    private int _seenMapVersion = -1;
    private bool _atlasCreated;
    private bool _disposed;

    public VkRenderCore(VkGpuDevice gpu, GlyphAtlas atlas, VkFormat targetFormat = VkFormat.FormatR8g8b8a8Unorm)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(atlas);
        _atlas = atlas;
        Gpu = gpu;
        _targetFormat = targetFormat;

        var bindings = new VkDescriptorSetLayoutBinding[]
        {
            new() { binding = 0, descriptorType = VkDescriptorType.DescriptorTypeCombinedImageSampler, descriptorCount = 1, stageFlags = VkShaderStageFlagBits.ShaderStageFragmentBit },
            new() { binding = 1, descriptorType = VkDescriptorType.DescriptorTypeUniformTexelBuffer, descriptorCount = 1, stageFlags = VkShaderStageFlagBits.ShaderStageVertexBit },
            new() { binding = 2, descriptorType = VkDescriptorType.DescriptorTypeCombinedImageSampler, descriptorCount = 1, stageFlags = VkShaderStageFlagBits.ShaderStageVertexBit },
            new() { binding = 3, descriptorType = VkDescriptorType.DescriptorTypeUniformBuffer, descriptorCount = 1, stageFlags = VkShaderStageFlagBits.ShaderStageVertexBit },
        };
        fixed (VkDescriptorSetLayoutBinding* p = bindings)
        {
            var layoutInfo = new VkDescriptorSetLayoutCreateInfo
            {
                sType = VkStructureType.StructureTypeDescriptorSetLayoutCreateInfo,
                bindingCount = (uint)bindings.Length,
                pBindings = p,
            };
            VkDescriptorSetLayout setLayoutOut;
            VkGpuDevice.Throw(VulkanApi.CreateDescriptorSetLayout(gpu.Device, &layoutInfo, null, &setLayoutOut), "vkCreateDescriptorSetLayout");
            _setLayout = setLayoutOut;
        }

        var sizes = new VkDescriptorPoolSize[]
        {
            new() { type = VkDescriptorType.DescriptorTypeCombinedImageSampler, descriptorCount = 2 * FrameSlots },
            new() { type = VkDescriptorType.DescriptorTypeUniformTexelBuffer, descriptorCount = FrameSlots },
            new() { type = VkDescriptorType.DescriptorTypeUniformBuffer, descriptorCount = FrameSlots },
        };
        fixed (VkDescriptorPoolSize* p = sizes)
        {
            var poolInfo = new VkDescriptorPoolCreateInfo
            {
                sType = VkStructureType.StructureTypeDescriptorPoolCreateInfo,
                maxSets = FrameSlots,
                poolSizeCount = (uint)sizes.Length,
                pPoolSizes = p,
            };
            VkDescriptorPool descriptorPoolOut;
            VkGpuDevice.Throw(VulkanApi.CreateDescriptorPool(gpu.Device, &poolInfo, null, &descriptorPoolOut), "vkCreateDescriptorPool");
            _descriptorPool = descriptorPoolOut;
        }

        // 每帧槽一个描述符集: cells/params 是环形缓冲, 不能共用一个集(否则会指向正在被 GPU 读的槽)。
        var setLayouts = new VkDescriptorSetLayout[FrameSlots];
        for (var i = 0; i < FrameSlots; i++)
            setLayouts[i] = _setLayout;
        fixed (VkDescriptorSetLayout* p = setLayouts)
        {
            var allocate = new VkDescriptorSetAllocateInfo
            {
                sType = VkStructureType.StructureTypeDescriptorSetAllocateInfo,
                descriptorPool = _descriptorPool,
                descriptorSetCount = FrameSlots,
                pSetLayouts = p,
            };
            fixed (VkDescriptorSet* sets = _descriptorSets)
                VkGpuDevice.Throw(VulkanApi.AllocateDescriptorSets(gpu.Device, &allocate, sets), "vkAllocateDescriptorSets");
        }

        var range = new VkPushConstantRange
        {
            stageFlags = VkShaderStageFlagBits.ShaderStageVertexBit,
            offset = 0,
            size = sizeof(int),
        };
        var pipelineSetLayout = _setLayout;
        var pipelineLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            sType = VkStructureType.StructureTypePipelineLayoutCreateInfo,
            setLayoutCount = 1,
            pSetLayouts = &pipelineSetLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &range,
        };
        VkPipelineLayout pipelineLayoutOut;
        VkGpuDevice.Throw(VulkanApi.CreatePipelineLayout(gpu.Device, &pipelineLayoutInfo, null, &pipelineLayoutOut), "vkCreatePipelineLayout");
        _pipelineLayout = pipelineLayoutOut;

        _renderPass = CreateRenderPass(targetFormat);
        _pipeline = CreatePipeline();

        var samplerInfo = new VkSamplerCreateInfo
        {
            sType = VkStructureType.StructureTypeSamplerCreateInfo,
            magFilter = VkFilter.FilterNearest,
            minFilter = VkFilter.FilterNearest,
            mipmapMode = VkSamplerMipmapMode.SamplerMipmapModeNearest,
            addressModeU = VkSamplerAddressMode.SamplerAddressModeClampToEdge,
            addressModeV = VkSamplerAddressMode.SamplerAddressModeClampToEdge,
            addressModeW = VkSamplerAddressMode.SamplerAddressModeClampToEdge,
            // 图集/字形映射表都只创建了 1 级 mip, maxLod 必须 <= 0: 写 1f 属非法采样器状态(UB),
            // AMD 上表现为采样到未定义内容 —— 用户可见"字形下缘被切/变简"(GL 侧采样器独立所以正常)。
            maxLod = 0f,
        };
        VkSampler samplerOut;
        VkGpuDevice.Throw(VulkanApi.CreateSampler(gpu.Device, &samplerInfo, null, &samplerOut), "vkCreateSampler");
        _sampler = samplerOut;

        var stagingSize = (ulong)Math.Max(atlas.AtlasWidth * atlas.AtlasHeight, GlyphMapSide * GlyphMapSide * 4);
        for (var slot = 0; slot < FrameSlots; slot++)
        {
            _paramsBuffers[slot] = gpu.CreateMappedBuffer(ParamsSize, VkBufferUsageFlagBits.BufferUsageUniformBufferBit, out var paramsMemory, out var paramsMapped);
            _paramsMemories[slot] = paramsMemory;
            _paramsMapped[slot] = paramsMapped;
            _stagingBuffers[slot] = gpu.CreateMappedBuffer(stagingSize, VkBufferUsageFlagBits.BufferUsageTransferSrcBit, out var stagingMemory, out var stagingMapped);
            _stagingMemories[slot] = stagingMemory;
            _stagingMapped[slot] = stagingMapped;
        }

        (_atlasImage, _atlasMemory, _atlasView) = CreateImage(VkFormat.FormatR8Unorm, (uint)atlas.AtlasWidth, (uint)atlas.AtlasHeight);
        (_glyphImage, _glyphMemory, _glyphView) = CreateImage(VkFormat.FormatR32Sint, GlyphMapSide, GlyphMapSide);

        EnsureCells(GlyphAtlas.Columns * GlyphAtlas.Rows / 64);
        UploadGlyphMapIfChanged();
    }

    /** 当前帧槽(由帧目标决定; 无目标时恒为 0)。 */
    private int CurrentSlot => Target?.CurrentSlot ?? 0;

    public VkGpuDevice Gpu { get; }

    public int GlyphWidth => _atlas.GlyphWidth;

    public int GlyphHeight => _atlas.GlyphHeight;

    /** 本核心的帧目标(交换链或无头离屏 image); 由 VkSurfaceHost/VkOffscreenHost 在创建后回填。 */
    internal IVkFrameTarget? Target { get; set; }

    /** 诊断/回归: 把 GPU 上的字形映射表整幅回读(R32_SINT, 行主序 GlyphMapSide×GlyphMapSide), 与 GlyphAtlas.MapUpload 对比。 */
    internal void ReadGlyphMapTexture(Span<int> destination)
    {
        var length = GlyphMapSide * GlyphMapSide;
        if (destination.Length < length)
            throw new ArgumentException($"目标缓冲需要至少 {length} 个 int", nameof(destination));
        VulkanApi.DeviceWaitIdle(Gpu.Device);
        var buffer = Gpu.CreateMappedReadbackBuffer((ulong)length * sizeof(int), out var memory, out var mapped, out var coherent);
        try
        {
            Gpu.OneShot(cmd =>
            {
                Barrier(cmd, _glyphImage, VkImageLayout.ImageLayoutShaderReadOnlyOptimal, VkImageLayout.ImageLayoutTransferSrcOptimal,
                    VkAccessFlagBits.AccessShaderReadBit, VkAccessFlagBits.AccessTransferReadBit);
                var copy = new VkBufferImageCopy
                {
                    bufferOffset = 0,
                    imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlagBits.ImageAspectColorBit, 0, 0, 1),
                    imageOffset = new VkOffset3D(0, 0, 0),
                    imageExtent = new VkExtent3D(GlyphMapSide, GlyphMapSide, 1),
                };
                VulkanApi.CmdCopyImageToBuffer(cmd, _glyphImage, VkImageLayout.ImageLayoutTransferSrcOptimal, buffer, 1, &copy);
                Barrier(cmd, _glyphImage, VkImageLayout.ImageLayoutTransferSrcOptimal, VkImageLayout.ImageLayoutShaderReadOnlyOptimal,
                    VkAccessFlagBits.AccessTransferReadBit, VkAccessFlagBits.AccessShaderReadBit);
            });
            if (!coherent)
                Gpu.InvalidateMappedRange(memory);
            new ReadOnlySpan<int>((void*)mapped, length).CopyTo(destination);
        }
        finally
        {
            VulkanApi.UnmapMemory(Gpu.Device, memory);
            VulkanApi.DestroyBuffer(Gpu.Device, buffer, null);
            VulkanApi.FreeMemory(Gpu.Device, memory, null);
        }
    }

    /** 诊断/回归: 把 GPU 上的 atlas 纹理整幅回读(R8, 行主序 AtlasWidth×AtlasHeight), 与 GlyphAtlas.TextureData 对比。 */
    internal void ReadAtlasTexture(Span<byte> destination)
    {
        var length = _atlas.AtlasWidth * _atlas.AtlasHeight;
        if (destination.Length < length)
            throw new ArgumentException($"目标缓冲需要至少 {length} 字节", nameof(destination));
        VulkanApi.DeviceWaitIdle(Gpu.Device);
        var buffer = Gpu.CreateMappedReadbackBuffer((ulong)length, out var memory, out var mapped, out var coherent);
        try
        {
            Gpu.OneShot(cmd =>
            {
                Barrier(cmd, _atlasImage, VkImageLayout.ImageLayoutShaderReadOnlyOptimal, VkImageLayout.ImageLayoutTransferSrcOptimal,
                    VkAccessFlagBits.AccessShaderReadBit, VkAccessFlagBits.AccessTransferReadBit);
                var copy = new VkBufferImageCopy
                {
                    bufferOffset = 0,
                    imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlagBits.ImageAspectColorBit, 0, 0, 1),
                    imageOffset = new VkOffset3D(0, 0, 0),
                    imageExtent = new VkExtent3D((uint)_atlas.AtlasWidth, (uint)_atlas.AtlasHeight, 1),
                };
                VulkanApi.CmdCopyImageToBuffer(cmd, _atlasImage, VkImageLayout.ImageLayoutTransferSrcOptimal, buffer, 1, &copy);
                Barrier(cmd, _atlasImage, VkImageLayout.ImageLayoutTransferSrcOptimal, VkImageLayout.ImageLayoutShaderReadOnlyOptimal,
                    VkAccessFlagBits.AccessTransferReadBit, VkAccessFlagBits.AccessShaderReadBit);
            });
            if (!coherent)
                Gpu.InvalidateMappedRange(memory);
            new ReadOnlySpan<byte>((void*)mapped, length).CopyTo(destination);
        }
        finally
        {
            VulkanApi.UnmapMemory(Gpu.Device, memory);
            VulkanApi.DestroyBuffer(Gpu.Device, buffer, null);
            VulkanApi.FreeMemory(Gpu.Device, memory, null);
        }
    }

    public string DeviceInfo => $"vk: {Gpu.DeviceName} ({FormatName(_targetFormat)})";

    public bool ContextLost => false;

    /** 图集在构造时已随核心建好; 该调用只做参数校验以对齐接口语义。 */
    public void Initialize(GlyphAtlas atlas) => ArgumentNullException.ThrowIfNull(atlas);

    public void EnsureCellCapacity(int cellCount) => EnsureCells(cellCount);

    /**
     * 脏行增量上传: 源取 packed[0..count], 写进 CPU 侧整屏影子(数组下标 firstCell 起)。
     * 因 cells 缓冲是每槽一套的环形缓冲, 不能只写脏行(别的槽没有旧内容); 逐帧在 RenderFrame
     * 把整屏影子一次性刷进当前槽 —— 即 §2.6 的"全量提交"。语义上仍与 GL 的 glBufferSubData 一致。
     */
    public void UploadCells(uint[] packed, int firstCell, int count)
    {
        if (count <= 0)
            return;
        EnsureShadow(firstCell + count);
        new ReadOnlySpan<uint>(packed, 0, count).CopyTo(new Span<uint>(_packed, firstCell, count));
        if (_frameUploads.Count >= 64)
            _frameUploads.Clear();                       // 段太多 → 退化为"整屏"(消费方按整帧处理)
        _frameUploads.Add((firstCell, count));
    }

    /** 本帧经 UploadCells 上报的脏段(cell 单位); 消费方读完须调 ClearFrameUploads。段过多时为整屏一段。 */
    internal IReadOnlyList<(int FirstCell, int Count)> FrameUploads => _frameUploads;

    internal void ClearFrameUploads() => _frameUploads.Clear();

    /** 本帧回读只需这些像素行(宿主按脏段换算); 空 = 整帧。 */
    public void SetCaptureRows(ReadOnlySpan<(int FirstRow, int RowCount)> pixelRows) => Target?.SetCaptureRows(pixelRows);

    public void RequestCapture() => Target?.RequestCapture();

    /**
     * GpuRenderer 驱动的一帧: 上传图集/字形映射/params, 再在交换链当前 image 上录两趟 draw。
     * 目标由 VkWindowRenderer.BeginFrame 取得(acquire + 开命令缓冲 + 图像布局转换)。
     */
    public void RenderFrame(GlyphAtlas atlas, int gridWidth, int gridHeight)
    {
        UploadAtlasIfDirty();
        UploadGlyphMapIfChanged();
        UploadParams(gridWidth, gridHeight);
        FlushCellsToSlot(gridWidth * gridHeight);
        var target = Target;
        if (target is null || !target.BeginFrame())
            return;
        RecordFrame(target.CommandBuffer, target.CurrentFramebuffer, target.CurrentExtent.Width, target.CurrentExtent.Height, gridWidth, gridHeight);
        target.MarkRendered();
    }

    public void Resize(int pixelWidth, int pixelHeight) => Target?.RequestResize();

    public bool TryReadPixels(Span<byte> destination, int width, int height)
        => Target?.TryReadPixels(destination, width, height) ?? false;

    /** 只读回指定像素行(其余行保持 destination 原值); rows 为空等价于整帧。 */
    public bool TryReadPixels(Span<byte> destination, int width, int height, ReadOnlySpan<(int FirstRow, int RowCount)> rows)
        => Target?.TryReadPixels(destination, width, height, rows) ?? false;

    private static string FormatName(VkFormat format) => format switch
    {
        VkFormat.FormatR8g8b8a8Unorm => "R8G8B8A8_UNORM",
        VkFormat.FormatB8g8r8a8Unorm => "B8G8R8A8_UNORM",
        VkFormat.FormatR8g8b8a8Srgb => "R8G8B8A8_SRGB",
        VkFormat.FormatB8g8r8a8Srgb => "B8G8R8A8_SRGB",
        _ => format.ToString(),
    };

    /** CPU 侧打包 + 图集/映射/params 上传 + cells 写入; 顺序要紧, 见下方注释。 */
    public void Prepare(CellGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        EnsureCells(grid.Width * grid.Height);
        // 顺序要紧: 先在 CPU 侧打包(会烘焙缺失字形并膨胀字形映射表), 再上传图集/映射, 否则首帧图集不含新字形。
        PackCells(grid);
        UploadAtlasIfDirty();
        UploadGlyphMapIfChanged();
        FlushCellsToSlot(grid.Width * grid.Height);
        UploadParams(grid.Width, grid.Height);
    }

    /** 为目标 image view 建 framebuffer(与核心 render pass 匹配)。 */
    public VkFramebuffer CreateFramebuffer(VkImageView view, uint width, uint height)
    {
        var attachments = stackalloc VkImageView[1];
        attachments[0] = view;
        var framebufferInfo = new VkFramebufferCreateInfo
        {
            sType = VkStructureType.StructureTypeFramebufferCreateInfo,
            renderPass = _renderPass,
            attachmentCount = 1,
            pAttachments = attachments,
            width = width,
            height = height,
            layers = 1,
        };
        VkFramebuffer framebufferOut;
        VkGpuDevice.Throw(VulkanApi.CreateFramebuffer(Gpu.Device, &framebufferInfo, null, &framebufferOut), "vkCreateFramebuffer");
        return framebufferOut;
    }

    /**
     * 录制一帧: 整帧 clear + 两趟 instanced draw(pass0 背景、pass1 字形)。
     * 结束后目标处于 ColorAttachmentOptimal, 由前端自行转换(读回/呈现)。调用前必须先 Prepare。
     */
    public void RecordFrame(VkCommandBuffer cmd, VkFramebuffer framebuffer, uint width, uint height, int columns, int rows)
    {
        // cells: staging → device-local(本帧全量), 就绪后转给顶点着色器读; 拷贝必须在 render pass 外录制。
        var cellsSlot = CurrentSlot;
        var copyRegion = new VkBufferCopy { srcOffset = 0, dstOffset = 0, size = (ulong)(columns * rows) * sizeof(uint) };
        VulkanApi.CmdCopyBuffer(cmd, _cellsStagingBuffers[cellsSlot], _cellsBuffers[cellsSlot], 1, &copyRegion);
        BufferBarrier(cmd, _cellsBuffers[cellsSlot], VkAccessFlagBits.AccessTransferWriteBit, VkAccessFlagBits.AccessShaderReadBit);

        var clear = new VkClearValue();
        clear.color.float32[0] = 0f;
        clear.color.float32[1] = 0f;
        clear.color.float32[2] = 0f;
        clear.color.float32[3] = 1f;
        var renderArea = new VkRect2D(new VkOffset2D(0, 0), new VkExtent2D(width, height));
        var begin = new VkRenderPassBeginInfo
        {
            sType = VkStructureType.StructureTypeRenderPassBeginInfo,
            renderPass = _renderPass,
            framebuffer = framebuffer,
            renderArea = renderArea,
            clearValueCount = 1,
            pClearValues = &clear,
        };
        VulkanApi.CmdBeginRenderPass(cmd, &begin, VkSubpassContents.SubpassContentsInline);

        var viewport = new VkViewport { x = 0, y = 0, width = width, height = height, minDepth = 0, maxDepth = 1 };
        var scissor = renderArea;
        VulkanApi.CmdSetViewport(cmd, 0, 1, &viewport);
        VulkanApi.CmdSetScissor(cmd, 0, 1, &scissor);
        VulkanApi.CmdBindPipeline(cmd, VkPipelineBindPoint.PipelineBindPointGraphics, _pipeline);
        var set = _descriptorSets[CurrentSlot];
        VulkanApi.CmdBindDescriptorSets(cmd, VkPipelineBindPoint.PipelineBindPointGraphics, _pipelineLayout, 0, 1, &set, 0, null);

        // 一趟 draw 画完两层: instance 0..N-1 = 背景层, N..2N-1 = 字形层(§2.6 "2 趟合 1")。
        var instanceCount = (uint)(columns * rows * 2);
        var instanceBase = 0;
        VulkanApi.CmdPushConstants(cmd, _pipelineLayout, VkShaderStageFlagBits.ShaderStageVertexBit, 0, sizeof(int), &instanceBase);
        VulkanApi.CmdDraw(cmd, VertexCount, instanceCount, 0, 0);

        VulkanApi.CmdEndRenderPass(cmd);
    }

    /** 图像布局转换 + 访问掩码屏障; 两个前端与其上传路径共用。 */
    internal static void Barrier(VkCommandBuffer cmd, VkImage image, VkImageLayout from, VkImageLayout to, VkAccessFlagBits sourceAccess, VkAccessFlagBits destinationAccess)
    {
        var barrier = new VkImageMemoryBarrier
        {
            sType = VkStructureType.StructureTypeImageMemoryBarrier,
            oldLayout = from,
            newLayout = to,
            srcQueueFamilyIndex = VulkanApi.QueueFamilyIgnored,
            dstQueueFamilyIndex = VulkanApi.QueueFamilyIgnored,
            image = image,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlagBits.ImageAspectColorBit, 0, 1, 0, 1),
            srcAccessMask = sourceAccess,
            dstAccessMask = destinationAccess,
        };
        var sourceStage = StageOf(sourceAccess, VkPipelineStageFlagBits.PipelineStageTransferBit, VkPipelineStageFlagBits.PipelineStageColorAttachmentOutputBit);
        var destinationStage = StageOf(destinationAccess, VkPipelineStageFlagBits.PipelineStageTransferBit, VkPipelineStageFlagBits.PipelineStageColorAttachmentOutputBit);
        if (destinationAccess == 0)
            destinationStage = VkPipelineStageFlagBits.PipelineStageBottomOfPipeBit;
        if (sourceAccess == 0)
            sourceStage = VkPipelineStageFlagBits.PipelineStageTopOfPipeBit;
        VulkanApi.CmdPipelineBarrier(cmd, sourceStage, destinationStage, 0, 0, null, 0, null, 1, &barrier);
    }

    private static VkPipelineStageFlagBits StageOf(VkAccessFlagBits access, VkPipelineStageFlagBits transfer, VkPipelineStageFlagBits color)
        => (access & (VkAccessFlagBits.AccessTransferReadBit | VkAccessFlagBits.AccessTransferWriteBit)) != 0 ? transfer : color;

    /** 缓冲区屏障: staging → cells 拷贝后, 让顶点着色器看到新内容。 */
    private static void BufferBarrier(VkCommandBuffer cmd, VkBuffer buffer, VkAccessFlagBits sourceAccess, VkAccessFlagBits destinationAccess)
    {
        var barrier = new VkBufferMemoryBarrier
        {
            sType = VkStructureType.StructureTypeBufferMemoryBarrier,
            srcAccessMask = sourceAccess,
            dstAccessMask = destinationAccess,
            srcQueueFamilyIndex = VulkanApi.QueueFamilyIgnored,
            dstQueueFamilyIndex = VulkanApi.QueueFamilyIgnored,
            buffer = buffer,
            offset = 0,
            size = VulkanApi.WholeSize,
        };
        VulkanApi.CmdPipelineBarrier(cmd, VkPipelineStageFlagBits.PipelineStageTransferBit,
            VkPipelineStageFlagBits.PipelineStageVertexShaderBit, 0, 0, null, 1, &barrier, 0, null);
    }

    private (VkImage Image, VkDeviceMemory Memory, VkImageView View) CreateImage(VkFormat format, uint width, uint height)
    {
        var info = new VkImageCreateInfo
        {
            sType = VkStructureType.StructureTypeImageCreateInfo,
            imageType = VkImageType.ImageType2D,
            format = format,
            extent = new VkExtent3D(width, height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlagBits.SampleCount1Bit,
            tiling = VkImageTiling.ImageTilingOptimal,
            usage = VkImageUsageFlagBits.ImageUsageSampledBit | VkImageUsageFlagBits.ImageUsageTransferDstBit,
            sharingMode = VkSharingMode.SharingModeExclusive,
            initialLayout = VkImageLayout.ImageLayoutUndefined,
        };
        var image = Gpu.CreateImage(info, out var memory);
        return (image, memory, Gpu.CreateImageView(image, format));
    }

    private void EnsureCells(int cellCount)
    {
        EnsureShadow(cellCount);
        if (cellCount <= _cellsCapacity && _cellsBuffers[0].Handle != 0)
            return;

        // 重建会销毁"其它在途槽"还在被 GPU 读的 cells 缓冲: 先等设备空闲(仅在网格增长时发生, 冷路径)。
        if (Gpu.Device.Handle != 0 && _cellsBuffers[0].Handle != 0)
            VulkanApi.DeviceWaitIdle(Gpu.Device);

        _cellsCapacity = Math.Max(cellCount, 80 * 25);
        for (var slot = 0; slot < FrameSlots; slot++)
        {
            if (_cellsBuffers[slot].Handle != 0)
            {
                VulkanApi.DestroyBufferView(Gpu.Device, _cellsViews[slot], null);
                VulkanApi.DestroyBuffer(Gpu.Device, _cellsBuffers[slot], null);
                VulkanApi.FreeMemory(Gpu.Device, _cellsMemories[slot], null);
                VulkanApi.UnmapMemory(Gpu.Device, _cellsStagingMemories[slot]);
                VulkanApi.DestroyBuffer(Gpu.Device, _cellsStagingBuffers[slot], null);
                VulkanApi.FreeMemory(Gpu.Device, _cellsStagingMemories[slot], null);
            }

            // cells 是顶点着色器读的 uniform texel buffer: 放 device-local, 每帧由 staging → CmdCopyBuffer 送入(§2.6 全量提交)。
            var cellsBytes = (ulong)_cellsCapacity * sizeof(uint);
            _cellsBuffers[slot] = Gpu.CreateBuffer(cellsBytes,
                VkBufferUsageFlagBits.BufferUsageUniformTexelBufferBit | VkBufferUsageFlagBits.BufferUsageTransferDstBit,
                VkMemoryPropertyFlagBits.MemoryPropertyDeviceLocalBit, out var memory);
            _cellsMemories[slot] = memory;
            _cellsStagingBuffers[slot] = Gpu.CreateMappedBuffer(cellsBytes, VkBufferUsageFlagBits.BufferUsageTransferSrcBit, out var stagingMemory, out var stagingMapped);
            _cellsStagingMemories[slot] = stagingMemory;
            _cellsStagingMapped[slot] = stagingMapped;

            // cells 是 uniform texel buffer(R32_UINT), 用 BufferView 绑定到描述符 1。
            var viewInfo = new VkBufferViewCreateInfo
            {
                sType = VkStructureType.StructureTypeBufferViewCreateInfo,
                buffer = _cellsBuffers[slot],
                format = VkFormat.FormatR32Uint,
                offset = 0,
                range = VulkanApi.WholeSize,
            };
            VkBufferView cellsView;
            VkGpuDevice.Throw(VulkanApi.CreateBufferView(Gpu.Device, &viewInfo, null, &cellsView), "vkCreateBufferView");
            _cellsViews[slot] = cellsView;
        }

        for (var slot = 0; slot < FrameSlots; slot++)
            WriteDescriptors(slot);
    }

    /** 把某槽的 4 个绑定(atlas/cells/glyph/params)写进该槽的描述符集。 */
    private void WriteDescriptors(int slot)
    {
        var atlasInfo = new VkDescriptorImageInfo { sampler = _sampler, imageView = _atlasView, imageLayout = VkImageLayout.ImageLayoutShaderReadOnlyOptimal };
        var glyphInfo = new VkDescriptorImageInfo { sampler = _sampler, imageView = _glyphView, imageLayout = VkImageLayout.ImageLayoutShaderReadOnlyOptimal };
        var paramsInfo = new VkDescriptorBufferInfo { buffer = _paramsBuffers[slot], offset = 0, range = ParamsSize };
        var cellsView = _cellsViews[slot];
        var writes = new VkWriteDescriptorSet[]
        {
            new() { sType = VkStructureType.StructureTypeWriteDescriptorSet, dstSet = _descriptorSets[slot], dstBinding = 0, descriptorCount = 1, descriptorType = VkDescriptorType.DescriptorTypeCombinedImageSampler, pImageInfo = &atlasInfo },
            new() { sType = VkStructureType.StructureTypeWriteDescriptorSet, dstSet = _descriptorSets[slot], dstBinding = 1, descriptorCount = 1, descriptorType = VkDescriptorType.DescriptorTypeUniformTexelBuffer, pTexelBufferView = &cellsView },
            new() { sType = VkStructureType.StructureTypeWriteDescriptorSet, dstSet = _descriptorSets[slot], dstBinding = 2, descriptorCount = 1, descriptorType = VkDescriptorType.DescriptorTypeCombinedImageSampler, pImageInfo = &glyphInfo },
            new() { sType = VkStructureType.StructureTypeWriteDescriptorSet, dstSet = _descriptorSets[slot], dstBinding = 3, descriptorCount = 1, descriptorType = VkDescriptorType.DescriptorTypeUniformBuffer, pBufferInfo = &paramsInfo },
        };
        fixed (VkWriteDescriptorSet* p = writes)
            VulkanApi.UpdateDescriptorSets(Gpu.Device, (uint)writes.Length, p, 0, null);
    }

    private void UploadGlyphMapIfChanged()
    {
        if (_atlas.MapVersion == _seenMapVersion)
            return;

        var slot = CurrentSlot;
        var map = _atlas.MapUpload;
        map.CopyTo(new Span<int>((void*)_stagingMapped[slot], GlyphMapSide * GlyphMapSide));
        var first = _seenMapVersion < 0;
        Gpu.OneShot(cmd =>
        {
            Barrier(cmd, _glyphImage, first ? VkImageLayout.ImageLayoutUndefined : VkImageLayout.ImageLayoutShaderReadOnlyOptimal, VkImageLayout.ImageLayoutTransferDstOptimal,
                first ? 0 : VkAccessFlagBits.AccessShaderReadBit, VkAccessFlagBits.AccessTransferWriteBit);
            var copy = new VkBufferImageCopy
            {
                bufferOffset = 0,
                imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlagBits.ImageAspectColorBit, 0, 0, 1),
                imageOffset = new VkOffset3D(0, 0, 0),
                imageExtent = new VkExtent3D(GlyphMapSide, GlyphMapSide, 1),
            };
            VulkanApi.CmdCopyBufferToImage(cmd, _stagingBuffers[slot], _glyphImage, VkImageLayout.ImageLayoutTransferDstOptimal, 1, &copy);
            Barrier(cmd, _glyphImage, VkImageLayout.ImageLayoutTransferDstOptimal, VkImageLayout.ImageLayoutShaderReadOnlyOptimal, VkAccessFlagBits.AccessTransferWriteBit, VkAccessFlagBits.AccessShaderReadBit);
        });
        _seenMapVersion = _atlas.MapVersion;
    }

    private void UploadAtlasIfDirty()
    {
        if (!_atlasCreated)
        {
            var slot = CurrentSlot;
            var data = _atlas.CreateTextureData();
            data.CopyTo(new Span<byte>((void*)_stagingMapped[slot], data.Length));
            Gpu.OneShot(cmd =>
            {
                Barrier(cmd, _atlasImage, VkImageLayout.ImageLayoutUndefined, VkImageLayout.ImageLayoutTransferDstOptimal, 0, VkAccessFlagBits.AccessTransferWriteBit);
                var copy = new VkBufferImageCopy
                {
                    bufferOffset = 0,
                    imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlagBits.ImageAspectColorBit, 0, 0, 1),
                    imageOffset = new VkOffset3D(0, 0, 0),
                    imageExtent = new VkExtent3D((uint)_atlas.AtlasWidth, (uint)_atlas.AtlasHeight, 1),
                };
                VulkanApi.CmdCopyBufferToImage(cmd, _stagingBuffers[slot], _atlasImage, VkImageLayout.ImageLayoutTransferDstOptimal, 1, &copy);
                Barrier(cmd, _atlasImage, VkImageLayout.ImageLayoutTransferDstOptimal, VkImageLayout.ImageLayoutShaderReadOnlyOptimal, VkAccessFlagBits.AccessTransferWriteBit, VkAccessFlagBits.AccessShaderReadBit);
            });
            _atlasCreated = true;
            return;
        }

        if (_atlas.DirtyCount == 0)
            return;

        var stagingSlot = CurrentSlot;
        // 脏槽: 逐槽按行拷进 staging, 再按槽区域 CmdCopyBufferToImage(GPU 侧只更新变化的槽)。
        var slots = new int[MaxFlushSlots];
        var texture = _atlas.TextureData;
        var glyphWidth = _atlas.GlyphWidth;
        var glyphHeight = _atlas.GlyphHeight;
        var atlasWidth = _atlas.AtlasWidth;
        var dirty = new List<int>();
        var flushed = _atlas.FlushDirtyRegions(slots);
        while (flushed > 0)
        {
            for (var i = 0; i < flushed; i++)
            {
                var slot = slots[i];
                var slotX = (slot % GlyphAtlas.Columns) * glyphWidth;
                var slotY = (slot / GlyphAtlas.Columns) * glyphHeight;
                var baseOffset = (long)slot * (glyphWidth * glyphHeight);
                for (var y = 0; y < glyphHeight; y++)
                {
                    var source = ((slotY + y) * atlasWidth) + slotX;
                    new ReadOnlySpan<byte>(texture, source, glyphWidth)
                        .CopyTo(new Span<byte>((byte*)_stagingMapped[stagingSlot] + baseOffset + (y * glyphWidth), glyphWidth));
                }

                dirty.Add(slot);
            }

            flushed = _atlas.FlushDirtyRegions(slots);
        }

        if (dirty.Count == 0)
            return;

        Gpu.OneShot(cmd =>
        {
            Barrier(cmd, _atlasImage, VkImageLayout.ImageLayoutShaderReadOnlyOptimal, VkImageLayout.ImageLayoutTransferDstOptimal, VkAccessFlagBits.AccessShaderReadBit, VkAccessFlagBits.AccessTransferWriteBit);
            foreach (var slot in dirty)
            {
                var slotX = (slot % GlyphAtlas.Columns) * glyphWidth;
                var slotY = (slot / GlyphAtlas.Columns) * glyphHeight;
                var copy = new VkBufferImageCopy
                {
                    bufferOffset = (ulong)((long)slot * (glyphWidth * glyphHeight)),
                    bufferRowLength = (uint)glyphWidth,
                    bufferImageHeight = (uint)glyphHeight,
                    imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlagBits.ImageAspectColorBit, 0, 0, 1),
                    imageOffset = new VkOffset3D(slotX, slotY, 0),
                    imageExtent = new VkExtent3D((uint)glyphWidth, (uint)glyphHeight, 1),
                };
                VulkanApi.CmdCopyBufferToImage(cmd, _stagingBuffers[stagingSlot], _atlasImage, VkImageLayout.ImageLayoutTransferDstOptimal, 1, &copy);
            }

            Barrier(cmd, _atlasImage, VkImageLayout.ImageLayoutTransferDstOptimal, VkImageLayout.ImageLayoutShaderReadOnlyOptimal, VkAccessFlagBits.AccessTransferWriteBit, VkAccessFlagBits.AccessShaderReadBit);
        });
    }

    /** CPU 侧打包(并烘焙缺失字形); 结果留在 _packed, 待 WriteCells 拷进 GPU 缓冲。 */
    private void PackCells(CellGrid grid)
    {
        var length = grid.Width * grid.Height;
        EnsureShadow(length);
        CellPacker.PackRows(grid, 0, grid.Height, _packed, _atlas);
    }

    /** 影子数组至少容纳 count 个格(增长时旧内容作废, 由随后的整屏重包/全量上传重建)。 */
    private void EnsureShadow(int count)
    {
        if (_packed.Length >= count)
            return;
        _packed = new uint[Math.Max(count, 80 * 25)];
    }

    /** 把整屏影子写进当前槽的 staging(§2.6 全量提交); staging→cells 的拷贝由 RecordFrame 录进命令缓冲。 */
    private void FlushCellsToSlot(int length)
    {
        if (length <= 0 || _cellsBuffers[0].Handle == 0)
            return;
        var slot = CurrentSlot;
        new ReadOnlySpan<uint>(_packed, 0, length).CopyTo(new Span<uint>((void*)_cellsStagingMapped[slot], _cellsCapacity));
    }

    private void UploadParams(int columns, int rows)
    {
        var slot = CurrentSlot;
        var ints = new Span<int>((void*)_paramsMapped[slot], ParamsSize / 4);
        ints[0] = columns;
        ints[1] = rows;
        ints[4] = GlyphAtlas.Columns;
        ints[5] = GlyphAtlas.Rows;
        ints[8] = _atlas.AtlasWidth;
        ints[9] = _atlas.AtlasHeight;
        ints[12] = _atlas.GlyphWidth;
        ints[13] = _atlas.GlyphHeight;

        // palette / 默认前背景逐帧写当前槽: 每槽各有自己的 params 缓冲, 不能只写一次(其它槽会是零)。
        var table = CellColorTable.Shared;
        var palette = new Span<float>((void*)_paramsMapped[slot], ParamsSize / 4);
        var count = table.Count;
        for (var index = 0; index < count; index++)
        {
            var rgba = TerminalColorPalette.ToRgba(table[index]);
            var basis = HeaderFloats + (index * 4);
            palette[basis + 0] = rgba.R;
            palette[basis + 1] = rgba.G;
            palette[basis + 2] = rgba.B;
            palette[basis + 3] = rgba.A;
        }

        var defaults = DefaultsFloats;
        var background = TerminalColorPalette.DefaultBackground;
        var foreground = TerminalColorPalette.DefaultForeground;
        palette[defaults + 0] = background.R;
        palette[defaults + 1] = background.G;
        palette[defaults + 2] = background.B;
        palette[defaults + 3] = background.A;
        palette[defaults + 4] = foreground.R;
        palette[defaults + 5] = foreground.G;
        palette[defaults + 6] = foreground.B;
        palette[defaults + 7] = foreground.A;
    }

    private VkRenderPass CreateRenderPass(VkFormat format)
    {
        var attachment = new VkAttachmentDescription
        {
            format = format,
            samples = VkSampleCountFlagBits.SampleCount1Bit,
            loadOp = VkAttachmentLoadOp.AttachmentLoadOpClear,
            storeOp = VkAttachmentStoreOp.AttachmentStoreOpStore,
            stencilLoadOp = VkAttachmentLoadOp.AttachmentLoadOpDontCare,
            stencilStoreOp = VkAttachmentStoreOp.AttachmentStoreOpDontCare,
            initialLayout = VkImageLayout.ImageLayoutUndefined,
            finalLayout = VkImageLayout.ImageLayoutColorAttachmentOptimal,
        };
        var reference = new VkAttachmentReference { attachment = 0, layout = VkImageLayout.ImageLayoutColorAttachmentOptimal };
        var subpass = new VkSubpassDescription
        {
            pipelineBindPoint = VkPipelineBindPoint.PipelineBindPointGraphics,
            colorAttachmentCount = 1,
            pColorAttachments = &reference,
        };
        var dependency = new VkSubpassDependency
        {
            srcSubpass = VulkanApi.SubpassExternal,
            dstSubpass = 0,
            srcStageMask = VkPipelineStageFlagBits.PipelineStageColorAttachmentOutputBit | VkPipelineStageFlagBits.PipelineStageTransferBit,
            dstStageMask = VkPipelineStageFlagBits.PipelineStageColorAttachmentOutputBit,
            srcAccessMask = VkAccessFlagBits.AccessColorAttachmentWriteBit | VkAccessFlagBits.AccessTransferReadBit,
            dstAccessMask = VkAccessFlagBits.AccessColorAttachmentWriteBit,
        };
        var info = new VkRenderPassCreateInfo
        {
            sType = VkStructureType.StructureTypeRenderPassCreateInfo,
            attachmentCount = 1,
            pAttachments = &attachment,
            subpassCount = 1,
            pSubpasses = &subpass,
            dependencyCount = 1,
            pDependencies = &dependency,
        };
        VkRenderPass renderPass;
        VkGpuDevice.Throw(VulkanApi.CreateRenderPass(Gpu.Device, &info, null, &renderPass), "vkCreateRenderPass");
        return renderPass;
    }

    private VkPipeline CreatePipeline()
    {
        var vertex = CreateModule(VkShaders.TextVertex);
        var fragment = CreateModule(VkShaders.TextFragment);
        var entry = (byte*)Marshal.StringToCoTaskMemUTF8("main");
        try
        {
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo { sType = VkStructureType.StructureTypePipelineShaderStageCreateInfo, stage = VkShaderStageFlagBits.ShaderStageVertexBit, module = vertex, pName = entry };
            stages[1] = new VkPipelineShaderStageCreateInfo { sType = VkStructureType.StructureTypePipelineShaderStageCreateInfo, stage = VkShaderStageFlagBits.ShaderStageFragmentBit, module = fragment, pName = entry };

            var vertexInput = new VkPipelineVertexInputStateCreateInfo { sType = VkStructureType.StructureTypePipelineVertexInputStateCreateInfo };
            var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo
            {
                sType = VkStructureType.StructureTypePipelineInputAssemblyStateCreateInfo,
                topology = VkPrimitiveTopology.PrimitiveTopologyTriangleList,
            };
            var viewportState = new VkPipelineViewportStateCreateInfo
            {
                sType = VkStructureType.StructureTypePipelineViewportStateCreateInfo,
                viewportCount = 1,
                scissorCount = 1,
            };
            var rasterization = new VkPipelineRasterizationStateCreateInfo
            {
                sType = VkStructureType.StructureTypePipelineRasterizationStateCreateInfo,
                polygonMode = VkPolygonMode.PolygonModeFill,
                cullMode = VkCullModeFlagBits.CullModeNone,
                frontFace = VkFrontFace.FrontFaceCounterClockwise,
                lineWidth = 1f,
            };
            var multisample = new VkPipelineMultisampleStateCreateInfo
            {
                sType = VkStructureType.StructureTypePipelineMultisampleStateCreateInfo,
                rasterizationSamples = VkSampleCountFlagBits.SampleCount1Bit,
            };
            var blendAttachment = new VkPipelineColorBlendAttachmentState
            {
                // 字形层按图集 alpha 混到已画好的背景层上(层序在顶点着色器里由实例下标保证)。
                blendEnable = 1,
                srcColorBlendFactor = VkBlendFactor.BlendFactorSrcAlpha,
                dstColorBlendFactor = VkBlendFactor.BlendFactorOneMinusSrcAlpha,
                colorBlendOp = VkBlendOp.BlendOpAdd,
                srcAlphaBlendFactor = VkBlendFactor.BlendFactorOne,
                dstAlphaBlendFactor = VkBlendFactor.BlendFactorOneMinusSrcAlpha,
                alphaBlendOp = VkBlendOp.BlendOpAdd,
                colorWriteMask = VkColorComponentFlagBits.ColorComponentRBit | VkColorComponentFlagBits.ColorComponentGBit | VkColorComponentFlagBits.ColorComponentBBit | VkColorComponentFlagBits.ColorComponentABit,
            };
            var colorBlend = new VkPipelineColorBlendStateCreateInfo
            {
                sType = VkStructureType.StructureTypePipelineColorBlendStateCreateInfo,
                attachmentCount = 1,
                pAttachments = &blendAttachment,
            };
            var dynamicStates = stackalloc VkDynamicState[2];
            dynamicStates[0] = VkDynamicState.DynamicStateViewport;
            dynamicStates[1] = VkDynamicState.DynamicStateScissor;
            var dynamicState = new VkPipelineDynamicStateCreateInfo
            {
                sType = VkStructureType.StructureTypePipelineDynamicStateCreateInfo,
                dynamicStateCount = 2,
                pDynamicStates = dynamicStates,
            };

            var info = new VkGraphicsPipelineCreateInfo
            {
                sType = VkStructureType.StructureTypeGraphicsPipelineCreateInfo,
                stageCount = 2,
                pStages = stages,
                pVertexInputState = &vertexInput,
                pInputAssemblyState = &inputAssembly,
                pViewportState = &viewportState,
                pRasterizationState = &rasterization,
                pMultisampleState = &multisample,
                pColorBlendState = &colorBlend,
                pDynamicState = &dynamicState,
                layout = _pipelineLayout,
                renderPass = _renderPass,
                subpass = 0,
            };
            VkPipeline pipeline;
            VkGpuDevice.Throw(VulkanApi.CreateGraphicsPipelines(Gpu.Device, default, 1, &info, null, &pipeline), "vkCreateGraphicsPipelines");
            return pipeline;
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)entry);
            VulkanApi.DestroyShaderModule(Gpu.Device, vertex, null);
            VulkanApi.DestroyShaderModule(Gpu.Device, fragment, null);
        }
    }

    private VkShaderModule CreateModule(byte[] spv)
    {
        fixed (byte* code = spv)
        {
            var info = new VkShaderModuleCreateInfo
            {
                sType = VkStructureType.StructureTypeShaderModuleCreateInfo,
                codeSize = (nuint)spv.Length,
                pCode = (uint*)code,
            };
            VkShaderModule module;
            VkGpuDevice.Throw(VulkanApi.CreateShaderModule(Gpu.Device, &info, null, &module), "vkCreateShaderModule");
            return module;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (Gpu.Device.Handle != 0)
            VulkanApi.DeviceWaitIdle(Gpu.Device);

        for (var slot = 0; slot < FrameSlots; slot++)
        {
            if (_cellsBuffers[slot].Handle != 0)
            {
                VulkanApi.DestroyBufferView(Gpu.Device, _cellsViews[slot], null);
                VulkanApi.DestroyBuffer(Gpu.Device, _cellsBuffers[slot], null);
                VulkanApi.FreeMemory(Gpu.Device, _cellsMemories[slot], null);
                VulkanApi.UnmapMemory(Gpu.Device, _cellsStagingMemories[slot]);
                VulkanApi.DestroyBuffer(Gpu.Device, _cellsStagingBuffers[slot], null);
                VulkanApi.FreeMemory(Gpu.Device, _cellsStagingMemories[slot], null);
            }

            VulkanApi.UnmapMemory(Gpu.Device, _paramsMemories[slot]);
            VulkanApi.DestroyBuffer(Gpu.Device, _paramsBuffers[slot], null);
            VulkanApi.FreeMemory(Gpu.Device, _paramsMemories[slot], null);

            VulkanApi.UnmapMemory(Gpu.Device, _stagingMemories[slot]);
            VulkanApi.DestroyBuffer(Gpu.Device, _stagingBuffers[slot], null);
            VulkanApi.FreeMemory(Gpu.Device, _stagingMemories[slot], null);
        }

        VulkanApi.DestroyImageView(Gpu.Device, _atlasView, null);
        VulkanApi.DestroyImage(Gpu.Device, _atlasImage, null);
        VulkanApi.FreeMemory(Gpu.Device, _atlasMemory, null);
        VulkanApi.DestroyImageView(Gpu.Device, _glyphView, null);
        VulkanApi.DestroyImage(Gpu.Device, _glyphImage, null);
        VulkanApi.FreeMemory(Gpu.Device, _glyphMemory, null);

        VulkanApi.DestroySampler(Gpu.Device, _sampler, null);
        VulkanApi.DestroyPipeline(Gpu.Device, _pipeline, null);
        VulkanApi.DestroyRenderPass(Gpu.Device, _renderPass, null);
        VulkanApi.DestroyPipelineLayout(Gpu.Device, _pipelineLayout, null);
        VulkanApi.DestroyDescriptorPool(Gpu.Device, _descriptorPool, null);
        VulkanApi.DestroyDescriptorSetLayout(Gpu.Device, _setLayout, null);
    }
}
