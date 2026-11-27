using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using V12.Components;
using V12.Core;
using V12.Core.Networking;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Multi-writer behaviour for <see cref="SyncValue{T}"/>: last-write-wins
/// ordering, and a client write reaching another client through the host.
/// </summary>
public sealed class ValueSyncMultiWriterTests
{
    // ── Version layout ───────────────────────────────────────────────────────

    [Fact]
    public void PackedVersion_OrdersByClockThenWriter()
    {
        ulong a = SyncValue<int>.PackVersion(5, 3);
        ulong b = SyncValue<int>.PackVersion(5, 4);
        ulong c = SyncValue<int>.PackVersion(6, 1);

        Assert.True(a < b);
        Assert.True(b < c);
        Assert.Equal(5UL, SyncValue<int>.ClockOf(a));
        Assert.Equal((ushort)3, SyncValue<int>.WriterOf(a));
    }

    // ── Conflict resolution ──────────────────────────────────────────────────

    [Fact]
    public void SameClock_HigherWriterWinsAndTheLowerOneCannotReturn()
    {
        var value = new SyncValue<int>("test/mw/tie", 0, markDirtyInitially: false) { WriterId = 1 };

        // Two peers wrote in the same millisecond. The writer id is the
        // tie-break, and the loser cannot re-apply afterwards.
        ulong low = SyncValue<int>.PackVersion(10, 500);
        ulong high = SyncValue<int>.PackVersion(10, 900);

        Assert.True(value.TryApplyRemote(1, low));
        Assert.True(value.TryApplyRemote(2, high));
        Assert.False(value.TryApplyRemote(1, low));

        Assert.Equal(2, value.Value);
        Assert.Equal(high, value.Version);
    }

    [Fact]
    public void NewerClock_BeatsAHigherWriterId()
    {
        var value = new SyncValue<int>("test/mw/newer", 0, markDirtyInitially: false) { WriterId = 60000 };

        // Local stamp is clock 10 with a huge writer id; the remote write is one
        // clock tick later with the smallest writer id. Time must win.
        Assert.True(value.TryApplyRemote(42, SyncValue<int>.PackVersion(10, 60001)));
        Assert.True(value.TryApplyRemote(43, SyncValue<int>.PackVersion(11, 1)));

        Assert.Equal(43, value.Value);
    }

    [Fact]
    public void StaleAndDuplicateVersions_AreRejected()
    {
        var value = new SyncValue<int>("test/mw/stale", 0, markDirtyInitially: false);
        ulong stamp = SyncValue<int>.PackVersion(100, 7);

        Assert.True(value.TryApplyRemote(1, stamp));
        Assert.False(value.TryApplyRemote(2, stamp));
        Assert.False(value.TryApplyRemote(3, SyncValue<int>.PackVersion(99, 7)));

        Assert.Equal(1, value.Value);
    }

    [Fact]
    public void RemoteClockForcesTheLocalClockForward()
    {
        var value = new SyncValue<int>("test/mw/clock", 0, markDirtyInitially: false) { WriterId = 7 };

        // A stamp far in the future: the local clock is behind it, the accepted
        // update is adopted, and the next local write must still sort after it.
        ulong future = SyncValue<int>.PackVersion(10_000_000_000_000UL, 1);
        Assert.True(value.TryApplyRemote(5, future));

        value.Set(6);
        Assert.True(value.Version > future, $"expected {value.Version} > {future}");
        Assert.Equal(6, value.Value);
    }

    [Fact]
    public void AlternatingWriters_AlwaysAgreeOnTheLatestWrite()
    {
        var a = new SyncValue<int>("test/mw/alt-a", 0, markDirtyInitially: false) { WriterId = 11 };
        var b = new SyncValue<int>("test/mw/alt-b", 0, markDirtyInitially: false) { WriterId = 22 };

        a.Set(1);
        Assert.True(b.TryApplyRemote(a.Value, a.Version));

        Thread.Sleep(2); // hybrid clock has millisecond resolution
        b.Set(2);
        Assert.True(a.TryApplyRemote(b.Value, b.Version));

        Assert.Equal(2, a.Value);
        Assert.Equal(2, b.Value);
    }

    [Fact]
    public void ChannelStampsItsWriterIdOntoFlushedValues()
    {
        var root = new GameRoot();
        var world = new World("WriterStamp");
        var element = new Element("Probe") { Uid = Guid.NewGuid() };
        var probe = new ProbeComponent { Uid = Guid.NewGuid() };
        element.AddComponent(probe);
        world.AddElement(element);

        var session = root.WorldManager.OpenWorld(world, focus: true);
        session.Sync.WriterId = 4242;
        session.Sync.Refresh();

        session.Sync.SendBytes = _ => { };
        probe.Value.Set(9f);
        session.Sync.FlushDirty();

        Assert.Equal((ushort)4242, probe.Value.WriterId);
        Assert.Equal((ushort)4242, SyncValue<float>.WriterOf(probe.Value.Version));
    }

    // ── Relay: client → host → other client ──────────────────────────────────

    [Fact]
    public void OwnerValueWrite_ReachesTheHostAndTheOtherClient()
    {
        int port = FreeUdpPort();

        var hostRoot = new GameRoot();
        var hostWorld = hostRoot.CreateWorld("HostWorld");
        hostWorld.Id = Guid.NewGuid();
        var hostElement = new Element("Probe") { Uid = Guid.NewGuid() };
        hostElement.AddComponent(new ProbeComponent { Uid = Guid.NewGuid() });
        hostWorld.AddElement(hostElement);
        var hostSession = hostRoot.FindWorldSession(hostWorld)!;
        hostSession.Sync.Refresh();

        var clientA = CreateClientRoot();
        var clientB = CreateClientRoot();

        using var hostLink = NetworkWorldLink.Host(hostSession, port);
        var joins = new List<long>();
        hostLink.ClientJoined += id => { lock (joins) joins.Add(id); };

        long idA = 0, idB = 0;
        var deadline = Stopwatch.StartNew();

        void Pump()
        {
            clientA.Session.LocalPlayerId = idA;
            clientB.Session.LocalPlayerId = idB;
            hostRoot.Update(1f / 60f);
            clientA.Root.Update(1f / 60f);
            clientB.Root.Update(1f / 60f);
            Thread.Sleep(5);
        }

        using var linkA = NetworkWorldLink.Connect(clientA.Session, "127.0.0.1", port);
        while (deadline.Elapsed < TimeSpan.FromSeconds(8) &&
               (clientA.World.Root.Count == 0 || JoinCount(joins) < 1))
            Pump();
        idA = JoinAt(joins, 0);

        using var linkB = NetworkWorldLink.Connect(clientB.Session, "127.0.0.1", port);
        while (deadline.Elapsed < TimeSpan.FromSeconds(15) &&
               (clientB.World.Root.Count == 0 || JoinCount(joins) < 2))
            Pump();
        idB = JoinAt(joins, 1);

        Assert.True(idA > 0 && idB > 0 && idA != idB, "both clients need a distinct player id");
        Assert.True(linkA.IsConnected && linkB.IsConnected, "client links did not connect");

        var probeA = clientA.World.Root[0].GetComponent<ProbeComponent>()!;
        var probeB = clientB.World.Root[0].GetComponent<ProbeComponent>()!;
        var hostProbe = hostElement.GetComponent<ProbeComponent>()!;

        // A value write is only accepted from the element's simulating user.
        Assert.True(hostLink.AssignSimulatingUser(hostElement, idA));
        deadline.Restart();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5) &&
               clientA.Session.Authorities.GetSimulatingPlayer(clientA.World, clientA.World.Root[0]) != idA)
            Pump();

        probeA.Value.Set(0.75f);
        deadline.Restart();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5) && probeB.Value.Value < 0.74f)
            Pump();

        Assert.True(MathF.Abs(hostProbe.Value.Value - 0.75f) < 0.001f,
            $"host did not apply the owner's write, got {hostProbe.Value.Value}");
        Assert.True(MathF.Abs(probeB.Value.Value - 0.75f) < 0.001f,
            $"other client did not receive the forwarded write, got {probeB.Value.Value}");
        // The channel stamps its own writer identity; only the host pins it to
        // its player id, clients use a random tie-break id.
        Assert.NotEqual((ushort)0, SyncValue<float>.WriterOf(probeA.Value.Version));
    }

    [Fact]
    public void ClientCannotWriteAValueForAnElementItDoesNotSimulate()
    {
        int port = FreeUdpPort();

        var hostRoot = new GameRoot();
        var hostWorld = hostRoot.CreateWorld("HostWorld");
        hostWorld.Id = Guid.NewGuid();
        var hostElement = new Element("Probe") { Uid = Guid.NewGuid() };
        hostElement.AddComponent(new ProbeComponent { Uid = Guid.NewGuid() });
        hostWorld.AddElement(hostElement);
        var hostSession = hostRoot.FindWorldSession(hostWorld)!;
        hostSession.Sync.Refresh();

        var client = CreateClientRoot();

        using var hostLink = NetworkWorldLink.Host(hostSession, port);
        var joins = new List<long>();
        hostLink.ClientJoined += id => { lock (joins) joins.Add(id); };

        long id = 0;
        var deadline = Stopwatch.StartNew();
        using var link = NetworkWorldLink.Connect(client.Session, "127.0.0.1", port);

        void Pump()
        {
            client.Session.LocalPlayerId = id;
            hostRoot.Update(1f / 60f);
            client.Root.Update(1f / 60f);
            Thread.Sleep(5);
        }

        while (deadline.Elapsed < TimeSpan.FromSeconds(8) &&
               (client.World.Root.Count == 0 || JoinCount(joins) < 1))
            Pump();
        id = JoinAt(joins, 0);

        var probe = client.World.Root[0].GetComponent<ProbeComponent>()!;
        var hostProbe = hostElement.GetComponent<ProbeComponent>()!;

        probe.Value.Set(0.5f);
        deadline.Restart();
        while (deadline.Elapsed < TimeSpan.FromMilliseconds(300))
            Pump();

        Assert.Equal(0f, hostProbe.Value.Value);
    }

    private static int JoinCount(List<long> joins)
    {
        lock (joins) return joins.Count;
    }

    private static long JoinAt(List<long> joins, int index)
    {
        lock (joins) return joins.Count > index ? joins[index] : 0L;
    }

    private sealed class ClientRoot
    {
        public ClientRoot(GameRoot root, World world, WorldSession session)
        {
            Root = root;
            World = world;
            Session = session;
        }

        public GameRoot Root { get; }
        public World World { get; }
        public WorldSession Session { get; }
    }

    private static ClientRoot CreateClientRoot()
    {
        var root = new GameRoot();
        var world = new World("ClientWorld") { Id = Guid.NewGuid() };
        var session = root.WorldManager.OpenWorld(
            world,
            focus: true,
            authority: WorldAuthority.Remote,
            hiddenPolicy: HiddenWorldPolicy.NetworkOnly);
        return new ClientRoot(root, world, session);
    }

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private sealed class ProbeComponent : ComponentBase
    {
        public SyncValue<float> Value { get; set; } =
            new("probe/value", 0f, markDirtyInitially: false);
    }
}
