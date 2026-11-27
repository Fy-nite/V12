using System;
using System.IO;
using System.Linq;
using System.Numerics;
using V12.Basic.Components;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Physics;
using V12.Core.Networking;
using V12.Core.Systems;
using V12.Physics;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Closing a world must detach and destroy its components. Without it, event
/// subscriptions, script runtimes and physics bodies outlive the world.
/// </summary>
public sealed class WorldTeardownTests
{
    [Fact]
    public void DetachAll_DetachesEveryComponentRecursively()
    {
        var world = new World("Teardown");
        var parent = new Element("Parent");
        var parentProbe = new DetachProbe();
        parent.AddComponent(parentProbe);

        var child = new Element("Child");
        var childProbe = new DetachProbe();
        child.AddComponent(childProbe);
        parent.AddChild(child);
        world.AddElement(parent);

        world.DetachAll();

        Assert.Equal(1, parentProbe.Detached);
        Assert.Equal(1, parentProbe.Destroyed);
        Assert.Equal(1, childProbe.Detached);
        Assert.Equal(1, childProbe.Destroyed);
    }

    [Fact]
    public void CloseWorld_DetachesComponents()
    {
        var root = new GameRoot();
        var world = new World("Closing");
        var element = new Element("Probe");
        var probe = new DetachProbe();
        element.AddComponent(probe);
        world.AddElement(element);

        var session = root.WorldManager.OpenWorld(world);
        Assert.Equal(WorldCloseResult.Closed, root.CloseWorld(session));

        Assert.Equal(1, probe.Detached);
        Assert.Equal(1, probe.Destroyed);
    }

    [Fact]
    public void CloseWorld_DestroysPhysicsBodies()
    {
        var root = new GameRoot();
        using var physics = new PhysicsService { AutoStep = false, Gravity = Vector3.Zero };
        physics.Initialize(root);
        root.Registry.Register("Physics", physics);

        var system = new PhysicsLocomotionSystem(root);
        system.Initialize(root);

        var world = new World("Bodies");
        var element = new Element("Crate");
        var body = new PhysicsBodyComponent();
        element.AddComponent(body);
        element.AddComponent(new ColliderComponent(MeshShape.Box, 1f, 1f, 1f));
        world.AddElement(element);

        var session = root.WorldManager.OpenWorld(world, focus: true);
        system.Update(1f / 60f);

        Assert.NotNull(body.Body);
        Assert.Equal(1, physics.BodyCount);

        root.CloseWorld(session);

        Assert.Null(body.Body);
        Assert.Equal(0, physics.BodyCount);
        Assert.Equal(0, physics.StaticBodyCount);
    }

    [Fact]
    public void RemovingAWorldFromTheRoot_AlsoDetachesIt()
    {
        var root = new GameRoot();
        var world = new World("Direct");
        var element = new Element("Probe");
        var probe = new DetachProbe();
        element.AddComponent(probe);
        world.AddElement(element);

        root.WorldManager.OpenWorld(world);
        root.Worlds.Remove(world);

        // Sessions reconcile against Worlds on the next enumeration.
        _ = root.WorldManager.Sessions.Count();

        Assert.Equal(1, probe.Detached);
        Assert.Equal(1, probe.Destroyed);
    }

    [Fact]
    public void ContractComponent_DetachReleasesTheVmHandle()
    {
        string path = Path.Combine(Path.GetTempPath(), $"v12_pin_{Guid.NewGuid():N}.ct");
        File.WriteAllText(path, """
            Contract PinProbe {
                fn on_detach() { }
            }
            """);

        try
        {
            var registry = new V12.Bindings.ContractComponentRegistry();
            var entries = registry.LoadFile(path);
            Assert.NotEmpty(entries);

            var component = registry.Create("PinProbe");
            Assert.NotNull(component);

            // Allocate roots the VM object: the host holds the handle, so the
            // interpreter's stack scan cannot see it.
            Assert.Equal(1, entries[0].Runtime.PinnedHandleCount);

            var element = new Element("Host");
            element.AddComponent(component!);
            Assert.Equal(1, entries[0].Runtime.PinnedHandleCount);

            element.RemoveComponent(component!);

            Assert.Equal(0, entries[0].Runtime.PinnedHandleCount);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void DetachAll_UnregistersExplicitSyncValues()
    {
        string key = $"test/lifetime/{Guid.NewGuid():N}";
        var value = new SyncValue<float>(key, 1f, markDirtyInitially: false);

        var world = new World("Values");
        var element = new Element("Probe");
        element.AddComponent(new ValueProbe { Value = value });
        world.AddElement(element);

        Assert.True(SyncRegistry.Default.TryGet(key, out var registered));
        Assert.Same(value, registered);

        world.DetachAll();

        Assert.False(SyncRegistry.Default.TryGet(key, out _));
    }

    [Fact]
    public void RemoveComponent_UnregistersSyncValues()
    {
        string key = $"test/lifetime/{Guid.NewGuid():N}";
        var value = new SyncValue<float>(key, 1f, markDirtyInitially: false);
        var component = new ValueProbe { Value = value };

        var element = new Element("Probe");
        element.AddComponent(component);
        Assert.True(SyncRegistry.Default.TryGet(key, out _));

        element.RemoveComponent(component);

        Assert.False(SyncRegistry.Default.TryGet(key, out _));
    }

    [Fact]
    public void Unregister_DoesNotRemoveAReplacementOnTheSameKey()
    {
        string key = $"test/lifetime/{Guid.NewGuid():N}";
        var first = new SyncValue<float>(key, 1f, markDirtyInitially: false);
        var second = new SyncValue<float>(key, 2f, markDirtyInitially: false);

        // The later value wins at registration.
        Assert.True(SyncRegistry.Default.TryGet(key, out var registered));
        Assert.Same(second, registered);

        // Teardown of the first must not delete the live one.
        first.Unregister();
        Assert.True(SyncRegistry.Default.TryGet(key, out registered));
        Assert.Same(second, registered);

        second.Unregister();
        Assert.False(SyncRegistry.Default.TryGet(key, out _));
    }

    private sealed class ValueProbe : ComponentBase
    {
        public SyncValue<float> Value { get; set; } =
            new($"test/probe/{Guid.NewGuid():N}", 0f, markDirtyInitially: false);
    }

    private sealed class DetachProbe : ComponentBase
    {
        public int Detached;
        public int Destroyed;

        public override void OnDetach(IWorldElement worldElement)
        {
            Detached++;
            base.OnDetach(worldElement);
        }

        public override void OnDestroy()
        {
            Destroyed++;
            base.OnDestroy();
        }
    }
}
