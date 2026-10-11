using System.Runtime.InteropServices;
using OpenTK.Graphics;
using OpenTK.Graphics.Vulkan;
// 裸名 `Vk` 会被外层命名空间 Dsh.Tui.Vk 遮蔽(编译期优先解析为命名空间), 因此用编译单元别名指向静态绑定类。
using VulkanApi = OpenTK.Graphics.Vulkan.Vk;

namespace Dsh.Tui.Vk;

/**
 * Vulkan 设备上下文: 实例 / 物理设备选择 / 逻辑设备与图形队列 / 命令池, 以及缓冲与图像的内存分配助手。
 * 只做与"表面"无关的公共设施; 窗口(交换链)与离屏目标各自在其上构建(见 VkOffscreenRenderer 与后续 VkSurfaceHost)。
 */
internal sealed unsafe class VkGpuDevice : IDisposable
{
    // OpenTK.Graphics.Vulkan.Vk 的函数是惰性解析的: 不先初始化加载器时, 第一次 VulkanApi.* 调用会在解析函数指针时递归自身并导致 StackOverflow(上游缺陷)。
    // 因此用一次性守卫保证 VKLoader.Init() 只调用一次。
    private static readonly Lazy<bool> Initialized = new(() =>
    {
        VKLoader.Init();
        return true;
    });

    private readonly VkInstance _instance;
    private readonly VkDevice _device;
    private bool _disposed;

    /** 建实例后暴露给窗口层, 供 SDL.VulkanCreateSurface 等需要裸 VkInstance 的调用。 */
    public VkInstance Instance => _instance;

    public VkPhysicalDevice PhysicalDevice { get; }

    public VkQueue GraphicsQueue { get; }

    public uint GraphicsFamily { get; }

    public VkCommandPool CommandPool { get; }

    public string DeviceName { get; }

    private VkGpuDevice(VkInstance instance, VkPhysicalDevice physicalDevice, VkDevice device, VkQueue queue, uint family, VkCommandPool pool, string name)
    {
        _instance = instance;
        PhysicalDevice = physicalDevice;
        _device = device;
        GraphicsQueue = queue;
        GraphicsFamily = family;
        CommandPool = pool;
        DeviceName = name;
    }

    public VkDevice Device => _device;

    internal static void EnsureInitialized() => _ = Initialized.Value;

    /**
     * 创建实例 + 逻辑设备。presentPredicate 供窗口层挑"图形+呈现"队列族(如经 SDL.VulkanGetPresentationSupport);
     * deviceExtensions 供窗口层启用 VK_KHR_swapchain。二者缺省即离屏路径的旧行为。
     */
    public static VkGpuDevice Create(
        IReadOnlyList<string>? instanceExtensions = null,
        string? deviceNameFilter = null,
        IReadOnlyList<string>? deviceExtensions = null,
        Func<VkInstance, VkPhysicalDevice, uint, bool>? presentPredicate = null)
    {
        EnsureInitialized();
        // Linux best-effort 环境自愈: 首次创建实例之前跑一次(非 Linux / 已可用时零成本 no-op)。
        // 见 VkLinuxEnvironmentRepair: 失败只回退, 绝不影响既有"无 Vulkan → 回退"路径。
        VkLinuxEnvironmentBootstrap.EnsureReady();
        var extensions = instanceExtensions is { Count: > 0 } ? instanceExtensions.ToArray() : [];
        // macOS(MoltenVK)按需启用 portability 枚举: 不启用 VK_KHR_portability_enumeration 就枚举不到 MoltenVK 设备。
        // 扩展不可用(旧 loader)时不启用, 保持 Vulkan 1.1 的默认行为。
        var portability = OperatingSystem.IsMacOS() && InstanceExtensionSupported("VK_KHR_portability_enumeration");
        if (portability)
            extensions = [.. extensions, "VK_KHR_portability_enumeration"];

        var namePtrs = new byte*[extensions.Length];
        for (var i = 0; i < extensions.Length; i++)
            namePtrs[i] = (byte*)Marshal.StringToCoTaskMemUTF8(extensions[i]);

        VkInstance instance;
        try
        {
            var appInfo = new VkApplicationInfo { sType = VkStructureType.StructureTypeApplicationInfo, apiVersion = VulkanApi.MAKE_VERSION(1, 1, 0) };
            fixed (byte** extensionNames = namePtrs)
            {
                var createInfo = new VkInstanceCreateInfo
                {
                    sType = VkStructureType.StructureTypeInstanceCreateInfo,
                    pApplicationInfo = &appInfo,
                    enabledExtensionCount = (uint)extensions.Length,
                    ppEnabledExtensionNames = extensionNames,
                    flags = portability ? VkInstanceCreateFlagBits.InstanceCreateEnumeratePortabilityBitKhr : 0,
                };
                Throw(VulkanApi.CreateInstance(&createInfo, null, &instance), "vkCreateInstance");
            }
        }
        finally
        {
            foreach (var p in namePtrs)
                Marshal.FreeCoTaskMem((nint)p);
        }

        VKLoader.SetInstance(instance);

        try
        {
            var (physicalDevice, family, name) = PickDevice(instance, deviceNameFilter, presentPredicate);
            float priority = 1f;
            var queueInfo = new VkDeviceQueueCreateInfo
            {
                sType = VkStructureType.StructureTypeDeviceQueueCreateInfo,
                queueFamilyIndex = family,
                queueCount = 1,
                pQueuePriorities = &priority,
            };

            var deviceExtensionNames = deviceExtensions is { Count: > 0 } ? deviceExtensions.ToArray() : [];
            foreach (var extension in deviceExtensionNames)
            {
                if (!SupportsDeviceExtension(physicalDevice, extension))
                    throw new InvalidOperationException($"物理设备不支持设备扩展 {extension}");
            }

            // MoltenVK(portability)设备暴露 VK_KHR_portability_subset 时必须显式启用(spec 要求)。
            const string portabilitySubset = "VK_KHR_portability_subset";
            if (!deviceExtensionNames.Contains(portabilitySubset) && SupportsDeviceExtension(physicalDevice, portabilitySubset))
                deviceExtensionNames = [.. deviceExtensionNames, portabilitySubset];

            var deviceNamePtrs = new byte*[deviceExtensionNames.Length];
            for (var i = 0; i < deviceExtensionNames.Length; i++)
                deviceNamePtrs[i] = (byte*)Marshal.StringToCoTaskMemUTF8(deviceExtensionNames[i]);

            VkDevice device;
            try
            {
                fixed (byte** enabledNames = deviceNamePtrs)
                {
                    var deviceInfo = new VkDeviceCreateInfo
                    {
                        sType = VkStructureType.StructureTypeDeviceCreateInfo,
                        queueCreateInfoCount = 1,
                        pQueueCreateInfos = &queueInfo,
                        enabledExtensionCount = (uint)deviceExtensionNames.Length,
                        ppEnabledExtensionNames = enabledNames,
                    };
                    Throw(VulkanApi.CreateDevice(physicalDevice, &deviceInfo, null, &device), "vkCreateDevice");
                }
            }
            finally
            {
                foreach (var p in deviceNamePtrs)
                    Marshal.FreeCoTaskMem((nint)p);
            }

            VkQueue queue;
            VulkanApi.GetDeviceQueue(device, family, 0, &queue);

            var poolInfo = new VkCommandPoolCreateInfo
            {
                sType = VkStructureType.StructureTypeCommandPoolCreateInfo,
                queueFamilyIndex = family,
                flags = VkCommandPoolCreateFlagBits.CommandPoolCreateResetCommandBufferBit,
            };
            VkCommandPool pool;
            Throw(VulkanApi.CreateCommandPool(device, &poolInfo, null, &pool), "vkCreateCommandPool");

            return new VkGpuDevice(instance, physicalDevice, device, queue, family, pool, name);
        }
        catch
        {
            VulkanApi.DestroyInstance(instance, null);
            throw;
        }
    }

    private static (VkPhysicalDevice Device, uint Family, string Name) PickDevice(
        VkInstance instance,
        string? nameFilter,
        Func<VkInstance, VkPhysicalDevice, uint, bool>? presentPredicate)
    {
        uint count = 0;
        Throw(VulkanApi.EnumeratePhysicalDevices(instance, &count, null), "vkEnumeratePhysicalDevices");
        if (count == 0)
            throw new InvalidOperationException("没有可用的 Vulkan 物理设备");
        var devices = new VkPhysicalDevice[count];
        fixed (VkPhysicalDevice* p = devices)
            Throw(VulkanApi.EnumeratePhysicalDevices(instance, &count, p), "vkEnumeratePhysicalDevices");

        (VkPhysicalDevice Device, uint Family, string Name)? fallback = null;
        foreach (var device in devices)
        {
            var name = NameOf(device);
            var family = FindGraphicsFamily(instance, device, presentPredicate);
            if (family is null)
                continue;
            fallback ??= (device, family.Value, name);
            if (nameFilter is null || name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                return (device, family.Value, name);
        }

        return fallback ?? throw new InvalidOperationException("没有支持图形队列的 Vulkan 物理设备");
    }

    private static uint? FindGraphicsFamily(
        VkInstance instance,
        VkPhysicalDevice device,
        Func<VkInstance, VkPhysicalDevice, uint, bool>? presentPredicate)
    {
        uint count = 0;
        VulkanApi.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new VkQueueFamilyProperties[count];
        fixed (VkQueueFamilyProperties* p = families)
            VulkanApi.GetPhysicalDeviceQueueFamilyProperties(device, &count, p);
        for (uint i = 0; i < count; i++)
        {
            if ((families[i].queueFlags & VkQueueFlagBits.QueueGraphicsBit) == 0)
                continue;
            if (presentPredicate is not null && !presentPredicate(instance, device, i))
                continue;
            return i;
        }

        return null;
    }

    private static bool InstanceExtensionSupported(string extension)
    {
        uint count = 0;
        if (VulkanApi.EnumerateInstanceExtensionProperties(null, &count, null) != VkResult.Success || count == 0)
            return false;
        var properties = new VkExtensionProperties[count];
        fixed (VkExtensionProperties* p = properties)
        {
            if (VulkanApi.EnumerateInstanceExtensionProperties(null, &count, p) != VkResult.Success)
                return false;
        }

        foreach (var property in properties)
        {
            var item = property;
            var span = MemoryMarshal.CreateReadOnlySpan(ref item.extensionName[0], 256);
            var end = span.IndexOf((byte)0);
            if (System.Text.Encoding.UTF8.GetString(end < 0 ? span : span[..end]) == extension)
                return true;
        }

        return false;
    }

    private static bool SupportsDeviceExtension(VkPhysicalDevice device, string extension)
    {
        uint count = 0;
        VulkanApi.EnumerateDeviceExtensionProperties(device, null, &count, null);
        var properties = new VkExtensionProperties[count];
        fixed (VkExtensionProperties* p = properties)
            VulkanApi.EnumerateDeviceExtensionProperties(device, null, &count, p);
        foreach (var property in properties)
        {
            var item = property;
            var span = MemoryMarshal.CreateReadOnlySpan(ref item.extensionName[0], 256);
            var end = span.IndexOf((byte)0);
            if (System.Text.Encoding.UTF8.GetString(end < 0 ? span : span[..end]) == extension)
                return true;
        }

        return false;
    }

    private static string NameOf(VkPhysicalDevice device)
    {
        VkPhysicalDeviceProperties props;
        VulkanApi.GetPhysicalDeviceProperties(device, &props);
        var span = MemoryMarshal.CreateReadOnlySpan(ref props.deviceName[0], 256);
        var end = span.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }

    public uint FindMemoryType(uint typeBits, VkMemoryPropertyFlagBits properties)
    {
        VkPhysicalDeviceMemoryProperties memory;
        VulkanApi.GetPhysicalDeviceMemoryProperties(PhysicalDevice, &memory);
        for (var i = 0u; i < memory.memoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) != 0 && (memory.memoryTypes[(int)i].propertyFlags & properties) == properties)
                return i;
        }

        throw new InvalidOperationException($"没有满足 {properties} 的显存类型");
    }

    public VkDeviceMemory AllocateMemory(ulong size, uint typeBits, VkMemoryPropertyFlagBits properties)
    {
        var info = new VkMemoryAllocateInfo
        {
            sType = VkStructureType.StructureTypeMemoryAllocateInfo,
            allocationSize = size,
            memoryTypeIndex = FindMemoryType(typeBits, properties),
        };
        VkDeviceMemory memory;
        Throw(VulkanApi.AllocateMemory(_device, &info, null, &memory), "vkAllocateMemory");
        return memory;
    }

    /** 创建缓冲并常驻映射(host-visible + host-coherent), 返回缓冲与映射指针(通过 out)。 */
    public VkBuffer CreateMappedBuffer(ulong size, VkBufferUsageFlagBits usage, out VkDeviceMemory memory, out nint mapped)
    {
        var buffer = CreateBuffer(size, usage, VkMemoryPropertyFlagBits.MemoryPropertyHostVisibleBit | VkMemoryPropertyFlagBits.MemoryPropertyHostCoherentBit, out memory);
        nint pointer = 0;
        Throw(VulkanApi.MapMemory(_device, memory, 0, size, 0, (void**)&pointer), "vkMapMemory");
        mapped = pointer;
        return buffer;
    }

    /** 回读用映射缓冲: 优先 HOST_CACHED(CPU 读快), 无则退回 HOST_COHERENT; coherent=false 时读前须 InvalidateMappedRange。 */
    public VkBuffer CreateMappedReadbackBuffer(ulong size, out VkDeviceMemory memory, out nint mapped, out bool coherent)
    {
        var info = new VkBufferCreateInfo
        {
            sType = VkStructureType.StructureTypeBufferCreateInfo,
            size = size,
            usage = VkBufferUsageFlagBits.BufferUsageTransferDstBit,
            sharingMode = VkSharingMode.SharingModeExclusive,
        };
        VkBuffer buffer;
        Throw(VulkanApi.CreateBuffer(_device, &info, null, &buffer), "vkCreateBuffer");
        VkMemoryRequirements requirements;
        VulkanApi.GetBufferMemoryRequirements(_device, buffer, &requirements);
        memory = AllocateReadbackMemory(requirements.size, requirements.memoryTypeBits, out coherent);
        Throw(VulkanApi.BindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");
        nint pointer = 0;
        Throw(VulkanApi.MapMemory(_device, memory, 0, size, 0, (void**)&pointer), "vkMapMemory");
        mapped = pointer;
        return buffer;
    }

    /** 打分选类型: HOST_CACHED(+2) 优于仅 HOST_COHERENT(+1)。 */
    private VkDeviceMemory AllocateReadbackMemory(ulong size, uint typeBits, out bool coherent)
    {
        VkPhysicalDeviceMemoryProperties properties;
        VulkanApi.GetPhysicalDeviceMemoryProperties(PhysicalDevice, &properties);
        var best = uint.MaxValue;
        var bestScore = -1;
        var bestCoherent = true;
        for (var i = 0u; i < properties.memoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) == 0)
                continue;
            var flags = properties.memoryTypes[(int)i].propertyFlags;
            if ((flags & VkMemoryPropertyFlagBits.MemoryPropertyHostVisibleBit) == 0)
                continue;
            var cached = (flags & VkMemoryPropertyFlagBits.MemoryPropertyHostCachedBit) != 0;
            var isCoherent = (flags & VkMemoryPropertyFlagBits.MemoryPropertyHostCoherentBit) != 0;
            var score = (cached ? 2 : 0) + (isCoherent ? 1 : 0);
            if (score <= bestScore)
                continue;
            bestScore = score;
            best = i;
            bestCoherent = isCoherent;
        }

        if (best == uint.MaxValue)
            throw new InvalidOperationException("没有 HOST_VISIBLE 的显存类型可作回读缓冲");
        coherent = bestCoherent;
        var allocate = new VkMemoryAllocateInfo
        {
            sType = VkStructureType.StructureTypeMemoryAllocateInfo,
            allocationSize = size,
            memoryTypeIndex = best,
        };
        VkDeviceMemory memory;
        Throw(VulkanApi.AllocateMemory(_device, &allocate, null, &memory), "vkAllocateMemory");
        return memory;
    }

    /** 非 HOST_COHERENT 映射缓冲在 GPU 写、CPU 读之间必须 invalidate(整段)。 */
    public void InvalidateMappedRange(VkDeviceMemory memory)
    {
        var range = new VkMappedMemoryRange
        {
            sType = VkStructureType.StructureTypeMappedMemoryRange,
            memory = memory,
            offset = 0,
            size = VulkanApi.WholeSize,
        };
        Throw(VulkanApi.InvalidateMappedMemoryRanges(_device, 1, &range), "vkInvalidateMappedMemoryRanges");
    }

    public VkBuffer CreateBuffer(ulong size, VkBufferUsageFlagBits usage, VkMemoryPropertyFlagBits properties, out VkDeviceMemory memory)
    {
        var info = new VkBufferCreateInfo
        {
            sType = VkStructureType.StructureTypeBufferCreateInfo,
            size = size,
            usage = usage,
            sharingMode = VkSharingMode.SharingModeExclusive,
        };
        VkBuffer buffer;
        Throw(VulkanApi.CreateBuffer(_device, &info, null, &buffer), "vkCreateBuffer");
        VkMemoryRequirements requirements;
        VulkanApi.GetBufferMemoryRequirements(_device, buffer, &requirements);
        memory = AllocateMemory(requirements.size, requirements.memoryTypeBits, properties);
        Throw(VulkanApi.BindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");
        return buffer;
    }

    public VkImage CreateImage(VkImageCreateInfo info, out VkDeviceMemory memory)
    {
        VkImage image;
        Throw(VulkanApi.CreateImage(_device, &info, null, &image), "vkCreateImage");
        VkMemoryRequirements requirements;
        VulkanApi.GetImageMemoryRequirements(_device, image, &requirements);
        memory = AllocateMemory(requirements.size, requirements.memoryTypeBits, VkMemoryPropertyFlagBits.MemoryPropertyDeviceLocalBit);
        Throw(VulkanApi.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory");
        return image;
    }

    public VkImageView CreateImageView(VkImage image, VkFormat format)
    {
        var info = new VkImageViewCreateInfo
        {
            sType = VkStructureType.StructureTypeImageViewCreateInfo,
            image = image,
            viewType = VkImageViewType.ImageViewType2D,
            format = format,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlagBits.ImageAspectColorBit, 0, 1, 0, 1),
        };
        VkImageView view;
        Throw(VulkanApi.CreateImageView(_device, &info, null, &view), "vkCreateImageView");
        return view;
    }

    /** 一次性命令: 分配命令缓冲, 执行 body, 提交并等待完成(用于资源初始化的上传/布局转换)。 */
    public void OneShot(Action<VkCommandBuffer> body)
    {
        var allocInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.StructureTypeCommandBufferAllocateInfo,
            commandPool = CommandPool,
            level = VkCommandBufferLevel.CommandBufferLevelPrimary,
            commandBufferCount = 1,
        };
        VkCommandBuffer commandBuffer;
        Throw(VulkanApi.AllocateCommandBuffers(_device, &allocInfo, &commandBuffer), "vkAllocateCommandBuffers");
        var begin = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.StructureTypeCommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlagBits.CommandBufferUsageOneTimeSubmitBit,
        };
        Throw(VulkanApi.BeginCommandBuffer(commandBuffer, &begin), "vkBeginCommandBuffer");
        body(commandBuffer);
        Throw(VulkanApi.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

        var submit = new VkSubmitInfo
        {
            sType = VkStructureType.StructureTypeSubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &commandBuffer,
        };
        Throw(VulkanApi.QueueSubmit(GraphicsQueue, 1, &submit, default), "vkQueueSubmit");
        Throw(VulkanApi.QueueWaitIdle(GraphicsQueue), "vkQueueWaitIdle");
        VulkanApi.FreeCommandBuffers(_device, CommandPool, 1, &commandBuffer);
    }

    public static void Throw(VkResult result, string what)
    {
        if (result != VkResult.Success)
            throw new InvalidOperationException($"{what} 失败: {result}");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_device.Handle != 0)
            VulkanApi.DeviceWaitIdle(_device);
        if (CommandPool.Handle != 0)
            VulkanApi.DestroyCommandPool(_device, CommandPool, null);
        if (_device.Handle != 0)
            VulkanApi.DestroyDevice(_device, null);
        if (_instance.Handle != 0)
            VulkanApi.DestroyInstance(_instance, null);
    }
}
