using System;
using System.Numerics;
using V12.Basic.Components;
using V12.Components;
using V12.Core;
using V12.Core.Interfaces.Physics;
using V12.Core.Systems;
using V12.Physics;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Physics follows the simulating user: a dynamic body where this peer
/// simulates the element, a kinematic follower everywhere else, rebuilt when the
/// role flips.
/// </summary>
public sealed class SimulatingUserPhysicsTests
{
    [Fact]
    public void BodyIsDynamicForTheSimulator_AndKinematicForEveryoneElse()
    {
        var root = new GameRoot();
        using var physics = new PhysicsService { AutoStep = false, Gravity = Vector3.Zero };
        physics.Initialize(root);
        root.Registry.Register("Physics", physics);

        var system = new PhysicsLocomotionSystem(root);
        system.Initialize(root);

        var world = root.WorldManager.OpenWorld(new World("Sim"), focus: true).World;
        var element = new Element("Box") { Uid = Guid.NewGuid() };
        element.AddComponent(new PhysicsBodyComponent());
        element.AddComponent(new ColliderComponent(MeshShape.Box, 1f, 1f, 1f));
        world.AddElement(element);

        var session = root.FindWorldSession(world)!;

        // Host-owned: simulate.
        system.Update(1f / 60f);
        var component = element.GetComponent<PhysicsBodyComponent>()!;
        Assert.NotNull(component.Body);
        Assert.False(component.IsReplicated);
        Assert.True(component.Body!.IsDynamic);

        // Delegated to a player: the host becomes a follower and the body is
        // rebuilt, not converted in place.
        var dynamicBody = component.Body;
        session.Authorities.Assign(world, element, 7);
        system.Update(1f / 60f);

        Assert.True(component.IsReplicated);
        Assert.NotNull(component.Body);
        Assert.NotSame(dynamicBody, component.Body);
        Assert.False(component.Body!.IsDynamic);

        // Released: the host simulates again with a dynamic body.
        session.Authorities.Release(world, element);
        system.Update(1f / 60f);

        Assert.False(component.IsReplicated);
        Assert.True(component.Body!.IsDynamic);
    }
}
