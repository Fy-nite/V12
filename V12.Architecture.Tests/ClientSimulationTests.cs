using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Networking;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Per-element simulating user: a component runs where its element is simulated.
/// The host simulates everything it has not delegated; a client simulates only
/// the elements delegated to it.
/// </summary>
public sealed class ClientSimulationTests
{
    // ── Tick routing ─────────────────────────────────────────────────────────

    [Fact]
    public void SimulatorScope_TicksOnlyWhereTheElementIsSimulated()
    {
        var world = new World("Routing");
        var element = new Element("Probe") { Uid = Guid.NewGuid() };
        var probe = new TickProbe();
        element.AddComponent(probe);
        world.AddElement(element);

        var authorities = new EntityAuthorityStore();

        // Nothing delegated: the host simulates it, a client does not.
        world.Update(1f / 60f, authorities, localPlayerId: 7, isHost: true);
        Assert.Equal(1, probe.Ticks);

        world.Update(1f / 60f, authorities, localPlayerId: 7, isHost: false);
        Assert.Equal(1, probe.Ticks);

        // Delegated to player 7: that client simulates it, the host stops.
        Assert.Equal(1, authorities.Assign(world, element, 7));

        world.Update(1f / 60f, authorities, localPlayerId: 7, isHost: true);
        Assert.Equal(1, probe.Ticks);

        world.Update(1f / 60f, authorities, localPlayerId: 7, isHost: false);
        Assert.Equal(2, probe.Ticks);

        // A different client still does not simulate it.
        world.Update(1f / 60f, authorities, localPlayerId: 8, isHost: false);
        Assert.Equal(2, probe.Ticks);
    }

    [Fact]
    public void ReplicaScope_TicksOnlyWhereTheElementIsNotSimulated()
    {
        var world = new World("ReplicaScope");
        var element = new Element("Probe") { Uid = Guid.NewGuid() };
        var replica = new ReplicaProbe();
        element.AddComponent(replica);
        world.AddElement(element);

        var authorities = new EntityAuthorityStore();
        Assert.Equal(1, authorities.Assign(world, element, 7));

        // Player 7 owns it: it simulates, so the replica helper stays idle.
        world.Update(1f / 60f, authorities, localPlayerId: 7, isHost: false);
        Assert.Equal(0, replica.Ticks);

        // Player 8 only sees a replica, so its presentation component runs.
        world.Update(1f / 60f, authorities, localPlayerId: 8, isHost: false);
        Assert.Equal(1, replica.Ticks);
    }

    [Fact]
    public void EverywhereScope_TicksOnPeersThatDoNotSimulate()
    {
        var world = new World("Everywhere");
        var element = new Element("Probe") { Uid = Guid.NewGuid() };
        var everywhere = new EverywhereProbe();
        element.AddComponent(everywhere);
        world.AddElement(element);

        var authorities = new EntityAuthorityStore();
        authorities.Assign(world, element, 7);

        world.Update(1f / 60f, authorities, localPlayerId: 8, isHost: false);
        Assert.Equal(1, everywhere.Ticks);
    }

    [Fact]
    public void Release_ReturnsTheElementToTheHost()
    {
        var world = new World("Release");
        var element = new Element("Probe") { Uid = Guid.NewGuid() };
        var probe = new TickProbe();
        element.AddComponent(probe);
        world.AddElement(element);

        var authorities = new EntityAuthorityStore();
        authorities.Assign(world, element, 7);
        Assert.True(authorities.Release(world, element));
        Assert.Equal(0, authorities.GetSimulatingPlayer(world, element));

        world.Update(1f / 60f, authorities, localPlayerId: 8, isHost: true);
        Assert.Equal(1, probe.Ticks);
    }

    [Fact]
    public void Assign_RefusesElementsWithoutAStableUid()
    {
        var world = new World("NoUid");
        var element = new Element("LocalOnly");
        var authorities = new EntityAuthorityStore();

        Assert.Equal(0, authorities.Assign(world, element, 7));
        Assert.Equal(0, authorities.GetSimulatingPlayer(world, element));
        Assert.False(authorities.HasDelegatedEntities);
    }

    [Fact]
    public void CanWrite_IsHostAlways_AndOwnerOtherwise()
    {
        var world = new World("Write");
        var element = new Element("Probe") { Uid = Guid.NewGuid() };
        world.AddElement(element);
        var authorities = new EntityAuthorityStore();
        authorities.Assign(world, element, 7);

        Assert.True(authorities.CanWrite(world, element, playerId: 7, isHost: false));
        Assert.False(authorities.CanWrite(world, element, playerId: 8, isHost: false));
        Assert.True(authorities.CanWrite(world, element, playerId: 8, isHost: true));
    }

    // ── Session integration ──────────────────────────────────────────────────

    [Fact]
    public void Client_SimulatesTheElementTheHostAssignedToIt()
    {
        using var harness = new Loopback();
        harness.PumpUntil(() => harness.ClientWorld.Root.Count > 0, TimeSpan.FromSeconds(8));
        Assert.True(harness.ClientPlayerId > 0, "host never reported a client player id");

        var clientElement = Assert.Single(harness.ClientWorld.Root);
        var clientProbe = clientElement.GetComponent<TickProbe>()!;
        int clientBaseline = clientProbe.Ticks;
        int hostBaseline = harness.HostProbe.Ticks;

        // Host-owned object: the client must not simulate it.
        harness.PumpFor(TimeSpan.FromMilliseconds(200));
        Assert.Equal(clientBaseline, clientProbe.Ticks);
        Assert.True(harness.HostProbe.Ticks > hostBaseline);

        // Hand the object to the client. The host keeps arbitrating, but from
        // here the client is the simulating user.
        Assert.True(harness.HostLink.AssignSimulatingUser(harness.HostElement, harness.ClientPlayerId));

        harness.PumpUntil(
            () => harness.ClientSession.Authorities.GetSimulatingPlayer(
                      harness.ClientWorld, clientElement) == harness.ClientPlayerId,
            TimeSpan.FromSeconds(5));

        int clientBeforeOwnership = clientProbe.Ticks;
        int hostBeforeOwnership = harness.HostProbe.Ticks;

        harness.PumpFor(TimeSpan.FromMilliseconds(200));

        Assert.True(clientProbe.Ticks > clientBeforeOwnership,
            $"client did not simulate its own element ({clientBeforeOwnership} -> {clientProbe.Ticks})");
        Assert.Equal(hostBeforeOwnership, harness.HostProbe.Ticks);
    }

    [Fact]
    public void Client_DoesNotSimulateWhenTheSessionDisablesIt()
    {
        using var harness = new Loopback();
        harness.PumpUntil(() => harness.ClientWorld.Root.Count > 0, TimeSpan.FromSeconds(8));

        var clientElement = Assert.Single(harness.ClientWorld.Root);
        var clientProbe = clientElement.GetComponent<TickProbe>()!;
        harness.HostLink.AssignSimulatingUser(harness.HostElement, harness.ClientPlayerId);
        harness.PumpFor(TimeSpan.FromMilliseconds(100));

        harness.ClientSession.SimulateRemoteWorld = false;
        int baseline = clientProbe.Ticks;

        harness.PumpFor(TimeSpan.FromMilliseconds(200));

        Assert.Equal(baseline, clientProbe.Ticks);
    }

    [Fact]
    public void ClientCannotReportAPoseForAnElementItDoesNotSimulate()
    {
        using var harness = new Loopback();
        harness.PumpUntil(() => harness.ClientWorld.Root.Count > 0, TimeSpan.FromSeconds(8));

        var clientElement = Assert.Single(harness.ClientWorld.Root);
        var original = harness.HostElement.LocalTransform.Position;

        var trs = clientElement.LocalTransform;
        trs.Position = original + new System.Numerics.Vector3(9f, 0f, 0f);
        clientElement.LocalTransform = trs;

        harness.PumpFor(TimeSpan.FromMilliseconds(250));

        var hostPosition = harness.HostElement.LocalTransform.Position;
        Assert.True((hostPosition - original).LengthSquared() < 1e-6f,
            $"host applied an unauthorised pose: {hostPosition}");
    }

    [Fact]
    public void ClientOwnedPose_ReachesTheHostAndTheOtherClient()
    {
        int port = FreeUdpPort();

        var hostRoot = new GameRoot();
        var hostWorld = hostRoot.CreateWorld("HostWorld");
        hostWorld.Id = Guid.NewGuid();
        var hostElement = new Element("Probe") { Uid = Guid.NewGuid() };
        hostWorld.AddElement(hostElement);
        var hostSession = hostRoot.FindWorldSession(hostWorld)!;

        var clientRootA = new GameRoot();
        var worldA = new World("ClientA") { Id = Guid.NewGuid() };
        var sessionA = clientRootA.WorldManager.OpenWorld(
            worldA, focus: true, authority: WorldAuthority.Remote,
            hiddenPolicy: HiddenWorldPolicy.NetworkOnly);

        var clientRootB = new GameRoot();
        var worldB = new World("ClientB") { Id = Guid.NewGuid() };
        var sessionB = clientRootB.WorldManager.OpenWorld(
            worldB, focus: true, authority: WorldAuthority.Remote,
            hiddenPolicy: HiddenWorldPolicy.NetworkOnly);

        using var hostLink = NetworkWorldLink.Host(hostSession, port);
        var joins = new List<long>();
        hostLink.ClientJoined += id => { lock (joins) joins.Add(id); };

        long idA = 0, idB = 0;
        var deadline = Stopwatch.StartNew();

        void PumpAll()
        {
            sessionA.LocalPlayerId = idA;
            sessionB.LocalPlayerId = idB;
            hostRoot.Update(1f / 60f);
            clientRootA.Update(1f / 60f);
            clientRootB.Update(1f / 60f);
            Thread.Sleep(5);
        }

        // Connect sequentially so join order identifies the clients.
        using var linkA = NetworkWorldLink.Connect(sessionA, "127.0.0.1", port);
        while (deadline.Elapsed < TimeSpan.FromSeconds(8) &&
               (worldA.Root.Count == 0 || JoinCount(joins) < 1))
            PumpAll();
        idA = JoinAt(joins, 0);

        using var linkB = NetworkWorldLink.Connect(sessionB, "127.0.0.1", port);
        while (deadline.Elapsed < TimeSpan.FromSeconds(15) &&
               (worldB.Root.Count == 0 || JoinCount(joins) < 2))
            PumpAll();
        idB = JoinAt(joins, 1);

        Assert.True(idA > 0 && idB > 0 && idA != idB, "both clients need a distinct player id");
        Assert.True(worldA.Root.Count > 0 && worldB.Root.Count > 0, "clients did not receive the world");

        // Give the object to client A.
        Assert.True(hostLink.AssignSimulatingUser(hostElement, idA));

        deadline.Restart();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5) &&
               (sessionA.Authorities.GetSimulatingPlayer(worldA, worldA.Root[0]) != idA ||
                sessionB.Authorities.GetSimulatingPlayer(worldB, worldB.Root[0]) != idA))
            PumpAll();

        var elementA = worldA.Root[0];
        var target = new System.Numerics.Vector3(4f, 1f, -2f);
        var trs = elementA.LocalTransform;
        trs.Position = target;
        elementA.LocalTransform = trs;

        deadline.Restart();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5) &&
               ((hostElement.LocalTransform.Position - target).LengthSquared() > 1e-4f ||
                (worldB.Root[0].LocalTransform.Position - target).LengthSquared() > 1e-4f))
            PumpAll();

        Assert.True((hostElement.LocalTransform.Position - target).LengthSquared() < 1e-4f,
            $"host did not apply the owner's pose: {hostElement.LocalTransform.Position}");
        Assert.True((worldB.Root[0].LocalTransform.Position - target).LengthSquared() < 1e-4f,
            $"the other client did not receive the owner's pose: {worldB.Root[0].LocalTransform.Position}");
    }

    private static int JoinCount(List<long> joins)
    {
        lock (joins) return joins.Count;
    }

    private static long JoinAt(List<long> joins, int index)
    {
        lock (joins) return joins.Count > index ? joins[index] : 0L;
    }

    [Fact]
    public void ClientClaim_MakesItTheSimulatingUser()
    {
        using var harness = new Loopback();
        harness.PumpUntil(() => harness.ClientWorld.Root.Count > 0, TimeSpan.FromSeconds(8));
        Assert.True(harness.ClientPlayerId > 0, "host never reported a client player id");

        var clientElement = Assert.Single(harness.ClientWorld.Root);
        Assert.True(
            (harness.HostSession.Authorities.GetSimulatingPlayer(harness.HostWorld, harness.HostElement)) == 0,
            "element should start host-owned");

        Assert.True(harness.ClientLink.RequestSimulatingUser(clientElement));

        harness.PumpUntil(
            () => harness.ClientSession.Authorities.GetSimulatingPlayer(
                      harness.ClientWorld, clientElement) == harness.ClientPlayerId,
            TimeSpan.FromSeconds(5));

        Assert.Equal(harness.ClientPlayerId,
            harness.HostSession.Authorities.GetSimulatingPlayer(harness.HostWorld, harness.HostElement));
        Assert.True(harness.ClientSession.IsSimulatedLocally(clientElement));
        Assert.False(harness.HostSession.IsSimulatedLocally(harness.HostElement));

        // And it can hand it back.
        Assert.True(harness.ClientLink.ReleaseSimulatingUser(clientElement));
        harness.PumpUntil(
            () => harness.HostSession.Authorities.GetSimulatingPlayer(
                      harness.HostWorld, harness.HostElement) == 0,
            TimeSpan.FromSeconds(5));

        Assert.True(harness.HostSession.IsSimulatedLocally(harness.HostElement));
    }

    [Fact]
    public void DisconnectingClient_HandsItsElementsBackToTheHost()
    {
        using var harness = new Loopback();
        harness.PumpUntil(() => harness.ClientWorld.Root.Count > 0, TimeSpan.FromSeconds(8));
        Assert.True(harness.HostLink.AssignSimulatingUser(harness.HostElement, harness.ClientPlayerId));

        harness.PumpUntil(
            () => harness.ClientSession.IsSimulatedLocally(
                      Assert.Single(harness.ClientWorld.Root)),
            TimeSpan.FromSeconds(5));
        Assert.False(harness.HostSession.IsSimulatedLocally(harness.HostElement));

        // Closing the client must notify the host, not wait for its timeout.
        harness.ClientLink.Dispose();

        harness.PumpUntil(
            () => harness.HostSession.Authorities.GetSimulatingPlayer(
                      harness.HostWorld, harness.HostElement) == 0,
            TimeSpan.FromSeconds(5));

        Assert.True(harness.HostSession.IsSimulatedLocally(harness.HostElement),
            "the host did not take the element back after the owner left");
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed class Loopback : IDisposable
    {
        private readonly NetworkWorldLink _hostLink;
        private readonly NetworkWorldLink _clientLink;

        public Loopback()
        {
            int port = FreeUdpPort();

            HostRoot = new GameRoot();
            HostWorld = HostRoot.CreateWorld("HostWorld");
            HostWorld.Id = Guid.NewGuid();

            HostElement = new Element("Probe") { Uid = Guid.NewGuid() };
            HostProbe = new TickProbe();
            HostElement.AddComponent(HostProbe);
            HostWorld.AddElement(HostElement);

            var hostSession = HostRoot.FindWorldSession(HostWorld)!;
            HostSession = hostSession;

            ClientRoot = new GameRoot();
            ClientWorld = new World("ClientWorld") { Id = Guid.NewGuid() };
            ClientSession = ClientRoot.WorldManager.OpenWorld(
                ClientWorld,
                focus: true,
                authority: WorldAuthority.Remote,
                hiddenPolicy: HiddenWorldPolicy.NetworkOnly);

            _hostLink = NetworkWorldLink.Host(hostSession, port);
            _hostLink.ClientJoined += id => ClientPlayerId = id;
            ClientSession.LocalPlayerId = ClientPlayerId;

            _clientLink = NetworkWorldLink.Connect(ClientSession, "127.0.0.1", port);
        }

        public GameRoot HostRoot { get; }
        public GameRoot ClientRoot { get; }
        public World HostWorld { get; }
        public World ClientWorld { get; }
        public WorldSession ClientSession { get; }
        public WorldSession HostSession { get; }
        public IWorldElement HostElement { get; }
        public TickProbe HostProbe { get; }
        public NetworkWorldLink HostLink => _hostLink;
        public NetworkWorldLink ClientLink => _clientLink;
        public long ClientPlayerId { get; private set; }

        public void PumpUntil(Func<bool> condition, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < timeout && !condition())
            {
                Pump();
            }

            Assert.True(condition(), "timed out waiting for the loopback");
        }

        public void PumpFor(TimeSpan duration)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < duration)
            {
                Pump();
            }
        }

        private void Pump()
        {
            // ClientJoined fires on the network poll thread, possibly after the
            // client has already started; keep the session id in sync so the
            // client knows what it owns.
            ClientSession.LocalPlayerId = ClientPlayerId;
            HostRoot.Update(1f / 60f);
            ClientRoot.Update(1f / 60f);
            Thread.Sleep(5);
        }

        public void Dispose()
        {
            _clientLink.Dispose();
            _hostLink.Dispose();
        }
    }

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private sealed class TickProbe : ComponentBase
    {
        public int Ticks;
        public override void Update(float deltaTime) => Ticks++;
    }

    private sealed class ReplicaProbe : ComponentBase
    {
        public int Ticks;

        public ReplicaProbe()
        {
            SimulationScope = ComponentSimulationScope.Replica;
        }

        public override void Update(float deltaTime) => Ticks++;
    }

    private sealed class EverywhereProbe : ComponentBase
    {
        public int Ticks;

        public EverywhereProbe()
        {
            SimulationScope = ComponentSimulationScope.Everywhere;
        }

        public override void Update(float deltaTime) => Ticks++;
    }
}
