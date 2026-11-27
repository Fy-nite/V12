using System;
using System.Numerics;

namespace V12.Voxel;

/// <summary>
/// The analytic part of a planet: an ideal sphere pushed around by seeded value
/// noise. Negative density is solid, positive is air, so the surface is the zero
/// set. Edits live in <see cref="VoxelTerrain"/> on top of this field; the base
/// is never stored.
///
/// The noise is a tiny in-package value-noise implementation on purpose. It has
/// no dependency, no global static state, and produces identical numbers on
/// every machine, which is what makes an edit log replayable.
/// </summary>
public sealed class VoxelField
{
    private readonly float _radius;
    private readonly float _amplitude;
    private readonly float _frequency;
    private readonly int _octaves;
    private readonly int _seed;

    public VoxelField(VoxelPlanetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        _radius = settings.Radius;
        _amplitude = settings.NoiseAmplitude;
        _frequency = settings.NoiseFrequency;
        _octaves = settings.NoiseOctaves;
        _seed = settings.Seed;
    }

    public float Radius => _radius;

    /// <summary>Signed distance to the base surface, in metres.</summary>
    public float SampleBase(Vector3 planetLocal)
    {
        float noise = _amplitude > 0f ? FractalNoise(planetLocal) * _amplitude : 0f;
        return planetLocal.Length() - (_radius + noise);
    }

    /// <summary>Analytic gradient of the base field, pointing outward.</summary>
    public Vector3 GradientBase(Vector3 planetLocal, float epsilon)
    {
        var x = new Vector3(
            SampleBase(planetLocal + new Vector3(epsilon, 0f, 0f)) -
            SampleBase(planetLocal - new Vector3(epsilon, 0f, 0f)),
            SampleBase(planetLocal + new Vector3(0f, epsilon, 0f)) -
            SampleBase(planetLocal - new Vector3(0f, epsilon, 0f)),
            SampleBase(planetLocal + new Vector3(0f, 0f, epsilon)) -
            SampleBase(planetLocal - new Vector3(0f, 0f, epsilon)));

        return x.LengthSquared() > 1e-12f ? Vector3.Normalize(x) : Vector3.UnitY;
    }

    // ── Value noise ─────────────────────────────────────────────────────────

    private float FractalNoise(Vector3 p)
    {
        float total = 0f;
        float amplitude = 1f;
        float normaliser = 0f;
        float scale = _frequency;

        for (int octave = 0; octave < _octaves; octave++)
        {
            total += ValueNoise(p * scale, _seed + octave * 1013) * amplitude;
            normaliser += amplitude;
            amplitude *= 0.5f;
            scale *= 2.03f; // slightly off 2 so octaves do not line up on a grid
        }

        return normaliser > 0f ? total / normaliser : 0f;
    }

    private static float ValueNoise(Vector3 p, int seed)
    {
        int x0 = (int)MathF.Floor(p.X);
        int y0 = (int)MathF.Floor(p.Y);
        int z0 = (int)MathF.Floor(p.Z);
        int x1 = x0 + 1;
        int y1 = y0 + 1;
        int z1 = z0 + 1;

        float tx = SmootherStep(p.X - x0);
        float ty = SmootherStep(p.Y - y0);
        float tz = SmootherStep(p.Z - z0);

        float c000 = Hash01(x0, y0, z0, seed);
        float c100 = Hash01(x1, y0, z0, seed);
        float c010 = Hash01(x0, y1, z0, seed);
        float c110 = Hash01(x1, y1, z0, seed);
        float c001 = Hash01(x0, y0, z1, seed);
        float c101 = Hash01(x1, y0, z1, seed);
        float c011 = Hash01(x0, y1, z1, seed);
        float c111 = Hash01(x1, y1, z1, seed);

        float x00 = Lerp(c000, c100, tx);
        float x10 = Lerp(c010, c110, tx);
        float x01 = Lerp(c001, c101, tx);
        float x11 = Lerp(c011, c111, tx);

        float y0Lerp = Lerp(x00, x10, ty);
        float y1Lerp = Lerp(x01, x11, ty);

        return Lerp(y0Lerp, y1Lerp, tz) * 2f - 1f;
    }

    private static float Hash01(int x, int y, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)seed * 0x9E3779B1u;
            h ^= (uint)x * 0x85EBCA77u;
            h = (h << 13) | (h >> 19);
            h ^= (uint)y * 0xC2B2AE3Du;
            h = (h << 13) | (h >> 19);
            h ^= (uint)z * 0x27D4EB2Fu;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            h *= 0x297A2D39u;
            h ^= h >> 15;
            return (h & 0xFFFFFFu) / 16777215f;
        }
    }

    private static float SmootherStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
