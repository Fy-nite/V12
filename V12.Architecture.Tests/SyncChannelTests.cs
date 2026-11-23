using System;
using System.Linq;
using V12.Components;
using V12.Core;
using V12.Core.NetworkCable;
using V12.Core.Networking;
using Xunit;

namespace V12.Architecture.Tests;

public sealed class SyncChannelTests
{
    [Fact]
    public void ScopedRegistry_KeysIncludeWorldElementAndComponent()
    {
        var root = new GameRoot();
        var worldId = Guid.NewGuid();
        var elementUid = Guid.NewGuid();
        var componentUid = Guid.NewGuid();

        var world = new World("A") { Id = worldId };
        var element = new Element("Probe") { Uid = elementUid };
        var component = new SyncProbeComponent { Uid = componentUid };
        element.AddComponent(component);
        world.AddElement(element);

        var session = root.WorldManager.OpenWorld(world, focus: true);
        session.Sync.Refresh();

        string scoped = $"world/{worldId:N}/element/{elementUid:N}/component/{componentUid:N}/probe/value";
        Assert.True(session.Sync.Registry.TryGet(scoped, out var value));
        Assert.Equal(typeof(int), value.ValueType);
        Assert.False(session.Sync.Registry.TryGet("probe/value", out _));
    }

    [Fact]
    public void TwoSessions_DoNotShareRawSyncKeys()
    {
        var root = new GameRoot();
        var a = CreateWorld("A", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var b = CreateWorld("B", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var aSession = root.WorldManager.OpenWorld(a, focus: true);
        var bSession = root.WorldManager.OpenWorld(b, focus: false);

        string aKey = ScopedKey(a, aSession.World.Id);
        string bKey = ScopedKey(b, bSession.World.Id);

        Assert.True(aSession.Sync.Registry.TryGet(aKey, out _));
        Assert.False(aSession.Sync.Registry.TryGet(bKey, out _));
        Assert.True(bSession.Sync.Registry.TryGet(bKey, out _));
        Assert.False(bSession.Sync.Registry.TryGet(aKey, out _));
    }

    [Fact]
    public void ApplyIncoming_MatchesOnlyTheSameScope()
    {
        var root = new GameRoot();
        var sharedWorldId = Guid.NewGuid();
        var sharedElementUid = Guid.NewGuid();
        var sharedComponentUid = Guid.NewGuid();

        var senderWorld = CreateWorld("Sender", sharedWorldId, sharedElementUid, sharedComponentUid);
        var receiverWorld = CreateWorld("Receiver", sharedWorldId, sharedElementUid, sharedComponentUid);

        var senderSession = root.WorldManager.OpenWorld(senderWorld, focus: true);
        var receiverSession = root.WorldManager.OpenWorld(receiverWorld, focus: false);

        var senderProbe = senderWorld.Root.Single().GetComponent<SyncProbeComponent>()!;
        var receiverProbe = receiverWorld.Root.Single().GetComponent<SyncProbeComponent>()!;

        byte[]? batch = null;
        senderSession.Sync.SendBytes = bytes => batch = bytes;
        senderProbe.Value.Set(42);
        senderSession.Sync.FlushDirty();

        Assert.NotNull(batch);
        Assert.Equal(0, receiverProbe.Value.Value);

        int applied = receiverSession.Sync.ApplyIncoming(new ReadOnlyMemory<byte>(batch!));

        Assert.Equal(1, applied);
        Assert.Equal(42, receiverProbe.Value.Value);
    }

    [Fact]
    public void DirtyTracker_WithSessionChannel_FlushesScopedBatch()
    {
        var root = new GameRoot();
        var worldId = Guid.NewGuid();
        var world = CreateWorld("A", worldId, Guid.NewGuid(), Guid.NewGuid());
        var session = root.WorldManager.OpenWorld(world, focus: true);

        var cables = new NetworkCables();
        var tracker = new DirtyTracker(cables, "test://session")
        {
            IsAuthority = true,
            HasConnectedPeer = () => true,
            Sync = session.Sync,
        };

        byte[]? sent = null;
        session.Sync.SendBytes = bytes => sent = bytes;

        world.Root.Single().GetComponent<SyncProbeComponent>()!.Value.Set(7);
        tracker.SendDirtyUpdates();

        Assert.NotNull(sent);
        var keys = SyncBatchBuilder.EnumerateBatch(new ReadOnlyMemory<byte>(sent!))
            .Select(item => item.key)
            .ToList();
        Assert.Contains(keys, key =>
            key.StartsWith($"world/{worldId:N}/element/", StringComparison.Ordinal) &&
            key.EndsWith("/probe/value", StringComparison.Ordinal));
    }

    private static World CreateWorld(string name, Guid worldId, Guid elementUid, Guid componentUid)
    {
        var world = new World(name) { Id = worldId };
        var element = new Element("Probe") { Uid = elementUid };
        element.AddComponent(new SyncProbeComponent { Uid = componentUid });
        world.AddElement(element);
        return world;
    }

    private static string ScopedKey(World world, Guid worldId)
    {
        var element = world.Root.Single();
        var component = element.GetComponent<SyncProbeComponent>()!;
        return $"world/{worldId:N}/element/{element.Uid:N}/component/{component.Uid:N}/probe/value";
    }

    private sealed class SyncProbeComponent : ComponentBase
    {
        public SyncValue<int> Value { get; set; } = new("probe/value", 0, markDirtyInitially: false);
    }
}
