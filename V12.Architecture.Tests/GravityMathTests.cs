using System;
using System.Numerics;
using V12.Core;
using V12.Gravity;
using V12.Gravity.Components;
using V12.Gravity.Systems;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Covers V12.Gravity: point field sampling, circular-orbit elements, and
/// double-precision universal-variable propagation.
/// </summary>
public sealed class GravityMathTests
{
    private const double EarthMu = 3.986004418e14;
    private const double TestRadius = 7_000_000;

    [Fact]
    public void CircularOrbit_HasExpectedElements()
    {
        var state = CircularState(TestRadius);

        Assert.InRange(state.Eccentricity, 0, 1e-6);
        Assert.InRange(state.SemiMajorAxis, TestRadius * 0.999, TestRadius * 1.001);
        Assert.InRange(state.Apoapsis, TestRadius * 0.999, TestRadius * 1.001);
        Assert.InRange(state.Periapsis, TestRadius * 0.999, TestRadius * 1.001);
        Assert.True(state.IsBound);

        double expectedPeriod = 2 * Math.PI * Math.Sqrt(TestRadius * TestRadius * TestRadius / EarthMu);
        Assert.InRange(state.Period, expectedPeriod * 0.999, expectedPeriod * 1.001);
    }

    [Fact]
    public void EcoVRProfile_MatchesDocumentWorkingPlanet()
    {
        var planet = PlanetProfile.EcoVRWorking;

        Assert.Equal(2_400_000, planet.Radius, 0);
        Assert.InRange(planet.SurfaceGravity, 9.8, 9.82);
        Assert.InRange(planet.CircularSpeed(), 4800, 4900);
        Assert.InRange(planet.OrbitalPeriod() / 60, 42, 60);
        Assert.InRange(planet.EscapeSpeed() / planet.CircularSpeed(),
            Math.Sqrt(2) - 1e-6, Math.Sqrt(2) + 1e-6);
    }

    [Fact]
    public void SphereOfInfluence_MatchesEarthMoon()
    {
        double earthMu = 3.986004418e14;
        double moonMu = 4.9048695e12;
        double separation = 3.844e8;

        double soi = OrbitalMechanics.SphereOfInfluence(earthMu, moonMu, separation);

        // The real Moon SOI is about 66,100 km.
        Assert.InRange(soi, 6.5e7, 6.7e7);
    }

    [Fact]
    public void CircularSpeed_And_EscapeSpeed_Agree()
    {
        double circular = OrbitalMechanics.CircularSpeed(EarthMu, TestRadius);
        double escape = OrbitalMechanics.EscapeSpeed(EarthMu, TestRadius);

        Assert.InRange(escape / circular, Math.Sqrt(2) - 1e-6, Math.Sqrt(2) + 1e-6);
    }

    [Fact]
    public void Propagate_QuarterPeriod_StaysOnCircleAndTurnsNinetyDegrees()
    {
        var state = CircularState(TestRadius);
        var next = state.Propagate(state.Period / 4);

        Assert.InRange(next.Radius, TestRadius * 0.999, TestRadius * 1.001);

        double cosine = Double3.Dot(next.Position, state.Position) / (next.Radius * state.Radius);
        Assert.InRange(cosine, -1e-3, 1e-3);
    }

    [Fact]
    public void Propagate_FullPeriod_ReturnsToStart()
    {
        var state = CircularState(TestRadius);
        var next = state.Propagate(state.Period);

        double positionError = (next.Position - state.Position).Length;
        double velocityError = (next.Velocity - state.Velocity).Length;

        Assert.True(positionError < TestRadius * 1e-4,
            $"position error {positionError} m was too large");
        Assert.True(velocityError < state.Speed * 1e-4,
            $"velocity error {velocityError} m/s was too large");
    }

    [Fact]
    public void Propagate_EllipticOrbit_ReturnsToPeriapsisAfterOnePeriod()
    {
        double periapsis = 7_000_000;
        double apoapsis = 14_000_000;
        double semiMajor = (periapsis + apoapsis) * 0.5;
        double speed = Math.Sqrt(EarthMu * (2 / periapsis - 1 / semiMajor));

        var state = new OrbitalState(
            new Double3(periapsis, 0, 0),
            new Double3(0, 0, speed),
            EarthMu);

        Assert.InRange(state.Eccentricity, 0.33, 0.34);
        Assert.InRange(state.Apoapsis, apoapsis * 0.999, apoapsis * 1.001);
        Assert.InRange(state.Periapsis, periapsis * 0.999, periapsis * 1.001);

        var half = state.Propagate(state.Period / 2);
        Assert.InRange(half.Radius, apoapsis * 0.999, apoapsis * 1.001);

        var full = state.Propagate(state.Period);
        Assert.True((full.Position - state.Position).Length < periapsis * 1e-3,
            $"position drift {(full.Position - state.Position).Length} m");
    }

    [Fact]
    public void Propagate_HyperbolicState_RemainsFinite()
    {
        double radius = 7_000_000;
        double speed = OrbitalMechanics.EscapeSpeed(EarthMu, radius) * 1.1;
        var state = new OrbitalState(
            new Double3(radius, 0, 0),
            new Double3(0, 0, speed),
            EarthMu);

        Assert.True(state.Eccentricity > 1);
        Assert.False(state.IsBound);

        var next = state.Propagate(1000);
        Assert.True(next.Radius > radius);
        Assert.False(double.IsNaN(next.Position.X));
        Assert.False(double.IsInfinity(next.Position.X));
    }

    [Fact]
    public void GravityField_SamplesOutsideAndInsideSurface()
    {
        var field = new GravityField();
        field.Add(Vector3.Zero, mu: 1000f, surfaceRadius: 10f);

        var outside = field.SampleAcceleration(new Vector3(10f, 0f, 0f));
        Assert.InRange(outside.X, -10.1f, -9.9f);

        var inside = field.SampleAcceleration(new Vector3(1f, 0f, 0f));
        Assert.InRange(inside.X, -10.1f, -9.9f);

        var far = field.SampleAcceleration(new Vector3(20f, 0f, 0f));
        Assert.InRange(far.X, -2.51f, -2.49f);
    }

    [Fact]
    public void GravityFieldSystem_CollectsEnabledSources()
    {
        var root = new GameRoot();
        var world = new World("Gravity");
        root.Worlds.Add(world);
        root.SelectWorld(world);
        root.WorldManager.ReconcileAll();

        var planet = new Element("Planet");
        planet.AddComponent(new GravitySourceComponent { Mu = 1000f, SurfaceRadius = 10f });
        world.AddElement(planet);

        var system = new GravityFieldSystem(root);
        system.Initialize(root);
        system.Update(1f / 60f);

        Assert.Equal(1, system.LastSourceCount);
        var acceleration = system.Field.SampleAcceleration(new Vector3(10f, 0f, 0f));
        Assert.InRange(acceleration.X, -10.1f, -9.9f);
    }

    private static OrbitalState CircularState(double radius)
    {
        double speed = OrbitalMechanics.CircularSpeed(EarthMu, radius);
        return new OrbitalState(
            new Double3(radius, 0, 0),
            new Double3(0, 0, speed),
            EarthMu);
    }
}
