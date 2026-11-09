using System.Numerics;
using System.Runtime.InteropServices;

namespace V12.Rendering;

// ── Geometry ────────────────────────────────────────────────────────────────

public enum VertexSemantic
{
    Position,
    Normal,
    Tangent,
    Color,
    Uv0,
    Uv1,
    SkinWeights,
    SkinIndices,
}

public enum VertexFormat
{
    Float1,
    Float2,
    Float3,
    Float4,
    UByte4,
    UByte4Normalized,
}

public readonly record struct VertexAttribute(VertexSemantic Semantic, VertexFormat Format, int Offset, int Stream);

public readonly record struct VertexStream(int Stride, ReadOnlyMemory<byte> Data);

public enum IndexFormat
{
    UInt16,
    UInt32,
}

public readonly record struct SubmeshRange(int IndexStart, int IndexCount, int MaterialSlot = 0);

/// <summary>
/// Geometry only. Placement lives in <see cref="DrawItem.World"/>; dimensions are
/// baked into the vertices. Triangles use counter-clockwise winding as seen from
/// the front face; backends convert to their native convention. The streams are
/// immutable while a frame lease holds them.
/// </summary>
public sealed record MeshData(
    ReadOnlyMemory<VertexStream> VertexStreams,
    ReadOnlyMemory<VertexAttribute> Layout,
    IndexFormat IndexFormat,
    ReadOnlyMemory<byte> Indices,
    ReadOnlyMemory<SubmeshRange> Submeshes)
{
    public static MeshData Empty { get; } = new(
        ReadOnlyMemory<VertexStream>.Empty,
        ReadOnlyMemory<VertexAttribute>.Empty,
        IndexFormat.UInt32,
        ReadOnlyMemory<byte>.Empty,
        ReadOnlyMemory<SubmeshRange>.Empty);
}

/// <summary>Helpers for the common V12 position/normal/UV0/color layout.</summary>
public static class MeshDataFactory
{
    /// <summary>
    /// Builds one interleaved stream: Position Float3, Normal Float3, UV0 Float2, Color Float4.
    /// All arrays must have the same vertex count; indices are UInt32.
    /// </summary>
    public static MeshData CreatePositionNormalUv0Color(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector3> normals,
        ReadOnlySpan<Vector2> uvs,
        ReadOnlySpan<Vector4> colors,
        ReadOnlySpan<uint> indices)
    {
        int count = positions.Length;
        if (normals.Length != count || uvs.Length != count || colors.Length != count)
            throw new ArgumentException("Position, normal, UV0 and color arrays must have the same length.");

        const int strideFloats = 3 + 3 + 2 + 4;
        var packed = new float[count * strideFloats];
        for (int i = 0; i < count; i++)
        {
            int o = i * strideFloats;
            packed[o + 0] = positions[i].X;
            packed[o + 1] = positions[i].Y;
            packed[o + 2] = positions[i].Z;
            packed[o + 3] = normals[i].X;
            packed[o + 4] = normals[i].Y;
            packed[o + 5] = normals[i].Z;
            packed[o + 6] = uvs[i].X;
            packed[o + 7] = uvs[i].Y;
            packed[o + 8] = colors[i].X;
            packed[o + 9] = colors[i].Y;
            packed[o + 10] = colors[i].Z;
            packed[o + 11] = colors[i].W;
        }

        var vertexBytes = MemoryMarshal.AsBytes(packed.AsSpan()).ToArray();
        var indexBytes = MemoryMarshal.AsBytes(indices).ToArray();

        var layout = new[]
        {
            new VertexAttribute(VertexSemantic.Position, VertexFormat.Float3, 0, 0),
            new VertexAttribute(VertexSemantic.Normal, VertexFormat.Float3, 12, 0),
            new VertexAttribute(VertexSemantic.Uv0, VertexFormat.Float2, 24, 0),
            new VertexAttribute(VertexSemantic.Color, VertexFormat.Float4, 32, 0),
        };

        return new MeshData(
            new[] { new VertexStream(strideFloats * sizeof(float), vertexBytes) },
            layout,
            IndexFormat.UInt32,
            indexBytes,
            indices.Length == 0
                ? ReadOnlyMemory<SubmeshRange>.Empty
                : new[] { new SubmeshRange(0, indices.Length) });
    }

    /// <summary>
    /// Builds one interleaved stream: Position Float3, Normal Float3, UV0 Float2,
    /// Tangent Float4 (xyz + binormal sign w), Color Float4. Used when a model
    /// supplies its own tangents so normal maps get a full tangent basis.
    /// All arrays must have the same vertex count; indices are UInt32.
    /// </summary>
    public static MeshData CreatePositionNormalUv0TangentColor(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector3> normals,
        ReadOnlySpan<Vector2> uvs,
        ReadOnlySpan<Vector4> tangents,
        ReadOnlySpan<Vector4> colors,
        ReadOnlySpan<uint> indices)
    {
        int count = positions.Length;
        if (normals.Length != count || uvs.Length != count ||
            tangents.Length != count || colors.Length != count)
            throw new ArgumentException("Position, normal, UV0, tangent and color arrays must have the same length.");

        const int strideFloats = 3 + 3 + 2 + 4 + 4;
        var packed = new float[count * strideFloats];
        for (int i = 0; i < count; i++)
        {
            int o = i * strideFloats;
            packed[o + 0] = positions[i].X;
            packed[o + 1] = positions[i].Y;
            packed[o + 2] = positions[i].Z;
            packed[o + 3] = normals[i].X;
            packed[o + 4] = normals[i].Y;
            packed[o + 5] = normals[i].Z;
            packed[o + 6] = uvs[i].X;
            packed[o + 7] = uvs[i].Y;
            packed[o + 8] = tangents[i].X;
            packed[o + 9] = tangents[i].Y;
            packed[o + 10] = tangents[i].Z;
            packed[o + 11] = tangents[i].W;
            packed[o + 12] = colors[i].X;
            packed[o + 13] = colors[i].Y;
            packed[o + 14] = colors[i].Z;
            packed[o + 15] = colors[i].W;
        }

        var vertexBytes = MemoryMarshal.AsBytes(packed.AsSpan()).ToArray();
        var indexBytes = MemoryMarshal.AsBytes(indices).ToArray();

        var layout = new[]
        {
            new VertexAttribute(VertexSemantic.Position, VertexFormat.Float3, 0, 0),
            new VertexAttribute(VertexSemantic.Normal, VertexFormat.Float3, 12, 0),
            new VertexAttribute(VertexSemantic.Uv0, VertexFormat.Float2, 24, 0),
            new VertexAttribute(VertexSemantic.Tangent, VertexFormat.Float4, 32, 0),
            new VertexAttribute(VertexSemantic.Color, VertexFormat.Float4, 48, 0),
        };

        return new MeshData(
            new[] { new VertexStream(strideFloats * sizeof(float), vertexBytes) },
            layout,
            IndexFormat.UInt32,
            indexBytes,
            indices.Length == 0
                ? ReadOnlyMemory<SubmeshRange>.Empty
                : new[] { new SubmeshRange(0, indices.Length) });
    }
}

// ── Textures and targets ────────────────────────────────────────────────────

public enum TextureDimension
{
    Texture2D,
    Cube,
    Array2D,
    Texture3D,
}

public enum TextureFormat
{
    R8Unorm,
    Rgba8Unorm,
    Rgba8Srgb,
    Rgba16Float,
    Rgba32Float,
    Depth24Stencil8,
}

public enum TextureColorSpace
{
    Linear,
    Srgb,
}

[Flags]
public enum TextureUsage
{
    None = 0,
    Sampled = 1 << 0,
    RenderTarget = 1 << 1,
    DepthStencil = 1 << 2,
}

public readonly record struct TextureSubresource(
    int Mip,
    int Layer,
    int RowPitch,
    int SlicePitch,
    ReadOnlyMemory<byte> Bytes);

/// <summary>CPU-side image description; the renderer owns only the GPU realization.</summary>
public sealed record TextureData(
    TextureDimension Dimension,
    int Width,
    int Height,
    int DepthOrLayers,
    int MipCount,
    TextureFormat Format,
    TextureColorSpace ColorSpace,
    TextureUsage Usage,
    ReadOnlyMemory<TextureSubresource> Subresources);

public readonly record struct RenderTargetDesc(
    int Width,
    int Height,
    TextureFormat ColorFormat = TextureFormat.Rgba8Unorm,
    bool Depth = true,
    bool Mipmaps = false,
    bool Transparent = false,
    bool OwnWorld3D = false);

/// <summary>Well-known layer masks for v1 3D and UI routing.</summary>
public static class RenderLayers
{
    public const uint Default = 1u << 0;
    public const uint Ui = 1u << 1;
    public const uint All = uint.MaxValue;
}

// ── Materials and shaders ───────────────────────────────────────────────────

public enum BlendMode
{
    Opaque,
    Alpha,
    Additive,
    Multiply,
}

public enum CullMode
{
    Back,
    Front,
    None,
}

public enum MaterialValueKind
{
    None,
    Float,
    Int,
    Bool,
    Vector2,
    Vector3,
    Vector4,
    Matrix4x4,
    Texture,
}

/// <summary>Tagged value used for material defaults and per-draw overrides.</summary>
public readonly struct MaterialValue
{
    public MaterialValueKind Kind { get; }
    public float FloatValue { get; }
    public int IntValue { get; }
    public bool BoolValue { get; }
    public Vector2 Vector2Value { get; }
    public Vector3 Vector3Value { get; }
    public Vector4 Vector4Value { get; }
    public Matrix4x4 MatrixValue { get; }
    public TextureHandle TextureValue { get; }

    private MaterialValue(
        MaterialValueKind kind,
        float f = 0f,
        int i = 0,
        bool b = false,
        Vector2 v2 = default,
        Vector3 v3 = default,
        Vector4 v4 = default,
        Matrix4x4 m = default,
        TextureHandle t = default)
    {
        Kind = kind;
        FloatValue = f;
        IntValue = i;
        BoolValue = b;
        Vector2Value = v2;
        Vector3Value = v3;
        Vector4Value = v4;
        MatrixValue = m;
        TextureValue = t;
    }

    public static MaterialValue From(float value) => new(MaterialValueKind.Float, f: value);
    public static MaterialValue From(int value) => new(MaterialValueKind.Int, i: value);
    public static MaterialValue From(bool value) => new(MaterialValueKind.Bool, b: value);
    public static MaterialValue From(Vector2 value) => new(MaterialValueKind.Vector2, v2: value);
    public static MaterialValue From(Vector3 value) => new(MaterialValueKind.Vector3, v3: value);
    public static MaterialValue From(Vector4 value) => new(MaterialValueKind.Vector4, v4: value);
    public static MaterialValue From(Matrix4x4 value) => new(MaterialValueKind.Matrix4x4, m: value);
    public static MaterialValue From(TextureHandle value) => new(MaterialValueKind.Texture, t: value);
}

public readonly record struct MaterialTextureBinding(int Slot, TextureHandle Texture);

/// <summary>
/// Shared material state. Object-specific values live in <see cref="DrawOverrides"/>,
/// never on the material.
/// </summary>
public readonly record struct MaterialDesc(
    BlendMode Blend = BlendMode.Opaque,
    CullMode Cull = CullMode.Back,
    bool DepthTest = true,
    bool DepthWrite = true,
    bool Lit = true,
    bool CastShadows = true,
    bool ReceiveShadows = true,
    ReadOnlyMemory<MaterialValue> Parameters = default,
    ReadOnlyMemory<MaterialTextureBinding> Textures = default);

public readonly record struct DrawOverride(int ParameterIndex, MaterialValue Value);

public readonly record struct DrawOverrides(ReadOnlyMemory<DrawOverride> Values)
{
    public static DrawOverrides Empty { get; } = new(ReadOnlyMemory<DrawOverride>.Empty);
}

public enum BuiltInShader
{
    None,
    Lit,
    Unlit,
    Ui,
    Text,
}

public enum ShaderSourceLanguage
{
    None,
    GodotSpatial,
    GodotCanvasItem,
    Slang,
    Glsl,
    Hlsl,
}

public readonly record struct ShaderParameter(string Name, int Index, MaterialValueKind Kind);

public readonly record struct ShaderTextureSlot(string Name, int Slot);

public sealed record ShaderReflection(
    IReadOnlyList<ShaderParameter> Parameters,
    IReadOnlyList<ShaderTextureSlot> Textures,
    IReadOnlyList<VertexAttribute> VertexLayout)
{
    public static ShaderReflection Empty { get; } = new(
        Array.Empty<ShaderParameter>(),
        Array.Empty<ShaderTextureSlot>(),
        Array.Empty<VertexAttribute>());
}

public readonly record struct ShaderProgram(
    BuiltInShader BuiltIn = BuiltInShader.None,
    ReadOnlyMemory<byte> VertexSpirV = default,
    ReadOnlyMemory<byte> FragmentSpirV = default,
    ShaderReflection? Reflection = null,
    string? AssetUri = null,
    string? Source = null,
    ShaderSourceLanguage SourceLanguage = ShaderSourceLanguage.None);

// ── Views, items and frames ─────────────────────────────────────────────────

public enum ProjectionKind
{
    Perspective,
    Orthographic,
}

public enum ClearMode
{
    Color,
    Sky,
    DepthOnly,
    None,
}

public enum StereoEye
{
    None,
    Left,
    Right,
}

public readonly record struct ViewportRect(float X, float Y, float Width, float Height);

public readonly record struct ClipRect(float X, float Y, float Width, float Height, bool Enabled = true)
{
    public static ClipRect None { get; } = new(0f, 0f, 0f, 0f, false);
}

public readonly record struct ProjectionMetadata(ProjectionKind Kind, float Near, float Far, float FovY, float OrthoSize);

public readonly record struct ClearSettings(ClearMode Mode, Vector4 Color = default);

public struct RenderView
{
    public long Id;
    public long CameraElementId;
    public bool IsCurrentCamera;
    public Matrix4x4 View;
    public Matrix4x4 Projection;
    public ProjectionMetadata ProjectionMetadata;
    public ViewportRect Viewport;
    public ClearSettings Clear;
    public RenderTargetHandle? Target;
    public uint LayerMask;
    public StereoEye Eye;
}

public enum LightKind
{
    Directional,
    Point,
    Spot,
}

public struct LightItem
{
    public long ElementId;
    public bool Active;
    public LightKind Kind;
    public Matrix4x4 World;
    public Vector3 Color;
    public float Intensity;
    /// <summary>World-unit range for point/spot lights.</summary>
    public float Range;
    /// <summary>Spot cone angles in degrees: inner full-intensity cone and outer falloff edge.</summary>
    public float InnerCone;
    public float OuterCone;
    public bool CastShadows;
    public uint LayerMask;
}

public struct DrawItem
{
    public long ElementId;
    public long ViewportId;
    public MeshHandle Mesh;
    public MaterialHandle Material;
    public Matrix4x4 World;
    public ClipRect Clip;
    public uint SortKey;
    public uint LayerMask;
    public bool Active;
    /// <summary>Index into <see cref="RenderFrame.DrawOverrides"/>; -1 means none.</summary>
    public int OverridesIndex;
}

/// <summary>Immutable frame view. The renderer reads only this and never the live world.</summary>
public struct RenderFrame
{
    public ReadOnlyMemory<RenderView> Views;
    public ReadOnlyMemory<DrawItem> Items;
    public ReadOnlyMemory<DrawOverrides> DrawOverrides;
    public ReadOnlyMemory<LightItem> Lights;
    public EnvironmentData Environment;
    public float Time;
    public float DeltaTime;
}

// ── Capabilities and readback ───────────────────────────────────────────────

[Flags]
public enum RendererFeature
{
    None = 0,
    Shadows = 1 << 0,
    Fog = 1 << 1,
    VolumetricFog = 1 << 2,
    Sky = 1 << 3,
    Stereo = 1 << 4,
    RenderTargets = 1 << 5,
    Readback = 1 << 6,
    Skeletons = 1 << 7,
    Instancing = 1 << 8,
    Tonemap = 1 << 9,
    Bloom = 1 << 10,
    AutoExposure = 1 << 11,
    Ssao = 1 << 12,
    Ssil = 1 << 13,
    Ssgi = 1 << 14,
    Ssr = 1 << 15,
    Dof = 1 << 16,
}

public readonly record struct RendererCapabilities(RendererFeature Features)
{
    public bool Supports(RendererFeature feature) => (Features & feature) == feature;

    public static RendererCapabilities All { get; } = new((RendererFeature)(-1));
}

public readonly record struct ReadbackResult(
    bool Success,
    int Width,
    int Height,
    TextureFormat Format,
    ReadOnlyMemory<byte> Pixels,
    string? Detail = null);

// ── Presentation data (host/input bridge, never IRenderer) ─────────────────

public readonly record struct ViewportInfo(
    long Id,
    float X,
    float Y,
    float Width,
    float Height,
    bool HasCamera,
    Matrix4x4 View,
    Matrix4x4 Projection);

public enum WindowEventKind
{
    Resize,
    FocusGained,
    FocusLost,
    MouseMove,
    MouseButton,
    Close,
}

public readonly record struct WindowEvent(
    WindowEventKind Kind,
    int Width = 0,
    int Height = 0,
    float X = 0f,
    float Y = 0f,
    int Button = 0,
    bool Pressed = false);
