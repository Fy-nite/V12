using System;
using System.Collections.Generic;
using System.Numerics;

namespace V12.Voxel;

/// <summary>
/// The editable planet data. A base sphere field plus a sparse override map for
/// every voxel a brush has touched. Only edited voxels cost memory, and only
/// chunks whose sample window overlaps an edit are remeshed.
///
/// This type has no ECS or renderer dependency: it is plain data and maths, so a
/// host can simulate a planet on a server with no renderer at all.
/// </summary>
public sealed class VoxelTerrain
{
    private readonly VoxelField _field;
    private readonly Dictionary<long, float> _overrides = new();
    private readonly Dictionary<VoxelChunkKey, float[]> _chunkSamples = new();
    private readonly float _maxEditDensity;

    public VoxelTerrain(VoxelPlanetSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Settings.Validate();
        _field = new VoxelField(Settings);
        _maxEditDensity = Settings.VoxelSize * 4f;
    }

    public VoxelPlanetSettings Settings { get; }
    public VoxelField Field => _field;

    /// <summary>Number of voxel samples a brush has written. Diagnostics only.</summary>
    public int EditedVoxelCount => _overrides.Count;

    public Vector3 SampleColor(Vector3 planetLocal)
    {
        // Depth is measured against the noisy base surface, not the nominal
        // radius: a pristine dip keeps its topsoil, and only material the player
        // actually cut or raised exposes a lower band.
        float baseDistance = _field.SampleBase(planetLocal);
        float depth = MathF.Max(0f, -baseDistance - Settings.VoxelSize * 0.9f);
        Vector3 color = Settings.Palette.ColorAt(depth);

        // A little deterministic grain keeps large walls from reading as flat
        // paint. Quantised position means neighbouring vertices share a value
        // instead of shimmering per triangle.
        float grain = (HashColor(planetLocal) - 0.5f) * 0.10f;
        color *= 1f + grain;

        return Vector3.Clamp(color, Vector3.Zero, Vector3.One);
    }

    // ── Sampling ────────────────────────────────────────────────────────────

    /// <summary>Density at an exact voxel sample, including brush overrides.</summary>
    public float SampleVoxel(int x, int y, int z)
    {
        long key = VoxelKeyPacker.Pack(x, y, z);
        if (_overrides.TryGetValue(key, out float edited))
            return edited;

        return _field.SampleBase(new Vector3(x, y, z) * Settings.VoxelSize);
    }

    /// <summary>
    /// Trilinearly interpolated density at any point. This is the field the
    /// raycast, the mesher's normals and any collision code should agree on.
    /// </summary>
    public float SampleDensity(Vector3 planetLocal)
    {
        float h = Settings.VoxelSize;
        float fx = planetLocal.X / h;
        float fy = planetLocal.Y / h;
        float fz = planetLocal.Z / h;

        int x0 = (int)MathF.Floor(fx);
        int y0 = (int)MathF.Floor(fy);
        int z0 = (int)MathF.Floor(fz);

        float tx = fx - x0;
        float ty = fy - y0;
        float tz = fz - z0;

        float c000 = SampleVoxel(x0, y0, z0);
        float c100 = SampleVoxel(x0 + 1, y0, z0);
        float c010 = SampleVoxel(x0, y0 + 1, z0);
        float c110 = SampleVoxel(x0 + 1, y0 + 1, z0);
        float c001 = SampleVoxel(x0, y0, z0 + 1);
        float c101 = SampleVoxel(x0 + 1, y0, z0 + 1);
        float c011 = SampleVoxel(x0, y0 + 1, z0 + 1);
        float c111 = SampleVoxel(x0 + 1, y0 + 1, z0 + 1);

        float x00 = Lerp(c000, c100, tx);
        float x10 = Lerp(c010, c110, tx);
        float x01 = Lerp(c001, c101, tx);
        float x11 = Lerp(c011, c111, tx);

        return Lerp(Lerp(x00, x10, ty), Lerp(x01, x11, ty), tz);
    }

    /// <summary>Outward surface normal from the interpolated density field.</summary>
    public Vector3 SampleGradient(Vector3 planetLocal, float epsilon)
    {
        if (epsilon <= 0f)
            epsilon = Settings.VoxelSize * 0.5f;

        float dx = SampleDensity(planetLocal + new Vector3(epsilon, 0f, 0f)) -
                   SampleDensity(planetLocal - new Vector3(epsilon, 0f, 0f));
        float dy = SampleDensity(planetLocal + new Vector3(0f, epsilon, 0f)) -
                   SampleDensity(planetLocal - new Vector3(0f, epsilon, 0f));
        float dz = SampleDensity(planetLocal + new Vector3(0f, 0f, epsilon)) -
                   SampleDensity(planetLocal - new Vector3(0f, 0f, epsilon));

        var gradient = new Vector3(dx, dy, dz);
        return gradient.LengthSquared() > 1e-12f
            ? Vector3.Normalize(gradient)
            : Vector3.UnitY;
    }

    // ── Meshing ─────────────────────────────────────────────────────────────

    /// <summary>Meshes one chunk into chunk-local metre coordinates.</summary>
    public VoxelMeshData BuildChunkMesh(VoxelChunkKey key) =>
        VoxelSurfaceNets.Build(this, key, GetChunkSamples(key));

    /// <summary>
    /// Cached (n + 2)^3 density grid for one chunk. Caching is what keeps a
    /// brush pass cheap: after the first mesh, an edit only rewrites the few
    /// samples the brush touched instead of re-running noise for the whole box.
    /// </summary>
    public float[] GetChunkSamples(VoxelChunkKey key)
    {
        int n = Settings.ChunkCells;
        int stride = n + 2;

        if (_chunkSamples.TryGetValue(key, out float[]? cached))
            return cached;

        var samples = new float[stride * stride * stride];
        int baseX = key.X * n;
        int baseY = key.Y * n;
        int baseZ = key.Z * n;

        for (int z = 0; z < stride; z++)
        for (int y = 0; y < stride; y++)
        for (int x = 0; x < stride; x++)
            samples[x + y * stride + z * stride * stride] =
                SampleVoxel(baseX + x, baseY + y, baseZ + z);

        _chunkSamples[key] = samples;
        return samples;
    }

    /// <summary>
    /// Gradient of the cached chunk grid, in chunk-local voxel coordinates.
    /// Reads the array directly instead of the field, so it is allocation-free
    /// and does no noise work.
    /// </summary>
    public Vector3 GradientFromSamples(float[] samples, Vector3 localVoxel)
    {
        int stride = Settings.ChunkCells + 2;
        const float epsilon = 0.5f;

        float dx = SampleGrid(samples, stride, localVoxel + new Vector3(epsilon, 0f, 0f)) -
                   SampleGrid(samples, stride, localVoxel - new Vector3(epsilon, 0f, 0f));
        float dy = SampleGrid(samples, stride, localVoxel + new Vector3(0f, epsilon, 0f)) -
                   SampleGrid(samples, stride, localVoxel - new Vector3(0f, epsilon, 0f));
        float dz = SampleGrid(samples, stride, localVoxel + new Vector3(0f, 0f, epsilon)) -
                   SampleGrid(samples, stride, localVoxel - new Vector3(0f, 0f, epsilon));

        var gradient = new Vector3(dx, dy, dz);
        return gradient.LengthSquared() > 1e-12f
            ? Vector3.Normalize(gradient)
            : Vector3.UnitY;
    }

    private static float SampleGrid(float[] samples, int stride, Vector3 p)
    {
        float max = stride - 1.001f;
        float fx = Math.Clamp(p.X, 0f, max);
        float fy = Math.Clamp(p.Y, 0f, max);
        float fz = Math.Clamp(p.Z, 0f, max);

        int x0 = (int)fx;
        int y0 = (int)fy;
        int z0 = (int)fz;
        int x1 = Math.Min(x0 + 1, stride - 1);
        int y1 = Math.Min(y0 + 1, stride - 1);
        int z1 = Math.Min(z0 + 1, stride - 1);

        float tx = fx - x0;
        float ty = fy - y0;
        float tz = fz - z0;

        int i000 = x0 + y0 * stride + z0 * stride * stride;
        int i100 = x1 + y0 * stride + z0 * stride * stride;
        int i010 = x0 + y1 * stride + z0 * stride * stride;
        int i110 = x1 + y1 * stride + z0 * stride * stride;
        int i001 = x0 + y0 * stride + z1 * stride * stride;
        int i101 = x1 + y0 * stride + z1 * stride * stride;
        int i011 = x0 + y1 * stride + z1 * stride * stride;
        int i111 = x1 + y1 * stride + z1 * stride * stride;

        float x00 = Lerp(samples[i000], samples[i100], tx);
        float x10 = Lerp(samples[i010], samples[i110], tx);
        float x01 = Lerp(samples[i001], samples[i101], tx);
        float x11 = Lerp(samples[i011], samples[i111], tx);

        return Lerp(Lerp(x00, x10, ty), Lerp(x01, x11, ty), tz);
    }

    /// <summary>
    /// Chunks whose box can contain the noisy crust. This is the startup set;
    /// deeper chunks are created on demand when a brush reaches them.
    /// </summary>
    public IEnumerable<VoxelChunkKey> EnumerateSurfaceChunks()
    {
        float chunkSize = Settings.ChunkSize;
        float inner = Settings.MinCrustRadius - Settings.VoxelSize;
        float outer = Settings.MaxRadius + Settings.VoxelSize;

        int min = (int)MathF.Floor(-outer / chunkSize);
        int max = (int)MathF.Ceiling(outer / chunkSize);

        for (int z = min; z <= max; z++)
        for (int y = min; y <= max; y++)
        for (int x = min; x <= max; x++)
        {
            float ox = x * chunkSize;
            float oy = y * chunkSize;
            float oz = z * chunkSize;

            // Nearest and farthest distance from the planet centre to the box.
            float nearest = NearestDistanceToBox(ox, oy, oz, chunkSize);
            float farthest = FarthestDistanceToBox(ox, oy, oz, chunkSize);

            if (farthest < inner || nearest > outer)
                continue;

            yield return new VoxelChunkKey(x, y, z);
        }
    }

    // ── Raycast ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Marches the density field along a planet-local ray and returns the first
    /// solid crossing. Steps at 0.4 voxels, then bisects eight times, so the hit
    /// point is accurate enough to place a brush flush with the surface.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out VoxelHit hit)
    {
        hit = default;

        float length = direction.Length();
        if (length < 1e-6f)
            return false;

        direction /= length;

        float bound = Settings.MaxRadius + Settings.VoxelSize * 2f;
        if (!RaySphere(origin, direction, bound, out float enter, out float exit))
            return false;

        float step = Settings.VoxelSize * 0.4f;
        float t = MathF.Max(0f, enter);
        float end = MathF.Min(exit, maxDistance > 0f ? maxDistance : exit);

        // A ray that starts inside solid (a camera in a cave wall) skips out to
        // the first air before it starts looking for the next surface.
        bool startedSolid = SampleDensity(origin + direction * t) <= 0f;
        if (startedSolid)
        {
            while (t < end && SampleDensity(origin + direction * t) <= 0f)
                t += step;
        }

        float previous = t;
        float previousDensity = SampleDensity(origin + direction * t);

        while (t < end)
        {
            t = MathF.Min(t + step, end);
            float density = SampleDensity(origin + direction * t);

            if (density <= 0f && previousDensity > 0f)
            {
                float lo = previous;
                float hi = t;
                for (int i = 0; i < 8; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (SampleDensity(origin + direction * mid) > 0f)
                        lo = mid;
                    else
                        hi = mid;
                }

                Vector3 point = origin + direction * hi;
                Vector3 normal = SampleGradient(point, Settings.VoxelSize * 0.5f);
                hit = new VoxelHit(point, normal, hi);
                return true;
            }

            previous = t;
            previousDensity = density;
        }

        return false;
    }

    // ── Brushes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies one brush to the density field and reports the chunk region that
    /// must be remeshed. The caller owns scheduling and throttling; this method
    /// is deterministic and synchronous.
    /// </summary>
    public VoxelBrushResult Apply(VoxelBrush brush)
    {
        float h = Settings.VoxelSize;
        float radius = MathF.Max(h * 0.5f, brush.Radius);
        float strength = brush.Mode == VoxelBrushMode.Smooth
            ? Math.Clamp(brush.Strength, 0f, 1f)
            : MathF.Max(0f, brush.Strength);

        float invRadius = 1f / radius;

        int minX = (int)MathF.Floor((brush.Center.X - radius) / h);
        int minY = (int)MathF.Floor((brush.Center.Y - radius) / h);
        int minZ = (int)MathF.Floor((brush.Center.Z - radius) / h);
        int maxX = (int)MathF.Ceiling((brush.Center.X + radius) / h);
        int maxY = (int)MathF.Ceiling((brush.Center.Y + radius) / h);
        int maxZ = (int)MathF.Ceiling((brush.Center.Z + radius) / h);

        int changed = 0;
        var edits = new List<(int X, int Y, int Z, float Value)>(64);

        // Smooth reads its neighbours from the pre-edit state, so its writes are
        // staged. Dig and Fill are independent per sample and can write inline.
        Dictionary<long, float>? staged = brush.Mode == VoxelBrushMode.Smooth
            ? new Dictionary<long, float>()
            : null;

        for (int z = minZ; z <= maxZ; z++)
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            var position = new Vector3(x, y, z) * h;
            float distance = Vector3.Distance(position, brush.Center);
            if (distance > radius)
                continue;

            float t = 1f - distance * invRadius;
            float falloff = t * t * (3f - 2f * t);

            float current = SampleVoxel(x, y, z);
            float next;

            switch (brush.Mode)
            {
                case VoxelBrushMode.Fill:
                    next = current - strength * falloff;
                    break;

                case VoxelBrushMode.Smooth:
                    float average = NeighbourAverage(x, y, z);
                    next = Lerp(current, average, falloff * strength);
                    break;

                default:
                    next = current + strength * falloff;
                    break;
            }

            next = Math.Clamp(next, -_maxEditDensity, _maxEditDensity);
            if (MathF.Abs(next - current) < 1e-5f)
                continue;

            long key = VoxelKeyPacker.Pack(x, y, z);
            if (staged is null)
                _overrides[key] = next;
            else
                staged[key] = next;

            edits.Add((x, y, z, next));
            changed++;
        }

        if (staged is not null)
        {
            foreach (var (key, value) in staged)
                _overrides[key] = value;
        }

        if (edits.Count > 0)
            UpdateCachedSamples(edits);

        int n = Settings.ChunkCells;

        // A chunk samples [base, base + n + 1], so a changed voxel can dirty a
        // chunk one cell to its low side as well. The exact bounds matter: the
        // old n+1 padding marked up to 27 chunks for a 2 m brush, which tripled
        // the remesh work for no visible gain.
        int chunkMinX = CeilDiv(minX - n - 1, n);
        int chunkMinY = CeilDiv(minY - n - 1, n);
        int chunkMinZ = CeilDiv(minZ - n - 1, n);
        int chunkMaxX = FloorDiv(maxX, n);
        int chunkMaxY = FloorDiv(maxY, n);
        int chunkMaxZ = FloorDiv(maxZ, n);

        return new VoxelBrushResult(
            changed,
            chunkMinX, chunkMinY, chunkMinZ,
            chunkMaxX, chunkMaxY, chunkMaxZ);
    }

    /// <summary>Signed density of one chunk cell centre, for gameplay checks.</summary>
    public bool IsSolidAt(Vector3 planetLocal) => SampleDensity(planetLocal) <= 0f;

    // ── Helpers ─────────────────────────────────────────────────────────────

    private float NeighbourAverage(int x, int y, int z)
    {
        float sum = SampleVoxel(x - 1, y, z) + SampleVoxel(x + 1, y, z) +
                    SampleVoxel(x, y - 1, z) + SampleVoxel(x, y + 1, z) +
                    SampleVoxel(x, y, z - 1) + SampleVoxel(x, y, z + 1);
        return sum / 6f;
    }

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        if ((value % divisor != 0) && ((value < 0) != (divisor < 0)))
            quotient--;
        return quotient;
    }

    private static int CeilDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        if ((value % divisor != 0) && ((value < 0) == (divisor < 0)))
            quotient++;
        return quotient;
    }

    /// <summary>
    /// Writes edited voxels into every cached chunk grid that contains them.
    /// A voxel can sit in up to eight sample windows because chunks overlap by
    /// one cell, and all eight copies must agree or the seam cracks.
    /// </summary>
    private void UpdateCachedSamples(List<(int X, int Y, int Z, float Value)> edits)
    {
        int n = Settings.ChunkCells;
        int stride = n + 2;
        int span = n + 1;

        foreach (var (x, y, z, value) in edits)
        {
            int minCx = CeilDiv(x - span, n);
            int maxCx = FloorDiv(x, n);
            int minCy = CeilDiv(y - span, n);
            int maxCy = FloorDiv(y, n);
            int minCz = CeilDiv(z - span, n);
            int maxCz = FloorDiv(z, n);

            for (int cz = minCz; cz <= maxCz; cz++)
            for (int cy = minCy; cy <= maxCy; cy++)
            for (int cx = minCx; cx <= maxCx; cx++)
            {
                var key = new VoxelChunkKey(cx, cy, cz);
                if (!_chunkSamples.TryGetValue(key, out float[]? samples))
                    continue;

                int lx = x - cx * n;
                int ly = y - cy * n;
                int lz = z - cz * n;
                if ((uint)lx > (uint)span || (uint)ly > (uint)span || (uint)lz > (uint)span)
                    continue;

                samples[lx + ly * stride + lz * stride * stride] = value;
            }
        }
    }

    private static float NearestDistanceToBox(float x, float y, float z, float size)
    {
        float dx = x > 0f ? x : (x + size < 0f ? -(x + size) : 0f);
        float dy = y > 0f ? y : (y + size < 0f ? -(y + size) : 0f);
        float dz = z > 0f ? z : (z + size < 0f ? -(z + size) : 0f);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static float FarthestDistanceToBox(float x, float y, float z, float size)
    {
        float dx = MathF.Max(MathF.Abs(x), MathF.Abs(x + size));
        float dy = MathF.Max(MathF.Abs(y), MathF.Abs(y + size));
        float dz = MathF.Max(MathF.Abs(z), MathF.Abs(z + size));
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static bool RaySphere(
        Vector3 origin,
        Vector3 direction,
        float radius,
        out float enter,
        out float exit)
    {
        float b = Vector3.Dot(origin, direction);
        float c = Vector3.Dot(origin, origin) - radius * radius;
        float discriminant = b * b - c;

        if (discriminant < 0f)
        {
            enter = exit = 0f;
            return false;
        }

        float root = MathF.Sqrt(discriminant);
        enter = -b - root;
        exit = -b + root;
        return exit > 0f;
    }

    private static float HashColor(Vector3 p)
    {
        int x = (int)MathF.Round(p.X * 4f);
        int y = (int)MathF.Round(p.Y * 4f);
        int z = (int)MathF.Round(p.Z * 4f);

        unchecked
        {
            uint h = 0x811C9DC5u;
            h = (h ^ (uint)x) * 16777619u;
            h = (h ^ (uint)y) * 16777619u;
            h = (h ^ (uint)z) * 16777619u;
            h ^= h >> 13;
            h *= 0x5BD1E995u;
            h ^= h >> 15;
            return (h & 0xFFFFFFu) / 16777215f;
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
