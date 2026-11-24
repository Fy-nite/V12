using System;
using System.Numerics;
using V12.Basic.Components;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Physics;
using V12.Core.Interfaces.Renderer;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Covers the double-origin / local-bubble contract. Global positions must not
/// change when the bubble is re-centred; only the local coordinates move.
/// </summary>
public sealed class FloatingOriginTests
{
    [Fact]
    public void RebaseOrigin_ShiftsRootsAndKeepsGlobalPositions()
    {
        var world = new World("OriginTest");

        var root = new Element("Root");
        root.LocalTransform = new TRS
        {
            Position = new Vector3(100f, 0f, 0f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };

        var child = new Element("Child");
        child.LocalTransform = new TRS
        {
            Position = new Vector3(10f, 0f, 0f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };

        var probe = new ShiftProbe();
        child.AddComponent(probe);
        root.AddChild(child);
        world.AddElement(root);

        var rootBefore = world.GlobalPositionOf(root);
        var childBefore = world.GlobalPositionOf(child);

        Assert.True(world.RebaseOrigin(new Vector3(100f, 0f, 0f)));

        Assert.Equal(0f, root.LocalTransform.Position.X, 3);
        Assert.Equal(10f, child.LocalTransform.Position.X, 3);
        Assert.Equal(100d, world.Origin.X, 3);
        Assert.Equal(1, world.OriginRebaseCount);

        // The world moved, but the global answer did not.
        Assert.Equal(rootBefore.X, world.GlobalPositionOf(root).X, 3);
        Assert.Equal(childBefore.X, world.GlobalPositionOf(child).X, 3);
        Assert.Equal(100f, probe.Delta.X, 3);
    }

    [Fact]
    public void FloatingOriginComponent_RebasesWhenTheAnchorLeavesTheBubble()
    {
        var world = new World("OriginTest");
        var anchor = new Element("Anchor");
        anchor.LocalTransform = new TRS
        {
            Position = new Vector3(0f, 0f, 250f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        world.AddElement(anchor);

        var component = new FloatingOriginComponent
        {
            World = world,
            Anchor = anchor,
            Threshold = 100f,
        };
        anchor.AddComponent(component);

        component.Update(0f);

        Assert.Equal(0f, anchor.LocalTransform.Position.Length(), 3);
        Assert.Equal(250d, world.Origin.Z, 3);
        Assert.Equal(1, world.OriginRebaseCount);
    }

    [Fact]
    public void PhysicsBody_OnOriginShift_MovesTheBackendBody()
    {
        var body = new TestBody { Position = new Vector3(10f, 0f, -4f) };
        var component = new PhysicsBodyComponent { Body = body };

        component.OnOriginShift(new Vector3(4f, 0f, -1f));

        Assert.Equal(6f, body.Position.X, 3);
        Assert.Equal(-3f, body.Position.Z, 3);
    }

    private sealed class ShiftProbe : ComponentBase, IFloatingOriginAware
    {
        public Vector3 Delta { get; private set; }

        public void OnOriginShift(Vector3 delta) => Delta += delta;
    }

    private sealed class TestBody : IPhysicsBody
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
        public Vector3 LinearVelocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public bool IsDynamic => true;
        public bool IsCharacterController => false;
        public bool IsOnFloor => false;
        public bool LockRotation { get; set; }
        public float Mass { get; set; } = 1f;
        public float GravityScale { get; set; } = 1f;

        public void AddForce(Vector3 force) { }
        public void AddTorque(Vector3 torque) { }
        public void AddImpulse(Vector3 impulse) { }
        public void AddAngularImpulse(Vector3 impulse) { }
        public void SetKinematic(bool kinematic) { }
    }
}
