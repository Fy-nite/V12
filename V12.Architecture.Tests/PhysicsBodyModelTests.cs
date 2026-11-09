using System.Numerics;
using V12.Basic.Building;
using V12.Basic.Components;
using V12.Components;
using V12.Core;
using V12.Core.Interfaces.Physics;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Covers the generic physics body model: <see cref="PhysicsBodyType"/> mapping,
/// the legacy <see cref="PhysicsBodyDesc"/> constructor, and the
/// <see cref="PhysicsBodyComponent"/> velocity pass-through.
/// </summary>
public sealed class PhysicsBodyModelTests
{
    [Fact]
    public void LegacyDesc_MapsFlagsToBodyType()
    {
        var pos = Vector3.Zero;
        var rot = Quaternion.Identity;
        var size = Vector3.One;

        var dynamicBody = new PhysicsBodyDesc(pos, rot, MeshShape.Box, size,
            mass: 2f, isKinematic: false);
        Assert.Equal(PhysicsBodyType.Dynamic, dynamicBody.Type);
        Assert.False(dynamicBody.IsKinematic);
        Assert.False(dynamicBody.IsCharacterController);

        var kinematicBody = new PhysicsBodyDesc(pos, rot, MeshShape.Box, size,
            isKinematic: true);
        Assert.Equal(PhysicsBodyType.Kinematic, kinematicBody.Type);
        Assert.True(kinematicBody.IsKinematic);
        Assert.False(kinematicBody.IsCharacterController);

        var characterBody = new PhysicsBodyDesc(pos, rot, MeshShape.Capsule, size,
            isKinematic: true, isCharacterController: true);
        Assert.Equal(PhysicsBodyType.Character, characterBody.Type);
        Assert.True(characterBody.IsKinematic);
        Assert.True(characterBody.IsCharacterController);
    }

    [Fact]
    public void Desc_NewConstructorCarriesSettings()
    {
        var desc = new PhysicsBodyDesc(Vector3.Zero, Quaternion.Identity, MeshShape.Box, Vector3.One,
            mass: 5f, type: PhysicsBodyType.Dynamic, gravityScale: 0.5f,
            linearDamping: 0.2f, angularDamping: 0.3f, canSleep: false);

        Assert.Equal(5f, desc.Mass);
        Assert.Equal(PhysicsBodyType.Dynamic, desc.Type);
        Assert.Equal(0.5f, desc.GravityScale);
        Assert.Equal(0.2f, desc.LinearDamping);
        Assert.Equal(0.3f, desc.AngularDamping);
        Assert.False(desc.CanSleep);
    }

    [Fact]
    public void Component_BodyTypeDrivesLegacyIsKinematic()
    {
        var body = new PhysicsBodyComponent();

        Assert.Equal(PhysicsBodyType.Dynamic, body.BodyType);
        Assert.False(body.IsKinematic);

        body.IsKinematic = true;
        Assert.Equal(PhysicsBodyType.Kinematic, body.BodyType);

        body.BodyType = PhysicsBodyType.Character;
        Assert.True(body.IsKinematic);

        body.BodyType = PhysicsBodyType.Dynamic;
        Assert.False(body.IsKinematic);
    }

    [Fact]
    public void Component_VelocityAndForceForwardToBody()
    {
        var body = new PhysicsBodyComponent();
        Assert.Equal(Vector3.Zero, body.LinearVelocity);
        Assert.False(body.IsOnFloor);

        // Written before the backend body exists: queued and readable back.
        body.LinearVelocity = new Vector3(1f, 2f, 3f);
        Assert.Equal(new Vector3(1f, 2f, 3f), body.LinearVelocity);

        var fake = new FakeBody { IsOnFloor = true };
        body.Body = fake;

        body.LinearVelocity = new Vector3(4f, 5f, 6f);
        Assert.Equal(new Vector3(4f, 5f, 6f), fake.LinearVelocity);
        Assert.True(body.IsOnFloor);

        body.AddForce(new Vector3(0f, 10f, 0f));
        Assert.Equal(new Vector3(0f, 10f, 0f), fake.LastForce);
    }

    [Fact]
    public void AddPhysics_WithType_SetsBodyType()
    {
        var element = new Element("Player");
        var body = element.AddPhysics(PhysicsBodyType.Character, gravityScale: 0.25f);

        Assert.Equal(PhysicsBodyType.Character, body.BodyType);
        Assert.Equal(0.25f, body.GravityScale);
        Assert.True(body.IsKinematic);
    }

    private sealed class FakeBody : IPhysicsBody
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 LinearVelocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public bool IsDynamic { get; set; }
        public bool IsCharacterController { get; set; }
        public bool IsOnFloor { get; set; }
        public bool LockRotation { get; set; }
        public Vector3 LastForce { get; private set; }
        public Vector3 LastTorque { get; private set; }

        public void AddForce(Vector3 force) => LastForce = force;
        public void AddTorque(Vector3 torque) => LastTorque = torque;
        public void SetKinematic(bool kinematic) => IsDynamic = !kinematic;
    }
}
