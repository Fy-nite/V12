using System;
using System.Numerics;
using SpaceFlight.Components;
using V12.Basic.Building;
using V12.Basic.Components;
using V12.Components;
using V12.Components.Renderables;
using V12.Core;
using V12.Core.Audio;
using V12.Core.Interfaces.Renderer;
using V12.Gravity;
using V12.Gravity.Components;

namespace SpaceFlight;

/// <summary>
/// Builds the visual orbit demo. The planet is a gravity source, the ship is a
/// normal dynamic Bepu body, and the ring is the initial circular orbit. The
/// ship's motion is produced by the physics field, not by an animation.
/// </summary>
public static class SpaceFlightScene
{
    // Visual scale: the near field is metres (the hull is 28 x 26 x 42, the
    // chase camera starts 180 units out), while heliocentric distances are
    // compressed so the whole system stays inside a float32 bubble. Planet
    // radii use the same "big world" scale as the hull so each body reads as a
    // world instead of a dot when the cinematic camera visits it. Earth is
    // 420 km in this scale; the Sun is compressed to 2,200 km so it still
    // dominates the inner view without swallowing Mercury's 3,000 km orbit.
    public const float PlanetRadius = 420_000f;
    public const float SurfaceGravity = 9.81f;
    public const float ParkingAltitude = 60_000f;
    public const float OrbitRadius = PlanetRadius + ParkingAltitude; // from Earth's centre

    public const float SunRadius = 2_200_000f;
    public const float SunSurfaceGravity = 274f;

    // Multiplies every heliocentric distance. Planet sizes are untouched.
    public const float DistanceScale = 1_000f;

    public const float MercuryOrbit = 4_000f * DistanceScale;
    public const float VenusOrbit = 5_500f * DistanceScale;
    public const float EarthOrbitRadius = 9_000f * DistanceScale;
    public const float MarsOrbit = 12_500f * DistanceScale;
    public const float JupiterOrbit = 18_000f * DistanceScale;
    public const float SaturnOrbit = 24_000f * DistanceScale;
    public const float UranusOrbit = 30_000f * DistanceScale;
    public const float NeptuneOrbit = 36_000f * DistanceScale;

    // Planet radii are a stylised "big world" set: real ordering, but the
    // giants are compressed so they do not overlap their compressed orbits.
    public const float MercuryRadius = 180_000f;
    public const float VenusRadius = 400_000f;
    public const float MarsRadius = 260_000f;
    public const float JupiterRadius = 1_800_000f;
    public const float SaturnRadius = 1_500_000f;
    public const float UranusRadius = 700_000f;
    public const float NeptuneRadius = 680_000f;

    // Authored patched-conic frames. These are gameplay bubbles sized to sit
    // between neighbouring orbits, not literal Hill spheres: the compressed
    // orbits and oversized planets would overlap if the SOI were derived from
    // the raw mass ratio. They still nest correctly (Moon inside Earth) and
    // the physics field uses them to decide which body owns the ship.
    public const float MercurySoi = 500_000f;
    public const float VenusSoi = 1_000_000f;
    public const float EarthSoi = 1_700_000f;
    public const float MarsSoi = 1_400_000f;
    public const float JupiterSoi = 2_200_000f;
    public const float SaturnSoi = 2_600_000f;
    public const float UranusSoi = 2_600_000f;
    public const float NeptuneSoi = 2_600_000f;

    // Keeps the orbit rings visible at the larger scale.
    public const float OrbitRingThicknessFraction = 0.0008f;

    public const float MoonRadius = 115_000f;          // ~0.27 x Earth, like irl
    public const float MoonSurfaceGravity = 1.62f;
    public const float MoonOrbitRadius = PlanetRadius * 3.5f; // irl is ~60x, compressed
    public const float MoonSoi = 450_000f;

    // Constant world-space thickness for the live orbit tube. It is sized for
    // the initial parking orbit, so a burn changes the conic without visibly
    // fattening the line.
    public const float OrbitLineHalfWidth = 900f;

    public static readonly PlanetProfile VisualPlanet =
        new("Terra", PlanetRadius, SurfaceGravity);
    public static readonly PlanetProfile SunProfile =
        new("Sun", SunRadius, SunSurfaceGravity);
    public static readonly PlanetProfile MoonProfile =
        new("Moon", MoonRadius, MoonSurfaceGravity);

    public static readonly float Mu = (float)VisualPlanet.Mu;
    public static readonly float CircularSpeed =
        (float)VisualPlanet.CircularSpeed(ParkingAltitude);
    public static readonly float OrbitPeriod =
        (float)VisualPlanet.OrbitalPeriod(ParkingAltitude);

    public static readonly float SunMu = (float)SunProfile.Mu;
    public static readonly float EarthOrbitSpeed =
        MathF.Sqrt(SunMu / EarthOrbitRadius);
    public static readonly float EarthOrbitAngularSpeed =
        EarthOrbitSpeed / EarthOrbitRadius;
    public static readonly float MoonOrbitSpeed =
        MathF.Sqrt(Mu / MoonOrbitRadius);
    public static readonly float MoonOrbitAngularSpeed =
        MoonOrbitSpeed / MoonOrbitRadius;

    public static readonly Vector3 EarthStart =
        new(EarthOrbitRadius, 0f, 0f);
    public static readonly Vector3 ShipStart =
        EarthStart + new Vector3(OrbitRadius, 0f, 0f);
    public static readonly Vector3 ShipStartVelocity =
        new(0f, 0f, EarthOrbitSpeed + CircularSpeed);

    public static void Build(GameRoot root)
    {
        // Pure point gravity: the planet field is the only source of "down".
        if (root.PhysicsBackend != null)
            root.PhysicsBackend.Gravity = Vector3.Zero;

        var world = root.CreateWorld("SpaceFlight");

        // C# hot reload re-runs OnStart on the live root. CreateWorld returns the
        // existing world, so building again would duplicate the Sun, planets and
        // ship on top of the running scene. A full restart is required to pick up
        // changed scene constants; the alternative is a world clear that destroys
        // the old physics bodies, which the engine does not expose yet.
        if (world.Root.Any(e => e.Name == "Sun"))
        {
            Console.WriteLine("[SpaceFlight] scene already built; skipping hot-reload rebuild");
            return;
        }

        var gravityBodies = new GravityBodySet();

        // The nebula is painted on an equirectangular 4096x2048 panorama (pulled
        // out of SourceAssets/skyboxspace.glb), so it goes through the
        // environment sky instead of a giant inside-out mesh shell. The solid sky
        // colour stays as the fallback for when the pano fails to load.
        var environment = new Element("Environment");
        environment.AddComponent(new EnvironmentComponent(
            BackgroundMode.Skybox, 0.012f, 0.018f, 0.035f)
        {
            SkyboxPath = "v12://Assets/skybox_space.jpg",
            AmbientR = 0.08f,
            AmbientG = 0.10f,
            AmbientB = 0.15f,
            AmbientEnergy = 1f,
            ShadowsEnabled = false,
        });
        world.AddElement(environment);

        // Quiet looped background music. SpatialBlend 0 keeps it out of the 3D
        // attenuation path: the camera ranges from 180 to a few thousand units
        // from the ship, which would otherwise fade the track to silence.
        var music = new Element("Music");
        music.AddComponent(new AudioSourceComponent
        {
            AudioClipPath = "v12://Assets/spaceflight.mp3",
            Volume = 0.12f,
            Loop = true,
            Autoplay = true,
            SpatialBlend = 0f,
            Bus = AudioSourceBus.Music,
        });
        world.AddElement(music);
        var sun = new Element("SunLight");
        sun.SetRotation(-38f, -32f, 0f);
        sun.AddComponent(new GenericLightComponent
        {
            Type = LightType.Directional,
            Energy = 1.6f,
            ColorR = 1f,
            ColorG = 0.94f,
            ColorB = 0.84f,
            ShadowEnabled = false,
        });
        world.AddElement(sun);

        // A cool fill from the opposite side keeps the unlit half readable
        // without washing out the warm key light.
        var fill = new Element("Fill");
        fill.SetRotation(28f, 155f, 0f);
        fill.AddComponent(new GenericLightComponent
        {
            Type = LightType.Directional,
            Energy = 0.22f,
            ColorR = 0.45f,
            ColorG = 0.62f,
            ColorB = 0.90f,
            ShadowEnabled = false,
        });
        world.AddElement(fill);

        // A compact solar system, scaled for the ship. Every planet starts on
        // the +X line so the order is readable, then the orbit components move
        // them at their own angular rates.
        AddPlanet(
            world,
            "Sun",
            SunProfile,
            Vector3.Zero,
            new Vector3(1f, 0.86f, 0.55f),
            "v12://Assets/2k_sun.jpg",
            roughness: 1f,
            bodies: gravityBodies,
            unlit: true);

        var earth = AddOrbitingPlanet(
            world,
            "Earth",
            VisualPlanet,
            EarthOrbitRadius,
            new Vector3(1f),
            "v12://Assets/2k_earth_daymap.jpg",
            gravityBodies,
            SunMu,
            EarthSoi);

        var moon = AddPlanet(
            world,
            "Moon",
            MoonProfile,
            EarthStart + new Vector3(MoonOrbitRadius, 0f, 0f),
            new Vector3(0.55f, 0.55f, 0.58f),
            "v12://Assets/2k_moon.jpg",
            bodies: gravityBodies,
            sphereOfInfluence: MoonSoi);
        moon.AddComponent(new PlanetOrbitComponent
        {
            CenterElement = earth,
            Radius = MoonOrbitRadius,
            AngularSpeed = MoonOrbitAngularSpeed,
        });

        AddOrbitingPlanet(
            world,
            "Mercury",
            new PlanetProfile("Mercury", MercuryRadius, 3.7f),
            MercuryOrbit,
            new Vector3(0.52f, 0.49f, 0.45f),
            null,
            gravityBodies,
            SunMu,
            MercurySoi);

        AddOrbitingPlanet(
            world,
            "Venus",
            new PlanetProfile("Venus", VenusRadius, 8.87f),
            VenusOrbit,
            new Vector3(0.80f, 0.68f, 0.42f),
            null,
            gravityBodies,
            SunMu,
            VenusSoi);

        AddOrbitingPlanet(
            world,
            "Mars",
            new PlanetProfile("Mars", MarsRadius, 3.71f),
            MarsOrbit,
            new Vector3(0.62f, 0.28f, 0.16f),
            "v12://Assets/2k_mars.jpg",
            gravityBodies,
            SunMu,
            MarsSoi);

        AddOrbitingPlanet(
            world,
            "Jupiter",
            new PlanetProfile("Jupiter", JupiterRadius, 24.79f),
            JupiterOrbit,
            new Vector3(0.68f, 0.54f, 0.38f),
            "v12://Assets/2k_jupiter.jpg",
            gravityBodies,
            SunMu,
            JupiterSoi);

        var saturnRadius = SaturnRadius;
        var saturn = AddOrbitingPlanet(
            world,
            "Saturn",
            new PlanetProfile("Saturn", saturnRadius, 10.44f),
            SaturnOrbit,
            new Vector3(0.72f, 0.64f, 0.46f),
            "v12://Assets/2k_saturn.jpg",
            gravityBodies,
            SunMu,
            SaturnSoi);
        var saturnTransform = saturn.LocalTransform;
        saturnTransform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.24f);
        saturn.LocalTransform = saturnTransform;
        AddSaturnRing(saturn, saturnRadius);

        AddOrbitingPlanet(
            world,
            "Uranus",
            new PlanetProfile("Uranus", UranusRadius, 8.69f),
            UranusOrbit,
            new Vector3(0.52f, 0.74f, 0.78f),
            "v12://Assets/2k_uranus.jpg",
            gravityBodies,
            SunMu,
            UranusSoi);

        AddOrbitingPlanet(
            world,
            "Neptune",
            new PlanetProfile("Neptune", NeptuneRadius, 11.15f),
            NeptuneOrbit,
            new Vector3(0.20f, 0.36f, 0.78f),
            "v12://Assets/2k_neptune.jpg",
            gravityBodies,
            SunMu,
            NeptuneSoi);

        // The visible hull is a 28 x 26 x 42 rocket, but the contact proxy is a
        // 28-unit sphere. A box collider catches a corner on the curved surface
        // and pole-vaults the hull: at 240 m/s a box is thrown 74 m/s outward
        // (about 450 units up) while a sphere just skids. The box stays as the
        // visual and as the mass basis; only the contact shape is rounded.
        var hullSize = new Vector3(28f, 26f, 42f);
        var ship = world.SpawnPrimitive(
            MeshShape.Sphere,
            new Vector3(hullSize.X),
            ShipStart,
            dynamic: true,
            name: "Ship",
            mesh: false);

        // A contact's velocity change is mass-independent against a kinematic
        // planet: the impulse scales with mass, so it cancels out. Mass still
        // matters against other dynamic bodies; it does not stop a landing bounce.
        const float HullDensity = 0.4f; // kg per cubic unit, tuned for the demo
        float shipMass = MathF.Max(
            1f,
            hullSize.X * hullSize.Y * hullSize.Z * HullDensity);

        ship.AddComponent(new RigidBodyComponent
        {
            Mass = shipMass,
            GravityScale = 1f,
            CanSleep = false,
            AngularDamping = 0.2f,
        });

        var shipBody = ship.GetComponent<PhysicsBodyComponent>()!;
        // The ship shares Earth's solar orbital velocity, then adds the local
        // circular orbit around Earth. Without the first term the whole orbit
        // would be a huge hyperbola in the Sun's frame.
        shipBody.LinearVelocity = ShipStartVelocity;
        shipBody.SyncLocomotionVelocity = false;

        // Start already pointing prograde so the attitude hold does not need a
        // 180-degree spin on the first tick.
        var initialAttitude = OrbitCameraComponent.LookAt(
            Vector3.Zero, new Vector3(0f, 0f, 1f), Vector3.UnitY).Rotation;
        ship.LocalTransform = new TRS
        {
            Position = ShipStart,
            Rotation = initialAttitude,
            Scale = Vector3.One,
        };

        ship.AddComponent(new OrbitTelemetryComponent
        {
            Bodies = gravityBodies,
        });

        var attitude = new ProgradeAttitudeComponent
        {
            Bodies = gravityBodies,
        };
        ship.AddComponent(attitude);

        var input = new SpaceFlightInputComponent
        {
            SpawnPosition = ShipStart,
            SpawnVelocity = ShipStartVelocity,
            Bodies = gravityBodies,
            ResetTarget = earth,
            ResetMu = Mu,
            ResetRadius = OrbitRadius,
            Attitude = attitude,
        };
        ship.AddComponent(input);

        // Keep the float bubble centred on the ship. The true position lives in
        // World.Origin (double); the ship, planets and camera stay in local
        // coordinates, so GPU/BEPU precision does not depend on the 270k-unit
        // distance to the Sun.
        var floatingOrigin = new FloatingOriginComponent
        {
            Anchor = ship,
            Threshold = 2000f,
        };
        floatingOrigin.Rebased += (world, origin, delta) =>
            Console.WriteLine(
                $"[SpaceFlight] origin rebase #{world.OriginRebaseCount}: " +
                $"global={origin} localShift={delta.Length():F1}");
        ship.AddComponent(floatingOrigin);

        // The GLB is Y-up and about 3.26 units tall. Rotate +Y onto local -Z
        // (the nose), scale it to roughly the 48-unit hull, and recenter it
        // after the rotation moves its 0.61-unit Y centre onto Z.
        var shipModel = new Element("ShipModel");
        shipModel.LocalTransform = new TRS
        {
            Position = new Vector3(0f, 0f, 7.31f),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2f),
            Scale = new Vector3(12f),
        };
        shipModel.AddComponent(new ModelInstance("v12://Assets/rocket.glb"));
        ship.AddChild(shipModel);

        var plume = new Element("ThrustPlume");
        var plumeMesh = new ConeMesh("ThrustPlume")
        {
            Radius = 4.5f,
            Height = 14f,
            Slices = 24,
        };
        plume.AddComponent(plumeMesh);
        plume.AddComponent(new MeshRenderer { Mesh = plumeMesh });
        plume.AddComponent(new MaterialComponent
        {
            R = 1f,
            G = 0.52f,
            B = 0.18f,
            A = 1f,
            Roughness = 1f,
            Unlit = true,
            EmissionR = 1f,
            EmissionG = 0.36f,
            EmissionB = 0.1f,
            EmissionEnergy = 2f,
        });
        var plumeComponent = new ThrustPlumeComponent
        {
            OnScale = 1f,
            OffScale = 0.001f,
        };
        plume.AddComponent(plumeComponent);

        // Held-space exhaust loop. It rides the plume element so it follows the
        // hull, but SpatialBlend 0 keeps it audible from the orbit camera too;
        // the camera can sit thousands of units away.
        var thrusterAudio = new AudioSourceComponent
        {
            AudioClipPath = "v12://Assets/benvibrant-thruster-loop-562715.mp3",
            Volume = 0.35f,
            Loop = true,
            Autoplay = false,
            SpatialBlend = 0f,
            Bus = AudioSourceBus.Sfx,
        };
        plume.AddComponent(thrusterAudio);

        plume.LocalTransform = new TRS
        {
            Position = new Vector3(0f, 0f, 26f),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f),
            Scale = new Vector3(0.001f),
        };
        ship.AddChild(plume);
        input.ThrustPlume = plumeComponent;
        input.ThrustAudio = thrusterAudio;

        // A live prediction of the current orbit. It starts on the circular
        // reference ring and becomes an ellipse after a burn.
        var liveOrbitElement = new Element("LiveOrbit");
        var liveOrbit = new OrbitPathMesh
        {
            Mu = Mu,
            Ship = ship,
            Bodies = gravityBodies,
            Segments = 192,
            HalfWidth = OrbitLineHalfWidth,
        };
        liveOrbitElement.AddComponent(liveOrbit);
        liveOrbitElement.AddComponent(new MeshRenderer { Mesh = liveOrbit });
        liveOrbitElement.AddComponent(new MaterialComponent
        {
            R = 0f,
            G = 0.8f,
            B = 0.8f,
            A = 1f,
            Roughness = 0.5f,
            DoubleSided = true,
            Unlit = true,
        });
        world.AddElement(liveOrbitElement);
        liveOrbit.SetState(
            OrbitalState.FromVectors(
                Mu,
                new Vector3(OrbitRadius, 0f, 0f),
                new Vector3(0f, 0f, CircularSpeed)),
            EarthStart);

        var camera = new Element("Camera");
        camera.LocalTransform = OrbitCameraComponent.LookAt(
            new Vector3(0f, 2500f, 1750f), Vector3.Zero);
        camera.AddComponent(new CameraComponent
        {
            IsCurrent = true,
            Fov = 70f,
            NearClip = 5f,
            FarClip = 100_000_000f,
        });
        var cameraRig = new OrbitCameraComponent
        {
            Radius = 1750f,
            Height = 2500f,
            AngularSpeed = 0.014f,
            FollowTarget = ship,
            Bodies = gravityBodies,
            FollowDistance = 180f,
            FollowHeight = 50f,
            ReferenceRadius = 1_000f,
        };
        camera.AddComponent(cameraRig);
        world.AddElement(camera);
        liveOrbit.Camera = cameraRig;

        // A small 3D navball fixed in camera space. The ball is a plain sphere;
        // the horizon and meridian rings are oriented from the ship's own axes,
        // and the projected markers show where the nose and velocity point.
        var navball = new Element("Navball");
        var navballComponent = new NavballComponent
        {
            Ship = ship,
            Bodies = gravityBodies,
            // Kept well past the 5-unit near clip. The component's marker math
            // is depth/scale invariant, so this is the same instrument at the
            // same screen size, just not clipped by the camera plane.
            LocalPosition = new Vector3(1.95f, -1.35f, -6f),
            LocalScale = 1.5f,
        };
        navball.AddComponent(navballComponent);
        navball.LocalTransform = new TRS
        {
            Position = navballComponent.LocalPosition,
            Rotation = Quaternion.Identity,
            Scale = new Vector3(navballComponent.LocalScale),
        };

        var navballBall = new Element("NavballBall");
        var navballBallMesh = new PlanetMesh
        {
            Radius = 0.5f,
            Stacks = 32,
            Slices = 64,
        };
        navballBall.AddComponent(navballBallMesh);
        navballBall.AddComponent(new MeshRenderer { Mesh = navballBallMesh });
        navballBall.AddComponent(new MaterialComponent
        {
            R = 0.11f,
            G = 0.12f,
            B = 0.12f,
            A = 1f,
            Roughness = 0.95f,
        });
        navball.AddChild(navballBall);

        var horizon = new Element("NavballHorizon");
        var horizonCurve = new NavballCurveMesh("NavballHorizon")
        {
            Segments = 128,
            HalfWidth = 0.010f,
        };
        horizon.AddComponent(horizonCurve);
        horizon.AddComponent(new MeshRenderer { Mesh = horizonCurve });
        horizon.AddComponent(new MaterialComponent
        {
            R = 0.86f,
            G = 0.82f,
            B = 0.70f,
            A = 1f,
            Roughness = 0.45f,
            Unlit = true,
            NoDepthTest = true,
        });
        navball.AddChild(horizon);

        var meridian = new Element("NavballMeridian");
        var meridianCurve = new NavballCurveMesh("NavballMeridian")
        {
            Segments = 128,
            HalfWidth = 0.007f,
        };
        meridian.AddComponent(meridianCurve);
        meridian.AddComponent(new MeshRenderer { Mesh = meridianCurve });
        meridian.AddComponent(new MaterialComponent
        {
            R = 0.60f,
            G = 0.56f,
            B = 0.48f,
            A = 1f,
            Roughness = 0.5f,
            Unlit = true,
            NoDepthTest = true,
        });
        navball.AddChild(meridian);

        var housing = new Element("NavballHousing");
        housing.LocalTransform = new TRS
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f),
            Scale = Vector3.One,
        };
        var housingMesh = new TorusSegmentMesh("NavballHousing")
        {
            RingRadius = 0.52f,
            TubeRadius = 0.012f,
            RadialSegments = 6,
            TubularSegments = 96,
        };
        housing.AddComponent(housingMesh);
        housing.AddComponent(new MeshRenderer { Mesh = housingMesh });
        housing.AddComponent(new MaterialComponent
        {
            R = 0.46f,
            G = 0.44f,
            B = 0.40f,
            A = 1f,
            Roughness = 0.6f,
            Unlit = true,
        });
        navball.AddChild(housing);

        var progradeMarker = AddNavballMarker(
            navball, "NavballPrograde", 0.20f, 0.95f, 1f);
        var retrogradeMarker = AddNavballMarker(
            navball, "NavballRetrograde", 0.95f, 0.32f, 0.24f);

        navballComponent.HorizonCurve = horizonCurve;
        navballComponent.MeridianCurve = meridianCurve;
        navballComponent.ProgradeMarker = progradeMarker;
        navballComponent.RetrogradeMarker = retrogradeMarker;

        camera.AddChild(navball);

        input.Camera = cameraRig;

        var docPlanet = PlanetProfile.EcoVRWorking;
        Console.WriteLine(
            $"[SpaceFlight] visual R={PlanetRadius} mu={Mu:G6}; " +
            $"orbit r={OrbitRadius} v={CircularSpeed:F2} T={OrbitPeriod:F1}s");
        Console.WriteLine(
            $"[SpaceFlight] patched conic: Earth SOI={EarthSoi:F0} " +
            $"(hill={OrbitalMechanics.SphereOfInfluence(SunMu, Mu, EarthOrbitRadius):F0}) " +
            $"moon a={MoonOrbitRadius} SOI={MoonSoi:F0} " +
            $"(hill={OrbitalMechanics.SphereOfInfluence(Mu, MoonProfile.Mu, MoonOrbitRadius):F0})");
        Console.WriteLine(
            $"[SpaceFlight] heliocentric: Sun mu={SunMu:G6} " +
            $"Earth v={EarthOrbitSpeed:F1} T={2 * MathF.PI / EarthOrbitAngularSpeed:F0}s");
        Console.WriteLine(
            $"[SpaceFlight] doc target: {docPlanet.Name} " +
            $"R={docPlanet.Radius / 1000:F0} km, g={docPlanet.SurfaceGravity:F2}, " +
            $"v={docPlanet.CircularSpeed() / 1000:F2} km/s, " +
            $"T={docPlanet.OrbitalPeriod() / 60:F1} min");
        Console.WriteLine(
            "[SpaceFlight] controls: [ slower  ] faster  B prograde  N retrograde  " +
            "C camera  T attitude  WASD rotate  Q/E roll  Space thrust  " +
            "arrows or mouse orbit camera  wheel zoom  R reset");
    }

    private static void AddSaturnRing(Element planet, float radius)
    {
        var ring = new Element("SaturnRing");
        var mesh = new PlanetRingMesh("SaturnRing")
        {
            InnerRadius = 1.35f,
            OuterRadius = 2.15f,
            Segments = 160,
        };
        ring.AddComponent(mesh);
        ring.AddComponent(new MeshRenderer { Mesh = mesh });
        ring.AddComponent(new MaterialComponent
        {
            R = 0.74f,
            G = 0.66f,
            B = 0.50f,
            A = 1f,
            Roughness = 0.9f,
            DoubleSided = true,
            Unlit = true,
        });
        ring.LocalTransform = new TRS
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = new Vector3(radius),
        };
        planet.AddChild(ring);
    }

    private static Element AddOrbitingPlanet(
        World world,
        string name,
        PlanetProfile profile,
        float orbitRadius,
        Vector3 colour,
        string? texture,
        GravityBodySet bodies,
        float parentMu,
        float sphereOfInfluence)
    {
        var planet = AddPlanet(
            world,
            name,
            profile,
            new Vector3(orbitRadius, 0f, 0f),
            colour,
            texture,
            bodies: bodies,
            sphereOfInfluence: sphereOfInfluence);

        planet.AddComponent(new PlanetOrbitComponent
        {
            Center = Vector3.Zero,
            Radius = orbitRadius,
            AngularSpeed = MathF.Sqrt(
                parentMu / (orbitRadius * orbitRadius * orbitRadius)),
        });

        AddOrbitRing(
            world,
            orbitRadius,
            MathF.Max(80f, orbitRadius * OrbitRingThicknessFraction));
        return planet;
    }

    private static void AddOrbitRing(World world, float radius, float tubeRadius)
    {
        var ring = new Element($"PlanetOrbit{radius:F0}");
        var mesh = new TorusSegmentMesh($"PlanetOrbit{radius:F0}")
        {
            RingRadius = radius,
            TubeRadius = tubeRadius,
            RadialSegments = 4,
            TubularSegments = 256,
        };
        ring.AddComponent(mesh);
        ring.AddComponent(new MeshRenderer { Mesh = mesh });
        ring.AddComponent(new MaterialComponent
        {
            R = 0.20f,
            G = 0.24f,
            B = 0.28f,
            A = 1f,
            Roughness = 0.85f,
            Unlit = true,
        });
        world.AddElement(ring);
    }

    private static Element AddPlanet(
        World world,
        string name,
        PlanetProfile profile,
        Vector3 position,
        Vector3 colour,
        string? texture = null,
        float roughness = 0.85f,
        bool gravity = true,
        GravityBodySet? bodies = null,
        bool unlit = false,
        float sphereOfInfluence = 0f)
    {
        // Solid sphere collider plus gravity. Impacts are pure physics now; the
        // ship's derived mass and the contact material decide the response.
        var planet = world.SpawnPrimitive(
            MeshShape.Sphere,
            new Vector3((float)profile.Radius * 2f),
            position,
            dynamic: false,
            name: name,
            mesh: false);

        // Planets move every tick. Driving the body by velocity (rather than
        // teleporting it) gives contacts the same surface speed, so a landed ship
        // rides with the planet instead of being scraped out of its frame.
        var planetBody = planet.GetComponent<PhysicsBodyComponent>();
        if (planetBody != null)
            planetBody.ContinuousKinematicMotion = true;

        var mesh = new PlanetMesh
        {
            Radius = (float)profile.Radius,
            Stacks = 32,
            Slices = 64,
        };
        planet.AddComponent(mesh);
        planet.AddComponent(new MeshRenderer { Mesh = mesh });

        var material = new MaterialComponent
        {
            R = colour.X,
            G = colour.Y,
            B = colour.Z,
            A = 1f,
            Roughness = roughness,
            Unlit = unlit,
        };
        if (texture != null)
        {
            material.AlbedoTexture = texture;
            material.R = 1f;
            material.G = 1f;
            material.B = 1f;
        }
        planet.AddComponent(material);

        var source = GravitySourceComponent.FromProfile(profile, sphereOfInfluence);
        source.Enabled = gravity;
        planet.AddComponent(source);
        bodies?.Add(source);
        return planet;
    }

    /// <summary>
    /// A navball marker: a thin ring with a filled centre dot. The element has
    /// no renderer of its own; its children are moved by
    /// <see cref="NavballComponent"/> and face the camera.
    /// </summary>
    private static Element AddNavballMarker(
        Element parent, string name, float r, float g, float b)
    {
        var marker = new Element(name);
        marker.Active = false;

        var ring = new Element(name + "Ring");
        var ringMesh = new TorusSegmentMesh(name + "Ring")
        {
            RingRadius = 0.095f,
            TubeRadius = 0.010f,
            RadialSegments = 6,
            TubularSegments = 48,
        };
        ring.AddComponent(ringMesh);
        ring.AddComponent(new MeshRenderer { Mesh = ringMesh });
        ring.AddComponent(new MaterialComponent
        {
            R = r,
            G = g,
            B = b,
            A = 1f,
            Roughness = 0.4f,
            Unlit = true,
            NoDepthTest = true,
        });
        marker.AddChild(ring);

        var dot = new Element(name + "Dot");
        // The marker is rotated +90 degrees around X so the torus ring faces the
        // camera. The quad is already in the XY plane, so compensate locally or
        // the centre dot turns edge-on and disappears.
        dot.LocalTransform = new TRS
        {
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2f),
        };
        var dotMesh = new QuadMesh(name + "Dot")
        {
            Size = new Vector2(0.034f, 0.034f),
        };
        dot.AddComponent(dotMesh);
        dot.AddComponent(new MeshRenderer { Mesh = dotMesh });
        dot.AddComponent(new MaterialComponent
        {
            R = r,
            G = g,
            B = b,
            A = 1f,
            Roughness = 0.4f,
            Unlit = true,
            NoDepthTest = true,
        });
        marker.AddChild(dot);

        parent.AddChild(marker);
        return marker;
    }
}
