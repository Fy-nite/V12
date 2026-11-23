using System;
using System.Linq;
using V12.Components;
using V12.Core;
using Xunit;

namespace V12.Architecture.Tests;

public sealed class WorldSessionTests
{
    [Fact]
    public void CreateWorld_FocusesHydratesAndSimulates()
    {
        var root = new GameRoot();

        var world = root.CreateWorld("A");
        var session = root.FindWorldSession(world);

        Assert.NotNull(session);
        Assert.Same(world, root.SelectedWorld);
        Assert.Same(world, root.FocusedWorld);
        Assert.Same(session, root.FocusedSession);
        Assert.True(session!.Applied.Hydrated);
        Assert.True(session.Applied.Visible);
        Assert.True(session.Applied.Simulated);
        Assert.False(session.Applied.Subscribed);
        Assert.Contains(world, root.ActiveWorlds);
        Assert.Contains(world, root.VisibleWorlds);
    }

    [Fact]
    public void FocusSwitch_HidesAndPausesThePreviousLocalWorld()
    {
        var root = new GameRoot();
        var a = root.CreateWorld("A");
        var b = root.WorldManager.OpenWorld(new World("B"), focus: true);

        var aSession = root.FindWorldSession(a)!;
        var bSession = root.FindWorldSession(b.World)!;

        Assert.False(aSession.Applied.Visible);
        Assert.False(aSession.Applied.Simulated);
        Assert.True(aSession.Applied.Hydrated, "a local draft must stay hydrated while hidden");
        Assert.DoesNotContain(a, root.ActiveWorlds);

        Assert.True(bSession.Applied.Visible);
        Assert.True(bSession.Applied.Simulated);
        Assert.Same(b.World, root.FocusedWorld);
    }

    [Fact]
    public void HiddenLocalWorld_CanOptIntoSimulation()
    {
        var root = new GameRoot();
        var a = root.CreateWorld("A");
        root.WorldManager.OpenWorld(new World("B"), focus: true);

        var session = root.FindWorldSession(a)!;
        Assert.False(session.Applied.Simulated);

        session.HiddenPolicy = HiddenWorldPolicy.Simulate;

        Assert.True(session.Applied.Simulated);
        Assert.Contains(a, root.ActiveWorlds);
        Assert.DoesNotContain(a, root.VisibleWorlds);
    }

    [Fact]
    public void PinnedLocalWorld_IsVisibleAndSimulated()
    {
        var root = new GameRoot();
        var a = root.CreateWorld("A");
        root.WorldManager.OpenWorld(new World("B"), focus: true);

        var session = root.FindWorldSession(a)!;
        session.Pinned = true;

        Assert.True(session.Applied.Visible);
        Assert.True(session.Applied.Simulated);
        Assert.Contains(a, root.VisibleWorlds);
    }

    [Fact]
    public void HostedHiddenWorld_KeepsSimulatingWhilePlayersAreConnected()
    {
        var root = new GameRoot();
        var world = new World("B");
        var session = root.WorldManager.OpenWorld(
            world,
            focus: false,
            authority: WorldAuthority.Hosted,
            hiddenPolicy: HiddenWorldPolicy.Paused);

        Assert.False(session.Applied.Simulated);

        session.PlayersConnected = 1;

        Assert.True(session.Applied.Simulated);
        Assert.Contains(world, root.ActiveWorlds);

        session.PlayersConnected = 0;

        Assert.False(session.Applied.Simulated);
        Assert.DoesNotContain(world, root.ActiveWorlds);
    }

    [Fact]
    public void RemoteWorld_SubscribesWhenVisibleButNeverSimulatesLocally()
    {
        var root = new GameRoot();
        var world = new World("B");
        var session = root.WorldManager.OpenWorld(
            world,
            focus: false,
            authority: WorldAuthority.Remote,
            hiddenPolicy: HiddenWorldPolicy.Paused);

        Assert.False(session.Applied.Subscribed);
        Assert.False(session.Applied.Hydrated);
        Assert.DoesNotContain(world, root.ActiveWorlds);

        root.FocusWorld(world);

        Assert.True(session.Applied.Visible);
        Assert.True(session.Applied.Subscribed);
        Assert.True(session.Applied.Hydrated);
        Assert.False(session.Applied.Simulated);
        Assert.DoesNotContain(world, root.ActiveWorlds);
        Assert.Contains(world, root.VisibleWorlds);
    }

    [Fact]
    public void RemoteHiddenWorld_WithNetworkOnly_StaysSubscribed()
    {
        var root = new GameRoot();
        var world = new World("B");
        var session = root.WorldManager.OpenWorld(
            world,
            focus: false,
            authority: WorldAuthority.Remote,
            hiddenPolicy: HiddenWorldPolicy.NetworkOnly);

        Assert.True(session.Applied.Subscribed);
        Assert.True(session.Applied.Hydrated);
        Assert.False(session.Applied.Simulated);
        Assert.DoesNotContain(world, root.VisibleWorlds);
    }

    [Fact]
    public void FindElementInLoadedWorlds_FindsPlayerInRemoteSession()
    {
        var root = new GameRoot();
        var world = new World("B");
        var session = root.WorldManager.OpenWorld(
            world,
            focus: true,
            authority: WorldAuthority.Remote,
            hiddenPolicy: HiddenWorldPolicy.NetworkOnly);

        var player = new Element("Player");
        world.AddElement(player);

        Assert.True(session.Applied.Hydrated);
        Assert.False(session.Applied.Simulated);
        Assert.DoesNotContain(world, root.ActiveWorlds);

        // The old global lookup only walks ActiveWorlds, so a Remote session's
        // local rig is invisible to it.
        Assert.Null(root.FindElement(e => e.Name == "Player"));
        Assert.Same(player, root.FindElementInLoadedWorlds(e => e.Name == "Player"));
    }

    [Fact]
    public void RemoteWorld_LinkAssignedAfterFocus_SubscribesAndRequestsSnapshot()
    {
        var root = new GameRoot();
        var world = new World("B");
        var session = root.WorldManager.OpenWorld(
            world,
            focus: false,
            authority: WorldAuthority.Remote,
            hiddenPolicy: HiddenWorldPolicy.Paused);

        root.FocusWorld(world);
        var link = new RecordingWorldLink();
        session.Link = link;

        Assert.True(link.Subscribed);
        Assert.Equal(1, link.SnapshotRequests);
        Assert.Contains(world, root.VisibleWorlds);

        root.DeselectWorld();

        Assert.True(link.Unsubscribed);
        Assert.False(session.Applied.Subscribed);
    }

    [Fact]
    public void DirectlyAddedWorld_GetsAPausedSession()
    {
        var root = new GameRoot();
        var world = new World("Direct");
        root.Worlds.Add(world);

        var session = root.FindWorldSession(world);

        Assert.NotNull(session);
        Assert.True(session!.Applied.Hydrated);
        Assert.False(session.Applied.Visible);
        Assert.False(session.Applied.Simulated);
        Assert.DoesNotContain(world, root.ActiveWorlds);

        root.FocusWorld(world);

        Assert.True(session.Applied.Visible);
        Assert.True(session.Applied.Simulated);
    }

    [Fact]
    public void ClosingHostedWorldWithPlayers_RequiresConfirmation()
    {
        var root = new GameRoot();
        var world = new World("B");
        var session = root.WorldManager.OpenWorld(
            world,
            focus: true,
            authority: WorldAuthority.Hosted,
            hiddenPolicy: HiddenWorldPolicy.Simulate);
        session.PlayersConnected = 2;

        var result = root.CloseWorld(session);

        Assert.Equal(WorldCloseResult.RequiresConfirmation, result);
        Assert.Contains(world, root.Worlds);
        Assert.Contains(session, root.WorldSessions);

        var closed = root.CloseWorld(session, new WorldCloseOptions
        {
            Mode = WorldCloseMode.Shutdown,
            Confirmed = true,
        });

        Assert.Equal(WorldCloseResult.Closed, closed);
        Assert.DoesNotContain(world, root.Worlds);
        Assert.DoesNotContain(session, root.WorldSessions);
        Assert.Null(root.FocusedWorld);
    }

    [Fact]
    public void ClosingRemoteWorld_LeavesWithoutConfirmation()
    {
        var root = new GameRoot();
        var world = new World("Remote");
        var session = root.WorldManager.OpenWorld(
            world,
            focus: true,
            authority: WorldAuthority.Remote,
            hiddenPolicy: HiddenWorldPolicy.NetworkOnly);

        var result = root.CloseWorld(session, new WorldCloseOptions
        {
            Mode = WorldCloseMode.Leave,
            ClearWorld = false,
        });

        Assert.Equal(WorldCloseResult.Left, result);
        Assert.DoesNotContain(world, root.Worlds);
        Assert.DoesNotContain(session, root.WorldSessions);
    }

    [Fact]
    public void ClosingHandoffWithoutCallback_IsUnsupported()
    {
        var root = new GameRoot();
        var world = new World("Hosted");
        var session = root.WorldManager.OpenWorld(
            world,
            focus: true,
            authority: WorldAuthority.Hosted,
            hiddenPolicy: HiddenWorldPolicy.Simulate);
        session.PlayersConnected = 1;

        var result = root.CloseWorld(session, new WorldCloseOptions
        {
            Mode = WorldCloseMode.HandoffTo,
        });

        Assert.Equal(WorldCloseResult.Unsupported, result);
        Assert.Contains(world, root.Worlds);
    }

    [Fact]
    public void WorldScopedFind_DoesNotCrossWorlds()
    {
        var root = new GameRoot();
        var a = root.CreateWorld("A");
        var b = root.WorldManager.OpenWorld(new World("B"), focus: false).World;

        var aElement = new Element("Probe") { Uid = Guid.NewGuid() };
        var bElement = new Element("Probe") { Uid = Guid.NewGuid() };
        a.AddElement(aElement);
        b.AddElement(bElement);

        Assert.Same(aElement, root.FindElement(a, e => e.Name == "Probe"));
        Assert.Same(bElement, root.FindElement(b, e => e.Name == "Probe"));
        Assert.Same(aElement, root.FindElementByUid(a, aElement.Uid));
        Assert.Same(bElement, root.FindElementByUid(b, bElement.Uid));
        Assert.Null(root.FindElementByUid(a, bElement.Uid));
    }

    [Fact]
    public void NonTickingWorld_IsActiveButDoesNotUpdate()
    {
        var root = new GameRoot();
        var world = new World("Scene") { TickingEnabled = false };
        root.Worlds.Add(world);
        root.FocusWorld(world);

        var element = new Element("Probe");
        var probe = new CountingComponent();
        element.AddComponent(probe);
        world.AddElement(element);

        Assert.Contains(world, root.ActiveWorlds);

        root.FixedUpdate(1f / 60f);

        Assert.Equal(0, probe.Count);
    }

    [Fact]
    public void PersistentWorld_RemainsInTheSimulatedSet()
    {
        var root = new GameRoot();

        Assert.Contains(root.PersistentWorld, root.ActiveWorlds);
        Assert.Contains(root.WorldSessions, s => s.World == root.PersistentWorld);
    }

    [Fact]
    public void SessionOwnsPerWorldReplicationState()
    {
        var root = new GameRoot();
        var world = root.CreateWorld("A");
        var session = root.FindWorldSession(world)!;

        Assert.NotNull(session.Cables);
        Assert.Same(session.Sync, session.Dirty.Sync);
        Assert.True(session.Dirty.IsAuthority);

        session.Authority = WorldAuthority.Remote;

        Assert.False(session.Dirty.IsAuthority);
        Assert.False(session.Applied.Simulated);
        Assert.True(session.Applied.Subscribed);
    }

    private sealed class CountingComponent : ComponentBase
    {
        public int Count { get; private set; }

        public override void Update(float deltaTime) => Count++;
    }

    private sealed class RecordingWorldLink : IWorldLink
    {
        public bool IsConnected => true;
        public bool Subscribed { get; private set; }
        public bool Unsubscribed { get; private set; }
        public int SnapshotRequests { get; private set; }

        public void Subscribe(WorldSession session) => Subscribed = true;
        public void Unsubscribe(WorldSession session) => Unsubscribed = true;
        public void RequestSnapshot(WorldSession session, long sinceRevision) => SnapshotRequests++;
        public void Close(WorldSession session, WorldCloseMode mode) { }
    }
}
