using System;
using System.Collections.Generic;
using System.Numerics;

namespace V12.Voxel;

/// <summary>Triangle soup for one chunk, already in chunk-local metres.</summary>
public sealed class VoxelMeshData
{
    public static VoxelMeshData Empty { get; } = new(
        Array.Empty<double>(),
        Array.Empty<uint>(),
        Array.Empty<double>(),
        Array.Empty<double>());

    public VoxelMeshData(double[] positions, uint[] indices, double[] normals, double[] colors)
    {
        Positions = positions;
        Indices = indices;
        Normals = normals;
        Colors = colors;
    }

    public double[] Positions { get; }
    public uint[] Indices { get; }
    public double[] Normals { get; }
    public double[] Colors { get; }

    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// Naive surface nets mesher. One vertex per active cell, one quad per sign
/// changing grid edge, normals taken from the interpolated density gradient.
///
/// Chunks are crack-free by construction: every quad is emitted by exactly the
/// chunk that owns the grid edge's minimum cell, and vertex positions are
/// derived from global voxel coordinates so the two chunks that share a cell
/// compute bit-identical positions for it. No table of 256 cases, no seam pass.
/// </summary>
public static class VoxelSurfaceNets
{
    public static VoxelMeshData Build(VoxelTerrain terrain, VoxelChunkKey key, float[] samples)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(samples);

        VoxelPlanetSettings settings = terrain.Settings;
        int n = settings.ChunkCells;
        float h = settings.VoxelSize;
        int stride = n + 2;

        int baseX = key.X * n;
        int baseY = key.Y * n;
        int baseZ = key.Z * n;

        // The caller supplies the cached (n + 2)^3 chunk grid: n^3 owned cells
        // plus a one-cell border on the high side. Cells 0..n can be meshed;
        // only cells 0..n-1 belong to this chunk, so its quads can borrow the
        // neighbour's first cell vertex.
        float[] density = samples;

        int cellStride = n + 1;
        var cellVertex = new int[cellStride * cellStride * cellStride];
        Array.Fill(cellVertex, -1);

        var positions = new List<Vector3>((n + 1) * (n + 1) * 2);
        var normals = new List<Vector3>(positions.Capacity);
        var colors = new List<Vector3>(positions.Capacity);
        var indices = new List<uint>(n * n * 6);

        // ── Vertices: one per active cell ───────────────────────────────────
        Span<float> corner = stackalloc float[8];
        Span<Vector3> cornerOffset = stackalloc Vector3[8];

        for (int z = 0; z <= n; z++)
        for (int y = 0; y <= n; y++)
        for (int x = 0; x <= n; x++)
        {
            FillCorners(density, x, y, z, stride, corner);

            bool solid0 = corner[0] < 0f;
            bool uniform = true;
            for (int i = 1; i < 8; i++)
            {
                if ((corner[i] < 0f) != solid0)
                {
                    uniform = false;
                    break;
                }
            }
            if (uniform)
                continue;

            FillCornerOffsets(cornerOffset);

            Vector3 sum = Vector3.Zero;
            int crossings = 0;

            for (int edge = 0; edge < 12; edge++)
            {
                int a = EdgeCorners[edge * 2];
                int b = EdgeCorners[edge * 2 + 1];
                float da = corner[a];
                float db = corner[b];
                if ((da < 0f) == (db < 0f))
                    continue;

                float denominator = da - db;
                float t = MathF.Abs(denominator) > 1e-8f ? da / denominator : 0.5f;
                sum += Vector3.Lerp(cornerOffset[a], cornerOffset[b], t);
                crossings++;
            }

            if (crossings == 0)
                continue;

            Vector3 localVoxel = new(x, y, z);
            localVoxel += sum / crossings; // still in voxel units, chunk-relative

            int vertex = positions.Count;
            cellVertex[Index(x, y, z, cellStride)] = vertex;

            // Chunk-local metres for V12 (the chunk element carries the origin).
            positions.Add(localVoxel * h);

            Vector3 planetLocal = new Vector3(baseX, baseY, baseZ) + localVoxel;
            planetLocal *= h;

            normals.Add(terrain.GradientFromSamples(samples, localVoxel));
            colors.Add(terrain.SampleColor(planetLocal));
        }

        // ── Quads: one per sign-changing grid edge, owned once ──────────────
        for (int z = 0; z <= n; z++)
        for (int y = 0; y <= n; y++)
        for (int x = 0; x <= n; x++)
        {
            float d = density[Index(x, y, z, stride)];

            if (x < n && y >= 1 && z >= 1 &&
                SignDiffers(d, density[Index(x + 1, y, z, stride)]))
            {
                EmitQuad(
                    cellVertex, cellStride,
                    Cell(x, y - 1, z - 1, cellStride),
                    Cell(x, y, z - 1, cellStride),
                    Cell(x, y, z, cellStride),
                    Cell(x, y - 1, z, cellStride),
                    positions, normals, indices);
            }

            if (y < n && x >= 1 && z >= 1 &&
                SignDiffers(d, density[Index(x, y + 1, z, stride)]))
            {
                EmitQuad(
                    cellVertex, cellStride,
                    Cell(x - 1, y, z - 1, cellStride),
                    Cell(x - 1, y, z, cellStride),
                    Cell(x, y, z, cellStride),
                    Cell(x, y, z - 1, cellStride),
                    positions, normals, indices);
            }

            if (z < n && x >= 1 && y >= 1 &&
                SignDiffers(d, density[Index(x, y, z + 1, stride)]))
            {
                EmitQuad(
                    cellVertex, cellStride,
                    Cell(x - 1, y - 1, z, cellStride),
                    Cell(x, y - 1, z, cellStride),
                    Cell(x, y, z, cellStride),
                    Cell(x - 1, y, z, cellStride),
                    positions, normals, indices);
            }
        }

        return Pack(positions, normals, colors, indices);
    }

    private static void EmitQuad(
        int[] cellVertex,
        int cellStride,
        int a, int b, int c, int d,
        List<Vector3> positions,
        List<Vector3> normals,
        List<uint> indices)
    {
        int ia = cellVertex[a];
        int ib = cellVertex[b];
        int ic = cellVertex[c];
        int id = cellVertex[d];

        // A crossing edge guarantees all four cells are active, but a defensive
        // check keeps a numerically weird zero-density sample from throwing.
        if (ia < 0 || ib < 0 || ic < 0 || id < 0)
            return;

        Vector3 pa = positions[ia];
        Vector3 pb = positions[ib];
        Vector3 pc = positions[ic];

        // Winding is derived from the vertex normals the field produced, so a
        // dug wall and a raised mound are both outward-facing without a second
        // orientation rule per brush mode.
        Vector3 geometric = Vector3.Cross(pb - pa, pc - pa);
        Vector3 expected = normals[ia] + normals[ib] + normals[ic] + normals[id];
        if (Vector3.Dot(geometric, expected) < 0f)
        {
            indices.Add((uint)ia); indices.Add((uint)ic); indices.Add((uint)ib);
            indices.Add((uint)ia); indices.Add((uint)id); indices.Add((uint)ic);
        }
        else
        {
            indices.Add((uint)ia); indices.Add((uint)ib); indices.Add((uint)ic);
            indices.Add((uint)ia); indices.Add((uint)ic); indices.Add((uint)id);
        }
    }

    private static VoxelMeshData Pack(
        List<Vector3> positions,
        List<Vector3> normals,
        List<Vector3> colors,
        List<uint> indices)
    {
        if (positions.Count == 0 || indices.Count == 0)
            return VoxelMeshData.Empty;

        int count = positions.Count;
        var outPositions = new double[count * 3];
        var outNormals = new double[count * 3];
        var outColors = new double[count * 4];

        for (int i = 0; i < count; i++)
        {
            Vector3 p = positions[i];
            Vector3 n = normals[i];
            Vector3 c = colors[i];

            outPositions[i * 3] = p.X;
            outPositions[i * 3 + 1] = p.Y;
            outPositions[i * 3 + 2] = p.Z;

            outNormals[i * 3] = n.X;
            outNormals[i * 3 + 1] = n.Y;
            outNormals[i * 3 + 2] = n.Z;

            outColors[i * 4] = c.X;
            outColors[i * 4 + 1] = c.Y;
            outColors[i * 4 + 2] = c.Z;
            outColors[i * 4 + 3] = 1f;
        }

        return new VoxelMeshData(outPositions, indices.ToArray(), outNormals, outColors);
    }

    private static void FillCorners(float[] density, int x, int y, int z, int stride, Span<float> corner)
    {
        corner[0] = density[Index(x, y, z, stride)];
        corner[1] = density[Index(x + 1, y, z, stride)];
        corner[2] = density[Index(x, y + 1, z, stride)];
        corner[3] = density[Index(x + 1, y + 1, z, stride)];
        corner[4] = density[Index(x, y, z + 1, stride)];
        corner[5] = density[Index(x + 1, y, z + 1, stride)];
        corner[6] = density[Index(x, y + 1, z + 1, stride)];
        corner[7] = density[Index(x + 1, y + 1, z + 1, stride)];
    }

    private static void FillCornerOffsets(Span<Vector3> offsets)
    {
        offsets[0] = new Vector3(0f, 0f, 0f);
        offsets[1] = new Vector3(1f, 0f, 0f);
        offsets[2] = new Vector3(0f, 1f, 0f);
        offsets[3] = new Vector3(1f, 1f, 0f);
        offsets[4] = new Vector3(0f, 0f, 1f);
        offsets[5] = new Vector3(1f, 0f, 1f);
        offsets[6] = new Vector3(0f, 1f, 1f);
        offsets[7] = new Vector3(1f, 1f, 1f);
    }

    private static bool SignDiffers(float a, float b) => (a < 0f) != (b < 0f);

    private static int Index(int x, int y, int z, int stride) => x + y * stride + z * stride * stride;

    private static int Cell(int x, int y, int z, int stride) => x + y * stride + z * stride * stride;

    /// <summary>Corner pairs for the twelve cube edges, in the corner order above.</summary>
    private static readonly int[] EdgeCorners =
    {
        0, 1, 2, 3, 4, 5, 6, 7, // +X edges (y/z fixed)
        0, 2, 1, 3, 4, 6, 5, 7, // +Y edges (x/z fixed)
        0, 4, 1, 5, 2, 6, 3, 7, // +Z edges (x/y fixed)
    };
}
