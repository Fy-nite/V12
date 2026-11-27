using System;
using System.Collections.Generic;
using System.Linq;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Networking;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// <c>[Sync]</c> field binding: one value per marked member, published only when
/// the field changes, and coexisting with explicit <see cref="SyncValue{T}"/>
/// members on the same component.
/// </summary>
public sealed class SyncFieldBinderTests
{
    [Fact]
    public void MarkedMembers_GetOneValueEach()
    {
        var (session, world) = CreateWorld("A", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var component = world.Root.Single().GetComponent<KnobComponent>()!;

        session.Sync.Refresh();

        var keys = KeysFor(session, world);
        Assert.Contains(keys, k => k.EndsWith("/Speed", StringComparison.Ordinal));
        Assert.Contains(keys, k => k.EndsWith("/Volume", StringComparison.Ordinal));
        Assert.Contains(keys, k => k.EndsWith("/custom/count", StringComparison.Ordinal));

        // Unmarked field and the explicit SyncValue<T> property are not bound as
        // fields; the explicit value is discovered by the member scan instead.
        Assert.DoesNotContain(keys, k => k.EndsWith("/Local", StringComparison.Ordinal));
        Assert.Contains(keys, k => k.EndsWith("/manual", StringComparison.Ordinal));
    }

    [Fact]
    public void Poll_PublishesOnlyChangedFields()
    {
        var (session, world) = CreateWorld("A", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var component = world.Root.Single().GetComponent<KnobComponent>()!;
        session.Sync.Refresh();

        var speed = FindValue(session, world, "/Speed");
        ulong before = speed.Version;

        component.Speed = 1f;                 // unchanged
        component.PollSyncValues();
        Assert.Equal(before, speed.Version);

        component.Speed = 12.5f;              // changed
        component.PollSyncValues();
        Assert.True(speed.Version > before);
        Assert.Equal(12.5f, component.Speed);
    }

    [Fact]
    public void RemoteApply_WritesTheField_AndDoesNotEchoThroughAProperty()
    {
        Guid worldId = Guid.NewGuid(), elementUid = Guid.NewGuid(), componentUid = Guid.NewGuid();
        var (senderSession, senderWorld) = CreateWorld("Sender", worldId, elementUid, componentUid);
        var (receiverSession, receiverWorld) = CreateWorld("Receiver", worldId, elementUid, componentUid);

        var sender = senderWorld.Root.Single().GetComponent<KnobComponent>()!;
        var receiver = receiverWorld.Root.Single().GetComponent<KnobComponent>()!;

        senderSession.Sync.Refresh();
        receiverSession.Sync.Refresh();

        byte[]? batch = null;
        senderSession.Sync.SendBytes = bytes => batch = bytes;

        // Use the property (its setter calls MarkDirty) so the echo guard is the
        // thing under test, not just a plain field write.
        sender.Volume = 0.7f;
        sender.PollSyncValues();
        senderSession.Sync.FlushDirty();
        Assert.NotNull(batch);

        int dirtyOnReceiver = 0;
        receiver.OnDirty += _ => dirtyOnReceiver++;

        int applied = receiverSession.Sync.ApplyIncoming(new ReadOnlyMemory<byte>(batch!));

        Assert.True(applied > 0);
        Assert.Equal(0.7f, receiver.Volume, 3);
        Assert.Equal(0, dirtyOnReceiver);

        // Polling after a remote apply must not republish the same value.
        var receiverVolume = FindValue(receiverSession, receiverWorld, "/Volume");
        ulong version = receiverVolume.Version;
        receiver.PollSyncValues();
        Assert.Equal(version, receiverVolume.Version);
    }

    [Fact]
    public void FixedTick_PollsBoundFields_WithoutAnyUserPlumbing()
    {
        var root = new GameRoot();
        var world = new World("Tick") { Id = Guid.NewGuid() };
        var element = new Element("Probe") { Uid = Guid.NewGuid() };
        var component = new KnobComponent { Uid = Guid.NewGuid() };
        element.AddComponent(component);
        world.AddElement(element);
        var session = root.WorldManager.OpenWorld(world, focus: true);
        session.Sync.Refresh();

        var speed = FindValue(session, world, "/Speed");
        ulong before = speed.Version;

        component.Speed = 9f;
        root.Update(1f / 60f);

        Assert.True(speed.Version > before,
            "the fixed tick did not publish the changed [Sync] field");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (WorldSession Session, World World) CreateWorld(
        string name, Guid worldId, Guid elementUid, Guid componentUid)
    {
        var root = new GameRoot();
        var world = new World(name) { Id = worldId };
        var element = new Element("Probe") { Uid = elementUid };
        var component = new KnobComponent { Uid = componentUid };
        element.AddComponent(component);
        world.AddElement(element);
        return (root.WorldManager.OpenWorld(world, focus: true), world);
    }

    private static List<string> KeysFor(WorldSession session, World world)
        => session.Sync.Registry.AllValues().Select(v => v.Key).ToList();

    private static ISyncValue FindValue(WorldSession session, World world, string suffix)
        => session.Sync.Registry.AllValues()
            .Single(v => v.Key.EndsWith(suffix, StringComparison.Ordinal));

    private sealed class KnobComponent : ComponentBase
    {
        [Sync] public float Speed = 1f;

        [Sync("custom/count")] public int Count = 2;

        private float _volume = 0.5f;

        /// <summary>A user-facing property whose setter dirties the component.</summary>
        [Sync]
        public float Volume
        {
            get => _volume;
            set
            {
                if (MathF.Abs(_volume - value) < 1e-4f) return;
                _volume = value;
                MarkDirty();
            }
        }

        /// <summary>Not marked: stays local.</summary>
        public float Local = 3f;

        /// <summary>Explicit value channel: discovered by the member scan, not bound.</summary>
        [Sync]
        public SyncValue<int>? Manual { get; set; } = new("manual", 0, markDirtyInitially: false);
    }
}
