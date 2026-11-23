using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using V12.Components;
using V12.Core;
using V12.Core.Networking;
using Xunit;

namespace V12.Architecture.Tests;

public sealed class NetworkWorldLinkTests
{
    [Fact]
    public void HostAndClient_ExchangeSnapshotAndScopedSyncValue()
    {
        int port = FreeUdpPort();

        var hostRoot = new GameRoot();
        var hostWorld = hostRoot.CreateWorld("HostWorld");
        hostWorld.Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var hostProbe = new NetworkProbeComponent();
        var hostElement = new Element("Probe") { Uid = Guid.Parse("22222222-2222-2222-2222-222222222222") };
        hostElement.AddComponent(hostProbe);
        hostWorld.AddElement(hostElement);

        // GameRoot.Instance is process-global. Create the client root last so
        // snapshot application resolves the client's WorldManager.
        var clientRoot = new GameRoot();
        var clientWorld = new World("ClientWorld");
        clientWorld.Id = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var clientSession = clientRoot.WorldManager.OpenWorld(
            clientWorld,
            focus: true,
            authority: WorldAuthority.Remote,
            hiddenPolicy: HiddenWorldPolicy.NetworkOnly);

        var hostSession = hostRoot.FindWorldSession(hostWorld)!;
        hostSession.Sync.Refresh();

        using var hostLink = NetworkWorldLink.Host(hostSession, port);
        using var clientLink = NetworkWorldLink.Connect(clientSession, "127.0.0.1", port);

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            hostRoot.Update(1f / 60f);
            clientRoot.Update(1f / 60f);
            Thread.Sleep(5);

            if (clientWorld.Root.Count > 0 &&
                clientWorld.Root[0].GetComponent<NetworkProbeComponent>()?.Value.Value > 0.25f)
                break;
        }

        var clientElement = Assert.Single(clientWorld.Root);
        var clientProbe = clientElement.GetComponent<NetworkProbeComponent>();
        Assert.NotNull(clientProbe);
        Assert.Equal(hostWorld.Id, clientWorld.Id);
        Assert.True(clientProbe!.Value.Value > 0.25f,
            $"expected a host value, got {clientProbe.Value.Value}");
    }

    [Fact]
    public async System.Threading.Tasks.Task ClientCommand_ReachesHostWithServerPlayerId()
    {
        int port = FreeUdpPort();

        var hostRoot = new GameRoot();
        var hostWorld = hostRoot.CreateWorld("HostWorld");
        hostWorld.AddElement(new Element("Seed"));
        var hostSession = hostRoot.FindWorldSession(hostWorld)!;

        var clientRoot = new GameRoot();
        var clientWorld = new World("ClientWorld");
        var clientSession = clientRoot.WorldManager.OpenWorld(
            clientWorld,
            focus: true,
            authority: WorldAuthority.Remote,
            hiddenPolicy: HiddenWorldPolicy.NetworkOnly);

        using var hostLink = NetworkWorldLink.Host(hostSession, port);
        using var clientLink = NetworkWorldLink.Connect(clientSession, "127.0.0.1", port);

        var received = new System.Threading.Tasks.TaskCompletionSource<(long PlayerId, string Text)>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        hostLink.CommandReceived += (playerId, message) =>
            received.TrySetResult((playerId, Encoding.UTF8.GetString(message.Message)));

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && clientWorld.Root.Count == 0)
        {
            hostRoot.Update(1f / 60f);
            clientRoot.Update(1f / 60f);
            Thread.Sleep(5);
        }

        Assert.True(clientLink.IsConnected, "client did not connect");
        clientLink.SendToServer(new MessageDTO
        {
            Sender = new Uri("networkcables://client"),
            MessageType = MessageType.Command,
            Message = Encoding.UTF8.GetBytes("move-forward"),
        });

        while (DateTime.UtcNow < deadline && !received.Task.IsCompleted)
        {
            hostRoot.Update(1f / 60f);
            clientRoot.Update(1f / 60f);
            Thread.Sleep(5);
        }

        Assert.True(received.Task.IsCompleted, "host did not receive the client command");
        var result = await received.Task;
        Assert.True(result.PlayerId > 0, "host did not attach a server player id");
        Assert.Equal("move-forward", result.Text);
    }

    [Fact]
    public void HostSpawnedElement_ReplicatesToClient()
    {
        int port = FreeUdpPort();

        var hostRoot = new GameRoot();
        var hostWorld = hostRoot.CreateWorld("HostWorld");
        hostWorld.AddElement(new Element("Seed"));
        var hostSession = hostRoot.FindWorldSession(hostWorld)!;

        var clientRoot = new GameRoot();
        var clientWorld = new World("ClientWorld");
        var clientSession = clientRoot.WorldManager.OpenWorld(
            clientWorld,
            focus: true,
            authority: WorldAuthority.Remote,
            hiddenPolicy: HiddenWorldPolicy.NetworkOnly);

        using var hostLink = NetworkWorldLink.Host(hostSession, port);
        using var clientLink = NetworkWorldLink.Connect(clientSession, "127.0.0.1", port);

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && clientWorld.Root.Count == 0)
        {
            hostRoot.Update(1f / 60f);
            clientRoot.Update(1f / 60f);
            Thread.Sleep(5);
        }

        Assert.True(clientLink.IsConnected, "client did not connect");

        hostWorld.AddElement(new Element("SpawnedAvatar"));

        while (DateTime.UtcNow < deadline &&
               !clientWorld.Root.Any(e => e.Name == "SpawnedAvatar"))
        {
            hostRoot.Update(1f / 60f);
            clientRoot.Update(1f / 60f);
            Thread.Sleep(5);
        }

        Assert.Contains(clientWorld.Root, e => e.Name == "SpawnedAvatar");
    }

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private sealed class NetworkProbeComponent : ComponentBase
    {
        public SyncValue<float> Value { get; set; } = new("probe/value", 0f, markDirtyInitially: true);
        private float _elapsed;

        public override void Update(float deltaTime)
        {
            _elapsed += deltaTime;
            Value.Set(_elapsed);
        }

        public override void OnAttach(V12.Core.Core.Interfaces.IWorldElement worldElement)
        {
            base.OnAttach(worldElement);
            Value.OnRemoteApplied += (value, _) => _elapsed = value;
        }
    }
}
