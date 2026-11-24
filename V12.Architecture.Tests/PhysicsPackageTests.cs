using System;
using System.Numerics;
using Assimp;
using V12.Components;
using V12.Core;
using V12.Core.Interfaces.Physics;
using V12.Core.Interfaces.Renderer;
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

    [Fact]
    public void CapsuleDimensions_UseTotalHeight()
    {
        var (radius, segment) = BepuShapeFactory.CapsuleDimensions(
            new Vector3(0.6f, 1.8f, 0.6f));

        Assert.Equal(0.3f, radius, 3);
        Assert.Equal(1.2f, segment, 3);
        Assert.Equal(1.8f, segment + radius * 2f, 3);
    }

    [Fact]
    public void CharacterCapsule_RestsAtItsRequestedTotalHeight()
    {
        using var service = CreateService();
        service.Gravity = new Vector3(0f, -10f, 0f);
        var backend = (IPhysicsBackend)service;

        // Floor top is at y = 0.
        backend.CreateBody(new PhysicsBodyDesc(
            new Vector3(0f, -0.5f, 0f),
            Quaternion.Identity,
            MeshShape.Box,
            new Vector3(20f, 1f, 20f),
            mass: 1f,
            type: PhysicsBodyType.Kinematic,
            canSleep: false));

        var character = backend.CreateBody(new PhysicsBodyDesc(
            new Vector3(0f, 5f, 0f),
            Quaternion.Identity,
            MeshShape.Capsule,
            new Vector3(0.6f, 1.8f, 0.6f),
            mass: 1f,
            type: PhysicsBodyType.Character,
            canSleep: false));

        for (int i = 0; i < 300; i++)
            service.Step(1f / 60f);

        // A 1.8-unit capsule resting on the floor has its centre at 0.9.
        // The old shape was 2.4 tall, so it settled at 1.2 and the ground
        // probe (height / 2 + 0.15) stopped above the floor.
        Assert.InRange(character.Position.Y, 0.85f, 1.0f);
        Assert.True(character.IsOnFloor,
            $"character was not grounded; y={character.Position.Y:F3}");
    }

    [Fact]
    public void DestroyBody_RemovesTheBodyFromTheSimulation()
    {
        using var service = CreateService();
        var backend = (IPhysicsBackend)service;
        var body = (BepuPhysicsBody)CreateBody(backend, Vector3.Zero);
        var handle = body.Handle;

        Assert.True(service.Simulation!.Bodies.BodyExists(handle));
        backend.DestroyBody(body);
        Assert.False(service.Simulation.Bodies.BodyExists(handle));
    }

    [Fact]
    public void StaticBody_UsesBepuStatics()
    {
        using var service = CreateService();
        var backend = (IPhysicsBackend)service;

        var body = (BepuStaticBody)backend.CreateBody(new PhysicsBodyDesc(
            Vector3.Zero,
            Quaternion.Identity,
            MeshShape.Box,
            Vector3.One,
            mass: 1f,
            type: PhysicsBodyType.Static));

        Assert.True(service.Simulation!.Statics.StaticExists(body.Handle));

        backend.MoveKinematic(body, new Vector3(1f, 0f, 0f));
        Assert.Equal(new Vector3(1f, 0f, 0f), body.Position);

        backend.DestroyBody(body);
        Assert.False(service.Simulation.Statics.StaticExists(body.Handle));
    }

    [Fact]
    public void CustomCollider_UsesTheMeshPayload()
    {
        using var service = CreateService();
        var backend = (IPhysicsBackend)service;
        var mesh = new OffsetCubeMesh(new Vector3(5f, 0f, 0f));

        backend.CreateBody(new PhysicsBodyDesc(
            Vector3.Zero,
            Quaternion.Identity,
            MeshShape.Custom,
            Vector3.One,
            mass: 1f,
            type: PhysicsBodyType.Static)
        {
            Mesh = mesh,
        });

        // The fallback box would sit at the origin; this ray only hits if the
        // custom triangle mesh was passed through to the backend.
        bool hit = backend.Raycast(new V12.Core.Interfaces.Physics.Ray
        {
            Origin = new Vector3(5f, 5f, 0f),
            Direction = new Vector3(0f, -1f, 0f),
            MaxDistance = 20f,
        }, out var rayHit);

        Assert.True(hit, "the custom mesh collider was not hit at all");
        Assert.NotNull(rayHit.Body);
        // Either face of the offset cube proves the mesh payload was used; the
        // fallback 1-unit box at the origin would not be hit by this ray.
        Assert.True(rayHit.Distance is >= 4.4f and <= 5.6f,
            $"expected a hit on the offset mesh, got {rayHit.Distance:F3}");
    }

    [Fact]
    public void Trigger_RaisesEnterExitAndDoesNotBlock()
    {
        using var service = CreateService();
        service.Gravity = new Vector3(0f, -10f, 0f);
        var backend = (IPhysicsBackend)service;

        var trigger = backend.CreateBody(new PhysicsBodyDesc(
            Vector3.Zero,
            Quaternion.Identity,
            MeshShape.Box,
            new Vector3(4f, 1f, 4f),
            mass: 1f,
            type: PhysicsBodyType.Static,
            isTrigger: true));

        var faller = backend.CreateBody(new PhysicsBodyDesc(
            new Vector3(0f, 2f, 0f),
            Quaternion.Identity,
            MeshShape.Box,
            Vector3.One,
            mass: 1f,
            type: PhysicsBodyType.Dynamic,
            canSleep: false));

        int enters = 0, exits = 0;
        IPhysicsBody? enteredTrigger = null;
        IPhysicsBody? enteredOther = null;
        service.TriggerEnter += (t, o) =>
        {
            enters++;
            enteredTrigger = t;
            enteredOther = o;
        };
        service.TriggerExit += (_, _) => exits++;

        for (int i = 0; i < 120; i++)
            service.Step(1f / 60f);

        Assert.Equal(1, enters);
        Assert.Same(trigger, enteredTrigger);
        Assert.Same(faller, enteredOther);
        Assert.True(exits >= 1);
        Assert.True(faller.Position.Y < -1f,
            $"the trigger blocked the falling body; y={faller.Position.Y:F3}");
    }

    private sealed class OffsetCubeMesh : ComponentBase, IMeshRenderable
    {
        private readonly double[] _points;
        private readonly uint[] _indices;
        private readonly Material _material = new();

        public OffsetCubeMesh(Vector3 offset)
        {
            var corners = new[]
            {
                new Vector3(-0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f),
                new Vector3(-0.5f, 0.5f, 0.5f),
            };
            _points = new double[corners.Length * 3];
            for (int i = 0; i < corners.Length; i++)
            {
                var p = corners[i] + offset;
                _points[i * 3] = p.X;
                _points[i * 3 + 1] = p.Y;
                _points[i * 3 + 2] = p.Z;
            }
            _indices = new uint[]
            {
                0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
                0, 1, 5, 0, 5, 4, 2, 3, 7, 2, 7, 6,
                1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3,
            };
        }

        public RenderType RenderType => RenderType.Mesh;
        public double[] MeshPoints => _points;
        public uint[] Indices => _indices;
        public Material Material => _material;
        public bool IsWorldLocked => true;
        public Matrix4x4 WorldTransform => Matrix4x4.Identity;
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
