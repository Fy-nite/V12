using System;
using System.Numerics;

namespace V12.Voxel;

/// <summary>
/// Integer address of one chunk in a planet-local voxel grid. Chunk (0,0,0)
/// starts at the planet origin and extends one chunk size along +X, +Y and +Z.
/// </summary>
public readonly record struct VoxelChunkKey(int X, int Y, int Z)
{
    public Vector3 Origin(VoxelPlanetSettings settings) =>
        new(X * settings.ChunkSize, Y * settings.ChunkSize, Z * settings.ChunkSize);

    public override string ToString() => $"chunk({X},{Y},{Z})";
}

/// <summary>
/// Packs a voxel coordinate into one long. Coordinates are biased so negative
/// space packs too, and each axis gets 21 bits (+-1,048,576 voxels), which is
/// far past any planet this package is meant for.
/// </summary>
internal static class VoxelKeyPacker
{
    private const int Bias = 1 << 20;
    private const int Mask = (1 << 21) - 1;

    public static long Pack(int x, int y, int z)
    {
        long bx = (uint)(x + Bias) & Mask;
        long by = (uint)(y + Bias) & Mask;
        long bz = (uint)(z + Bias) & Mask;
        return (bx << 42) | (by << 21) | bz;
    }

    public static (int X, int Y, int Z) Unpack(long key)
    {
        int x = (int)((key >> 42) & Mask) - Bias;
        int y = (int)((key >> 21) & Mask) - Bias;
        int z = (int)(key & Mask) - Bias;
        return (x, y, z);
    }
}
