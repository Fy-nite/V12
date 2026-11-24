using System;
using System.Collections.Generic;
using System.Linq;
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
public sealed class PhysicsService : IGameService, IPhysicsBackend, IGravityFieldBackend,
    IPhysicsTriggerEvents, IDisposable
{
    public Simulation? Simulation { get; private set; }

    public event Action<IPhysicsBody, IPhysicsBody>? TriggerEnter;
    public event Action<IPhysicsBody, IPhysicsBody>? TriggerExit;

    private BufferPool? _bufferPool;
    private ThreadDispatcher? _threadDispatcher;
    private readonly GravityContext _gravity = new();
    private readonly CollisionContext _collisions = new();

    private readonly Dictionary<long, BepuPhysicsBody> _bodyMap = new();
    private readonly Dictionary<BodyHandle, BepuPhysicsBody> _bodiesByHandle = new();
    private readonly Dictionary<long, BepuStaticBody> _staticMap = new();
    private readonly Dictionary<StaticHandle, BepuStaticBody> _staticsByHandle = new();

    private readonly Dictionary<long, IPhysicsBody> _triggers = new();
    private readonly HashSet<(long TriggerId, long OtherId)> _triggerPairs = new();
    private readonly HashSet<(long TriggerId, long OtherId)> _triggerPairsScratch = new();
    private readonly List<CollidableReference> _overlapScratch = new();
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
        Simulation = Simulation.Create(_bufferPool, new NarrowPhaseCallbacks(_collisions),
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

        // Continuous forces accumulate over the game tick. Applying them once
        // over the full budget gives the correct total impulse.
        foreach (var body in _bodyMap.Values)
            body.FlushPendingForces(budget);

        for (int i = 0; i < stepCount; i++)
        {
            // The active set can compact between substeps when a body sleeps.
            // Refresh the body index -> gravity scale mapping before every step,
            // otherwise a later substep applies another body's multiplier.
            _gravity.Prepare(Simulation, _bodiesByHandle);

            // Rotation locks and damping are pre-integration state. Applying
            // them after Timestep let the body rotate for one step first.
            ApplyBodySettings(step);

            Simulation.Timestep(step, _threadDispatcher);
        }

        UpdateGrounded();
        UpdateTriggers();
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

            var handler = new RayHitHandler
            {
                IgnoreHandle = body.Handle,
                IgnoreTriggers = _collisions,
            };
            Simulation.RayCast(position, down, body.GroundProbeDistance, _bufferPool!, ref handler);
            body.IsOnFloor = handler.HitFound && handler.T <= body.GroundProbeDistance;
        }
    }

    private void UpdateTriggers()
    {
        if (_triggers.Count == 0)
        {
            _triggerPairs.Clear();
            return;
        }

        _triggerPairsScratch.Clear();
        foreach (var (triggerId, trigger) in _triggers)
        {
            if (!TryGetPoseAndShape(trigger, out var pose, out var shapeIndex))
                continue;

            Simulation!.Shapes.UpdateBounds(pose, shapeIndex, out var bounds);
            _overlapScratch.Clear();
            var enumerator = new BroadPhaseOverlapEnumerator { Results = _overlapScratch };
            Simulation.BroadPhase.GetOverlaps(bounds, _bufferPool!, ref enumerator);

            foreach (var collidable in _overlapScratch)
            {
                if (!TryGetBody(collidable, out _, out long otherId) || otherId == triggerId)
                    continue;

                // Two triggers report one canonical pair, not both directions.
                if (_triggers.ContainsKey(otherId) && otherId < triggerId)
                    continue;

                _triggerPairsScratch.Add((triggerId, otherId));
            }
        }

        var enters = new List<(IPhysicsBody Trigger, IPhysicsBody Other)>();
        var exits = new List<(IPhysicsBody Trigger, IPhysicsBody Other)>();

        foreach (var pair in _triggerPairsScratch)
        {
            if (_triggerPairs.Contains(pair)) continue;
            if (_triggers.TryGetValue(pair.TriggerId, out var trigger) &&
                TryGetBodyById(pair.OtherId, out var other))
            {
                enters.Add((trigger, other));
            }
        }

        foreach (var pair in _triggerPairs)
        {
            if (_triggerPairsScratch.Contains(pair)) continue;
            if (_triggers.TryGetValue(pair.TriggerId, out var trigger) &&
                TryGetBodyById(pair.OtherId, out var other))
            {
                exits.Add((trigger, other));
            }
        }

        _triggerPairs.Clear();
        foreach (var pair in _triggerPairsScratch)
            _triggerPairs.Add(pair);

        // Raise after the pair sets are stable so a handler can safely create or
        // destroy bodies without mutating the set mid-iteration.
        foreach (var (trigger, other) in enters)
            TriggerEnter?.Invoke(trigger, other);
        foreach (var (trigger, other) in exits)
            TriggerExit?.Invoke(trigger, other);
    }

    private bool TryGetPoseAndShape(IPhysicsBody body, out RigidPose pose, out TypedIndex shapeIndex)
    {
        switch (body)
        {
            case BepuPhysicsBody b:
                pose = Simulation!.Bodies[b.Handle].Pose;
                shapeIndex = b.ShapeIndex;
                return true;
            case BepuStaticBody s:
                pose = Simulation!.Statics[s.Handle].Pose;
                shapeIndex = s.ShapeIndex;
                return true;
            default:
                pose = default;
                shapeIndex = default;
                return false;
        }
    }

    private bool TryGetBody(CollidableReference collidable, out IPhysicsBody body, out long id)
    {
        if (collidable.Mobility == CollidableMobility.Static)
        {
            if (_staticsByHandle.TryGetValue(collidable.StaticHandle, out var hitStatic))
            {
                body = hitStatic;
                id = hitStatic.Id;
                return true;
            }
        }
        else if (_bodiesByHandle.TryGetValue(collidable.BodyHandle, out var hitBody))
        {
            body = hitBody;
            id = hitBody.Id;
            return true;
        }

        body = null!;
        id = 0;
        return false;
    }

    private bool TryGetBodyById(long id, out IPhysicsBody body)
    {
        if (_bodyMap.TryGetValue(id, out var dynamicBody))
        {
            body = dynamicBody;
            return true;
        }
        if (_staticMap.TryGetValue(id, out var staticBody))
        {
            body = staticBody;
            return true;
        }

        body = null!;
        return false;
    }

    private void ReleaseTriggerPairs(long id, IPhysicsBody destroyed)
    {
        if (_triggerPairs.Count == 0) return;

        foreach (var pair in _triggerPairs.Where(p => p.TriggerId == id || p.OtherId == id).ToArray())
        {
            _triggerPairs.Remove(pair);
            if (!_triggers.TryGetValue(pair.TriggerId, out var trigger))
                continue;

            // The destroyed body has already left its map; use the reference we
            // were handed when it is the "other" side of the pair.
            if (TryGetBodyById(pair.OtherId, out var other))
                TriggerExit?.Invoke(trigger, other);
            else if (pair.OtherId == id)
                TriggerExit?.Invoke(trigger, destroyed);
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
        else if (body is BepuStaticBody s)
        {
            s.Position = s.Position + delta;
        }
        return delta;
    }

    Vector3 IPhysicsBackend.MoveKinematic(IPhysicsBody body, Vector3 delta, float deltaTime)
    {
        if (body is BepuStaticBody s)
        {
            s.Position = s.Position + delta;
            return delta;
        }

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

        if (desc.Type == PhysicsBodyType.Static)
            return CreateStaticBody(desc);

        var shapeIndex = BepuShapeFactory.Create(Simulation, _bufferPool, desc.Shape, desc.Size, desc.Mesh);

        bool isCharacter = desc.Type == PhysicsBodyType.Character;
        bool isDynamic = desc.Type is PhysicsBodyType.Dynamic or PhysicsBodyType.Character;
        float mass = desc.Mass > 0f ? desc.Mass : 1f;
        var collidable = new CollidableDescription(shapeIndex, 0.1f);
        var activity = desc.CanSleep
            ? new BodyActivityDescription(0.01f)
            : new BodyActivityDescription(-1f);

        // Kinematic bodies stay in the body map so the same raycast, pose and
        // DestroyBody paths work for them. PhysicsBodyType.Static is handled
        // separately above by CreateStaticBody.
        BodyDescription description;
        if (isDynamic)
        {
            var inertia = BepuInertia.ForShape(desc.Shape, desc.Size, mass);
            if (desc.LockRotation || isCharacter)
            {
                // Infinite rotational inertia is a real lock: contact impulses
                // cannot spin the body for one step before the angular velocity
                // is cleared.
                inertia.InverseInertiaTensor = default;
            }
            description = BodyDescription.CreateDynamic(
                new RigidPose(desc.Position, desc.Rotation), inertia, collidable, activity);
        }
        else
        {
            description = BodyDescription.CreateKinematic(
                new RigidPose(desc.Position, desc.Rotation), collidable, activity);
        }

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
        _collisions.SetBody(handle, desc.IsTrigger);
        if (desc.IsTrigger)
            _triggers[id] = body;
        return body;
    }

    private IPhysicsBody CreateStaticBody(in PhysicsBodyDesc desc)
    {
        var shapeIndex = BepuShapeFactory.Create(Simulation!, _bufferPool!, desc.Shape, desc.Size, desc.Mesh);
        var handle = Simulation!.Statics.Add(
            new StaticDescription(desc.Position, desc.Rotation, shapeIndex));
        long id = _nextBodyId++;
        var body = new BepuStaticBody(handle, Simulation, id, shapeIndex);

        _staticMap[id] = body;
        _staticsByHandle[handle] = body;
        _collisions.SetStatic(handle, desc.IsTrigger);
        if (desc.IsTrigger)
            _triggers[id] = body;
        return body;
    }

    void IPhysicsBackend.DestroyBody(IPhysicsBody body)
    {
        if (body is BepuStaticBody s)
        {
            if (!_staticMap.Remove(s.Id)) return;
            _staticsByHandle.Remove(s.Handle);
            ReleaseTriggerPairs(s.Id, s);
            _triggers.Remove(s.Id);
            _collisions.RemoveStatic(s.Handle);

            if (Simulation == null || _bufferPool == null) return;
            if (Simulation.Statics.StaticExists(s.Handle))
                Simulation.Statics.Remove(s.Handle);

            Simulation.Shapes.RemoveAndDispose(s.ShapeIndex, _bufferPool);
            return;
        }

        if (body is not BepuPhysicsBody b) return;

        // Remove() also guards against a double destroy disposing the shape twice.
        if (!_bodyMap.Remove(b.Id)) return;
        _bodiesByHandle.Remove(b.Handle);
        ReleaseTriggerPairs(b.Id, b);
        _triggers.Remove(b.Id);
        _collisions.RemoveBody(b.Handle);

        if (Simulation == null || _bufferPool == null) return;
        if (Simulation.Bodies.BodyExists(b.Handle))
            Simulation.Bodies.Remove(b.Handle);

        // Each body owns the shape created for it in CreateBody. Removing just
        // the body leaks the shape (and, for a custom mesh, its triangle buffer)
        // until the simulation is disposed.
        Simulation.Shapes.RemoveAndDispose(b.ShapeIndex, _bufferPool);
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
        if (collidable.Mobility == CollidableMobility.Static)
        {
            if (_staticsByHandle.TryGetValue(collidable.StaticHandle, out var hitStatic))
                hitBody = hitStatic;
        }
        else if (_bodiesByHandle.TryGetValue(collidable.BodyHandle, out var hitBodyImpl))
        {
            hitBody = hitBodyImpl;
        }

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
        _staticMap.Clear();
        _staticsByHandle.Clear();
        _triggers.Clear();
        _triggerPairs.Clear();
        _triggerPairsScratch.Clear();
        _collisions.Clear();

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

internal struct BroadPhaseOverlapEnumerator : IBreakableForEach<CollidableReference>
{
    public List<CollidableReference> Results;

    public bool LoopBody(CollidableReference item)
    {
        Results.Add(item);
        return true;
    }
}

internal struct RayHitHandler : IRayHitHandler
{
    public bool HitFound;
    public float T;
    public Vector3 Normal;
    public CollidableReference Collidable;

    /// <summary>Optional body handle to ignore, e.g. the body casting its own ground probe.</summary>
    public BodyHandle? IgnoreHandle;

    /// <summary>Skip trigger colliders for probes that must only find solid ground.</summary>
    public CollisionContext? IgnoreTriggers;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable)
        => (IgnoreTriggers == null || !IgnoreTriggers.IsTrigger(collidable))
           && (collidable.Mobility == CollidableMobility.Static
               || !IgnoreHandle.HasValue
               || collidable.BodyHandle != IgnoreHandle.Value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable, int childIndex)
        => (IgnoreTriggers == null || !IgnoreTriggers.IsTrigger(collidable))
           && (collidable.Mobility == CollidableMobility.Static
               || !IgnoreHandle.HasValue
               || collidable.BodyHandle != IgnoreHandle.Value);

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
