using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using V12.Components;
using V12.Core;
using V12.Core.Input;
using V12.Core.Introspection;
using V12.Core.Interfaces.Renderer;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Coverage for the engine-side introspection surface: snapshots are a flat,
/// consistent read of the live tree, and every mutation goes through the queue
/// at the tick boundary instead of touching the world from the caller's thread.
/// </summary>
public sealed class SceneIntrospectionTests
{
    private static (GameRoot Root, World World) NewRoot()
    {
        var root = new GameRoot();
        var world = new World("Scene");
        root.Worlds.Add(world);
        root.SelectWorld(world);
        return (root, world);
    }

    private static CommandReceipt Run(GameRoot root, SceneCommand command, long tick = 1)
    {
        var task = root.Introspection.Enqueue(command);
        root.Introspection.DrainCommands(tick);
        Assert.True(task.IsCompleted, "the drain must complete the command's receipt");
        return task.Result;
    }

    [Fact]
    public void Snapshot_FlattensHierarchy_AndReadsComponentProperties()
    {
        var (root, world) = NewRoot();

        var parent = new Element("Root");
        var child = new Element("Lamp");
        var light = new GenericLightComponent { Energy = 3f };
        child.AddComponent(light);
        parent.AddChild(child);
        world.AddElement(parent);

        var snapshot = root.Introspection.CaptureSnapshot();

        var parentSnapshot = snapshot.Elements.Single(e => e.Id == parent.Id);
        var childSnapshot = snapshot.Elements.Single(e => e.Id == child.Id);

        Assert.Equal(0, parentSnapshot.ParentId);
        Assert.Equal(0, parentSnapshot.Depth);
        Assert.Equal(parent.Id, childSnapshot.ParentId);
        Assert.Equal(1, childSnapshot.Depth);
        Assert.Equal(world.Id, childSnapshot.WorldId);
        Assert.Contains(snapshot.Worlds, w => w.Id == world.Id);

        var lightSnapshot = childSnapshot.Components.Single(c => c.Type == nameof(GenericLightComponent));
        Assert.True(lightSnapshot.Properties.ContainsKey("Energy"),
            "Energy is authoritative state and must appear in the snapshot");
        Assert.Equal(3.0, lightSnapshot.Properties["Energy"].Number, 3);
    }

    [Fact]
    public void Snapshot_SkipsRenderPayloadProperties()
    {
        var (root, world) = NewRoot();
        var element = new Element("Box");
        element.AddComponent(new MeshComponent());
        world.AddElement(element);

        var snapshot = root.Introspection.CaptureSnapshot();
        var mesh = snapshot.Elements.Single(e => e.Id == element.Id)
            .Components.Single(c => c.Type == nameof(MeshComponent));

        // Mesh points would be megabytes of JSON; the snapshot must stay small.
        Assert.DoesNotContain("MeshPoints", mesh.Properties.Keys);
        Assert.DoesNotContain("Indices", mesh.Properties.Keys);
    }

    [Fact]
    public void SetTransform_AppliesOnDrain_AndReportsThePreviousPose()
    {
        var (root, world) = NewRoot();
        var element = new Element("Mover");
        world.AddElement(element);

        var receipt = Run(root, new SceneCommand
        {
            Op = SceneOp.SetTransform,
            WorldId = world.Id,
            TargetId = element.Id,
            Transform = new TRS(position: new Vector3(1, 2, 3)),
        });

        Assert.True(receipt.Accepted, receipt.Error);
        Assert.Equal(new Vector3(1, 2, 3), element.LocalTransform.Position);
        Assert.NotNull(receipt.PreviousTransform);
        Assert.Equal(Vector3.Zero, receipt.PreviousTransform!.Value.Position);
    }

    [Fact]
    public void DuplicateRequestId_IsAppliedOnce()
    {
        var (root, world) = NewRoot();
        var command = new SceneCommand
        {
            RequestId = "spawn-once",
            Op = SceneOp.Spawn,
            WorldId = world.Id,
            Name = "Statue",
        };

        var first = Run(root, command);
        var second = Run(root, command);

        Assert.True(first.Accepted, first.Error);
        Assert.True(second.Accepted, second.Error);
        Assert.Equal(first.CreatedId, second.CreatedId);
        Assert.Single(world.Root, e => e.Name == "Statue");
    }

    [Fact]
    public void SetActive_MarksTheFrameDirty()
    {
        var (root, world) = NewRoot();
        var element = new Element("Toggle");
        world.AddElement(element);
        root.ConsumeRenderDirty();

        var receipt = Run(root, new SceneCommand
        {
            Op = SceneOp.SetActive,
            WorldId = world.Id,
            TargetId = element.Id,
            Value = SnapshotValue.From(false),
        });

        Assert.True(receipt.Accepted, receipt.Error);
        Assert.False(element.Active);
        Assert.True(root.ConsumeRenderDirty(),
            "Element.Active is not dirty-tracked, so the command must mark the frame itself");
    }

    [Fact]
    public void RemoteWorld_RejectsWrites()
    {
        var root = new GameRoot();
        var remote = new World("Remote");
        root.WorldManager.OpenWorld(
            remote,
            focus: false,
            authority: WorldAuthority.Remote,
            source: WorldSourceKind.Remote);

        var element = new Element("Mirror");
        remote.AddElement(element);

        var receipt = Run(root, new SceneCommand
        {
            Op = SceneOp.SetActive,
            WorldId = remote.Id,
            TargetId = element.Id,
            Value = SnapshotValue.From(false),
        });

        Assert.False(receipt.Accepted);
        Assert.Contains("remote", receipt.Error, System.StringComparison.OrdinalIgnoreCase);
        Assert.True(element.Active, "a rejected command must not change the element");
    }

    [Fact]
    public async Task PausedSimulation_StillAppliesCommands()
    {
        var (root, world) = NewRoot();
        var element = new Element("Paused");
        world.AddElement(element);

        root.SimulationPaused = true;
        var tickBefore = root.SimulationTick;

        var task = root.Introspection.Enqueue(new SceneCommand
        {
            Op = SceneOp.SetTransform,
            WorldId = world.Id,
            TargetId = element.Id,
            Transform = new TRS(position: new Vector3(4, 0, 0)),
        });

        root.Update(0.05f);

        Assert.True(task.IsCompleted, "a paused clock must still drain on the render tick");
        var receipt = await task;
        Assert.True(receipt.Accepted, receipt.Error);
        Assert.Equal(new Vector3(4, 0, 0), element.LocalTransform.Position);
        Assert.Equal(tickBefore, root.SimulationTick);
    }

    [Fact]
    public void AttachComponent_CreatesTracksAndReturnsTheNewId()
    {
        var (root, world) = NewRoot();
        var element = new Element("Lamp");
        world.AddElement(element);

        var receipt = Run(root, new SceneCommand
        {
            Op = SceneOp.AttachComponent,
            WorldId = world.Id,
            TargetId = element.Id,
            ComponentType = nameof(GenericLightComponent),
        });

        Assert.True(receipt.Accepted, receipt.Error);
        Assert.NotNull(receipt.CreatedId);
        var light = element.Components.OfType<GenericLightComponent>().Single();
        Assert.Equal(receipt.CreatedId!.Value, light.Id);
    }

    [Fact]
    public void SetProperty_ReportsThePreviousValue()
    {
        var (root, world) = NewRoot();
        var element = new Element("Lamp");
        var light = new GenericLightComponent { Energy = 1f };
        element.AddComponent(light);
        world.AddElement(element);

        var receipt = Run(root, new SceneCommand
        {
            Op = SceneOp.SetProperty,
            WorldId = world.Id,
            TargetId = element.Id,
            ComponentType = nameof(GenericLightComponent),
            Property = "Energy",
            Value = SnapshotValue.FromFloat(5f),
        });

        Assert.True(receipt.Accepted, receipt.Error);
        Assert.Equal(5f, light.Energy);
        Assert.NotNull(receipt.PreviousValue);
        Assert.Equal(1.0, receipt.PreviousValue!.Value.Number, 3);
    }

    [Fact]
    public void SendInput_ReachesRegisteredHandlers()
    {
        var (root, _) = NewRoot();
        var handler = new RecordingHandler();
        root.Registry.Get<InputService>()!.RegisterHandler(handler);

        var receipt = Run(root, new SceneCommand
        {
            Op = SceneOp.SendInput,
            Input = new InputCommand
            {
                Type = InputEventType.ButtonDown,
                Name = "Jump",
                Key = InputKey.Space,
            },
        });

        Assert.True(receipt.Accepted, receipt.Error);
        var received = Assert.Single(handler.Events);
        Assert.Equal("Jump", received.Name);
        Assert.Equal("introspection", received.Meta!["source"]);
        Assert.Equal(receipt.RequestId, received.Meta["command"]);
    }

    [Fact]
    public void Journal_RecordsAppliedCommands()
    {
        var (root, world) = NewRoot();
        var element = new Element("Journalled");
        world.AddElement(element);

        Run(root, new SceneCommand
        {
            RequestId = "j-1",
            Op = SceneOp.SetTransform,
            WorldId = world.Id,
            TargetId = element.Id,
            Actor = "test",
            Transform = new TRS(position: new Vector3(0, 1, 0)),
        });

        var entry = root.Introspection.RecentJournal(1).Single();
        Assert.Equal("j-1", entry.RequestId);
        Assert.Equal(SceneOp.SetTransform, entry.Op);
        Assert.Equal("test", entry.Actor);
        Assert.True(entry.Accepted);

        // The entry carries the command itself so the journal can be re-posted.
        Assert.Equal(element.Id, entry.Command.TargetId);
        Assert.Equal(world.Id, entry.Command.WorldId);
    }

    [Fact]
    public async Task SnapshotRequest_IsAnsweredOnTheTickThread()
    {
        var (root, world) = NewRoot();
        world.AddElement(new Element("Present"));

        var pending = root.Introspection.RequestSnapshotAsync(TimeSpan.FromSeconds(2));
        Assert.False(pending.IsCompleted, "a snapshot must wait for a drain, not read live components off-thread");

        root.Introspection.DrainCommands(1);
        var snapshot = await pending;

        Assert.Contains(snapshot.Elements, e => e.Name == "Present");
    }

    [Fact]
    public async Task SnapshotRequest_SeesCommandsQueuedBeforeIt()
    {
        var (root, world) = NewRoot();

        var spawn = root.Introspection.Enqueue(new SceneCommand
        {
            RequestId = "before-snapshot",
            Op = SceneOp.Spawn,
            WorldId = world.Id,
            Name = "Spawned",
        });

        var pending = root.Introspection.RequestSnapshotAsync(TimeSpan.FromSeconds(2));
        root.Introspection.DrainCommands(1);

        Assert.True((await spawn).Accepted);
        var snapshot = await pending;
        Assert.Contains(snapshot.Elements, e => e.Name == "Spawned");
    }

    [Fact]
    public async Task SnapshotRequest_TimesOutWhenTheGameStopsTicking()
    {
        var (root, _) = NewRoot();

        await Assert.ThrowsAsync<System.TimeoutException>(() =>
            root.Introspection.RequestSnapshotAsync(TimeSpan.FromMilliseconds(25)));
    }

    [Fact]
    public async Task ConcurrentCallers_CanWriteAndReadAtTheSameTime()
    {
        var (root, world) = NewRoot();
        var target = new Element("Target");
        world.AddElement(target);

        const int writers = 8;
        const int perWriter = 25;

        var commands = new List<Task<CommandReceipt>>();
        var writersDone = Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            for (int i = 0; i < perWriter; i++)
            {
                var receipt = root.Introspection.Enqueue(new SceneCommand
                {
                    RequestId = $"w{writer}-{i}",
                    Op = SceneOp.SetTransform,
                    WorldId = world.Id,
                    TargetId = target.Id,
                    Transform = new TRS(position: new Vector3(writer, i, 0)),
                });

                lock (commands) commands.Add(receipt);
            }
        })).ToArray();

        // Request reads from other threads while the writers are queuing.
        var reads = Enumerable.Range(0, 4)
            .Select(_ => root.Introspection.RequestSnapshotAsync(TimeSpan.FromSeconds(5)))
            .ToArray();

        await Task.WhenAll(writersDone);

        for (int tick = 1; tick <= 64 && commands.Any(c => !c.IsCompleted); tick++)
            root.Introspection.DrainCommands(tick);

        var receipts = await Task.WhenAll(commands);
        var snapshots = await Task.WhenAll(reads);

        Assert.Equal(writers * perWriter, receipts.Length);
        Assert.All(receipts, r => Assert.True(r.Accepted, r.Error));
        Assert.All(snapshots, s => Assert.Contains(s.Elements, e => e.Id == target.Id));
        Assert.Equal(snapshots.Length, snapshots.Select(s => s.Sequence).Distinct().Count());
    }

    private sealed class RecordingHandler : IInputHandler
    {
        public List<InputEvent> Events { get; } = new();
        public void OnInputEvent(InputEvent evt) => Events.Add(evt);
    }
}
