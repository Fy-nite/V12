using System;
using System.Numerics;

namespace V12.Voxel;

/// <summary>
/// Authoring settings for one <see cref="Components.VoxelPlanetComponent"/>.
/// Everything here is in planet-local metres. The defaults describe a small,
/// walkable moon: 56 m across, one-metre voxels, eight-cell chunks.
/// </summary>
public sealed class VoxelPlanetSettings
{
    /// <summary>Nominal surface radius in metres.</summary>
    public float Radius { get; set; } = 28f;

    /// <summary>Edge length of one density sample cell, in metres.</summary>
    public float VoxelSize { get; set; } = 1f;

    /// <summary>Cells per chunk edge. Chunk world size is VoxelSize * ChunkCells.</summary>
    public int ChunkCells { get; set; } = 8;

    /// <summary>Deterministic noise seed. Same seed, same planet.</summary>
    public int Seed { get; set; } = 1207;

    /// <summary>Peak radial displacement of the base surface, in metres.</summary>
    public float NoiseAmplitude { get; set; } = 2.9f;

    /// <summary>Base noise frequency in cycles per metre (larger = smaller landforms).</summary>
    public float NoiseFrequency { get; set; } = 0.075f;

    /// <summary>Value-noise octaves used for the base surface.</summary>
    public int NoiseOctaves { get; set; } = 2;

    /// <summary>
    /// Extra metres below the deepest possible surface that are created eagerly
    /// at startup. Chunks outside this band are created lazily when a brush
    /// reaches them, so this only affects the initial build and the first dig.
    /// </summary>
    public float CrustThickness { get; set; } = 4f;

    /// <summary>Depth bands and colours painted onto every vertex.</summary>
    public VoxelPalette Palette { get; set; } = VoxelPalette.Default;

    /// <summary>World size of one chunk edge, in metres.</summary>
    public float ChunkSize => VoxelSize * ChunkCells;

    /// <summary>Clamp values into a range the mesher and brush can trust.</summary>
    public void Validate()
    {
        Radius = MathF.Max(2f, Radius);
        VoxelSize = Math.Clamp(VoxelSize, 0.1f, 8f);
        ChunkCells = Math.Clamp(ChunkCells, 4, 24);
        NoiseAmplitude = Math.Clamp(NoiseAmplitude, 0f, Radius * 0.4f);
        NoiseFrequency = MathF.Max(0.001f, NoiseFrequency);
        NoiseOctaves = Math.Clamp(NoiseOctaves, 1, 5);
        CrustThickness = Math.Clamp(CrustThickness, 0f, Radius);
    }

    /// <summary>Outer radius the base field can reach, including noise.</summary>
    public float MaxRadius => Radius + NoiseAmplitude;

    /// <summary>Inner radius the eagerly built crust reaches.</summary>
    public float MinCrustRadius => MathF.Max(0f, Radius - NoiseAmplitude - CrustThickness);

    public VoxelPlanetSettings Clone() => new()
    {
        Radius = Radius,
        VoxelSize = VoxelSize,
        ChunkCells = ChunkCells,
        Seed = Seed,
        NoiseAmplitude = NoiseAmplitude,
        NoiseFrequency = NoiseFrequency,
        NoiseOctaves = NoiseOctaves,
        CrustThickness = CrustThickness,
        Palette = Palette,
    };
}

/// <summary>One colour band, measured in metres below the nominal surface.</summary>
public readonly record struct VoxelLayer(float Depth, Vector3 Color);

/// <summary>
/// Depth-ordered strata used for per-vertex terrain colour. Digging exposes
/// lower bands automatically because colour is chosen from the vertex's depth,
/// which is what gives a hole its layered walls.
/// </summary>
public sealed class VoxelPalette
{
    private readonly VoxelLayer[] _layers;

    public VoxelPalette(params VoxelLayer[] layers)
    {
        if (layers is null || layers.Length == 0)
            throw new ArgumentException("A palette needs at least one layer.", nameof(layers));

        _layers = (VoxelLayer[])layers.Clone();
        Array.Sort(_layers, static (a, b) => a.Depth.CompareTo(b.Depth));
    }

    public IReadOnlyList<VoxelLayer> Layers => _layers;

    /// <summary>Colour at a depth below the nominal surface, in metres.</summary>
    public Vector3 ColorAt(float depth)
    {
        Vector3 color = _layers[0].Color;
        for (int i = 0; i < _layers.Length; i++)
        {
            if (_layers[i].Depth > depth)
                break;
            color = _layers[i].Color;
        }
        return color;
    }

    /// <summary>
    /// Moss, soil, ochre clay, grey rock, dark basalt. Deliberately desaturated
    /// and tonal, so the planet reads as one material rather than a paint set.
    /// </summary>
    public static VoxelPalette Default { get; } = new(
        new VoxelLayer(0f, new Vector3(0.110f, 0.170f, 0.075f)),
        new VoxelLayer(0.7f, new Vector3(0.170f, 0.110f, 0.065f)),
        new VoxelLayer(2.2f, new Vector3(0.300f, 0.150f, 0.070f)),
        new VoxelLayer(6.0f, new Vector3(0.140f, 0.130f, 0.125f)),
        new VoxelLayer(12.0f, new Vector3(0.065f, 0.070f, 0.080f)));
}
