namespace Dsh.Tui.Vk;

/**
 * 编译期嵌入的 SPIR-V 着色器。
 * 源见 Shaders/*.glsl; 由 artifacts020/vk-probe 的 SpirvGen 用 Veldrid.SPIRV 离线编译(本机无 Vulkan SDK/glslc)。
 * 重新生成: 对 vk-probe 传 --spirv 及 vert/frag/输出目录三个参数(命令见该工程 SpirvGen.cs)。
 */
internal static class VkShaders
{
    public static byte[] TextVertex { get; } = Load("text.vert.spv");

    public static byte[] TextFragment { get; } = Load("text.frag.spv");

    private static byte[] Load(string fileName)
    {
        var assembly = typeof(VkShaders).Assembly;
        var resource = Array.Find(assembly.GetManifestResourceNames(), name => name.EndsWith(fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"缺少嵌入式着色器 {fileName}");
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"无法读取嵌入式着色器 {fileName}");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
