#version 450
// 文本格栅顶点着色器(Vulkan 版, 一趟 draw 画完"背景层 + 字形层"):
// 同一个 draw 里 instance 0..N-1 是背景层、N..2N-1 是字形层, 层号由实例下标算出;
// 这等于把原先的"背景趟 + 字形趟"两次 draw 合成一次, 而光栅化顺序不变
// (先全部背景、再全部字形), 因此宽字符右半不会被相邻格的背景盖掉, 也不依赖隐式覆盖(§9 B14)。
// 每实例 = 一格; 打包 cell 经统一回读缓冲(usamplerBuffer, R32_UINT)取字符与调色板槽。
// 见 plans/DSH-Sharp-§4-Vulkan与cellgrid方案.md §9: 宽字符背景横向 ×2、位图不缩放、续格不画字形。

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) flat out float vLayer; // 0 = 背景层, 1 = 字形层

layout(set = 0, binding = 0) uniform sampler2D uAtlas;
layout(set = 0, binding = 1) uniform usamplerBuffer uCells;
layout(set = 0, binding = 2) uniform isampler2D uGlyphMap;

// std140: 全部用 ivec4/vec4 承载, 避免 ivec2 的对齐歧义。
layout(set = 0, binding = 3, std140) uniform Params
{
    ivec4 uGridSize;    // xy = 列/行
    ivec4 uAtlasCells;  // xy = 图集槽列/行
    ivec4 uAtlasPixels; // xy = 图集像素宽/高
    ivec4 uCellPixels;  // xy = 单格像素宽/高
    vec4 uPalette[64];
    vec4 uDefaultBackground;
    vec4 uDefaultForeground;
} p;

layout(push_constant) uniform Push
{
    int uInstanceBase; // 环形/分段绘制的实例基址
} pc;

void main()
{
    // 无顶点缓冲: 三角形列表 6 顶点拼出单位方块。
    // 两个三角形必须共用对角线 (1,0)-(0,1): T1=(0,0)(1,0)(0,1)、T2=(1,0)(1,1)(0,1)。
    // 若第二个三角形写成 (1,1)(0,0)(1,0), 会与 T1 共享上边并在上半部重叠, 下半部留下一个三角空洞
    // ——每个格子的字形/光标下方都会缺一角(用 (i&1,(i>>1)&1) 逐顶点推角时最容易踩到这个)。
    const vec2 corners[6] = vec2[6](
        vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0),
        vec2(1.0, 0.0), vec2(1.0, 1.0), vec2(0.0, 1.0));
    vec2 aCorner = corners[gl_VertexIndex];
    int cellCount = p.uGridSize.x * p.uGridSize.y;
    int layer = cellCount > 0 ? gl_InstanceIndex / cellCount : 0;
    int cellIndex = (gl_InstanceIndex - (layer * cellCount)) + pc.uInstanceBase;
    int cy = cellIndex / p.uGridSize.x;
    int cx = cellIndex - cy * p.uGridSize.x;
    uint packedCell = texelFetch(uCells, cellIndex).r;
    int chr = int(packedCell & 0xFFFFu);
    int fg = int((packedCell >> 16) & 0x3Fu);
    int bg = int((packedCell >> 22) & 0x3Fu);
    int style = int((packedCell >> 28) & 0x7u);
    bool reversed = (style & 4) != 0;
    float span = 1.0;
    vec2 uv0 = vec2(0.0);
    vec2 uv1 = vec2(0.0);
    vec4 color;
    if (layer == 0)
    {
        color = reversed
            ? (fg == 0 ? p.uDefaultForeground : p.uPalette[fg])
            : (bg == 0 ? p.uDefaultBackground : p.uPalette[bg]);
    }
    else
    {
        // 续格('\0')与空格不画字形; 其余经字形映射表取槽位, 负数表示宽字形(占两槽)。
        int mapped = (chr == 0 || chr == 32) ? 0 : texelFetch(uGlyphMap, ivec2(chr & 255, chr >> 8), 0).r;
        if (mapped == 0)
        {
            gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
            vColor = vec4(0.0);
            vUv = vec2(0.0);
            vLayer = float(layer);
            return;
        }
        int slot = (mapped & 0x7FFFFFFF) - 1;
        span = mapped < 0 ? 2.0 : 1.0;
        int slotSpan = mapped < 0 ? 2 : 1;
        color = reversed
            ? (bg == 0 ? p.uDefaultBackground : p.uPalette[bg])
            : (fg == 0 ? p.uDefaultForeground : p.uPalette[fg]);
        if ((style & 2) != 0)
            color = vec4(color.rgb * 0.5, color.a);
        int slotCol = slot - (slot / p.uAtlasCells.x) * p.uAtlasCells.x;
        int slotRow = slot / p.uAtlasCells.x;
        uv0 = vec2(slotCol * p.uCellPixels.x, slotRow * p.uCellPixels.y) / vec2(p.uAtlasPixels.xy);
        uv1 = vec2((slotCol + slotSpan) * p.uCellPixels.x, (slotRow + 1) * p.uCellPixels.y) / vec2(p.uAtlasPixels.xy);
    }
    // Vulkan NDC: y 向下。格坐标自左上角起算, 故直接 2*y/H-1(不做 GL 的翻转)。
    vec2 cellPos = vec2(cx, cy) + aCorner * vec2(span, 1.0);
    vec2 ndc = vec2(cellPos.x / float(p.uGridSize.x) * 2.0 - 1.0, cellPos.y / float(p.uGridSize.y) * 2.0 - 1.0);
    gl_Position = vec4(ndc, 0.0, 1.0);
    vColor = color;
    vUv = mix(uv0, uv1, aCorner);
    vLayer = float(layer);
}
