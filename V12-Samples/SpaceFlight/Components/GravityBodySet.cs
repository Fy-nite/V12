using System;
using System.Collections.Generic;
using System.Numerics;
using V12.Gravity.Components;

namespace SpaceFlight.Components;

/// <summary>
/// The scene's gravity sources, in one place so the orbit path, telemetry,
/// navball and cameras agree on which body the ship is currently locked to.
///
/// A source carries its own sphere of influence on
/// <see cref="GravitySourceComponent.SphereOfInfluence"/>, which is also what
/// the physics field uses to pick the local patched-conic frame. The smallest
/// containing SOI wins, so the Moon owns the ship inside its own bubble even
/// while it sits inside Earth's much larger one. Outside every SOI the
/// strongest root source (SOI 0) owns the ship.
/// </summary>
public sealed class GravityBodySet
{
    private readonly List<GravitySourceComponent> _sources = new();

    public IReadOnlyList<GravitySourceComponent> Sources => _sources;

    public void Add(GravitySourceComponent? source, double sphereOfInfluence = 0)
    {
        if (source == null)
            return;

        if (!_sources.Contains(source))
            _sources.Add(source);

        // Keep a component that was authored with an SOI. Passing 0 here means
        // "no override", not "make this a root".
        if (sphereOfInfluence > 0)
            source.SphereOfInfluence = (float)sphereOfInfluence;
    }

    public GravitySourceComponent? FindDominant(Vector3 position)
    {
        GravitySourceComponent? patched = null;
        float smallestSoi = float.MaxValue;

        foreach (var source in _sources)
        {
            if (!source.Enabled || source.Mu <= 0f)
                continue;

            float soi = source.SphereOfInfluence;
            if (soi <= 0f)
                continue;

            var delta = source.WorldPosition - position;
            if (delta.LengthSquared() > soi * soi)
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

            if (source.SphereOfInfluence <= 0f
                && acceleration > bestRootAcceleration)
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
