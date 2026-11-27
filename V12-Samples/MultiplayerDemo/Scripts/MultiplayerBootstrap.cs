using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using V12.Components;
using V12.Components.Renderables;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;
using V12.Core.Networking;
using V12.Core.Timing;

namespace MultiplayerDemo;

/// <summary>
/// Starts the per-world network link and owns the player avatars.
///
/// The host spawns one body per player and applies that player's input. Clients
/// send small sequenced input commands, predict desktop movement locally, and
/// reconcile from the host's state acks. Every avatar is replicated through the
/// normal world snapshot / element-update path.
///
///   V12_MP_MODE=host|client|off
///   V12_MP_HOST=127.0.0.1
///   V12_MP_PORT=7777
/// </summary>
public sealed class MultiplayerBootstrap : ComponentBase
{
    private IWorldElement? _owner;
    private NetworkWorldLink? _link;
    private MultiplayerPlayerController? _controller;
    private readonly Dictionary<long, IWorldElement> _avatars = new();
    private readonly ConcurrentQueue<long> _joined = new();
    private readonly ConcurrentQueue<long> _left = new();
    private readonly HashSet<long> _loggedInput = new();
    private readonly Dictionary<long, Vector2> _lastLoggedInput = new();
    private static readonly bool DebugInput =
        Environment.GetEnvironmentVariable("V12_MP_DEBUG") == "1";
    private PlayerWelcome? _pendingWelcome;
    private bool _started;
    private bool _playerPumpRegistered;
    private bool _isClient;

    public override string Name => "Multiplayer Bootstrap";
    public override string Description => "Starts the host or client link for this world";

    public override void OnAttach(IWorldElement worldElement)
    {
        base.OnAttach(worldElement);
        _owner = worldElement;

        if (GameRoot.Instance is not { } root) return;

        root.Registry.RegisterOrReplace(
            $"MultiplayerBootstrap:{worldElement.Id}",
            new BootstrapPump(this));

        _controller = worldElement.GetComponent<MultiplayerPlayerController>();
        RegisterPlayerPump(root);
    }

    public override void OnDetach(IWorldElement worldElement)
    {
        if (_link != null)
        {
            _link.ClientJoined -= OnClientJoined;
            _link.ClientLeft -= OnClientLeft;
            _link.CommandReceived -= OnCommandReceived;
            _link.ServerMessage -= OnServerMessage;
            _link.Dispose();
            _link = null;
        }

        if (GameRoot.Instance is { } root)
        {
            root.Registry.Unregister($"MultiplayerBootstrap:{worldElement.Id}");
            root.Registry.Unregister($"MultiplayerPlayerPump:{worldElement.Id}");
        }

        base.OnDetach(worldElement);
    }

    internal void Tick(float deltaTime)
    {
        if (_owner == null) return;

        var root = GameRoot.Instance;
        if (root == null) return;

        _controller ??= _owner.GetComponent<MultiplayerPlayerController>();
        if (RegisterPlayerPump(root))
            _controller!.Tick(deltaTime);

        // Connection events arrive on the LiteNetLib poll thread; the actual
        // world mutation and welcome send happen here on the game thread.
        ProcessClientEvents();
        TryResolveWelcome();

        if (_started) return;

        var world = root.SelectedWorld;
        if (world == null) return;

        var session = root.FindWorldSession(world);
        if (session == null) return;

        string mode = (Environment.GetEnvironmentVariable("V12_MP_MODE") ?? "host")
            .Trim().ToLowerInvariant();

        if (mode is "off" or "none" or "solo")
        {
            _started = true;
            GameRoot.Log.Information("[MultiplayerDemo] Running solo");
            return;
        }

        int port = ReadInt("V12_MP_PORT", 7777);
        string host = Environment.GetEnvironmentVariable("V12_MP_HOST") ?? "127.0.0.1";

        try
        {
            if (mode == "host")
            {
                session.Authority = WorldAuthority.Hosted;
                session.HiddenPolicy = HiddenWorldPolicy.Simulate;
                _link = NetworkWorldLink.Host(session, port);
                _link.ClientJoined += OnClientJoined;
                _link.ClientLeft += OnClientLeft;
                _link.CommandReceived += OnCommandReceived;
                session.LocalPlayerId = _link.HostPlayerId;

                // The host has a body too, so clients can see it.
                var hostAvatar = SpawnAvatar(_link.HostPlayerId);
                if (hostAvatar != null)
                    _controller?.SetAvatar(hostAvatar, remote: false);

                GameRoot.Log.Information("[MultiplayerDemo] Hosting on udp/{Port}", port);
            }
            else if (mode == "client")
            {
                session.Authority = WorldAuthority.Remote;
                session.HiddenPolicy = HiddenWorldPolicy.NetworkOnly;
                session.KeepStale = true;
                _link = NetworkWorldLink.Connect(session, host, port);
                _isClient = true;
                _link.ServerMessage += OnServerMessage;
                GameRoot.Log.Information("[MultiplayerDemo] Connecting to {Host}:{Port}", host, port);
            }
            else
            {
                GameRoot.Log.Warning(
                    "[MultiplayerDemo] Unknown V12_MP_MODE '{Mode}'; use host, client, or off.", mode);
            }

            _started = true;
        }
        catch (Exception ex)
        {
            GameRoot.Log.Error(ex, "[MultiplayerDemo] Could not start networking");
            _started = true;
        }
    }

    internal void SendInput(PlayerInputCommand command)
    {
        if (!_isClient || _link == null) return;

        _link.SendToServer(new MessageDTO
        {
            Sender = new Uri("networkcables://client"),
            MessageType = MessageType.Command,
            Message = JsonSerializer.SerializeToUtf8Bytes(command, MultiplayerJson.Options),
        });
    }

    private void OnClientJoined(long playerId) => _joined.Enqueue(playerId);

    private void OnClientLeft(long playerId) => _left.Enqueue(playerId);

    private void ProcessClientEvents()
    {
        while (_joined.TryDequeue(out long playerId))
        {
            var avatar = SpawnAvatar(playerId, clientDriven: true);
            if (avatar == null) continue;

            _link?.SendToClient(playerId, new MessageDTO
            {
                Sender = new Uri("networkcables://worldhost"),
                MessageType = MessageType.PlayerWelcome,
                Message = JsonSerializer.SerializeToUtf8Bytes(new PlayerWelcome
                {
                    PlayerId = playerId,
                    AvatarId = avatar.Id,
                    AvatarUid = avatar.Uid,
                }, MultiplayerJson.Options),
            });
        }

        while (_left.TryDequeue(out long playerId))
        {
            if (!_avatars.Remove(playerId, out var avatar)) continue;

            if (avatar.Parent != null)
                avatar.Parent.RemoveChild(avatar);
            else
                GameRoot.Instance?.SelectedWorld?.RemoveElement(avatar);
        }
    }

    private void OnCommandReceived(long playerId, MessageDTO message)
    {
        var command = JsonSerializer.Deserialize<PlayerInputCommand>(message.Message, MultiplayerJson.Options);
        if (command == null || !_avatars.TryGetValue(playerId, out var avatar)) return;

        if (DebugInput)
        {
            var move = new Vector2(command.MoveX, command.MoveZ);
            bool changed = !_lastLoggedInput.TryGetValue(playerId, out var last) ||
                           (move - last).LengthSquared() > 0.09f;
            if (changed)
            {
                _lastLoggedInput[playerId] = move;
                GameRoot.Log.Information(
                    "[MultiplayerDemo] Player {PlayerId} input move=({X:F2},{Z:F2}) yaw={Yaw:F2} body={Body} xr={Xr}",
                    playerId, command.MoveX, command.MoveZ, command.Yaw,
                    command.HasBodyPose, command.HasXr);
            }
        }
        else if (_loggedInput.Add(playerId))
        {
            GameRoot.Log.Information(
                "[MultiplayerDemo] Input from player {PlayerId}: move=({X:F2},{Z:F2}) yaw={Yaw:F2} xr={Xr}",
                playerId, command.MoveX, command.MoveZ, command.Yaw, command.HasXr);
        }

        var component = avatar.GetComponent<MultiplayerAvatarComponent>();
        if (component is null) return;

        // Client bodies never integrate host-local input. Desktop commands are
        // applied as deterministic steps; XR clients own their rig and send the
        // captured body pose directly.
        component.ClientDriven = true;

        if (command.HasBodyPose)
        {
            component.SetBodyPose(command.BodyPosition, command.BodyYaw);
        }
        else
        {
            float delta = command.Delta > 0f
                ? command.Delta
                : FixedStepClock.DefaultFixedDeltaTime;
            component.ApplyInputStep(
                command.MoveX, command.MoveZ, command.Yaw, delta, command.Envelope, command.Sequence);
            SendStateAck(playerId, avatar, command.Sequence);
        }

        if (command.HasXr)
            component.SetXr(
                command.HeadPosition, command.HeadRotation,
                command.LeftHandPosition, command.LeftHandRotation,
                command.RightHandPosition, command.RightHandRotation);
    }

    /// <summary>
    /// Echo the last applied input sequence and the authoritative transform to
    /// the owning client. The client replays any inputs newer than this
    /// sequence from the authoritative state, so prediction converges on the
    /// host result without making the player wait for the round trip.
    /// </summary>
    private void SendStateAck(long playerId, IWorldElement avatar, uint sequence)
    {
        if (_isClient || _link is null) return;

        _link.SendToClient(playerId, new MessageDTO
        {
            Sender = new Uri("networkcables://worldhost"),
            MessageType = MessageType.PlayerAction,
            Message = JsonSerializer.SerializeToUtf8Bytes(new PlayerStateAck
            {
                Sequence = sequence,
                Position = avatar.LocalTransform.Position,
                Rotation = avatar.LocalTransform.Rotation,
            }, MultiplayerJson.Options),
        });
    }

    private void OnServerMessage(MessageDTO message)
    {
        switch (message.MessageType)
        {
            case MessageType.PlayerWelcome:
            {
                var welcome = JsonSerializer.Deserialize<PlayerWelcome>(
                    message.Message, MultiplayerJson.Options);
                if (welcome == null) return;

                // The avatar delta and the welcome can arrive in the same frame;
                // defer resolution until the element exists on the game thread.
                _pendingWelcome = welcome;
                TryResolveWelcome();
                break;
            }

            case MessageType.PlayerAction:
            {
                var ack = JsonSerializer.Deserialize<PlayerStateAck>(
                    message.Message, MultiplayerJson.Options);
                if (ack != null)
                    _controller?.ApplyServerState(ack);
                break;
            }
        }
    }

    private void TryResolveWelcome()
    {
        if (_pendingWelcome is not { } welcome) return;

        var root = GameRoot.Instance;
        var world = root?.SelectedWorld;
        if (root == null || world == null) return;

        var avatar = welcome.AvatarUid != Guid.Empty
            ? root.FindElementByUid(world, welcome.AvatarUid)
            : world.FindElementById(welcome.AvatarId);

        if (avatar == null) return;

        // Hide our own body and XR rig; the camera/headset is first person.
        // Other peers still receive and render the avatar.
        HideVisuals(avatar);

        // The client needs to know which player it is before it can decide which
        // elements it simulates (EntityAuthorityStore).
        if (root.FindWorldSession(world) is { } session)
            session.LocalPlayerId = welcome.PlayerId;

        _controller?.SetAvatar(avatar, remote: true);
        _pendingWelcome = null;
        GameRoot.Log.Information("[MultiplayerDemo] Assigned avatar {AvatarId}", welcome.AvatarId);
    }

    private IWorldElement? SpawnAvatar(long playerId, bool clientDriven = false)
    {
        var root = GameRoot.Instance;
        var world = root?.SelectedWorld;
        if (root == null || world == null) return null;

        var element = new Element($"Avatar_{playerId}")
        {
            Uid = Guid.NewGuid(),
            LocalTransform = new TRS(SpawnPosition(_avatars.Count)),
        };

        var mesh = new BoxMesh { Size = new Vector3(0.65f, 1.3f, 0.65f) };
        element.AddComponent(mesh);
        element.AddComponent(new MeshRenderer { Mesh = mesh });
        element.AddComponent(new MaterialComponent(
            0.24f, 0.62f, 0.95f, 1f, metallic: 0.1f, roughness: 0.5f));
        element.AddComponent(new MultiplayerAvatarComponent
        {
            PlayerId = playerId,
            ClientDriven = clientDriven,
        });

        var rig = new Element("XRRig");
        // Child poses are relative to the avatar root, which sits at the body
        // centre (0.65 m above the floor), so keep these offsets body-relative.
        rig.AddChild(AvatarPart("Head", new Vector3(0f, 0.95f, 0f),
            new Vector3(0.26f, 0.26f, 0.26f), new Vector3(0.92f, 0.75f, 0.60f)));
        rig.AddChild(AvatarPart("LeftHand", new Vector3(-0.30f, 0.60f, 0.10f),
            new Vector3(0.12f, 0.12f, 0.22f), new Vector3(0.30f, 0.62f, 0.95f)));
        rig.AddChild(AvatarPart("RightHand", new Vector3(0.30f, 0.60f, 0.10f),
            new Vector3(0.12f, 0.12f, 0.22f), new Vector3(0.30f, 0.62f, 0.95f)));
        element.AddChild(rig);

        // Keep the shared world root wrapper as the parent so a future snapshot
        // still describes the same tree.
        var parent = world.Root.FirstOrDefault(e =>
            string.Equals(e.Name, world.WorldName, StringComparison.OrdinalIgnoreCase))
            ?? world.Root.FirstOrDefault();

        if (parent != null)
            parent.AddChild(element);
        else
            world.AddElement(element);

        _avatars[playerId] = element;
        GameRoot.Log.Information("[MultiplayerDemo] Spawned avatar {AvatarId} for player {PlayerId}",
            element.Id, playerId);
        return element;
    }

    private static Vector3 SpawnPosition(int index)
    {
        // Host spawns nearest the shared cube; later clients spawn behind it so
        // each client can see at least the host avatar in front of the camera.
        float x = index % 2 == 0 ? -0.8f : 0.8f;
        return new Vector3(x, 0.65f, index * 2f);
    }

    private static IWorldElement AvatarPart(string name, Vector3 position, Vector3 size, Vector3 color)
    {
        var part = new Element(name)
        {
            LocalTransform = new TRS(position),
        };
        var mesh = new BoxMesh { Size = size };
        part.AddComponent(mesh);
        part.AddComponent(new MeshRenderer { Mesh = mesh });
        part.AddComponent(new MaterialComponent(
            color.X, color.Y, color.Z, 1f, metallic: 0.05f, roughness: 0.5f));
        return part;
    }

    private static void HideVisuals(IWorldElement element)
    {
        foreach (var component in element.Components.ToArray())
            if (component is MeshRenderer or MeshComponent)
                element.RemoveComponent(component);

        foreach (var child in element.Children.ToArray())
            HideVisuals(child);
    }

    private bool RegisterPlayerPump(GameRoot root)
    {
        if (_playerPumpRegistered || _owner == null || _controller == null) return false;

        root.Registry.RegisterOrReplace(
            $"MultiplayerPlayerPump:{_owner.Id}",
            new PlayerPump(_controller));
        _playerPumpRegistered = true;
        return true;
    }

    private static int ReadInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;

    private sealed class BootstrapPump : IGameService
    {
        private readonly MultiplayerBootstrap _bootstrap;

        public BootstrapPump(MultiplayerBootstrap bootstrap) => _bootstrap = bootstrap;

        public void Initialize(GameRoot g) { }
        public void Update(GameRoot gameRoot) { }
        public void Update(float deltaTime) => _bootstrap.Tick(deltaTime);
    }

    private sealed class PlayerPump : IGameService
    {
        private readonly MultiplayerPlayerController _controller;

        public PlayerPump(MultiplayerPlayerController controller) => _controller = controller;

        public void Initialize(GameRoot g) { }
        public void Update(GameRoot gameRoot) { }
        public void Update(float deltaTime) => _controller.Tick(deltaTime);
    }
}
