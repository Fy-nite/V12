using System;
using System.Numerics;
using Assimp;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;

namespace V12.Voxel.Components;

/// <summary>
/// One chunk of a <see cref="VoxelPlanetComponent"/>, rendered through the
/// ordinary <see cref="IMeshRenderable"/> contract. Vertices are chunk-local
/// metres and the chunk element carries the chunk origin, which keeps the
/// vertex data small and the mesh re-upload cheap after a brush pass.
///
/// The component rebuilds lazily: a brush only nulls the cache and marks the
/// element dirty, and the next frame capture asks for the arrays. That keeps
/// remeshing on the game thread where world state is safe to read.
/// </summary>
public sealed class VoxelChunkMesh : ComponentBase, IMeshRenderable
{
    private VoxelPlanetComponent? _planet;
    private VoxelChunkKey _key;
    private VoxelMeshData _data = VoxelMeshData.Empty;
    private bool _needsRebuild = true;

    /// <summary>WorldML/Activator-friendly constructor. Bind before attaching.</summary>
    public VoxelChunkMesh() { }

    internal VoxelChunkMesh(VoxelPlanetComponent planet, VoxelChunkKey key)
    {
        _planet = planet ?? throw new ArgumentNullException(nameof(planet));
        _key = key;
        Name = $"Voxel {key.X}_{key.Y}_{key.Z}";
    }

    public override string Description => "Deformable terrain chunk";

    public VoxelChunkKey Key => _key;

    /// <summary>True until the next <see cref="Rebuild"/> produces fresh geometry.</summary>
    public bool NeedsRebuild => _needsRebuild;

    /// <summary>
    /// Queues a remesh but keeps the previous mesh on screen. The planet drains
    /// its queue under a per-tick budget, so a brush never stalls a frame and a
    /// chunk never flickers empty while it waits its turn.
    /// </summary>
    public void RequestRebuild() => _needsRebuild = true;

    /// <summary>Rebuilds now and swaps the arrays the renderer reads.</summary>
    public bool Rebuild()
    {
        if (!_needsRebuild)
            return false;

        _data = Build();
        _needsRebuild = false;
        MarkDirty();
        return true;
    }

    /// <summary>Current chunk geometry, or an empty mesh before the first rebuild.</summary>
    public VoxelMeshData Data => _data;

    private VoxelMeshData Build()
    {
        VoxelTerrain? terrain = _planet?.Terrain;
        return terrain is null ? VoxelMeshData.Empty : terrain.BuildChunkMesh(_key);
    }

    // ── IMeshRenderable ─────────────────────────────────────────────────────

    public RenderType RenderType => RenderType.Mesh;

    public double[] MeshPoints => Data.Positions;
    public uint[] Indices => Data.Indices;
    public double[]? MeshNormals => Data.Normals;
    public double[]? MeshColors => Data.Colors;

    /// <summary>The surface comes from the sibling MaterialComponent.</summary>
    public Material Material => null!;

    public bool IsWorldLocked => true;

    public TRS LocalTransform => default;

    public Matrix4x4 WorldTransform => Owner?.WorldTransform ?? Matrix4x4.Identity;

    public override IWorldElement BuildUI() => new Element();

    public override string ToString() =>
        $"VoxelChunk {_key} ({Data.VertexCount} verts, {Data.TriangleCount} tris)";
}
