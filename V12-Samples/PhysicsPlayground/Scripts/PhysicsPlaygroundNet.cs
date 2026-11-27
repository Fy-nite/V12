using System;
using System.Collections.Concurrent;
using System.Text.Json;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Networking;

namespace PhysicsPlayground;

/// <summary>
/// Optional multiplayer for the physics playground. Solo stays the default; set
/// <c>V12_MP_MODE=host</c> on one machine and <c>V12_MP_MODE=client</c> on
/// another to share the yard.
/// <para>
/// The level is host-authoritative. Crates are delegated to the client that
/// grabs them (see <c>GrabberComponent</c> and
/// <c>EntityAuthorityStore</c>), so held objects are stepped by their holder and
/// everyone else follows. The local rig (<c>Player</c> and <c>PlayerBody</c>) is
/// marked <see cref="NonPersistentComponent"/>: it is never replicated, and a
/// client makes it its own simulating user so the controller and motor keep
/// ticking on a replica world.
/// </para>
/// </summary>
public sealed class PhysicsPlaygroundNet : IGameService
{
    private const string ModeVariable = "V12_MP_MODE";
    private const string HostVariable = "V12_MP_HOST";
    private const string PortVariable = "V12_MP_PORT";

    private readonly GameRoot _root;
    private readonly ConcurrentQueue<long> _joined = new();
    private readonly ConcurrentQueue<long> _left = new();

    private NetworkWorldLink? _link;
    private long _localPlayerId;
    private bool _started;
    private bool _rigPrepared;
    private bool _rigGranted;

    public PhysicsPlaygroundNet(GameRoot root)
    {
        _root = root;
    }

    public void Initialize(GameRoot g) { }
    public void Update(GameRoot g) { }
    public void Update(float deltaTime) => Tick();

    private void Tick()
    {
        var world = _root.SelectedWorld;
        if (world == null) return;
        var session = _root.FindWorldSession(world);
        if (session == null) return;

        if (!_started)
        {
            _started = true;
            Start(session);
        }

        // The local rig is never replicated, but on a client it still has to be
        // simulated so movement, the motor and the camera keep working. The
        // assignment is local only: the host has its own rig.
        if (!_rigGranted && _link is { IsHost: false } && _localPlayerId != 0)
        {
            _rigPrepared |= MarkLocalRig(world, session);
            if (_rigPrepared)
            {
                GrantLocalRig(world, session);
                _rigGranted = true;
            }
        }

        while (_joined.TryDequeue(out long playerId))
            SendWelcome(playerId);

        while (_left.TryDequeue(out _))
        {
            // Nothing to clean up: clients own no persistent elements by default.
        }
    }

    private void Start(WorldSession session)
    {
        string mode = (Environment.GetEnvironmentVariable(ModeVariable) ?? "off")
            .Trim()
            .ToLowerInvariant();

        if (mode is not ("host" or "client"))
        {
            // Solo (or an unknown value): leave the sample exactly as it was.
            return;
        }

        int port = int.TryParse(Environment.GetEnvironmentVariable(PortVariable), out int p) ? p : 7777;
        string host = Environment.GetEnvironmentVariable(HostVariable) ?? "127.0.0.1";

        // Both peers keep their own rig out of the snapshot before any traffic.
        _rigPrepared = MarkLocalRig(session.World, session);

        try
        {
            if (mode == "host")
            {
                session.Authority = WorldAuthority.Hosted;
                session.HiddenPolicy = HiddenWorldPolicy.Simulate;
                _link = NetworkWorldLink.Host(session, port);
                _link.ClientJoined += id => _joined.Enqueue(id);
                _link.ClientLeft += id => _left.Enqueue(id);

                _localPlayerId = _link.HostPlayerId;
                session.LocalPlayerId = _localPlayerId;

                GameRoot.Log.Information("[PhysicsPlayground] Hosting on udp/{Port}", port);
            }
            else
            {
                session.Authority = WorldAuthority.Remote;
                session.HiddenPolicy = HiddenWorldPolicy.NetworkOnly;
                session.KeepStale = true;
                _link = NetworkWorldLink.Connect(session, host, port);
                _link.ServerMessage += OnServerMessage;
                GameRoot.Log.Information("[PhysicsPlayground] Connecting to {Host}:{Port}", host, port);
            }
        }
        catch (Exception ex)
        {
            GameRoot.Log.Error(ex, "[PhysicsPlayground] Could not start networking");
            _link = null;
        }
    }

    private void OnServerMessage(MessageDTO message)
    {
        if (message.MessageType != MessageType.PlayerWelcome) return;

        try
        {
            var welcome = JsonSerializer.Deserialize<PhysicsPlaygroundWelcome>(message.Message);
            if (welcome is null || welcome.PlayerId == 0) return;

            _localPlayerId = welcome.PlayerId;

            var world = _root.SelectedWorld;
            var session = world != null ? _root.FindWorldSession(world) : null;
            if (session != null) session.LocalPlayerId = _localPlayerId;
        }
        catch
        {
            // A malformed welcome leaves the client as a spectator.
        }
    }

    private void SendWelcome(long playerId)
    {
        _link?.SendToClient(playerId, new MessageDTO
        {
            Sender = new Uri("networkcables://worldhost"),
            MessageType = MessageType.PlayerWelcome,
            Message = JsonSerializer.SerializeToUtf8Bytes(new PhysicsPlaygroundWelcome
            {
                PlayerId = playerId,
            }),
        });
    }

    /// <summary>
    /// Give the rig elements stable ids and mark them non-persistent. Descendants
    /// are covered by the marker, so <c>Player</c> covers the camera and hands;
    /// <c>PlayerBody</c> is a separate root and needs its own.
    /// </summary>
    private static bool MarkLocalRig(World world, WorldSession session)
    {
        bool found = false;
        foreach (string name in new[] { "Player", "PlayerBody" })
        {
            var element = FindByName(world, name);
            if (element == null) continue;

            found = true;
            if (element.Uid == Guid.Empty) element.Uid = Guid.NewGuid();
            if (element.GetComponent<NonPersistentComponent>() == null)
                element.AddComponent(new NonPersistentComponent());
        }

        return found;
    }

    private static void GrantLocalRig(World world, WorldSession session)
    {
        foreach (string name in new[] { "Player", "PlayerBody" })
        {
            var element = FindByName(world, name);
            if (element == null || element.Uid == Guid.Empty) continue;
            session.Authorities.Assign(world, element, session.LocalPlayerId);
        }
    }

    private static IWorldElement? FindByName(World world, string name)
    {
        foreach (var root in world.Root)
        {
            if (string.Equals(root.Name, name, StringComparison.OrdinalIgnoreCase))
                return root;

            var child = root.FindChildByNameRecursive(name);
            if (child != null) return child;
        }

        return null;
    }

    private sealed class PhysicsPlaygroundWelcome
    {
        public long PlayerId { get; set; }
    }
}
