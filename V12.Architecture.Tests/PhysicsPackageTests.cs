using System;
using System.Numerics;
using V12.Components;
using V12.Core;
using V12.Core.Interfaces.Physics;
using V12.Gravity;
using V12.Physics;
using V12.Physics.Extensions;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Covers the V12.Physics package: fixed-step behavior, force vs impulse
/// semantics, shape-derived inertia, gravity scale and point gravity.
/// </summary>
public sealed class PhysicsPackageTests
{
    [Fact]
    public void Step_UsesSuppliedDeltaTime_NotWallClock()
    {
        using var service = CreateService();
        var backend = (IPhysicsBackend)service;
        var body = CreateBody(backend, Vector3.Zero);
        body.LinearVelocity = new Vector3(1f, 0f, 0f);

        service.Step(1f);

        Assert.InRange(body.Position.X, 0.98f, 1.02f);
    }

    [Fact]
    public void Step_SplitsLargeDeltaIntoSubsteps()
    {
        using var service = CreateService();
        service.MaxSubStep = 1f / 60f;
        service.MaxSubStepsPerCall = 240;

        var backend = (IPhysicsBackend)service;
        var body = CreateBody(backend, Vector3.Zero);
        body.LinearVelocity = new Vector3(2f, 0f, 0f);

        service.Step(0.5f);

        Assert.InRange(body.Position.X, 0.98f, 1.02f);
    }

    [Fact]
    public void Force_IsIntegratedOverDeltaTime()
    {
        using var service = CreateService();
        var backend = (IPhysicsBackend)service;
        var body = CreateBody(backend, Vector3.Zero, mass: 2f);

        body.AddForce(new Vector3(10f, 0f, 0f));
        service.Step(0.5f);

        // F/m * dt = 10 / 2 * 0.5 = 2.5 m/s
        Assert.InRange(body.LinearVelocity.X, 2.45f, 2.55f);
    }

    [Fact]
    public void Impulse_ChangesVelocityImmediately()
    {
        using var service = CreateService();
        var backend = (IPhysicsBackend)service;
        var body = CreateBody(backend, Vector3.Zero, mass: 2f);

        body.AddImpulse(new Vector3(10f, 0f, 0f));

        Assert.InRange(body.LinearVelocity.X, 4.99f, 5.01f);
    }

    [Fact]
    public void Torque_ProducesAngularVelocity_FromShapeInertia()
    {
        using var service = CreateService();
        var backend = (IPhysicsBackend)service;
        var body = CreateBody(backend, Vector3.Zero, mass: 1f);

        body.AddTorque(new Vector3(0f, 1f, 0f));
        service.Step(1f / 60f);

        // Unit cube inertia is 1/6 kg*m^2, so one step gives ~0.1 rad/s.
        Assert.True(body.AngularVelocity.Y > 0.05f, $"angular velocity was {body.AngularVelocity}");
    }

    [Fact]
    public void GravityScale_Zero_MakesBodyWeightless()
    {
        using var service = CreateService();
        service.Gravity = new Vector3(0f, -10f, 0f);

        var backend = (IPhysicsBackend)service;
        var normal = CreateBody(backend, Vector3.Zero, gravityScale: 1f);
        var weightless = CreateBody(backend, new Vector3(5f, 0f, 0f), gravityScale: 0f);

        service.Step(1f / 60f);

        Assert.True(normal.LinearVelocity.Y < -0.1f);
        Assert.InRange(weightless.LinearVelocity.Y, -0.0001f, 0.0001f);
    }

    [Fact]
    public void PointGravity_PullsTowardSource()
    {
        using var service = CreateService();
        service.Gravity = Vector3.Zero;

        var field = new GravityField();
        field.Add(Vector3.Zero, mu: 1000f, surfaceRadius: 1f);
        ((IGravityFieldBackend)service).GravityField = field;

        var backend = (IPhysicsBackend)service;
        var body = CreateBody(backend, new Vector3(10f, 0f, 0f));
        var weightless = CreateBody(backend, new Vector3(10f, 5f, 0f), gravityScale: 0f);

        service.Step(1f / 60f);

        // a = -mu/r^2 = -10 m/s^2 at r=10, so vx is negative.
        Assert.True(body.LinearVelocity.X < -0.1f, $"velocity was {body.LinearVelocity}");
        Assert.InRange(weightless.LinearVelocity.X, -0.0001f, 0.0001f);
        Assert.InRange(weightless.LinearVelocity.Y, -0.0001f, 0.0001f);
    }

    [Fact]
    public void RawBepu_AddDynamicBody_GetsShapeInertia()
    {
        using var service = CreateService();
        var simulation = service.Simulation!;

        var handle = simulation.AddDynamicBody(
            new BepuPhysics.RigidPose(Vector3.Zero, Quaternion.Identity),
            MeshShape.Box, Vector3.One, mass: 2f);

        var body = simulation.Bodies[handle];
        body.ApplyAngularImpulse(new Vector3(0f, 1f, 0f));

        Assert.True(body.Velocity.Angular.Y > 0f, $"angular velocity was {body.Velocity.Angular}");
    }

    [Fact]
    public void Mass_Change_RecomputesInertia()
    {
        using var service = CreateService();
        var backend = (IPhysicsBackend)service;
        var body = CreateBody(backend, Vector3.Zero, mass: 1f);

        body.AddAngularImpulse(new Vector3(0f, 1f, 0f));
        float lightAngular = body.AngularVelocity.Y;

        body.AngularVelocity = Vector3.Zero;
        body.Mass = 10f;
        body.AddAngularImpulse(new Vector3(0f, 1f, 0f));
        float heavyAngular = body.AngularVelocity.Y;

        Assert.True(MathF.Abs(lightAngular) > MathF.Abs(heavyAngular) * 5f,
            $"light={lightAngular}, heavy={heavyAngular}");
    }

    [Fact]
    public void MoveKinematic_WithDeltaTime_CarriesARestingBody()
    {
        using var service = CreateService();
        service.Gravity = new Vector3(0f, -10f, 0f);
        var backend = (IPhysicsBackend)service;

        var platform = backend.CreateBody(new PhysicsBodyDesc(
            Vector3.Zero,
            Quaternion.Identity,
            MeshShape.Box,
            new Vector3(100f, 2f, 100f),
            mass: 1f,
            type: PhysicsBodyType.Kinematic,
            gravityScale: 0f,
            canSleep: false));

        var rider = backend.CreateBody(new PhysicsBodyDesc(
            new Vector3(0f, 2f, 0f),
            Quaternion.Identity,
            MeshShape.Box,
            new Vector3(2f, 2f, 2f),
            mass: 1f,
            type: PhysicsBodyType.Dynamic,
            gravityScale: 1f,
            canSleep: false));

        const float step = 1f / 60f;
        var platformVelocity = new Vector3(5f, 0f, 0f);
        for (int i = 0; i < 180; i++)
        {
            backend.MoveKinematic(platform, platformVelocity * step, step);
            service.Step(step);
        }

        // Friction must be able to see the platform moving. A teleported pose with
        // no velocity leaves the rider at world zero while the platform slides out
        // from under it, which is the bug a moving planet used to hit.
        Assert.InRange(rider.LinearVelocity.X, 4f, 6f);
        Assert.True(rider.Position.X > 8f,
            $"rider was left behind by the platform: x={rider.Position.X:F2}");
    }

    private static PhysicsService CreateService()
    {
        var service = new PhysicsService
        {
            AutoStep = false,
            Gravity = Vector3.Zero,
            MaxSubStep = 1f / 60f,
            MaxSubStepsPerCall = 256,
        };
        service.Initialize(new GameRoot());
        return service;
    }

    private static IPhysicsBody CreateBody(IPhysicsBackend backend, Vector3 position,
        float mass = 1f, float gravityScale = 1f)
    {
        return backend.CreateBody(new PhysicsBodyDesc(
            position,
            Quaternion.Identity,
            MeshShape.Box,
            Vector3.One,
            mass: mass,
            type: PhysicsBodyType.Dynamic,
            gravityScale: gravityScale,
            canSleep: false));
    }
}
