using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using V12.Components;
using V12.Core.Interfaces.Physics;
using V12.Physics.Extensions;

namespace V12.Physics;

internal sealed class BepuPhysicsBody : IPhysicsBody
{
    public BodyHandle Handle { get; }
    public Simulation Simulation { get; }
    public long Id { get; }

    /// <summary>The shape created for this body. Each body owns its shape, so
    /// <see cref="PhysicsService"/> disposes it when the body is destroyed.</summary>
    public TypedIndex ShapeIndex => _collidable.Shape;

    public bool IsDynamic { get; set; } = true;
    public bool IsCharacterController { get; set; }
    public bool IsOnFloor { get; set; }
    public bool LockRotation { get; set; }
    public float LinearDamping { get; set; }
    public float AngularDamping { get; set; }
    public float GroundProbeDistance { get; set; }
    public float GravityScale { get; set; } = 1f;

    private readonly CollidableDescription _collidable;
    private readonly BodyActivityDescription _activity;
    private readonly MeshShape _shape;
    private readonly Vector3 _size;

    private float _mass;
    private Vector3 _pendingForce;
    private Vector3 _pendingTorque;

    public BepuPhysicsBody(BodyHandle handle, Simulation simulation, long id,
        in CollidableDescription collidable, in BodyActivityDescription activity,
        MeshShape shape, Vector3 size, float mass, bool isDynamic, bool isCharacter,
        float gravityScale, bool lockRotation)
    {
        Handle = handle;
        Simulation = simulation;
        Id = id;
        _collidable = collidable;
        _activity = activity;
        _shape = shape;
        _size = size;
        _mass = mass > 0f ? mass : 1f;
        IsDynamic = isDynamic;
        IsCharacterController = isCharacter;
        GravityScale = gravityScale;
        LockRotation = lockRotation;
    }

    public Vector3 Position
    {
        get => Simulation.Bodies[Handle].Pose.Position;
        // Waking on pose/velocity writes is required: bodies sleep after
        // resting, and writing a sleeping body's pose does not wake it.
        set { var b = Simulation.Bodies[Handle]; b.Pose.Position = value; b.Awake = true; }
    }

    public Quaternion Rotation
    {
        get => Simulation.Bodies[Handle].Pose.Orientation;
        set { var b = Simulation.Bodies[Handle]; b.Pose.Orientation = value; b.Awake = true; }
    }

    public Vector3 LinearVelocity
    {
        get => Simulation.Bodies[Handle].Velocity.Linear;
        set { var b = Simulation.Bodies[Handle]; b.Velocity.Linear = value; b.Awake = true; }
    }

    public Vector3 AngularVelocity
    {
        get => Simulation.Bodies[Handle].Velocity.Angular;
        set { var b = Simulation.Bodies[Handle]; b.Velocity.Angular = value; b.Awake = true; }
    }

    public float Mass
    {
        get => _mass;
        set
        {
            _mass = value > 0f ? value : 0.001f;
            if (!IsDynamic) return;

            var b = Simulation.Bodies[Handle];
            var inertia = BepuInertia.ForShape(_shape, _size, _mass);
            if (LockRotation)
                inertia.InverseInertiaTensor = default;
            b.SetLocalInertia(inertia);
            b.Awake = true;
        }
    }

    public void AddImpulse(Vector3 impulse)
    {
        var b = Simulation.Bodies[Handle];
        b.ApplyLinearImpulse(impulse);
        // ApplyLinearImpulse explicitly does not wake the body; do it ourselves
        // so an impulse on a resting body is honored.
        b.Awake = true;
    }

    public void AddForce(Vector3 force)
    {
        _pendingForce += force;
    }

    public void AddAngularImpulse(Vector3 impulse)
    {
        var b = Simulation.Bodies[Handle];
        b.ApplyAngularImpulse(impulse);
        b.Awake = true;
    }

    public void AddTorque(Vector3 torque)
    {
        _pendingTorque += torque;
    }

    public void SetKinematic(bool kinematic)
    {
        if (kinematic == !IsDynamic) return;

        IsDynamic = !kinematic;
        var pose = new RigidPose(Position, Rotation);
        BodyDescription description;
        if (kinematic)
        {
            description = BodyDescription.CreateKinematic(pose, _collidable, _activity);
        }
        else
        {
            var inertia = BepuInertia.ForShape(_shape, _size, _mass);
            if (LockRotation)
                inertia.InverseInertiaTensor = default;
            description = BodyDescription.CreateDynamic(pose, inertia, _collidable, _activity);
        }
        Simulation.Bodies.ApplyDescription(Handle, description);
    }

    /// <summary>
    /// Convert accumulated continuous force/torque into impulses for the step.
    /// Called by <see cref="PhysicsService"/> immediately before Bepu integrates.
    /// </summary>
    internal void FlushPendingForces(float deltaTime)
    {
        if (!IsDynamic)
        {
            _pendingForce = Vector3.Zero;
            _pendingTorque = Vector3.Zero;
            return;
        }

        if (_pendingForce == Vector3.Zero && _pendingTorque == Vector3.Zero)
            return;

        var b = Simulation.Bodies[Handle];
        if (_pendingForce != Vector3.Zero)
            b.ApplyLinearImpulse(_pendingForce * deltaTime);
        if (_pendingTorque != Vector3.Zero)
            b.ApplyAngularImpulse(_pendingTorque * deltaTime);
        b.Awake = true;

        _pendingForce = Vector3.Zero;
        _pendingTorque = Vector3.Zero;
    }
}
