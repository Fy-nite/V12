using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using V12.Core.Interfaces.Physics;

namespace V12.Physics;

/// <summary>
/// A V12 static body backed by Bepu's static collidable set. It has no velocity
/// or force simulation; pose writes update the static description (and its
/// broadphase bounds) in place.
/// </summary>
internal sealed class BepuStaticBody : IPhysicsBody
{
    public StaticHandle Handle { get; }
    public Simulation Simulation { get; }
    public long Id { get; }
    public TypedIndex ShapeIndex { get; }

    public BepuStaticBody(StaticHandle handle, Simulation simulation, long id, TypedIndex shapeIndex)
    {
        Handle = handle;
        Simulation = simulation;
        Id = id;
        ShapeIndex = shapeIndex;
    }

    public Vector3 Position
    {
        get => Simulation.Statics[Handle].Pose.Position;
        set
        {
            var reference = Simulation.Statics[Handle];
            if (reference.Pose.Position == value) return;
            Simulation.Statics.ApplyDescription(Handle,
                new StaticDescription(new RigidPose(value, reference.Pose.Orientation), ShapeIndex));
        }
    }

    public Quaternion Rotation
    {
        get => Simulation.Statics[Handle].Pose.Orientation;
        set
        {
            var reference = Simulation.Statics[Handle];
            if (reference.Pose.Orientation == value) return;
            Simulation.Statics.ApplyDescription(Handle,
                new StaticDescription(new RigidPose(reference.Pose.Position, value), ShapeIndex));
        }
    }

    // Statics have no solver state. Gameplay reading these gets the only
    // sensible answer for an immovable collider.
    public Vector3 LinearVelocity { get => Vector3.Zero; set { } }
    public Vector3 AngularVelocity { get => Vector3.Zero; set { } }
    public bool IsDynamic => false;
    public bool IsCharacterController => false;
    public bool IsOnFloor => false;
    public bool LockRotation { get => true; set { } }
    public float Mass { get => 0f; set { } }
    public float GravityScale { get => 0f; set { } }

    public void AddForce(Vector3 force) { }
    public void AddTorque(Vector3 torque) { }
    public void AddImpulse(Vector3 impulse) { }
    public void AddAngularImpulse(Vector3 impulse) { }
    public void SetKinematic(bool kinematic) { }
}
