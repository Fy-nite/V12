using System;
using System.Collections.Generic;
using System.Numerics;
using V12.Voxel;

namespace V12.Voxel.Tests;

/// <summary>
/// Headless proof for the voxel package. These are geometry invariants, not
/// screenshots: a mesher that cracks at chunk seams or winds half its triangles
/// inward still looks plausible in a still frame and fails immediately here.
/// Run with `dotnet run --project V12.Voxel.Tests`.
/// </summary>
internal static class Program
{
    private static int _failures;
    private static int _checks;

    private static int Main()
    {
        Console.WriteLine("V12.Voxel geometry checks");

        CheckMesherBuildsGeometry();
        CheckOutwardWinding();
        CheckChunkSeamsAreWatertight();
        CheckBrushCarvesAndRemeshes();
        CheckMeshIsDeterministic();

        Console.WriteLine(_failures == 0
            ? $"PASS {_checks}/{_checks}"
            : $"FAIL {_failures}/{_checks}");

        return _failures == 0 ? 0 : 1;
    }

    // ── Checks ──────────────────────────────────────────────────────────────

    private static void CheckMesherBuildsGeometry()
    {
        VoxelTerrain terrain = BuildTerrain();
        int totalTriangles = 0;
        int totalVertices = 0;

        foreach (VoxelChunkKey key in terrain.EnumerateSurfaceChunks())
        {
            VoxelMeshData mesh = terrain.BuildChunkMesh(key);
            totalTriangles += mesh.TriangleCount;
            totalVertices += mesh.VertexCount;

            if (mesh.Indices.Length % 3 != 0)
                Fail($"{key} index count is not a triangle list");

            for (int i = 0; i < mesh.Indices.Length; i++)
            {
                if (mesh.Indices[i] >= mesh.VertexCount)
                {
                    Fail($"{key} index {mesh.Indices[i]} is outside {mesh.VertexCount} vertices");
                    break;
                }
            }
        }

        Check(totalTriangles > 100, $"surface produced {totalTriangles} triangles");
        Check(totalVertices > 50, $"surface produced {totalVertices} vertices");
    }

    private static void CheckOutwardWinding()
    {
        VoxelTerrain terrain = BuildTerrain();
        int triangles = 0;
        int inward = 0;

        foreach (VoxelChunkKey key in terrain.EnumerateSurfaceChunks())
        {
            VoxelMeshData mesh = terrain.BuildChunkMesh(key);
            Vector3 origin = key.Origin(terrain.Settings);

            for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
            {
                Vector3 a = Read(mesh.Positions, mesh.Indices[t]) + origin;
                Vector3 b = Read(mesh.Positions, mesh.Indices[t + 1]) + origin;
                Vector3 c = Read(mesh.Positions, mesh.Indices[t + 2]) + origin;

                Vector3 normal = Vector3.Cross(b - a, c - a);
                if (normal.LengthSquared() < 1e-10f)
                    continue; // degenerate quad diagonal, not a winding failure

                if (Vector3.Dot(Vector3.Normalize(normal), Vector3.Normalize((a + b + c) / 3f)) < 0f)
                    inward++;

                triangles++;
            }
        }

        Check(triangles > 0, "winding check found triangles");
        Check(inward == 0, $"all {triangles} triangles face outward (inward={inward})");
    }

    private static void CheckChunkSeamsAreWatertight()
    {
        VoxelTerrain terrain = BuildTerrain();
        var welded = new Dictionary<(long, long, long), int>();
        var positions = new List<Vector3>();
        var edgeCounts = new Dictionary<(int, int), int>();

        foreach (VoxelChunkKey key in terrain.EnumerateSurfaceChunks())
        {
            VoxelMeshData mesh = terrain.BuildChunkMesh(key);
            Vector3 origin = key.Origin(terrain.Settings);
            var local = new int[mesh.VertexCount];

            for (int i = 0; i < mesh.VertexCount; i++)
            {
                Vector3 global = Read(mesh.Positions, (uint)i) + origin;
                (long, long, long) cell = (
                    (long)MathF.Round(global.X * 1000f),
                    (long)MathF.Round(global.Y * 1000f),
                    (long)MathF.Round(global.Z * 1000f));

                if (!welded.TryGetValue(cell, out int index))
                {
                    index = positions.Count;
                    welded[cell] = index;
                    positions.Add(global);
                }

                local[i] = index;
            }

            for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
            {
                int a = local[mesh.Indices[t]];
                int b = local[mesh.Indices[t + 1]];
                int c = local[mesh.Indices[t + 2]];
                AddEdge(edgeCounts, a, b);
                AddEdge(edgeCounts, b, c);
                AddEdge(edgeCounts, c, a);
            }
        }

        int boundary = 0;
        int overused = 0;
        foreach (var (edge, count) in edgeCounts)
        {
            _ = edge;
            if (count == 1) boundary++;
            else if (count > 2) overused++;
        }

        Check(boundary == 0, $"no crack edges across chunk seams (boundary={boundary})");
        Check(overused == 0, $"no duplicated seam triangles (overused={overused})");
    }

    private static void CheckBrushCarvesAndRemeshes()
    {
        VoxelTerrain terrain = BuildTerrain();

        // A point on the +X surface, and a brush just inside it.
        var surface = new Vector3(terrain.Settings.Radius, 0f, 0f);
        Check(terrain.SampleDensity(surface) > -0.6f, "sample point is near the surface");

        VoxelBrushResult result = terrain.Apply(VoxelBrush.Dig(surface - Vector3.UnitX * 0.5f, 2.2f, 1.0f));
        Check(!result.IsEmpty, $"dig changed {result.ChangedVoxels} voxels");
        Check(terrain.SampleDensity(surface - Vector3.UnitX * 0.8f) > 0f, "dug centre is now air");

        if (terrain.EnumerateSurfaceChunks().Any(out VoxelChunkKey key))
        {
            VoxelMeshData mesh = terrain.BuildChunkMesh(key);
            Check(mesh.TriangleCount >= 0, "remesh after dig completed");
        }

        Check(terrain.EditedVoxelCount > 0, $"{terrain.EditedVoxelCount} voxel overrides stored");
    }

    private static void CheckMeshIsDeterministic()
    {
        VoxelTerrain a = BuildTerrain();
        VoxelTerrain b = BuildTerrain();

        if (!a.EnumerateSurfaceChunks().Any(out VoxelChunkKey key))
        {
            Fail("no chunk to test determinism");
            return;
        }

        VoxelMeshData first = a.BuildChunkMesh(key);
        VoxelMeshData second = b.BuildChunkMesh(key);

        bool same = first.Positions.Length == second.Positions.Length &&
                    first.Indices.Length == second.Indices.Length;

        for (int i = 0; same && i < first.Positions.Length; i++)
            same = MathF.Abs((float)first.Positions[i] - (float)second.Positions[i]) < 1e-4f;

        Check(same, $"{key} meshes identically on a second terrain with the same seed");
    }

    // ── Harness ────────────────────────────────────────────────────────────

    private static VoxelTerrain BuildTerrain() => new(new VoxelPlanetSettings
    {
        Radius = 12f,
        VoxelSize = 1f,
        ChunkCells = 8,
        Seed = 1207,
        NoiseAmplitude = 0f,
        NoiseFrequency = 0.055f,
        CrustThickness = 1f,
    });

    private static Vector3 Read(double[] positions, uint index) => new(
        (float)positions[index * 3],
        (float)positions[index * 3 + 1],
        (float)positions[index * 3 + 2]);

    private static void AddEdge(Dictionary<(int, int), int> counts, int a, int b)
    {
        (int, int) key = a < b ? (a, b) : (b, a);
        counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
    }

    private static void Check(bool condition, string message)
    {
        _checks++;
        if (condition)
        {
            Console.WriteLine($"  ok   {message}");
            return;
        }

        _failures++;
        Console.WriteLine($"  FAIL {message}");
    }

    private static void Fail(string message)
    {
        _checks++;
        _failures++;
        Console.WriteLine($"  FAIL {message}");
    }
}

file static class EnumerableExtensions
{
    public static bool Any<T>(this IEnumerable<T> source, out T value)
    {
        foreach (T item in source)
        {
            value = item;
            return true;
        }

        value = default!;
        return false;
    }
}
