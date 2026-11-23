using System;
using System.IO;
using System.Linq;
using V12.Components;
using V12.Core;
using V12.Core.Input;
using V12.WorldML;
using Xunit;

namespace V12.Architecture.Tests;

public sealed class MultiplayerDemoWorldTests
{
    [Fact]
    public void SampleWorld_HasXrAndDesktopPlayerRig()
    {
        _ = typeof(MultiplayerDemo.MultiplayerPlayerController);

        var path = Path.Combine(RepoRoot(), "V12-Samples", "MultiplayerDemo", "Scenes", "Main.worldml");

        var root = new GameRoot();
        var contents = new WorldMLParser().ParseFile(path);
        var world = new World("MultiplayerDemo");
        world.AddElement(contents);
        root.Worlds.Add(world);
        root.SelectWorld(world);

        var player = root.FindElement(world, e => e.Name == "Player");
        Assert.NotNull(player);
        Assert.True(NonPersistentComponent.IsTransient(player), "the player rig must stay local to each peer");
        Assert.NotNull(player!.GetComponent<MultiplayerDemo.MultiplayerBootstrap>());
        Assert.NotNull(player.GetComponent<MultiplayerDemo.MultiplayerPlayerController>());

        var camera = player.FindChildByNameRecursive("DesktopCamera");
        Assert.NotNull(camera);
        Assert.NotNull(camera!.GetComponent<CameraComponent>());

        var rig = player.FindChildByNameRecursive("XRRig");
        Assert.NotNull(rig);
        Assert.NotNull(rig!.GetComponent<XRRootComponent>());
        Assert.NotNull(rig.FindChildByNameRecursive("Head")?.GetComponent<XRHeadComponent>());
        Assert.NotNull(rig.FindChildByNameRecursive("LeftHand")?.GetComponent<XRHandComponent>());
        Assert.NotNull(rig.FindChildByNameRecursive("RightHand")?.GetComponent<XRHandComponent>());

        var cube = root.FindElement(world, e => e.Name == "SharedCube");
        Assert.NotNull(cube);
        Assert.NotNull(cube!.GetComponent<MultiplayerDemo.MultiplayerCubeComponent>());
    }

    [Fact]
    public void DesktopInput_MovesTheLocalPlayerThroughTheRegistryPump()
    {
        _ = typeof(MultiplayerDemo.MultiplayerPlayerController);

        var path = Path.Combine(RepoRoot(), "V12-Samples", "MultiplayerDemo", "Scenes", "Main.worldml");

        var root = new GameRoot();
        var contents = new WorldMLParser().ParseFile(path);
        var world = new World("MultiplayerDemo");
        world.AddElement(contents);
        root.Worlds.Add(world);
        root.SelectWorld(world);

        string? previousMode = Environment.GetEnvironmentVariable("V12_MP_MODE");
        Environment.SetEnvironmentVariable("V12_MP_MODE", "off");

        try
        {
            root.Initialize();

            var player = root.FindElement(world, e => e.Name == "Player");
            Assert.NotNull(player);
            var controller = player!.GetComponent<MultiplayerDemo.MultiplayerPlayerController>();
            Assert.NotNull(controller);
            Assert.NotNull(root.Registry.Get($"MultiplayerBootstrap:{player.Id}"));
            float startZ = player.LocalTransform.Position.Z;

            var input = root.Registry.Get<InputService>();
            Assert.NotNull(input);
            input!.SendEvent(new InputEvent
            {
                Type = InputEventType.ButtonDown,
                Name = "W",
                Key = InputKey.W,
                IsPressed = true,
                Value = 1f,
            });

            root.FixedUpdate(1f / 60f);

            Assert.True(controller!.TickCount > 0, "the player registry pump did not tick");
            Assert.True(player.LocalTransform.Position.Z < startZ,
                $"expected W to move forward, ticks={controller.TickCount}, start={startZ}, end={player.LocalTransform.Position.Z}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("V12_MP_MODE", previousMode);
        }
    }

    [Fact]
    public void PlayerInputCommand_RoundTripsXrPoses()
    {
        var command = new MultiplayerDemo.PlayerInputCommand
        {
            MoveX = 0.5f,
            MoveZ = -1f,
            Yaw = 0.25f,
            HasBodyPose = true,
            BodyPosition = new System.Numerics.Vector3(1f, 0.65f, -2f),
            BodyYaw = 0.75f,
            HasXr = true,
            HeadPosition = new System.Numerics.Vector3(0f, 1.6f, 0.1f),
            HeadRotation = System.Numerics.Quaternion.Identity,
            LeftHandPosition = new System.Numerics.Vector3(-0.3f, 1.2f, 0.2f),
            RightHandPosition = new System.Numerics.Vector3(0.3f, 1.2f, 0.2f),
        };

        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            command, MultiplayerDemo.MultiplayerJson.Options);
        var restored = System.Text.Json.JsonSerializer.Deserialize<MultiplayerDemo.PlayerInputCommand>(
            bytes, MultiplayerDemo.MultiplayerJson.Options);

        Assert.NotNull(restored);
        Assert.True(restored!.HasXr);
        Assert.True(restored.HasBodyPose);
        Assert.Equal(command.BodyPosition, restored.BodyPosition);
        Assert.Equal(command.BodyYaw, restored.BodyYaw, 4);
        Assert.Equal(command.HeadPosition, restored.HeadPosition);
        Assert.Equal(command.LeftHandPosition, restored.LeftHandPosition);
        Assert.Equal(command.RightHandPosition, restored.RightHandPosition);
    }

    [Fact]
    public void AvatarComponent_CopiesLocalXrPoses()
    {
        var root = new GameRoot();
        var world = root.CreateWorld("Avatars");

        var avatar = new Element("Avatar");
        avatar.AddChild(new Element("Head"));
        avatar.AddChild(new Element("LeftHand"));
        avatar.AddChild(new Element("RightHand"));
        var avatarComponent = new MultiplayerDemo.MultiplayerAvatarComponent { PlayerId = 1 };
        avatar.AddComponent(avatarComponent);
        world.AddElement(avatar);

        var localPlayer = new Element("LocalPlayer");
        localPlayer.AddChild(new Element("Head")
        {
            LocalTransform = new V12.Core.Interfaces.Renderer.TRS(
                new System.Numerics.Vector3(0f, 1.7f, 0.1f)),
        });
        localPlayer.AddChild(new Element("LeftHand")
        {
            LocalTransform = new V12.Core.Interfaces.Renderer.TRS(
                new System.Numerics.Vector3(-0.3f, 1.2f, 0.2f)),
        });
        localPlayer.AddChild(new Element("RightHand")
        {
            LocalTransform = new V12.Core.Interfaces.Renderer.TRS(
                new System.Numerics.Vector3(0.3f, 1.2f, 0.2f)),
        });

        avatarComponent.SyncLocalXr(localPlayer);

        Assert.Equal(new System.Numerics.Vector3(0f, 1.7f, 0.1f),
            avatar.FindChildByNameRecursive("Head")!.LocalTransform.Position);
        Assert.Equal(new System.Numerics.Vector3(-0.3f, 1.2f, 0.2f),
            avatar.FindChildByNameRecursive("LeftHand")!.LocalTransform.Position);
        Assert.Equal(new System.Numerics.Vector3(0.3f, 1.2f, 0.2f),
            avatar.FindChildByNameRecursive("RightHand")!.LocalTransform.Position);
    }

    [Fact]
    public void AvatarComponent_AppliesRemoteXrPoses()
    {
        var root = new GameRoot();
        var world = root.CreateWorld("Avatars");

        var avatar = new Element("Avatar");
        avatar.AddChild(new Element("Head"));
        avatar.AddChild(new Element("LeftHand"));
        avatar.AddChild(new Element("RightHand"));
        var component = new MultiplayerDemo.MultiplayerAvatarComponent { PlayerId = 1 };
        avatar.AddComponent(component);
        world.AddElement(avatar);

        component.SetXr(
            new System.Numerics.Vector3(0f, 1.7f, 0.1f),
            System.Numerics.Quaternion.Identity,
            new System.Numerics.Vector3(-0.3f, 1.2f, 0.2f),
            System.Numerics.Quaternion.Identity,
            new System.Numerics.Vector3(0.3f, 1.2f, 0.2f),
            System.Numerics.Quaternion.Identity);

        Assert.Equal(new System.Numerics.Vector3(0f, 1.7f, 0.1f),
            avatar.FindChildByNameRecursive("Head")!.LocalTransform.Position);
        Assert.Equal(new System.Numerics.Vector3(-0.3f, 1.2f, 0.2f),
            avatar.FindChildByNameRecursive("LeftHand")!.LocalTransform.Position);
        Assert.Equal(new System.Numerics.Vector3(0.3f, 1.2f, 0.2f),
            avatar.FindChildByNameRecursive("RightHand")!.LocalTransform.Position);
    }

    [Fact]
    public void AvatarComponent_MovesFromHostInput()
    {
        var root = new GameRoot();
        var world = root.CreateWorld("Avatars");
        var avatar = new Element("Avatar") { LocalTransform = new V12.Core.Interfaces.Renderer.TRS(new System.Numerics.Vector3(0f, 0.65f, 0f)) };
        var component = new MultiplayerDemo.MultiplayerAvatarComponent { PlayerId = 1 };
        avatar.AddComponent(component);
        world.AddElement(avatar);

        component.SetInput(0f, -1f, 0f);
        root.FixedUpdate(1f / 60f);

        Assert.True(avatar.LocalTransform.Position.Z < 0f,
            $"expected host input to move the avatar forward, z={avatar.LocalTransform.Position.Z}");
    }

    [Fact]
    public void RigPose_PutsBodyUnderHeadAndKeepsPartsRelative()
    {
        var localPlayer = new Element("LocalPlayer")
        {
            LocalTransform = new V12.Core.Interfaces.Renderer.TRS(
                new System.Numerics.Vector3(0f, 0f, 4f)),
        };

        var headYaw = MathF.PI / 2f;
        var head = new Element("Head")
        {
            LocalTransform = new V12.Core.Interfaces.Renderer.TRS(
                new System.Numerics.Vector3(0.5f, 1.7f, 0.2f),
                System.Numerics.Quaternion.CreateFromYawPitchRoll(headYaw, 0f, 0f)),
        };
        var leftHand = new Element("LeftHand")
        {
            LocalTransform = new V12.Core.Interfaces.Renderer.TRS(
                new System.Numerics.Vector3(0.2f, 1.2f, 0.3f)),
        };
        var rightHand = new Element("RightHand")
        {
            LocalTransform = new V12.Core.Interfaces.Renderer.TRS(
                new System.Numerics.Vector3(0.8f, 1.2f, 0.3f)),
        };

        var pose = MultiplayerDemo.MultiplayerRigPose.Capture(
            localPlayer, head, leftHand, rightHand);

        Assert.Equal(new System.Numerics.Vector3(0.5f, 0.65f, 4.2f), pose.BodyPosition);
        Assert.Equal(headYaw, pose.BodyYaw, 4);

        // The head sits directly over the body root: only its height above the
        // body centre remains.
        Assert.Equal(0f, pose.HeadPosition.X, 4);
        Assert.Equal(1.05f, pose.HeadPosition.Y, 4);
        Assert.Equal(0f, pose.HeadPosition.Z, 4);
        Assert.Equal(0f, MultiplayerDemo.MultiplayerRigPose.YawOf(pose.HeadRotation), 4);

        // Hand offsets keep their distance from the body root and rotate with it.
        Assert.Equal(MathF.Sqrt(0.3f * 0.3f + 0.55f * 0.55f + 0.1f * 0.1f),
            pose.LeftHandPosition.Length(), 4);
        Assert.Equal(MathF.Sqrt(0.3f * 0.3f + 0.55f * 0.55f + 0.1f * 0.1f),
            pose.RightHandPosition.Length(), 4);

        // The yawed body rotates the hand offsets into its own frame.
        Assert.Equal(-0.1f, pose.LeftHandPosition.X, 4);
        Assert.Equal(-0.3f, pose.LeftHandPosition.Z, 4);
        Assert.Equal(-0.1f, pose.RightHandPosition.X, 4);
        Assert.Equal(0.3f, pose.RightHandPosition.Z, 4);
    }

    [Fact]
    public void AvatarComponent_AppliesRigPoseBodyAndParts()
    {
        var root = new GameRoot();
        var world = root.CreateWorld("Avatars");

        var avatar = new Element("Avatar");
        avatar.AddChild(new Element("Head"));
        avatar.AddChild(new Element("LeftHand"));
        avatar.AddChild(new Element("RightHand"));
        var component = new MultiplayerDemo.MultiplayerAvatarComponent { PlayerId = 1 };
        avatar.AddComponent(component);
        world.AddElement(avatar);

        var pose = new MultiplayerDemo.MultiplayerRigPose(
            new System.Numerics.Vector3(2f, 0f, -3f),
            MathF.PI / 2f,
            new System.Numerics.Vector3(0f, 1.7f, 0f),
            System.Numerics.Quaternion.Identity,
            new System.Numerics.Vector3(-0.3f, 1.2f, 0.1f),
            System.Numerics.Quaternion.Identity,
            new System.Numerics.Vector3(0.3f, 1.2f, 0.1f),
            System.Numerics.Quaternion.Identity);

        component.SetRigPose(pose);

        Assert.Equal(pose.BodyPosition, avatar.LocalTransform.Position);
        Assert.Equal(pose.BodyYaw,
            MultiplayerDemo.MultiplayerRigPose.YawOf(avatar.LocalTransform.Rotation), 4);
        Assert.Equal(pose.HeadPosition,
            avatar.FindChildByNameRecursive("Head")!.LocalTransform.Position);
        Assert.Equal(pose.LeftHandPosition,
            avatar.FindChildByNameRecursive("LeftHand")!.LocalTransform.Position);
        Assert.Equal(pose.RightHandPosition,
            avatar.FindChildByNameRecursive("RightHand")!.LocalTransform.Position);
    }

    [Fact]
    public void XrStick_MovesForwardAndRight()
    {
        _ = typeof(MultiplayerDemo.MultiplayerPlayerController);

        var path = Path.Combine(RepoRoot(), "V12-Samples", "MultiplayerDemo", "Scenes", "Main.worldml");

        var root = new GameRoot();
        var contents = new WorldMLParser().ParseFile(path);
        var world = new World("MultiplayerDemo");
        world.AddElement(contents);
        root.Worlds.Add(world);
        root.SelectWorld(world);

        string? previousMode = Environment.GetEnvironmentVariable("V12_MP_MODE");
        Environment.SetEnvironmentVariable("V12_MP_MODE", "off");

        try
        {
            root.Initialize();

            // The controller only takes the XR path when a tracking service is
            // registered. The real host is Godot-specific; this marker is
            // enough to exercise the rig/head lookup and the stick axes.
            root.Registry.Register("XRTrackingService", new object());

            var player = root.FindElement(world, e => e.Name == "Player");
            Assert.NotNull(player);
            var controller = player!.GetComponent<MultiplayerDemo.MultiplayerPlayerController>();
            Assert.NotNull(controller);
            var input = root.Registry.Get<InputService>();
            Assert.NotNull(input);

            var start = player.LocalTransform.Position;

            input!.SendEvent(new InputEvent
            {
                Type = InputEventType.Axis,
                Name = "xr_left_stick_y",
                Value = 1f,
            });
            root.FixedUpdate(1f / 60f);

            Assert.True(controller!.TickCount > 0, "the player registry pump did not tick in XR mode");
            Assert.True(player.LocalTransform.Position.Z < start.Z,
                $"expected stick-up to move forward, ticks={controller.TickCount}, start={start.Z}, end={player.LocalTransform.Position.Z}");

            var afterForward = player.LocalTransform.Position;
            input.SendEvent(new InputEvent
            {
                Type = InputEventType.Axis,
                Name = "xr_left_stick_x",
                Value = 1f,
            });
            root.FixedUpdate(1f / 60f);

            Assert.True(player.LocalTransform.Position.X > afterForward.X,
                $"expected stick-right to move right, start={afterForward.X}, end={player.LocalTransform.Position.X}");

            var afterRight = player.LocalTransform.Position;
            input.SendEvent(new InputEvent
            {
                Type = InputEventType.Axis,
                Name = "xr_left_stick_y",
                Value = -1f,
            });
            root.FixedUpdate(1f / 60f);

            Assert.True(player.LocalTransform.Position.Z > afterRight.Z,
                $"expected stick-down to move backward, start={afterRight.Z}, end={player.LocalTransform.Position.Z}");

            var beforeTurn = MultiplayerDemo.MultiplayerRigPose.YawOf(
                player.LocalTransform.Rotation);
            input.SendEvent(new InputEvent
            {
                Type = InputEventType.Axis,
                Name = "xr_right_stick_x",
                Value = 1f,
            });
            root.FixedUpdate(1f / 60f);

            var afterTurn = MultiplayerDemo.MultiplayerRigPose.YawOf(
                player.LocalTransform.Rotation);
            Assert.True(afterTurn < beforeTurn,
                $"expected right stick to turn right, before={beforeTurn}, after={afterTurn}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("V12_MP_MODE", previousMode);
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "V12-Monorepo.slnx")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the V12-Monorepo root.");
    }
}
