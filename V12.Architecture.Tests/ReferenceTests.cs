using System.Linq;
using V12.Components;
using V12.Core;
using V12.Core.NetworkCable;
using V12.Core.Networking;
using V12.Core.References;
using V12.WorldML;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>Test-only component so reference behaviour does not depend on a
/// production component such as PortalComponent.</summary>
public sealed class RefProbeComponent : ComponentBase
{
    public override string Name => "RefProbe";

    public ElementRef ElementTarget { get; set; } = new();

    public ComponentRef ComponentTarget { get; set; } = new();
}

public sealed class ReferenceTests
{
    [Fact]
    public void ElementRef_RoundTrips_WithoutInjectingUids()
    {
        var root = new GameRoot();
        var world = new World("Refs");
        root.Worlds.Add(world);
        root.SelectWorld(world);

        var doorA = new Element("Door A");
        var doorB = new Element("Door B");
        doorB.AddComponent(new PortalComponent());
        doorA.AddComponent(new PortalComponent { ExitPortalRef = ElementRef.From(doorB) });
        world.AddElement(doorA);
        world.AddElement(doorB);

        var xml = WorldMLSerializer.Serialize(world.Root, "Refs");

        Assert.DoesNotContain("uid=", xml);
        Assert.DoesNotContain("ExitPortalElementId", xml);
        Assert.Contains("ExitPortalRef=\"Door B\"", xml);

        var parsed = (Element)new WorldMLParser().Parse(xml);
        var parsedWorld = new World("Refs");
        foreach (var child in parsed.Children.ToList())
        {
            parsed.RemoveChild(child);
            parsedWorld.AddElement(child);
        }
        root.Worlds.Add(parsedWorld);
        root.SelectWorld(parsedWorld);

        var parsedA = parsedWorld.Root.Single(e => e.Name == "Door A");
        var parsedB = parsedWorld.Root.Single(e => e.Name == "Door B");
        var portal = parsedA.GetComponent<PortalComponent>();

        Assert.NotNull(portal);
        Assert.Equal("Door B", portal!.ExitPortalRef.Selector);
        Assert.Same(parsedB, portal.ExitPortalRef.Resolve(parsedA));
    }

    [Fact]
    public void ElementRef_UsesIndexedPath_WhenNamesCollide()
    {
        var root = new GameRoot();
        var world = new World("Refs");
        root.Worlds.Add(world);
        root.SelectWorld(world);

        var doors = new Element("Doors");
        var doorA = new Element("Door");
        var doorB = new Element("Door");
        doors.AddChild(doorA);
        doors.AddChild(doorB);
        world.AddElement(doors);

        var source = new Element("Source");
        source.AddComponent(new PortalComponent { ExitPortalRef = ElementRef.From(doorB) });
        world.AddElement(source);

        var xml = WorldMLSerializer.Serialize(world.Root, "Refs");
        Assert.Contains("ExitPortalRef=\"Doors/Door[2]\"", xml);

        var parsed = (Element)new WorldMLParser().Parse(xml);
        var parsedWorld = new World("Refs");
        foreach (var child in parsed.Children.ToList())
        {
            parsed.RemoveChild(child);
            parsedWorld.AddElement(child);
        }
        root.Worlds.Add(parsedWorld);
        root.SelectWorld(parsedWorld);

        var parsedSource = parsedWorld.Root.Single(e => e.Name == "Source");
        var parsedDoors = parsedWorld.Root.Single(e => e.Name == "Doors");
        var parsedB = parsedDoors.Children.Where(e => e.Name == "Door").ElementAt(1);
        Assert.Same(parsedB, parsedSource.GetComponent<PortalComponent>()!.ExitPortalRef.Resolve(parsedSource));
    }

    [Fact]
    public void PinnedUid_RoundTrips_AndResolvesAfterRename()
    {
        var root = new GameRoot();
        var world = new World("Refs");
        root.Worlds.Add(world);
        root.SelectWorld(world);

        var door = new Element("Door B") { Uid = Guid.NewGuid() };
        var source = new Element("Source");
        source.AddComponent(new PortalComponent
        {
            ExitPortalRef = new ElementRef("@" + door.Uid.ToString("N")),
        });
        world.AddElement(source);
        world.AddElement(door);

        var xml = WorldMLSerializer.Serialize(world.Root, "Refs");
        Assert.Contains($"uid=\"{door.Uid:N}\"", xml);

        var parsed = (Element)new WorldMLParser().Parse(xml);
        var parsedWorld = new World("Refs");
        foreach (var child in parsed.Children.ToList())
        {
            parsed.RemoveChild(child);
            parsedWorld.AddElement(child);
        }
        root.Worlds.Add(parsedWorld);
        root.SelectWorld(parsedWorld);

        var parsedDoor = parsedWorld.Root.Single(e => e.Name == "Door B");
        var parsedSource = parsedWorld.Root.Single(e => e.Name == "Source");
        var portal = parsedSource.GetComponent<PortalComponent>()!;

        Assert.Equal(door.Uid, parsedDoor.Uid);
        Assert.Same(parsedDoor, portal.ExitPortalRef.Resolve(parsedSource));

        parsedDoor.Name = "Renamed Door";
        Assert.Same(parsedDoor, portal.ExitPortalRef.Resolve(parsedSource));
    }

    [Fact]
    public void ElementRef_ResolvesSuffixPath_AndIndex()
    {
        var root = new GameRoot();
        var world = new World("Refs");
        root.Worlds.Add(world);
        root.SelectWorld(world);

        var doors = new Element("Doors");
        var doorA = new Element("Door");
        var doorB = new Element("Door");
        doors.AddChild(doorA);
        doors.AddChild(doorB);
        world.AddElement(doors);

        Assert.Null(ReferenceResolver.ResolveElement(world, "Doors/Door"));
        Assert.Same(doorA, ReferenceResolver.ResolveElement(world, "Doors/Door[1]"));
        Assert.Same(doorA, ReferenceResolver.ResolveElement(world, "Door[1]"));
        Assert.Same(doorB, ReferenceResolver.ResolveElement(world, "Door[2]"));
        Assert.Null(ReferenceResolver.ResolveElement(world, "Door"));
    }

    [Fact]
    public void PinnedElementRef_SurvivesResolveThenSerialize()
    {
        var root = new GameRoot();
        var world = new World("Refs");
        root.Worlds.Add(world);
        root.SelectWorld(world);

        var uid = Guid.NewGuid();
        var door = new Element("Door B") { Uid = uid };
        var source = new Element("Source");
        var probe = new RefProbeComponent
        {
            ElementTarget = new ElementRef("@" + uid.ToString("N")),
        };
        source.AddComponent(probe);
        world.AddElement(source);
        world.AddElement(door);

        Assert.Same(door, probe.ElementTarget.Resolve(source));
        Assert.Equal("@" + uid.ToString("N"), probe.ElementTarget.ToSelector());

        var xml = WorldMLSerializer.Serialize(world.Root, "Refs");
        Assert.Contains($"ElementTarget=\"@{uid:N}\"", xml);
    }

    [Fact]
    public void PinnedComponentRef_SurvivesResolveThenSerialize()
    {
        var root = new GameRoot();
        var world = new World("Refs");
        root.Worlds.Add(world);
        root.SelectWorld(world);

        var uid = Guid.NewGuid();
        var door = new Element("Door B");
        var mesh = new MeshComponent { Uid = uid, Name = "frame" };
        door.AddComponent(mesh);

        var source = new Element("Source");
        var selector = $"Door B#@{uid:N}";
        var probe = new RefProbeComponent
        {
            ComponentTarget = new ComponentRef(selector),
        };
        source.AddComponent(probe);
        world.AddElement(source);
        world.AddElement(door);

        Assert.Same(mesh, probe.ComponentTarget.Resolve(source));
        Assert.Equal(selector, probe.ComponentTarget.ToSelector());

        var xml = WorldMLSerializer.Serialize(world.Root, "Refs");
        Assert.Contains($"ComponentTarget=\"{selector}\"", xml);
    }

    [Fact]
    public void ElementRef_ClearsCache_AfterTargetRemoved()
    {
        var root = new GameRoot();
        var world = new World("Refs");
        root.Worlds.Add(world);
        root.SelectWorld(world);

        var door = new Element("Door B");
        var source = new Element("Source");
        var probe = new RefProbeComponent
        {
            ElementTarget = new ElementRef("Door B"),
        };
        source.AddComponent(probe);
        world.AddElement(source);
        world.AddElement(door);

        Assert.Same(door, probe.ElementTarget.Resolve(source));

        world.RemoveElement(door);

        Assert.Null(probe.ElementTarget.Resolve(source));
    }

    [Fact]
    public void ComponentRef_ClearsCache_AfterComponentRemoved()
    {
        var root = new GameRoot();
        var world = new World("Refs");
        root.Worlds.Add(world);
        root.SelectWorld(world);

        var door = new Element("Door B");
        var mesh = new MeshComponent { Name = "frame" };
        door.AddComponent(mesh);

        var source = new Element("Source");
        var probe = new RefProbeComponent
        {
            ComponentTarget = new ComponentRef("Door B#frame"),
        };
        source.AddComponent(probe);
        world.AddElement(source);
        world.AddElement(door);

        Assert.Same(mesh, probe.ComponentTarget.Resolve(source));

        door.RemoveComponent(mesh);

        Assert.Null(probe.ComponentTarget.Resolve(source));
    }

    [Fact]
    public void ElementUid_RoundTripsThroughNetworkElementDto()
    {
        var uid = Guid.NewGuid();
        var door = new Element("Door B") { Uid = uid };

        var dto = AncientCompressor.CompressElement(door);
        Assert.NotNull(dto);
        Assert.Equal(uid, dto!.Uid);

        var restored = AncientCompressor.DecompressElement(dto);
        Assert.NotNull(restored);
        Assert.Equal(uid, restored!.Uid);
    }

    [Fact]
    public void ComponentNameAndUid_ChangesRaiseDirty()
    {
        var mesh = new MeshComponent();
        int dirty = 0;
        mesh.OnDirty += _ => dirty++;

        mesh.Name = "frame";
        Assert.Equal(1, dirty);

        mesh.Uid = Guid.NewGuid();
        Assert.Equal(2, dirty);

        mesh.Name = "frame";
        Assert.Equal(2, dirty);
    }

    [Fact]
    public void ElementUidChange_RaisesDirty()
    {
        var element = new Element("Door");
        int dirty = 0;
        element.OnDirty += _ => dirty++;

        element.Uid = Guid.NewGuid();
        Assert.Equal(1, dirty);

        element.Uid = Guid.NewGuid();
        Assert.Equal(2, dirty);
    }

    [Fact]
    public void ElementUid_IsCarriedByWorldElementUpdate()
    {
        BsonConfig.Initialize();
        var root = new GameRoot();
        var cables = new NetworkCables();
        var tracker = new DirtyTracker(cables, "test://tracker")
        {
            IsAuthority = true,
            MaxBatchSize = 0,
        };

        var world = new World("Refs");
        root.Worlds.Add(world);
        root.SelectWorld(world);
        var uid = Guid.NewGuid();
        var door = new Element("Door") { Uid = uid };
        world.AddElement(door);
        tracker.TrackWorld(world);

        door.Name = "Renamed Door";
        Assert.Equal(1, tracker.PendingElementCount);
        tracker.SendDirtyUpdates();

        Assert.True(cables.SendQueue.TryDequeue(out var message));
        Assert.Equal(MessageType.WorldElementUpdate, message.MessageType);

        var batch = AncientCompressor.Decompress<ElementUpdateBatchDTO>(message.Message);
        var update = Assert.Single(batch.Elements);
        Assert.Equal(uid, update.Uid);
        Assert.Equal(world.Id, update.WorldId);
    }

    [Fact]
    public void WorldId_RoundTripsThroughWorldSnapshot()
    {
        BsonConfig.Initialize();
        var world = new World("Refs") { Id = Guid.NewGuid() };
        world.AddElement(new Element("Door"));

        var bytes = AncientCompressor.Compress(world);
        var restored = AncientCompressor.Decompress<World>(bytes);

        Assert.Equal(world.Id, restored.Id);
    }

    [Fact]
    public void FindElementById_IsScopedToTheWorld()
    {
        var worldA = new World("A");
        var worldB = new World("B");
        var elementA = new Element("Door A") { Id = 42 };
        var elementB = new Element("Door B") { Id = 42 };
        worldA.AddElement(elementA);
        worldB.AddElement(elementB);

        Assert.Same(elementA, worldA.FindElementById(42));
        Assert.Same(elementB, worldB.FindElementById(42));
        Assert.Null(worldA.FindElementById(99));
    }
}
