using OpenTK.Graphics.OpenGL;
// 本命名空间末段就叫 GL, 裸名 `GL` 会被它遮蔽; 用别名 GlApi 引用 OpenTK 的静态绑定类(与 Vk 侧 VulkanApi 同形)。
using GlApi = OpenTK.Graphics.OpenGL.GL;

namespace Dsh.Tui.GL;

public sealed class GpuRenderCore : IDisposable
{
    private readonly int[] _dirtySlots = new int[64];
    private int _unitVbo;
    private int _ebo;
    private int _vao;
    private int _cellBuffer;
    private int _cellTexture;
    private int _glyphMapTexture;
    private int _shader;
    private int _atlasTexture;
    private int _cellCapacity;
    private int _seenMapVersion = -1;
    private int _seenPaletteVersion = -1;
    private int _atlasTextureLocation;
    private int _cellsLocation;
    private int _glyphMapLocation;
    private int _gridSizeLocation;
    private int _passLocation;
    private int _texEnabledLocation;
    private int _instanceBaseLocation;
    private bool _disposed;

    public void Initialize(GlyphAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        GlApi.ClearColor(0f, 0f, 0f, 1f);
        GlApi.Enable(EnableCap.Blend);
        GlApi.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _shader = CreateShader();
        _atlasTexture = CreateAtlasTexture(atlas);
        _atlasTextureLocation = GlApi.GetUniformLocation(_shader, "uTexture");
        _cellsLocation = GlApi.GetUniformLocation(_shader, "uCells");
        _glyphMapLocation = GlApi.GetUniformLocation(_shader, "uGlyphMap");
        _gridSizeLocation = GlApi.GetUniformLocation(_shader, "uGridSize");
        _passLocation = GlApi.GetUniformLocation(_shader, "uPass");
        _texEnabledLocation = GlApi.GetUniformLocation(_shader, "uTexEnabled");
        _instanceBaseLocation = GlApi.GetUniformLocation(_shader, "uInstanceBase");

        float[] unitQuad = [0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f];
        _unitVbo = GlApi.GenBuffer();
        GlApi.BindBuffer(BufferTarget.ArrayBuffer, _unitVbo);
        GlApi.BufferData(BufferTarget.ArrayBuffer, unitQuad.Length * sizeof(float), unitQuad, BufferUsage.StaticDraw);

        ushort[] indices = [0, 1, 2, 2, 1, 3];
        _ebo = GlApi.GenBuffer();
        GlApi.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GlApi.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(ushort), indices, BufferUsage.StaticDraw);

        _vao = GlApi.GenVertexArray();
        GlApi.BindVertexArray(_vao);
        GlApi.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GlApi.BindBuffer(BufferTarget.ArrayBuffer, _unitVbo);
        GlApi.EnableVertexAttribArray(0);
        GlApi.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
        GlApi.BindVertexArray(0);
        GlApi.BindBuffer(BufferTarget.ArrayBuffer, 0);

        _cellBuffer = GlApi.GenBuffer();
        _cellTexture = GlApi.GenTexture();
        GlApi.BindTexture(TextureTarget.TextureBuffer, _cellTexture);
        GlApi.TexBuffer(TextureTarget.TextureBuffer, SizedInternalFormat.R32ui, _cellBuffer);
        GlApi.BindTexture(TextureTarget.TextureBuffer, 0);

        _glyphMapTexture = GlApi.GenTexture();
        GlApi.BindTexture(TextureTarget.Texture2D, _glyphMapTexture);
        GlApi.TexParameteri(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GlApi.TexParameteri(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GlApi.TexParameteri(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GlApi.TexParameteri(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GlApi.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R32i, 256, 256, 0, PixelFormat.RedInteger, PixelType.Int, IntPtr.Zero);
        GlApi.BindTexture(TextureTarget.Texture2D, 0);

        GlApi.UseProgram(_shader);
        GlApi.Uniform4f(GlApi.GetUniformLocation(_shader, "uDefaultBackground"), 0f, 0f, 0f, 1f);
        GlApi.Uniform4f(GlApi.GetUniformLocation(_shader, "uDefaultForeground"), TerminalColorPalette.DefaultForeground.R, TerminalColorPalette.DefaultForeground.G, TerminalColorPalette.DefaultForeground.B, 1f);
        GlApi.Uniform2i(GlApi.GetUniformLocation(_shader, "uAtlasCells"), GlyphAtlas.Columns, GlyphAtlas.Rows);
        GlApi.Uniform2i(GlApi.GetUniformLocation(_shader, "uAtlasPixels"), GlyphAtlas.Columns * atlas.GlyphWidth, GlyphAtlas.Rows * atlas.GlyphHeight);
        GlApi.Uniform2i(GlApi.GetUniformLocation(_shader, "uCellPixels"), atlas.GlyphWidth, atlas.GlyphHeight);
        GlApi.Uniform1i(_atlasTextureLocation, 0);
        GlApi.Uniform1i(_cellsLocation, 1);
        GlApi.Uniform1i(_glyphMapLocation, 2);
        GlApi.Uniform1i(_instanceBaseLocation, 0);
    }

    public void EnsureCellCapacity(int cellCount)
    {
        if (cellCount <= _cellCapacity)
            return;
        _cellCapacity = Math.Max(cellCount, Math.Max(_cellCapacity * 2, 80 * 25));
        GlApi.BindBuffer(BufferTarget.TextureBuffer, _cellBuffer);
        GlApi.BufferData(BufferTarget.TextureBuffer, _cellCapacity * sizeof(uint), IntPtr.Zero, BufferUsage.DynamicDraw);
        GlApi.BindTexture(TextureTarget.TextureBuffer, _cellTexture);
        GlApi.TexBuffer(TextureTarget.TextureBuffer, SizedInternalFormat.R32ui, _cellBuffer);
        GlApi.BindTexture(TextureTarget.TextureBuffer, 0);
        GlApi.BindBuffer(BufferTarget.TextureBuffer, 0);
    }

    public void UploadCells(uint[] packed, int firstCell, int count)
    {
        if (count <= 0)
            return;
        GlApi.BindBuffer(BufferTarget.TextureBuffer, _cellBuffer);
        GlApi.BufferSubData(BufferTarget.TextureBuffer, firstCell * sizeof(uint), count * sizeof(uint), packed);
        GlApi.BindBuffer(BufferTarget.TextureBuffer, 0);
    }

    /**
     * 恒全帧重绘: 双缓冲交换链下后备缓冲是两帧前的内容, 脏行不重绘的假设不成立(曾致浮层多重高亮/内容丢失)。
     * 性能不受影响: 一次 instanced draw 画完全部格, 增量性保留在上传侧(只 UploadCells 脏行)。
     */
    public void RenderFrame(GlyphAtlas atlas, int gridWidth, int gridHeight)
    {
        PrepareFrame(atlas, gridWidth, gridHeight);
        GlApi.Disable(EnableCap.ScissorTest);
        GlApi.Clear(ClearBufferMask.ColorBufferBit);
        GlApi.Uniform1i(_instanceBaseLocation, 0);
        DrawPasses(gridWidth * gridHeight);
        GlApi.BindVertexArray(0);
    }

    private void PrepareFrame(GlyphAtlas atlas, int gridWidth, int gridHeight)
    {
        GlApi.UseProgram(_shader);
        UploadPalette();
        GlApi.ActiveTexture(TextureUnit.Texture0);
        GlApi.BindTexture(TextureTarget.Texture2D, _atlasTexture);
        GlApi.ActiveTexture(TextureUnit.Texture1);
        GlApi.BindTexture(TextureTarget.TextureBuffer, _cellTexture);
        GlApi.ActiveTexture(TextureUnit.Texture2);
        GlApi.BindTexture(TextureTarget.Texture2D, _glyphMapTexture);
        if (atlas.MapVersion != _seenMapVersion)
        {
            var mapData = atlas.MapUpload;
            GlApi.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 256, 256, PixelFormat.RedInteger, PixelType.Int, ref System.Runtime.InteropServices.MemoryMarshal.GetReference(mapData));
            _seenMapVersion = atlas.MapVersion;
        }
        FlushAtlasDirty(atlas);
        GlApi.Uniform2i(_gridSizeLocation, gridWidth, gridHeight);
        GlApi.BindVertexArray(_vao);
    }

    private void DrawPasses(int instanceCount)
    {
        GlApi.Uniform1f(_texEnabledLocation, 0f);
        GlApi.Uniform1i(_passLocation, 0);
        DrawInstances(instanceCount);
        GlApi.Uniform1f(_texEnabledLocation, 1f);
        GlApi.Uniform1i(_passLocation, 1);
        DrawInstances(instanceCount);
    }

    private static void DrawInstances(int instanceCount)
        => GlApi.DrawElementsInstanced(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedShort, IntPtr.Zero, instanceCount);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_shader != 0)
            GlApi.DeleteProgram(_shader);
        if (_atlasTexture != 0)
            GlApi.DeleteTexture(_atlasTexture);
        if (_glyphMapTexture != 0)
            GlApi.DeleteTexture(_glyphMapTexture);
        if (_cellTexture != 0)
            GlApi.DeleteTexture(_cellTexture);
        if (_cellBuffer != 0)
            GlApi.DeleteBuffer(_cellBuffer);
        if (_vao != 0)
            GlApi.DeleteVertexArray(_vao);
        if (_unitVbo != 0)
            GlApi.DeleteBuffer(_unitVbo);
        if (_ebo != 0)
            GlApi.DeleteBuffer(_ebo);
    }

    /** 调色板变化(新 RGB 槽位)时才重传 uPalette[64]; 索引稳定, 脏行增量上传不受影响。 */
    private void UploadPalette()
    {
        var table = CellColorTable.Shared;
        var version = table.Version;
        if (version == _seenPaletteVersion)
            return;
        var count = table.Count;
        for (var slot = 0; slot < count; slot++)
        {
            var rgba = TerminalColorPalette.ToRgba(table[slot]);
            GlApi.Uniform4f(GlApi.GetUniformLocation(_shader, $"uPalette[{slot}]"), rgba.R, rgba.G, rgba.B, rgba.A);
        }

        _seenPaletteVersion = version;
    }

    private void FlushAtlasDirty(GlyphAtlas atlas)
    {
        if (atlas.DirtyCount == 0)
            return;
        GlApi.ActiveTexture(TextureUnit.Texture0);
        GlApi.BindTexture(TextureTarget.Texture2D, _atlasTexture);
        GlApi.PixelStorei(PixelStoreParameter.UnpackRowLength, atlas.AtlasWidth);
        var flushed = atlas.FlushDirtyRegions(_dirtySlots);
        while (flushed > 0)
        {
            for (var index = 0; index < flushed; index++)
            {
                var slot = _dirtySlots[index];
                var slotX = (slot % GlyphAtlas.Columns) * atlas.GlyphWidth;
                var slotY = (slot / GlyphAtlas.Columns) * atlas.GlyphHeight;
                GlApi.TexSubImage2D(
                    TextureTarget.Texture2D,
                    0,
                    slotX,
                    slotY,
                    atlas.GlyphWidth,
                    atlas.GlyphHeight,
                    PixelFormat.Red,
                    PixelType.UnsignedByte,
                    ref atlas.TextureData[(slotY * atlas.AtlasWidth) + slotX]);
            }
            flushed = atlas.FlushDirtyRegions(_dirtySlots);
        }
        GlApi.PixelStorei(PixelStoreParameter.UnpackRowLength, 0);
    }

    private static int CreateAtlasTexture(GlyphAtlas atlas)
    {
        var texture = GlApi.GenTexture();
        GlApi.BindTexture(TextureTarget.Texture2D, texture);
        var data = atlas.CreateTextureData();
        GlApi.TexImage2D(
            TextureTarget.Texture2D,
            0,
            InternalFormat.R8,
            GlyphAtlas.Columns * atlas.GlyphWidth,
            GlyphAtlas.Rows * atlas.GlyphHeight,
            0,
            PixelFormat.Red,
            PixelType.UnsignedByte,
            data);
        GlApi.TexParameteri(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GlApi.TexParameteri(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GlApi.TexParameteri(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GlApi.TexParameteri(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GlApi.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    private static int CreateShader()
    {
        const string vertexSource = """
            #version 330 core
            layout(location = 0) in vec2 aCorner;
            uniform ivec2 uGridSize;
            uniform ivec2 uAtlasCells;
            uniform ivec2 uAtlasPixels;
            uniform ivec2 uCellPixels;
            uniform int uPass;
            uniform int uInstanceBase;
            uniform usamplerBuffer uCells;
            uniform isampler2D uGlyphMap;
            uniform vec4 uPalette[64];
            uniform vec4 uDefaultBackground;
            uniform vec4 uDefaultForeground;
            out vec4 vColor;
            out vec2 vUv;
            void main()
            {
                int cellIndex = gl_InstanceID + uInstanceBase;
                int cy = cellIndex / uGridSize.x;
                int cx = cellIndex - cy * uGridSize.x;
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
                if (uPass == 0)
                {
                    color = reversed
                        ? (fg == 0 ? uDefaultForeground : uPalette[fg])
                        : (bg == 0 ? uDefaultBackground : uPalette[bg]);
                }
                else
                {
                    int mapped = (chr == 0 || chr == 32) ? 0 : texelFetch(uGlyphMap, ivec2(chr & 255, chr >> 8), 0).r;
                    if (mapped == 0)
                    {
                        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
                        vColor = vec4(0.0);
                        vUv = vec2(0.0);
                        return;
                    }
                    int slot = (mapped & 0x7FFFFFFF) - 1;
                    span = mapped < 0 ? 2.0 : 1.0;
                    int slotSpan = mapped < 0 ? 2 : 1;
                    color = reversed
                        ? (bg == 0 ? uDefaultBackground : uPalette[bg])
                        : (fg == 0 ? uDefaultForeground : uPalette[fg]);
                    if ((style & 2) != 0)
                        color = vec4(color.rgb * 0.5, color.a);
                    int slotCol = slot - (slot / uAtlasCells.x) * uAtlasCells.x;
                    int slotRow = slot / uAtlasCells.x;
                    uv0 = vec2(slotCol * uCellPixels.x, slotRow * uCellPixels.y) / vec2(uAtlasPixels);
                    uv1 = vec2((slotCol + slotSpan) * uCellPixels.x, (slotRow + 1) * uCellPixels.y) / vec2(uAtlasPixels);
                }
                vec2 cellPos = vec2(cx, cy) + aCorner * vec2(span, 1.0);
                vec2 ndc = vec2(cellPos.x / float(uGridSize.x) * 2.0 - 1.0, 1.0 - cellPos.y / float(uGridSize.y) * 2.0);
                gl_Position = vec4(ndc, 0.0, 1.0);
                vColor = color;
                vUv = mix(uv0, uv1, aCorner);
            }
            """;

        const string fragmentSource = """
            #version 330 core
            in vec4 vColor;
            in vec2 vUv;
            uniform sampler2D uTexture;
            uniform float uTexEnabled;
            out vec4 FragColor;
            void main()
            {
                if (uTexEnabled > 0.5)
                {
                    float alpha = texture(uTexture, vUv).r;
                    FragColor = vec4(vColor.rgb, alpha);
                }
                else
                {
                    FragColor = vColor;
                }
            }
            """;

        var vertexShader = GlApi.CreateShader(ShaderType.VertexShader);
        GlApi.ShaderSource(vertexShader, vertexSource);
        GlApi.CompileShader(vertexShader);
        GlApi.GetShaderi(vertexShader, ShaderParameterName.CompileStatus, out var vertexStatus);
        if (vertexStatus == 0)
            throw new InvalidOperationException($"Vertex shader compile error: {GlApi.GetShaderInfoLog(vertexShader)}");

        var fragmentShader = GlApi.CreateShader(ShaderType.FragmentShader);
        GlApi.ShaderSource(fragmentShader, fragmentSource);
        GlApi.CompileShader(fragmentShader);
        GlApi.GetShaderi(fragmentShader, ShaderParameterName.CompileStatus, out var fragmentStatus);
        if (fragmentStatus == 0)
            throw new InvalidOperationException($"Fragment shader compile error: {GlApi.GetShaderInfoLog(fragmentShader)}");

        var program = GlApi.CreateProgram();
        GlApi.AttachShader(program, vertexShader);
        GlApi.AttachShader(program, fragmentShader);
        GlApi.LinkProgram(program);
        GlApi.GetProgrami(program, ProgramProperty.LinkStatus, out var linkStatus);
        if (linkStatus == 0)
            throw new InvalidOperationException($"Shader link error: {GlApi.GetProgramInfoLog(program)}");

        GlApi.DetachShader(program, vertexShader);
        GlApi.DetachShader(program, fragmentShader);
        GlApi.DeleteShader(vertexShader);
        GlApi.DeleteShader(fragmentShader);
        return program;
    }
}
