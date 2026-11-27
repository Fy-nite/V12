using System;
using System.Numerics;

namespace V12.Voxel;

/// <summary>What a brush does to the density field.</summary>
public enum VoxelBrushMode
{
    /// <summary>Add density, carving air into the planet.</summary>
    Dig,
    /// <summary>Remove density, adding solid ground.</summary>
    Fill,
    /// <summary>Blend each sample toward its neighbourhood average.</summary>
    Smooth,
}

/// <summary>
/// One spherical brush application, in planet-local space. Strength is in
/// metres of density, so a strength of <see cref="VoxelPlanetSettings.VoxelSize"/>
/// moves the surface a full voxel at the brush centre in one application.
/// </summary>
public readonly record struct VoxelBrush(
    Vector3 Center,
    float Radius,
    float Strength,
    VoxelBrushMode Mode)
{
    public static VoxelBrush Dig(Vector3 center, float radius, float strength) =>
        new(center, radius, strength, VoxelBrushMode.Dig);

    public static VoxelBrush Fill(Vector3 center, float radius, float strength) =>
        new(center, radius, strength, VoxelBrushMode.Fill);

    public static VoxelBrush Smooth(Vector3 center, float radius, float strength) =>
        new(center, radius, strength, VoxelBrushMode.Smooth);
}

/// <summary>
/// Summary of one applied brush: how many voxel samples actually changed and
/// which chunks need remeshing. The chunk list is the region the brush could
/// have touched, including the one-cell sampler overlap, so a caller can remesh
/// every dirty chunk without tracking individual voxels.
/// </summary>
public readonly record struct VoxelBrushResult(
    int ChangedVoxels,
    int MinChunkX,
    int MinChunkY,
    int MinChunkZ,
    int MaxChunkX,
    int MaxChunkY,
    int MaxChunkZ)
{
    public bool IsEmpty => ChangedVoxels == 0;

    /// <summary>Enumerates every chunk key in the affected region.</summary>
    public void ForEachChunk(Action<VoxelChunkKey> visit)
    {
        for (int z = MinChunkZ; z <= MaxChunkZ; z++)
        for (int y = MinChunkY; y <= MaxChunkY; y++)
        for (int x = MinChunkX; x <= MaxChunkX; x++)
            visit(new VoxelChunkKey(x, y, z));
    }
}

/// <summary>One surface hit against the voxel field, in planet-local space.</summary>
public readonly record struct VoxelHit(Vector3 Point, Vector3 Normal, float Distance);
