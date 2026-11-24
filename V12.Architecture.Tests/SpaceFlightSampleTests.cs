using System;
using System.Linq;
using System.Numerics;
using SpaceFlight;
using SpaceFlight.Components;
using V12.Audio;
using V12.Basic.Components;
using V12.Basic.Scene;
using V12.Components;
using V12.Core;
using V12.Core.Audio;
using V12.Core.Input;
using V12.Core.Interfaces.Physics;
using V12.Core.Interfaces.Renderer;
using V12.Gravity;
using V12.Gravity.Components;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Covers the SpaceFlight demo bindings: time scale keys and the prograde /
/// retrograde / reset actions.
/// </summary>
public sealed class SpaceFlightSampleTests
{
    [Fact]
    public void BindDefaults_MapsTimeAndOrbitKeys()
    {
        var map = new InputActionMap();
        SpaceFlightInputComponent.BindDefaults(map);

        AssertTap(map, InputKey.OemOpenBrackets, "TimeSlower");
        AssertTap(map, InputKey.OemCloseBrackets, "TimeFaster");
        AssertTap(map, InputKey.B, "OrbitBoost");
        AssertTap(map, InputKey.N, "OrbitBrake");
        AssertTap(map, InputKey.C, "CameraToggle");
        AssertTap(map, InputKey.T, "AttitudeToggle");
        AssertTap(map, InputKey.R, "ResetOrbit");
        AssertTap(map, InputKey.Space, "Thrust");

        AssertAxis(map, InputKey.D, "ShipRotate_X", 1f);
        AssertAxis(map, InputKey.A, "ShipRotate_X", -1f);
        AssertAxis(map, InputKey.W, "ShipRotate_Y", 1f);
        AssertAxis(map, InputKey.S, "ShipRotate_Y", -1f);

        AssertAxis(map, InputKey.Right, "CameraOrbit_X", 1f);
        AssertAxis(map, InputKey.Left, "CameraOrbit_X", -1f);
        AssertAxis(map, InputKey.Up, "CameraOrbit_Y", 1f);
        AssertAxis(map, InputKey.Down, "CameraOrbit_Y", -1f);
        AssertAxis(map, InputKey.E, "ShipRoll", 1f);
        AssertAxis(map, InputKey.Q, "ShipRoll", -1f);

        // The wheel took over Q/E zoom, and the right mouse button is the
        // capture/look input for the chase camera.
        map.OnInputEvent(new InputEvent
        {
            Type = InputEventType.Axis,
            Name = "ui_wheel",
            Value = 1f,
        });
        map.Update(0f);
        Assert.Equal(1f, map.GetAxis("CameraZoom"), 3);

        map.OnInputEvent(new InputEvent
        {
            Type = InputEventType.ButtonDown,
            Name = "mouse_right",
        });
        map.Update(0f);
        Assert.True(map.GetButtonDown("CameraLook"));

        map.OnInputEvent(new InputEvent
        {
            Type = InputEventType.ButtonUp,
            Name = "mouse_right",
        });
        map.Update(0f);

        map.OnInputEvent(new InputEvent
        {
            Type = InputEventType.ButtonDown,
            Name = "mouse_left",
        });
        map.Update(0f);
        Assert.True(map.GetButtonDown("CameraLook"));
    }

    [Fact]
    public void ToggleCamera_CyclesOrbitAndChase()
    {
        var camera = new OrbitCameraComponent();
        var input = new SpaceFlightInputComponent { Camera = camera };

        Assert.Equal(CameraViewMode.Follow, input.ToggleCamera());
        Assert.Equal(CameraViewMode.Orbit, input.ToggleCamera());
    }

    [Fact]
    public void FollowCamera_DoesNotRotateWhenTheShipMoves()
    {
        var cameraElement = new Element("Camera");
        var camera = new OrbitCameraComponent
        {
            FollowDistance = 100f,
            FollowHeight = 20f,
        };
        cameraElement.AddComponent(camera);

        var target = new Element("Ship");
        target.LocalTransform = new TRS
        {
            Position = new Vector3(800f, 0f, 0f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        camera.FollowTarget = target;
        camera.Follow = true;

        camera.Update(0.1f);
        var firstOffset = cameraElement.LocalTransform.Position
                        - target.LocalTransform.Position;

        target.LocalTransform = new TRS
        {
            Position = new Vector3(0f, 0f, 800f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        camera.Update(0.1f);
        var secondOffset = cameraElement.LocalTransform.Position
                         - target.LocalTransform.Position;

        var firstDirection = Vector3.Normalize(firstOffset);
        var secondDirection = Vector3.Normalize(secondOffset);
        Assert.True(
            Vector3.Dot(firstDirection, secondDirection) > 0.999f,
            $"camera rotated with the ship: {firstDirection} -> {secondDirection}");
    }

    [Fact]
    public void RealTimeInput_IsConsumedOncePerRenderedFrame()
    {
        double last = -1;

        Assert.Equal(0f, RealTimeInput.Delta(ref last, 10.0));
        Assert.Equal(0f, RealTimeInput.Delta(ref last, 10.0));
        Assert.Equal(0.05f, RealTimeInput.Delta(ref last, 10.05), 3);
        Assert.Equal(0f, RealTimeInput.Delta(ref last, 10.05));
        Assert.Equal(0.1f, RealTimeInput.Delta(ref last, 10.5), 3);
    }

    [Fact]
    public void OrbitFollow_PansTiltsAndZooms()
    {
        var camera = new OrbitCameraComponent
        {
            Mode = CameraViewMode.Follow,
            FollowDistance = 200f,
            FollowOrbitSpeed = 1f,
            FollowPitchSpeed = 1f,
            FollowZoomSpeed = 100f,
        };

        camera.OrbitFollow(new Vector2(1f, 1f), 1f, 0.5f);
        Assert.Equal(0.5f, camera.FollowYaw, 3);
        Assert.Equal(-0.5f, camera.FollowPitch, 3);
        Assert.Equal(150f, camera.FollowDistance, 3);

        camera.OrbitFollow(new Vector2(0f, 10f), 10f, 1f);
        Assert.Equal(-1.35f, camera.FollowPitch, 3);
        Assert.Equal(60f, camera.FollowDistance, 3);
    }

    [Fact]
    public void MouseLook_OrbitsTheChaseCamera()
    {
        var camera = new OrbitCameraComponent
        {
            Mode = CameraViewMode.Follow,
            MouseOrbitSensitivity = 0.1f,
        };

        camera.LookFollow(new Vector2(1f, 0f));
        Assert.Equal(-0.1f, camera.FollowYaw, 3);

        camera.LookFollow(new Vector2(0f, 2f));
        Assert.Equal(0.2f, camera.FollowPitch, 3);

        // The cinematic orbit is not driven by the mouse.
        camera.Mode = CameraViewMode.Orbit;
        camera.LookFollow(new Vector2(1f, 1f));
        Assert.Equal(-0.1f, camera.FollowYaw, 3);
        Assert.Equal(0.2f, camera.FollowPitch, 3);
    }

    [Fact]
    public void WheelZoom_StepsAndClampsBothCameras()
    {
        var follow = new OrbitCameraComponent
        {
            Mode = CameraViewMode.Follow,
            FollowDistance = 200f,
            MinFollowDistance = 60f,
            MaxFollowDistance = 900f,
        };

        follow.ZoomFollow(1f);
        Assert.Equal(176f, follow.FollowDistance, 3);
        follow.ZoomFollow(-1f);
        Assert.Equal(200f, follow.FollowDistance, 3);
        follow.ZoomFollow(-100f);
        Assert.Equal(900f, follow.FollowDistance, 3);
        follow.ZoomFollow(100f);
        Assert.Equal(60f, follow.FollowDistance, 3);

        var orbitElement = new Element("Camera");
        var orbit = new OrbitCameraComponent
        {
            Radius = 1000f,
            Height = 500f,
            AngularSpeed = 0f,
        };
        orbitElement.AddComponent(orbit);

        orbit.ZoomOrbit(1f);
        orbit.Update(0f);
        Assert.Equal(440f, orbitElement.LocalTransform.Position.Y, 3);
    }

    [Fact]
    public void ManualRoll_TurnsTheHullAboutItsNoseAxis()
    {
        var ship = new Element("Ship");
        var body = new TestBody();
        ship.AddComponent(new PhysicsBodyComponent { Body = body });

        var attitude = new ProgradeAttitudeComponent { ManualRollRate = 1f };
        ship.AddComponent(attitude);

        attitude.ManualRotate(0f, 0f, 1f, 1f);

        Assert.Equal(SpacecraftAttitudeMode.Free, attitude.Mode);
        var up = Vector3.Transform(Vector3.UnitY, body.Rotation);
        Assert.True(up.X > 0.8f, $"right roll should tip the hull toward +X, got {up}");
    }

    [Fact]
    public void GravityBodySet_PicksTheBodyWithTheStrongestLocalPull()
    {
        var earth = new Element("Earth");
        var earthSource = new GravitySourceComponent(1_000_000f, 400f);
        earth.AddComponent(earthSource);

        var mars = new Element("Mars");
        mars.LocalTransform = new TRS
        {
            Position = new Vector3(5000f, 0f, 0f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        var marsSource = new GravitySourceComponent(400_000f, 250f);
        mars.AddComponent(marsSource);

        var bodies = new GravityBodySet();
        bodies.Add(earthSource);
        bodies.Add(marsSource);

        Assert.Same(earthSource, bodies.FindDominant(new Vector3(800f, 0f, 0f)));
        Assert.Same(marsSource, bodies.FindDominant(new Vector3(4300f, 0f, 0f)));
    }

    [Fact]
    public void GravityBodySet_LocksOntoAPlanetInsideItsSoi()
    {
        var sun = new Element("Sun");
        var sunSource = new GravitySourceComponent(20_000_000f, 900f);
        sun.AddComponent(sunSource);

        var earth = new Element("Earth");
        earth.LocalTransform = new TRS
        {
            Position = new Vector3(4000f, 0f, 0f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        var earthSource = new GravitySourceComponent(1_500_000f, 400f);
        earth.AddComponent(earthSource);

        var moon = new Element("Moon");
        moon.LocalTransform = new TRS
        {
            Position = new Vector3(5500f, 0f, 0f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        var moonSource = new GravitySourceComponent(130_000f, 180f);
        moon.AddComponent(moonSource);

        var bodies = new GravityBodySet();
        bodies.Add(sunSource);
        bodies.Add(earthSource, 1576);
        bodies.Add(moonSource, 555);

        Assert.Same(earthSource, bodies.FindDominant(new Vector3(4800f, 0f, 0f)));
        Assert.Same(moonSource, bodies.FindDominant(new Vector3(4950f, 0f, 0f)));
        Assert.Same(moonSource, bodies.FindDominant(new Vector3(5400f, 0f, 0f)));
        Assert.Same(sunSource, bodies.FindDominant(new Vector3(8000f, 0f, 0f)));
    }

    [Fact]
    public void OrbitCamera_TargetsTheLocallyDominantBody()
    {
        var cameraElement = new Element("Camera");
        var camera = new OrbitCameraComponent
        {
            Radius = 100f,
            Height = 200f,
            AngularSpeed = 0f,
            ReferenceRadius = 400f,
        };
        cameraElement.AddComponent(camera);

        var mars = new Element("Mars");
        mars.LocalTransform = new TRS
        {
            Position = new Vector3(5000f, 0f, 0f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        var marsSource = new GravitySourceComponent(1_000_000f, 200f);
        mars.AddComponent(marsSource);

        var ship = new Element("Ship");
        ship.LocalTransform = new TRS
        {
            Position = new Vector3(5300f, 0f, 0f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };

        camera.FollowTarget = ship;
        camera.Bodies = new GravityBodySet();
        camera.Bodies.Add(marsSource);
        camera.Update(0f);

        var position = cameraElement.LocalTransform.Position;
        Assert.InRange(position.X, 4999f, 5001f);
        Assert.InRange(position.Y, 99f, 101f);
        Assert.InRange(position.Z, 49f, 51f);
    }

    [Fact]
    public void OrbitPath_CanBeRecentredOnAnotherPlanet()
    {
        var element = new Element("OrbitPath");
        var path = new OrbitPathMesh();
        element.AddComponent(path);

        var state = OrbitalState.FromVectors(
            1_000_000f,
            new Vector3(100f, 0f, 0f),
            new Vector3(0f, 0f, 100f));

        path.SetState(state, new Vector3(5000f, 0f, 0f));

        // Geometry is local; the element transform carries the new centre.
        Assert.InRange(element.LocalTransform.Position.X, 4999f, 5001f);

        var points = path.MeshPoints;
        Assert.NotEmpty(points);

        float minX = float.MaxValue;
        float maxX = float.MinValue;
        for (int i = 0; i < points.Length; i += 3)
        {
            minX = MathF.Min(minX, (float)points[i]);
            maxX = MathF.Max(maxX, (float)points[i]);
        }

        Assert.True(minX > -150f, $"path should be local to the element, minX={minX}");
        Assert.True(maxX < 150f, $"path should be local to the element, maxX={maxX}");
    }

    [Fact]
    public void NavballCurve_LevelHorizonIsAFlatDiameter()
    {
        var curve = new NavballCurveMesh("Test")
        {
            PlaneNormal = Vector3.UnitY,
        };
        var points = curve.MeshPoints;

        Assert.NotEmpty(points);
        for (int i = 0; i < points.Length; i += 3)
        {
            Assert.InRange(points[i + 2], -1e-4, 1e-4);
            Assert.InRange(Math.Abs(points[i + 1]), 0f, 0.02f);
            Assert.True(points[i] * points[i] + points[i + 1] * points[i + 1] <= 0.2501f);
        }
    }

    [Fact]
    public void ToggleAttitude_CyclesModes()
    {
        var attitude = new ProgradeAttitudeComponent();
        var input = new SpaceFlightInputComponent { Attitude = attitude };

        Assert.Equal(SpacecraftAttitudeMode.Retrograde, input.ToggleAttitude());
        Assert.Equal(SpacecraftAttitudeMode.TidalLock, input.ToggleAttitude());
        Assert.Equal(SpacecraftAttitudeMode.Free, input.ToggleAttitude());
        Assert.Equal(SpacecraftAttitudeMode.Prograde, input.ToggleAttitude());
    }

    [Fact]
    public void SpaceFlightShip_UsesASphereContactProxy()
    {
        var root = new GameRoot();
        SpaceFlightScene.Build(root);

        var world = root.Worlds.Single(w => w.WorldName == "SpaceFlight");
        var ship = world.Enumerate().Single(e => e.Name == "Ship");
        var collider = ship.GetComponent<ColliderComponent>();

        Assert.NotNull(collider);
        Assert.Equal(MeshShape.Sphere, collider!.Shape);
    }

    [Fact]
    public void SpaceFlightShip_KeepsTheFloatBubbleCentredOnIt()
    {
        var root = new GameRoot();
        SpaceFlightScene.Build(root);

        var world = root.Worlds.Single(w => w.WorldName == "SpaceFlight");
        var ship = world.Enumerate().Single(e => e.Name == "Ship");
        var origin = ship.GetComponent<FloatingOriginComponent>();

        Assert.NotNull(origin);
        Assert.Same(ship, origin!.Anchor);
        Assert.Equal(2000f, origin.Threshold, 1);
    }

    [Fact]
    public void SpaceFlightScene_HasMusicAndThrusterAudio()
    {
        var root = new GameRoot();
        SpaceFlightScene.Build(root);

        var world = root.Worlds.Single(w => w.WorldName == "SpaceFlight");
        var sources = world.Enumerate()
            .SelectMany(element => element.Components.OfType<AudioSourceComponent>())
            .ToList();

        var music = sources.Single(s => s.AudioClipPath.Contains("spaceflight.mp3"));
        Assert.True(music.Loop);
        Assert.True(music.Autoplay);
        Assert.Equal(0f, music.SpatialBlend);
        Assert.InRange(music.Volume, 0.01f, 0.25f); // quiet by default

        var thruster = sources.Single(s => s.AudioClipPath.Contains("thruster"));
        Assert.True(thruster.Loop);
        Assert.False(thruster.Autoplay);
        Assert.Equal(0f, thruster.SpatialBlend);

        var ship = world.Enumerate().Single(e => e.Name == "Ship");
        var input = ship.GetComponent<SpaceFlightInputComponent>();
        Assert.NotNull(input);
        Assert.Same(thruster, input!.ThrustAudio);
    }

    [Fact]
    public async Task SpaceFlightMusic_ReachesTheAudioSystem()
    {
        var root = new GameRoot();
        var engine = new NullAudioEngine();
        root.Registry.Register("IAudioEngine", engine);
        await engine.StartAsync();

        SpaceFlightScene.Build(root);

        var world = root.Worlds.Single(w => w.WorldName == "SpaceFlight");
        world.TickingEnabled = true;
        root.SelectWorld(world);
        root.Initialize();

        for (int i = 0; i < 3; i++)
            root.V12Tick(1f / 60f);

        var music = world.Enumerate()
            .SelectMany(element => element.Components.OfType<AudioSourceComponent>())
            .Single(source => source.AudioClipPath.Contains("spaceflight.mp3"));

        var system = root.Registry.Get<AudioSystem>("AudioSystem");
        Assert.NotNull(system);
        Assert.True(system!.TryGetChannel(music.Id, out var channel));
        Assert.True(channel.Loop);
        Assert.InRange(channel.Gain, 0.1f, 0.15f);

        engine.Dispose();
    }

    [Fact]
    public void Burn_UsesVelocityRelativeToTheDominantBody()
    {
        var planet = new Element("Planet");
        var planetSource = new GravitySourceComponent(1_000_000f, 100f);
        planet.AddComponent(planetSource);
        var orbit = new PlanetOrbitComponent
        {
            Center = Vector3.Zero,
            Radius = 1f,
            AngularSpeed = 1f,
        };
        planet.AddComponent(orbit);
        orbit.Update(0f);
        Assert.Equal(new Vector3(0f, 0f, 1f), orbit.LinearVelocity);

        var bodies = new GravityBodySet();
        bodies.Add(planetSource);

        var ship = new Element("Ship");
        var fake = new TestBody
        {
            Position = new Vector3(100f, 0f, 0f),
            LinearVelocity = new Vector3(10f, 0f, 1f),
            Mass = 2f,
        };
        ship.AddComponent(new PhysicsBodyComponent { Body = fake });

        var input = new SpaceFlightInputComponent { Bodies = bodies };
        ship.AddComponent(input);

        Assert.True(input.Burn(+1));

        // Local prograde is +X. A world-velocity burn would also add a +Z
        // component from the planet's own track velocity.
        Assert.InRange(fake.LinearVelocity.X, 10.99f, 11.01f);
        Assert.InRange(fake.LinearVelocity.Z, 0.99f, 1.01f);
    }

    [Fact]
    public void Build_IsIdempotentForHotReload()
    {
        var root = new GameRoot();

        SpaceFlightScene.Build(root);
        SpaceFlightScene.Build(root);

        var world = root.Worlds.Single(w => w.WorldName == "SpaceFlight");
        Assert.Single(world.Enumerate(), e => e.Name == "Sun");
        Assert.Single(world.Enumerate(), e => e.Name == "Ship");
    }

    [Fact]
    public void PlanetsAreLit_AndTheSunIsUnlit()
    {
        var root = new GameRoot();
        SpaceFlightScene.Build(root);

        var world = root.Worlds.Single(w => w.WorldName == "SpaceFlight");
        var earth = world.Enumerate().Single(e => e.Name == "Earth");
        var sun = world.Enumerate().Single(e => e.Name == "Sun");

        Assert.False(earth.GetComponent<MaterialComponent>()!.Unlit);
        Assert.True(sun.GetComponent<MaterialComponent>()!.Unlit);
    }

    private sealed class TestBody : IPhysicsBody
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
        public Vector3 LinearVelocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        public bool IsDynamic => true;
        public bool IsCharacterController => false;
        public bool IsOnFloor => false;
        public bool LockRotation { get; set; }
        public float Mass { get; set; } = 1f;
        public float GravityScale { get; set; } = 1f;

        public void AddForce(Vector3 force) { }
        public void AddTorque(Vector3 torque) { }
        public void AddImpulse(Vector3 impulse)
            => LinearVelocity += impulse / MathF.Max(0.0001f, Mass);
        public void AddAngularImpulse(Vector3 impulse) { }
        public void SetKinematic(bool kinematic) { }
    }

    private static void AssertTap(InputActionMap map, InputKey key, string action)
    {
        map.OnInputEvent(Button(InputEventType.ButtonDown, key));
        map.Update(0f);
        Assert.True(map.GetButtonDown(action), $"{key} should press {action}");

        map.OnInputEvent(Button(InputEventType.ButtonUp, key));
        map.Update(0f);
        Assert.True(map.GetButtonUp(action), $"{key} should release {action}");
    }

    private static void AssertAxis(
        InputActionMap map, InputKey key, string action, float expected)
    {
        map.OnInputEvent(Button(InputEventType.ButtonDown, key));
        map.Update(0f);
        Assert.Equal(expected, map.GetAxis(action), 3);

        map.OnInputEvent(Button(InputEventType.ButtonUp, key));
        map.Update(0f);
        Assert.Equal(0f, map.GetAxis(action), 3);
    }

    private static InputEvent Button(InputEventType type, InputKey key) => new()
    {
        Type = type,
        Name = key.ToString(),
        Key = key,
        IsPressed = type == InputEventType.ButtonDown,
    };
}
