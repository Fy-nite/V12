using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Physics;
using V12.Physics.Extensions;

namespace V12.Physics;

/// <summary>
/// Bepu 2 physics backend. Owns the simulation, the body map, and the fixed-step
/// loop. Stepping uses the delta handed in by the engine's fixed clock; there is
/// no wall-clock accumulator, so <c>GameRoot.TimeScale</c> and pause work.
/// </summary>
public sealed class PhysicsService : IGameService, IPhysicsBackend, IGravityFieldBackend, IDisposable
{
    public Simulation? Simulation { get; private set; }

    private BufferPool? _bufferPool;
    private ThreadDispatcher? _threadDispatcher;
    private readonly GravityContext _gravity = new();

    private readonly Dictionary<long, BepuPhysicsBody> _bodyMap = new();
    private readonly Dictionary<BodyHandle, BepuPhysicsBody> _bodiesByHandle = new();
    private long _nextBodyId;

    /// <summary>Largest single Bepu timestep. Larger deltas are split into substeps.</summary>
    public float MaxSubStep { get; set; } = 1f / 60f;

    /// <summary>Upper bound on substeps for one <see cref="Step"/> call.</summary>
    public int MaxSubStepsPerCall { get; set; } = 8;

    /// <summary>High priority so this backend wins over host fallbacks.</summary>
    public int Priority => 100;

    /// <summary>Uniform gravity applied to dynamic bodies in addition to any gravity field.</summary>
    public Vector3 Gravity
    {
        get => _gravity.UniformGravity;
        set => _gravity.UniformGravity = value;
    }

    /// <summary>Optional point-gravity field (planets, moons, stars).</summary>
    public IGravityField? GravityField
    {
        get => _gravity.Field;
        set => _gravity.Field = value;
    }

    /// <summary>
    /// When false the owner (PhysicsLocomotionSystem) is responsible for
    /// calling <see cref="Step"/>. Prevents the simulation from being
    /// advanced twice per tick.
    /// </summary>
    public bool AutoStep { get; set; } = true;

    public void Initialize(GameRoot g)
    {
        if (Simulation != null) return;

        _bufferPool = new BufferPool();
        _threadDispatcher = new ThreadDispatcher(Environment.ProcessorCount);
        Simulation = Simulation.Create(_bufferPool, new NarrowPhaseCallbacks(),
            new PoseIntegratorCallbacks(_gravity), new SolveDescription(8, 1));
    }

    public void Update(GameRoot g) { }

    public void Update(float deltaTime)
    {
        if (AutoStep) Step(deltaTime);
    }

    void IPhysicsBackend.Step(float deltaTime) => Step(deltaTime);

    /// <summary>
    /// Advances the simulation by <paramref name="deltaTime"/>. The engine's
    /// FixedStepClock already accumulates real time, so this normally receives
    /// exactly one fixed step. Large direct calls are split into Bepu-sized
    /// substeps up to <see cref="MaxSubStepsPerCall"/>.
    /// </summary>
    public void Step(float deltaTime)
    {
        if (Simulation == null || deltaTime <= 0f) return;

        float maxSubStep = MaxSubStep > 0f ? MaxSubStep : 1f / 60f;
        float budget = MathF.Min(deltaTime, maxSubStep * MathF.Max(1, MaxSubStepsPerCall));
        int stepCount = Math.Max(1, (int)MathF.Ceiling(budget / maxSubStep - 1e-6f));
        float step = budget / stepCount;

        // Snapshot the gravity field and per-body gravity multipliers before
        // Bepu hands the integrator to worker threads.
        _gravity.Prepare(Simulation, _bodiesByHandle);

        // Continuous forces accumulate over the game tick. Applying them once
        // over the full budget gives the correct total impulse.
        foreach (var body in _bodyMap.Values)
            body.FlushPendingForces(budget);

        for (int i = 0; i < stepCount; i++)
        {
            Simulation.Timestep(step, _threadDispatcher);
            ApplyBodySettings(step);
        }

        UpdateGrounded();
    }

    private void ApplyBodySettings(float deltaTime)
    {
        foreach (var body in _bodyMap.Values)
        {
            if (!body.IsDynamic) continue;
            var b = Simulation!.Bodies[body.Handle];
            if (body.LockRotation)
                b.Velocity.Angular = Vector3.Zero;
            if (body.LinearDamping > 0f)
                b.Velocity.Linear *= MathF.Max(0f, 1f - body.LinearDamping * deltaTime);
            if (body.AngularDamping > 0f)
                b.Velocity.Angular *= MathF.Max(0f, 1f - body.AngularDamping * deltaTime);
        }
    }

    private void UpdateGrounded()
    {
        foreach (var body in _bodyMap.Values)
        {
            if (!body.IsDynamic || body.GroundProbeDistance <= 0f) continue;

            var position = Simulation!.Bodies[body.Handle].Pose.Position;

            // Probe along local gravity, not always world -Y, so a character
            // stays grounded on a spherical planet.
            var gravity = _gravity.AccelerationAt(position, body.GravityScale);
            var down = gravity.LengthSquared() > 1e-8f ? Vector3.Normalize(gravity) : -Vector3.UnitY;

            var handler = new RayHitHandler { IgnoreHandle = body.Handle };
            Simulation.RayCast(position, down, body.GroundProbeDistance, _bufferPool!, ref handler);
            body.IsOnFloor = handler.HitFound && handler.T <= body.GroundProbeDistance;
        }
    }

    Vector3 IPhysicsBackend.MoveKinematic(IPhysicsBody body, Vector3 delta)
    {
        // This backend has no sliding: apply the full delta to the body.
        if (body is BepuPhysicsBody b)
        {
            b.Position = b.Position + delta;
            // A teleport is not continuous motion. Clear any velocity left over
            // from an earlier continuous move so Bepu does not integrate it too.
            if (b.LinearVelocity.LengthSquared() > 1e-8f)
                b.LinearVelocity = Vector3.Zero;
        }
        return delta;
    }

    Vector3 IPhysicsBackend.MoveKinematic(IPhysicsBody body, Vector3 delta, float deltaTime)
    {
        if (body is not BepuPhysicsBody b)
            return delta;

        // Continuous motion: publish the velocity and let Bepu integrate the pose.
        // Bepu integrates kinematic velocities unconditionally
        // (IntegrateVelocityForKinematics only controls the gravity callback), so
        // writing the pose here as well would move the body twice per step and make
        // it stutter between double-speed and stopped.
        b.LinearVelocity = deltaTime > 0f ? delta / deltaTime : Vector3.Zero;
        return delta;
    }

    IPhysicsBody IPhysicsBackend.CreateBody(in PhysicsBodyDesc desc)
    {
        if (Simulation == null || _bufferPool == null)
            throw new InvalidOperationException("PhysicsService.Initialize must run before CreateBody.");

        var shapeIndex = BepuShapeFactory.Create(Simulation, _bufferPool, desc.Shape, desc.Size);

        bool isCharacter = desc.Type == PhysicsBodyType.Character;
        bool isDynamic = desc.Type is PhysicsBodyType.Dynamic or PhysicsBodyType.Character;
        float mass = desc.Mass > 0f ? desc.Mass : 1f;
        var collidable = new CollidableDescription(shapeIndex, 0.1f);
        var activity = desc.CanSleep
            ? new BodyActivityDescription(0.01f)
            : new BodyActivityDescription(-1f);

        var description = isDynamic
            ? BodyDescription.CreateDynamic(new RigidPose(desc.Position, desc.Rotation),
                BepuInertia.ForShape(desc.Shape, desc.Size, mass), collidable, activity)
            : BodyDescription.CreateKinematic(new RigidPose(desc.Position, desc.Rotation), collidable, activity);

        var handle = Simulation.Bodies.Add(description);
        long id = _nextBodyId++;
        var body = new BepuPhysicsBody(handle, Simulation, id, collidable, activity,
            desc.Shape, desc.Size, mass, isDynamic, isCharacter, desc.GravityScale,
            desc.LockRotation || isCharacter)
        {
            LinearDamping = desc.LinearDamping,
            AngularDamping = desc.AngularDamping,
            GroundProbeDistance = isCharacter ? desc.Size.Y * 0.5f + 0.15f : 0f,
        };

        _bodyMap[id] = body;
        _bodiesByHandle[handle] = body;
        return body;
    }

    void IPhysicsBackend.DestroyBody(IPhysicsBody body)
    {
        if (body is not BepuPhysicsBody b) return;

        _bodyMap.Remove(b.Id);
        _bodiesByHandle.Remove(b.Handle);
        if (Simulation == null) return;
        if (Simulation.Bodies.BodyExists(b.Handle))
            Simulation.Bodies.Remove(b.Handle);
    }

    bool IPhysicsBackend.Raycast(in Ray ray, out RaycastHit hit)
    {
        hit = default;
        if (Simulation == null || _bufferPool == null) return false;

        var hitHandler = new RayHitHandler();
        Simulation.RayCast(ray.Origin, ray.Direction, ray.MaxDistance, _bufferPool, ref hitHandler);

        if (!hitHandler.HitFound) return false;

        IPhysicsBody? hitBody = null;
        var collidable = hitHandler.Collidable;
        if (collidable.Mobility != CollidableMobility.Static
            && _bodiesByHandle.TryGetValue(collidable.BodyHandle, out var hitBodyImpl))
            hitBody = hitBodyImpl;

        hit = new RaycastHit
        {
            Point = ray.Origin + ray.Direction * hitHandler.T,
            Normal = hitHandler.Normal,
            Distance = hitHandler.T,
            Body = hitBody
        };
        return true;
    }

    /// <summary>Raw raycast returning the Bepu collidable reference.</summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit)
    {
        hit = default;
        if (Simulation == null || _bufferPool == null) return false;

        var hitHandler = new RayHitHandler();
        Simulation.RayCast(origin, direction, maxDistance, _bufferPool, ref hitHandler);

        if (!hitHandler.HitFound) return false;

        hit = new RayHit
        {
            T = hitHandler.T,
            Location = origin + direction * hitHandler.T,
            Normal = hitHandler.Normal,
            Collidable = hitHandler.Collidable
        };
        return true;
    }

    public void Dispose()
    {
        _bodyMap.Clear();
        _bodiesByHandle.Clear();

        Simulation?.Dispose();
        Simulation = null;

        _threadDispatcher?.Dispose();
        _threadDispatcher = null;

        _bufferPool?.Clear();
        _bufferPool = null;
    }
}

public struct RayHit
{
    public float T;
    public Vector3 Location;
    public Vector3 Normal;
    public CollidableReference Collidable;
}

internal struct RayHitHandler : IRayHitHandler
{
    public bool HitFound;
    public float T;
    public Vector3 Normal;
    public CollidableReference Collidable;

    /// <summary>Optional body handle to ignore, e.g. the body casting its own ground probe.</summary>
    public BodyHandle? IgnoreHandle;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable)
        => collidable.Mobility == CollidableMobility.Static
           || !IgnoreHandle.HasValue
           || collidable.BodyHandle != IgnoreHandle.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable, int childIndex)
        => collidable.Mobility == CollidableMobility.Static
           || !IgnoreHandle.HasValue
           || collidable.BodyHandle != IgnoreHandle.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal,
        CollidableReference collidable, int childIndex)
    {
        if (t >= maximumT) return;
        maximumT = t;
        HitFound = true;
        T = t;
        Normal = normal;
        Collidable = collidable;
    }

    public void OnRayHit(in RayData ray, ref float maximumT, float t, Vector3 normal,
        CollidableReference collidable, int childIndex)
    {
        if (t >= maximumT) return;
        maximumT = t;
        HitFound = true;
        T = t;
        Normal = normal;
        Collidable = collidable;
    }
}
