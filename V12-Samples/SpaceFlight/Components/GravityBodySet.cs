using System;
using System.Collections.Generic;
using System.Numerics;
using V12.Gravity.Components;

namespace SpaceFlight.Components;

/// <summary>
/// The scene's gravity sources, in one place so the orbit path, telemetry,
/// navball and cameras agree on which body the ship is currently locked to.
///
/// A source can have a sphere-of-influence radius. When the ship is inside a
/// planet's SOI that planet wins, even if the Sun's raw acceleration is larger.
/// Outside every SOI the strongest local pull wins.
/// </summary>
public sealed class GravityBodySet
{
    private readonly List<GravitySourceComponent> _sources = new();
    private readonly Dictionary<GravitySourceComponent, float> _influence = new();

    public IReadOnlyList<GravitySourceComponent> Sources => _sources;

    public void Add(GravitySourceComponent? source, double sphereOfInfluence = 0)
    {
        if (source == null)
            return;

        if (!_sources.Contains(source))
            _sources.Add(source);

        if (sphereOfInfluence > 0)
            _influence[source] = (float)sphereOfInfluence;
    }

    public GravitySourceComponent? FindDominant(Vector3 position)
    {
        GravitySourceComponent? patched = null;
        float smallestSoi = float.MaxValue;

        foreach (var source in _sources)
        {
            if (!source.Enabled || source.Mu <= 0f)
                continue;

            var delta = source.WorldPosition - position;
            float distance = delta.Length();
            if (!_influence.TryGetValue(source, out float soi) || soi <= 0f)
                continue;

            if (distance > soi)
                continue;

            // The smallest containing SOI is the most local frame. This is
            // what makes the Moon win inside its own SOI while still inside
            // Earth's much larger one.
            if (soi < smallestSoi)
            {
                smallestSoi = soi;
                patched = source;
            }
        }

        if (patched != null)
            return patched;

        // Outside every planet SOI the root body (the Sun) owns the ship.
        // Fall back to the strongest root, or the strongest source if the
        // set has no roots (unit tests often use a flat body list).
        GravitySourceComponent? strongestRoot = null;
        GravitySourceComponent? strongest = null;
        float bestRootAcceleration = 0f;
        float bestAcceleration = 0f;

        foreach (var source in _sources)
        {
            if (!source.Enabled || source.Mu <= 0f)
                continue;

            var delta = source.WorldPosition - position;
            float distanceSquared = MathF.Max(
                delta.LengthSquared(),
                source.SurfaceRadius * source.SurfaceRadius);
            if (distanceSquared < 1e-6f)
                continue;

            float acceleration = source.Mu / distanceSquared;
            if (acceleration > bestAcceleration)
            {
                bestAcceleration = acceleration;
                strongest = source;
            }

            if (!_influence.ContainsKey(source) && acceleration > bestRootAcceleration)
            {
                bestRootAcceleration = acceleration;
                strongestRoot = source;
            }
        }

        return strongestRoot ?? strongest;
    }

    public Vector3 VelocityOf(GravitySourceComponent? source)
        => source?.Owner?.GetComponent<PlanetOrbitComponent>()?.LinearVelocity
           ?? Vector3.Zero;
}
