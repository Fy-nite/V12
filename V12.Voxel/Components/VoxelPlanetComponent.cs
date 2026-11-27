using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;
using V12.Core.UI;
namespace V12.Voxel.Components;

/// <summary>
/// A deformable voxel planet. Attach it to an element; the component builds a
/// sparse set of chunk elements around the base surface, meshes each one, and
/// exposes raycast plus brush operations in world space.
///
/// Everything is planet-local under the owning element, so the planet can be
/// moved or rotated as one object and the voxel data never needs to know. Brush
/// edits are stored as voxel overrides, and only the chunks a brush touched are
/// remeshed.
///
/// The component is deliberately not persistent yet: terrain is rebuilt from
/// its settings on load and edits live for the session. Replication should send
/// brush operations, not vertices, which is a game-level decision rather than a
/// property of the field.
/// </summary>
[ComponentEntry("VOXEL", "Voxel Planet", SortOrder = 10, Icon = "globe")]
public sealed class VoxelPlanetComponent : ComponentBase
{
    private const int LazyChunksPerTick = 8;

    private GameRoot? _root;
    private VoxelTerrain? _terrain;
    private bool _built;
    private bool _buildFailed;

    private readonly Dictionary<VoxelChunkKey, IWorldElement> _elements = new();
    private readonly Dictionary<VoxelChunkKey, VoxelChunkMesh> _meshes = new();
    private readonly HashSet<VoxelChunkKey> _pending = new();
    private readonly HashSet<VoxelChunkKey> _dirty = new();

    private Vector3 _lastBrushLocal;
    private int _remeshBatches;
    private double _lastRemeshMilliseconds;

    /// <summary>Nominal surface radius in planet-local metres.</summary>
    public float Radius { get; set; } = 28f;

    /// <summary>Voxel sample spacing in metres.</summary>
    public float VoxelSize { get; set; } = 1f;

    /// <summary>Cells per chunk edge. Chunk world size is VoxelSize * ChunkCells.</summary>
    public int ChunkCells { get; set; } = 8;

    /// <summary>Deterministic base-terrain seed.</summary>
    public int Seed { get; set; } = 1207;

    /// <summary>Peak radial displacement of the base surface, in metres.</summary>
    public float NoiseAmplitude { get; set; } = 2.9f;

    /// <summary>Base noise frequency in cycles per metre.</summary>
    public float NoiseFrequency { get; set; } = 0.075f;

    /// <summary>Metres of crust built eagerly below the deepest surface.</summary>
    public float CrustThickness { get; set; } = 4f;

    /// <summary>Live planet data. Null until the first fixed step after attach.</summary>
    public VoxelTerrain? Terrain => _terrain;

    public bool IsBuilt => _built;
    public int ChunkCount => _meshes.Count;
    public int EditedVoxelCount => _terrain?.EditedVoxelCount ?? 0;

    /// <summary>Chunks waiting for their budgeted remesh.</summary>
    public int PendingRemeshCount => _dirty.Count;

    /// <summary>Wall time of the most recent remesh batch. Diagnostics only.</summary>
    public double LastRemeshMilliseconds => _lastRemeshMilliseconds;

    /// <summary>Raised after a brush changed the field. Chunks are already dirty.</summary>
    public event Action<VoxelBrushResult>? BrushApplied;

    public override string Description => "Deformable signed-distance voxel planet";

    // ── Lifecycle ───────────────────────────────────────────────────────────

    public override void OnAttach(IWorldElement worldElement)
    {
        base.OnAttach(worldElement);

        _root = GameRoot.Instance;
        if (_root != null)
            _root.OnFixedUpdate += Pump;
    }

    public override void OnDetach(IWorldElement worldElement)
    {
        if (_root != null)
            _root.OnFixedUpdate -= Pump;

        _root = null;
        _elements.Clear();
        _meshes.Clear();
        _pending.Clear();
        _dirty.Clear();
        _terrain = null;
        _built = false;
        base.OnDetach(worldElement);
    }

    public override void BuildInspector(IInspector inspector)
    {
        inspector.Section("Voxel Planet");
        inspector.Float("Radius", () => Radius, v => Radius = v);
        inspector.Float("Voxel Size", () => VoxelSize, v => VoxelSize = v);
        inspector.Int("Chunk Cells", () => ChunkCells, v => ChunkCells = v);
        inspector.Int("Seed", () => Seed, v => Seed = v);
        inspector.Float("Noise Amplitude", () => NoiseAmplitude, v => NoiseAmplitude = v);
        inspector.Float("Noise Frequency", () => NoiseFrequency, v => NoiseFrequency = v);
        inspector.Float("Crust Thickness", () => CrustThickness, v => CrustThickness = v);

        inspector.Separator();
        inspector.ReadOnly("Chunks", ChunkCount);
        inspector.ReadOnly("Edited Voxels", EditedVoxelCount);
        inspector.ReadOnly("Pending Remesh", PendingRemeshCount);
        inspector.ReadOnly("Last Remesh (ms)", _lastRemeshMilliseconds);
        inspector.ReadOnly("Built", _built);
    }

    public override IWorldElement BuildUI() => new Element();

    // ── Build and upkeep ────────────────────────────────────────────────────

    private void Pump(float deltaTime)
    {
        if (!_built)
        {
            Build();
            return;
        }

        if (_pending.Count > 0)
            CreatePendingChunks();

        RebuildDirty(unlimited: false);
    }

    private void CreatePendingChunks()
    {
        // Lazy chunks appear a few per tick so a deep explosion cannot stall a
        // frame creating a hundred elements at once.
        int created = 0;
        foreach (VoxelChunkKey key in new List<VoxelChunkKey>(_pending))
        {
            _pending.Remove(key);
            CreateChunk(key);

            if (++created >= LazyChunksPerTick)
                break;
        }
    }

    private void Build()
    {
        if (_buildFailed || Owner == null)
            return;

        try
        {
            long started = Stopwatch.GetTimestamp();

            _terrain = new VoxelTerrain(BuildSettings());

            foreach (VoxelChunkKey key in _terrain.EnumerateSurfaceChunks())
                CreateChunk(key);

            // Startup is the one place a synchronous remesh is right: the planet
            // should be complete before the first frame that shows it.
            RebuildDirty(unlimited: true);

            _built = true;

            double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            GameRoot.Log?.Information(
                "[Voxel] Planet built: {Chunks} chunks, {Vertices} voxel samples edited, {Ms:F1} ms",
                _meshes.Count, _terrain.EditedVoxelCount, milliseconds);
        }
        catch (Exception exception)
        {
            _buildFailed = true;
            GameRoot.Log?.Error("[Voxel] Planet build failed: {Message}", exception.Message);
        }
    }

    private VoxelPlanetSettings BuildSettings()
    {
        return new VoxelPlanetSettings
        {
            Radius = Radius,
            VoxelSize = VoxelSize,
            ChunkCells = ChunkCells,
            Seed = Seed,
            NoiseAmplitude = NoiseAmplitude,
            NoiseFrequency = NoiseFrequency,
            CrustThickness = CrustThickness,
            Palette = VoxelPalette.Default,
        };
    }

    private void CreateChunk(VoxelChunkKey key)
    {
        if (Owner == null || _terrain == null || _elements.ContainsKey(key))
            return;

        var element = new Element($"VoxelChunk_{key.X}_{key.Y}_{key.Z}");
        element.LocalTransform = new TRS(key.Origin(_terrain.Settings));

        var mesh = new VoxelChunkMesh(this, key);
        element.AddComponent(mesh);
        element.AddComponent(new MaterialComponent
        {
            Metallic = 0f,
            Roughness = 0.96f,
            DoubleSided = false,
        });

        Owner.AddChild(element);

        _elements[key] = element;
        _meshes[key] = mesh;
        _dirty.Add(key);
    }

    /// <summary>
    /// Rebuilds dirty chunk meshes nearest the last brush first, under a time
    /// budget, so editing keeps the surface under the cursor current without
    /// turning a brush pass into a frame-long stall.
    /// </summary>
    private void RebuildDirty(bool unlimited)
    {
        if (_dirty.Count == 0)
            return;

        long started = Stopwatch.GetTimestamp();
        const double BudgetMilliseconds = 4.0;

        int rebuilt = 0;
        while (_dirty.Count > 0)
        {
            VoxelChunkKey key = NearestDirtyChunk();
            _dirty.Remove(key);

            if (!_meshes.TryGetValue(key, out VoxelChunkMesh? mesh))
                continue;

            mesh.Rebuild();
            rebuilt++;

            if (!unlimited &&
                Stopwatch.GetElapsedTime(started).TotalMilliseconds >= BudgetMilliseconds)
            {
                break;
            }
        }

        if (rebuilt == 0)
            return;

        _lastRemeshMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _remeshBatches++;

        if (unlimited || _remeshBatches == 1 || _remeshBatches % 20 == 0)
        {
            GameRoot.Log?.Information(
                "[Voxel] remesh {Chunks} chunk(s) in {Ms:F2} ms, {Pending} still queued",
                rebuilt, _lastRemeshMilliseconds, _dirty.Count);
        }
    }

    private VoxelChunkKey NearestDirtyChunk()
    {
        float chunkSize = _terrain?.Settings.ChunkSize ?? 1f;
        VoxelChunkKey best = default;
        float bestDistance = float.MaxValue;

        foreach (VoxelChunkKey key in _dirty)
        {
            float distance = Vector3.DistanceSquared(
                new Vector3(key.X * chunkSize, key.Y * chunkSize, key.Z * chunkSize),
                _lastBrushLocal);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = key;
            }
        }

        return best;
    }

    // ── Queries ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Casts a world ray against the edited field. Returns false before the
    /// planet has built or when the ray misses every surface.
    /// </summary>
    public bool TryRaycast(
        Vector3 worldOrigin,
        Vector3 worldDirection,
        out VoxelHit hit,
        float maxDistance = 300f)
    {
        hit = default;

        if (!TryToLocalRay(worldOrigin, worldDirection, maxDistance,
                out Vector3 localOrigin, out Vector3 localDirection, out float localDistance,
                out Matrix4x4 world, out float inverseScale))
        {
            return false;
        }

        if (!_terrain!.Raycast(localOrigin, localDirection, localDistance, out VoxelHit localHit))
            return false;

        Vector3 worldPoint = Vector3.Transform(localHit.Point, world);
        Vector3 worldNormal = Vector3.TransformNormal(localHit.Normal, world);

        hit = new VoxelHit(
            worldPoint,
            worldNormal.LengthSquared() > 1e-8f ? Vector3.Normalize(worldNormal) : Vector3.UnitY,
            Vector3.Distance(worldOrigin, worldPoint));

        _ = inverseScale;
        return true;
    }

    /// <summary>
    /// Rays from a world position and applies one brush where the ray hits.
    /// The brush is nudged into the surface for Dig and just outside it for Fill,
    /// which is what makes a held tool carve instead of shaving the skin.
    /// </summary>
    public bool TryUseTool(
        Vector3 worldOrigin,
        Vector3 worldDirection,
        float worldRadius,
        VoxelBrushMode mode,
        float worldStrength,
        out VoxelHit hit)
    {
        hit = default;

        if (!TryToLocalRay(worldOrigin, worldDirection, 300f,
                out Vector3 localOrigin, out Vector3 localDirection, out float localDistance,
                out Matrix4x4 world, out float inverseScale))
        {
            return false;
        }

        if (!_terrain!.Raycast(localOrigin, localDirection, localDistance, out VoxelHit localHit))
            return false;

        float radius = MathF.Max(_terrain.Settings.VoxelSize * 0.5f, worldRadius * inverseScale);
        float strength = worldStrength * inverseScale;

        float nudge = mode switch
        {
            VoxelBrushMode.Fill => radius * 0.45f,
            VoxelBrushMode.Dig => -radius * 0.35f,
            _ => 0f,
        };

        Vector3 center = localHit.Point + localHit.Normal * nudge;
        ApplyLocalBrush(new VoxelBrush(center, radius, strength, mode));

        Vector3 worldPoint = Vector3.Transform(localHit.Point, world);
        Vector3 worldNormal = Vector3.TransformNormal(localHit.Normal, world);

        hit = new VoxelHit(
            worldPoint,
            worldNormal.LengthSquared() > 1e-8f ? Vector3.Normalize(worldNormal) : Vector3.UnitY,
            Vector3.Distance(worldOrigin, worldPoint));

        return true;
    }

    /// <summary>
    /// Applies a brush at a world position. Use this for explosions, scripted
    /// cratering, or replaying a replicated edit log; no raycast is involved.
    /// </summary>
    public VoxelBrushResult ApplyWorldBrush(
        Vector3 worldCenter,
        float worldRadius,
        float worldStrength,
        VoxelBrushMode mode)
    {
        if (_terrain == null || Owner == null)
            return default;

        Matrix4x4 world = Owner.WorldTransform;
        if (!Matrix4x4.Invert(world, out Matrix4x4 inverse))
            return default;

        float scale = Vector3.TransformNormal(Vector3.UnitX, world).Length();
        scale = MathF.Max(scale, 1e-4f);

        Vector3 localCenter = Vector3.Transform(worldCenter, inverse);
        float radius = MathF.Max(_terrain.Settings.VoxelSize * 0.5f, worldRadius / scale);
        float strength = worldStrength / scale;

        return ApplyLocalBrush(new VoxelBrush(localCenter, radius, strength, mode));
    }

    /// <summary>Applies a planet-local brush. Public for server-side simulation.</summary>
    public VoxelBrushResult ApplyLocalBrush(VoxelBrush brush)
    {
        if (_terrain == null)
            return default;

        VoxelBrushResult result = _terrain.Apply(brush);
        if (result.IsEmpty)
            return result;

        _lastBrushLocal = brush.Center;

        result.ForEachChunk(key =>
        {
            if (_meshes.TryGetValue(key, out VoxelChunkMesh? mesh))
            {
                mesh.RequestRebuild();
                _dirty.Add(key);
            }
            else
            {
                _pending.Add(key);
            }
        });

        BrushApplied?.Invoke(result);
        return result;
    }

    private bool TryToLocalRay(
        Vector3 worldOrigin,
        Vector3 worldDirection,
        float maxDistance,
        out Vector3 localOrigin,
        out Vector3 localDirection,
        out float localDistance,
        out Matrix4x4 world,
        out float inverseScale)
    {
        localOrigin = default;
        localDirection = default;
        localDistance = 0f;
        world = Owner?.WorldTransform ?? Matrix4x4.Identity;
        inverseScale = 1f;

        if (_terrain is null || Owner is null)
            return false;

        if (!Matrix4x4.Invert(world, out Matrix4x4 inverse))
            return false;

        float scale = Vector3.TransformNormal(Vector3.UnitX, world).Length();
        scale = MathF.Max(scale, 1e-4f);
        inverseScale = 1f / scale;

        localOrigin = Vector3.Transform(worldOrigin, inverse);
        localDirection = Vector3.TransformNormal(worldDirection, inverse);
        float directionLength = localDirection.Length();
        if (directionLength < 1e-6f)
            return false;

        localDirection /= directionLength;
        localDistance = maxDistance * inverseScale;
        return true;
    }
}
