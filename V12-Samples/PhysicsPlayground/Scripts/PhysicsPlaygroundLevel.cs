using System;
using System.Collections.Generic;
using System.Numerics;
using V12.Basic.Building;
using V12.Basic.Components;
using V12.Basic.Grabbing;
using V12.Components;
using V12.Components.Renderables;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Physics;
using V12.Core.Interfaces.Renderer;

namespace PhysicsPlayground;

/// <summary>
/// Builds the training yard once, then owns the runtime state the controller
/// needs: reset poses for every grabbable prop, moving platform motors and the
/// bin trigger. The yard is deliberately made from primitives so the sample is
/// readable and every collider is visible.
/// </summary>
[ComponentEntry("GAMEPLAY", "Physics Playground Level", SingleInstance = true)]
public sealed class PhysicsPlaygroundLevel : ComponentBase
{
    public sealed record PropHome(IWorldElement Element, Vector3 Position, Quaternion Rotation, bool BinTarget);

    private readonly List<PropHome> _props = new();
    private readonly List<MovingPlatformComponent> _platforms = new();
    private World? _world;
    private GameRoot? _root;
    private bool _built;
    private bool _noWorldLogged;

    public IReadOnlyList<PropHome> Props => _props;
    public int PropCount => _props.Count;
    public int BinTargetCount { get; private set; }
    public string BinTriggerName => "BinTrigger";
    public string GateTriggerName => "GateTrigger";
    public MovingPlatformComponent? MovingPlatform { get; private set; }
    public IWorldElement? MovingPlatformElement { get; private set; }
    public Vector3 MovingPlatformHalfExtents { get; private set; } = new(1.5f, 0.15f, 1.5f);

    public override string Name => "Physics Playground";
    public override string Description => "Training yard props, triggers and motors";

    public override void OnAttach(IWorldElement worldElement)
    {
        base.OnAttach(worldElement);
        _root = GameRoot.Instance;
        _world = _root?.GetWorldForElement(worldElement);

        // World parse holds a read lock on the world, and adding root elements
        // takes the write lock. Build after the fixed step instead, where the
        // world lock is not held.
        if (_root != null)
            _root.OnFixedUpdate += BuildAfterStep;
    }

    public override void OnDetach(IWorldElement worldElement)
    {
        if (_root != null)
            _root.OnFixedUpdate -= BuildAfterStep;
        _root = null;
        base.OnDetach(worldElement);
    }

    private void BuildAfterStep(float deltaTime)
    {
        EnsureBuilt();
        if (_built && _root != null)
            _root.OnFixedUpdate -= BuildAfterStep;
    }

    /// <summary>Builds the yard once, from a point where the world write lock is
    /// safe to take.</summary>
    public void EnsureBuilt()
    {
        if (_built)
            return;

        _world ??= Owner != null ? GameRoot.Instance?.GetWorldForElement(Owner) : null;
        _world ??= GameRoot.Instance?.SelectedWorld;
        if (_world == null)
        {
            if (!_noWorldLogged)
            {
                _noWorldLogged = true;
                GameRoot.Log?.Warning("[PhysicsPlayground] level has no world yet (owner={Owner})",
                    Owner?.Name ?? "<null>");
            }
            return;
        }

        Build(_world);
    }

    public void Reset()
    {
        foreach (var prop in _props)
        {
            prop.Element.LocalTransform = new TRS(prop.Position, prop.Rotation, Vector3.One);

            if (prop.Element.GetComponent<PhysicsBodyComponent>() is { Body: { } body })
            {
                body.Position = prop.Position;
                body.Rotation = prop.Rotation;
                body.LinearVelocity = Vector3.Zero;
                body.AngularVelocity = Vector3.Zero;
                body.GravityScale = 1f;
                body.LockRotation = false;
            }
        }

        foreach (var platform in _platforms)
            platform.ResetMotion();
    }

    // ── Build ───────────────────────────────────────────────────────────────

    private void Build(World world)
    {
        if (_built)
            return;
        _built = true;

        // The yard is a closed bay: warm concrete, dark steel, one amber accent.
        var concrete = new Vector3(0.27f, 0.25f, 0.22f);
        var concreteDark = new Vector3(0.17f, 0.16f, 0.14f);
        var steel = new Vector3(0.12f, 0.13f, 0.14f);
        var rust = new Vector3(0.52f, 0.28f, 0.12f);
        var bone = new Vector3(0.68f, 0.61f, 0.47f);
        var amber = new Vector3(0.92f, 0.53f, 0.15f);

        // Ground and perimeter.
        AddStaticBox(world, "Ground", new Vector3(0f, -0.2f, 0f),
            new Vector3(30f, 0.4f, 30f), Vector3.Zero, concrete, 0.92f);

        AddStaticBox(world, "WallNorth", new Vector3(0f, 3f, -12f),
            new Vector3(30f, 6f, 0.6f), Vector3.Zero, concreteDark, 0.95f);
        AddStaticBox(world, "WallSouth", new Vector3(0f, 3f, 12f),
            new Vector3(30f, 6f, 0.6f), Vector3.Zero, concreteDark, 0.95f);
        AddStaticBox(world, "WallWest", new Vector3(-12f, 3f, 0f),
            new Vector3(0.6f, 6f, 30f), Vector3.Zero, concreteDark, 0.95f);
        AddStaticBox(world, "WallEast", new Vector3(12f, 3f, 0f),
            new Vector3(0.6f, 6f, 30f), Vector3.Zero, concreteDark, 0.95f);

        // Painted walkway and a loading door give the bay a focal wall.
        AddDecorBox(world, "Walkway", new Vector3(0f, 0.015f, 1.5f),
            new Vector3(3.6f, 0.03f, 17f), Vector3.Zero, concrete * 1.32f, 0.9f);
        AddDecorBox(world, "BayDoor", new Vector3(0f, 2.4f, -11.66f),
            new Vector3(5.2f, 4.4f, 0.12f), Vector3.Zero, steel * 0.8f, 0.6f, 0.4f);
        AddDecorBox(world, "BayDoorStripe", new Vector3(0f, 1.2f, -11.58f),
            new Vector3(5.2f, 0.16f, 0.04f), Vector3.Zero, amber * 0.85f, 0.5f);

        // A low plinth puts the first crate exactly on the desktop eye line.
        AddStaticBox(world, "TestPlinth", new Vector3(0f, 0.55f, 4.8f),
            new Vector3(1.2f, 1.1f, 1.2f), Vector3.Zero, concreteDark, 0.85f);
        AddDynamicBox(world, "TestCrate", new Vector3(0f, 1.55f, 4.8f),
            new Vector3(0.8f, 0.8f, 0.8f), 1.2f, bone, 0.55f, 0.82f, binTarget: false, throwMultiplier: 1.1f);

        // Loading ramp and raised platform.
        AddStaticBox(world, "Platform", new Vector3(-6f, 1.3f, -3.5f),
            new Vector3(6f, 0.4f, 6f), Vector3.Zero, concrete, 0.9f);
        AddStaticBox(world, "Ramp", new Vector3(-6f, 0.82f, 2.0f),
            new Vector3(4f, 0.3f, 6.4f), new Vector3(14f, 0f, 0f), concrete, 0.9f);

        // Gate trigger on the platform. The frame is solid; only the volume is
        // a trigger, so the player can walk through it.
        AddStaticBox(world, "GatePostL", new Vector3(-7.3f, 2.0f, -2.0f),
            new Vector3(0.3f, 2.8f, 0.3f), Vector3.Zero, steel, 0.55f, 0.5f);
        AddStaticBox(world, "GatePostR", new Vector3(-4.7f, 2.0f, -2.0f),
            new Vector3(0.3f, 2.8f, 0.3f), Vector3.Zero, steel, 0.55f, 0.5f);
        AddStaticBox(world, "GateBeam", new Vector3(-6f, 3.4f, -2.0f),
            new Vector3(2.9f, 0.3f, 0.3f), Vector3.Zero, steel, 0.55f, 0.5f);
        AddTrigger(world, GateTriggerName, new Vector3(-6f, 2.5f, -2.0f),
            new Vector3(2.4f, 2.2f, 1.4f), amber);

        // Crate stack.
        AddDynamicBox(world, "CrateA", new Vector3(3f, 0.55f, -3.0f),
            new Vector3(1f, 1f, 1f), 1.0f, rust, 0.68f, 0.8f, binTarget: false);
        AddDynamicBox(world, "CrateB", new Vector3(4.1f, 0.55f, -3.0f),
            new Vector3(1f, 1f, 1f), 0.9f, rust * 0.9f, 0.68f, 0.8f, binTarget: false);
        AddDynamicBox(world, "CrateC", new Vector3(3.55f, 1.58f, -3.0f),
            new Vector3(1f, 1f, 1f), 0.8f, rust * 1.05f, 0.68f, 0.8f, binTarget: false);
        AddDynamicBox(world, "CrateD", new Vector3(3f, 2.61f, -3.0f),
            new Vector3(0.9f, 0.9f, 0.9f), 0.7f, bone * 0.8f, 0.55f, 0.82f, binTarget: false);

        // Heavy and light props prove the mass falloff in the grabber.
        AddDynamicSphere(world, "IronBall", new Vector3(5.6f, 0.8f, 2.2f),
            0.7f, 14f, steel, 0.35f, 0.6f, binTarget: false, throwMultiplier: 0.35f);
        AddDynamicCylinder(world, "LightDrum", new Vector3(4.4f, 0.65f, -5.8f),
            0.42f, 1.0f, 0.35f, bone * 0.85f, 0.45f, 0.72f, binTarget: false, throwMultiplier: 1.5f);

        // Target bin. Five light blocks are the score; the pen is solid.
        AddBin(world, new Vector3(8f, 0f, -5f), amber);
        AddDynamicBox(world, "Toss1", new Vector3(6.5f, 0.35f, 2.0f),
            new Vector3(0.5f, 0.5f, 0.5f), 0.28f, amber, 0.4f, 0.7f, binTarget: true, throwMultiplier: 1.35f);
        AddDynamicBox(world, "Toss2", new Vector3(7.2f, 0.35f, 1.2f),
            new Vector3(0.5f, 0.5f, 0.5f), 0.28f, amber * 0.9f, 0.4f, 0.7f, binTarget: true, throwMultiplier: 1.35f);
        AddDynamicBox(world, "Toss3", new Vector3(8.0f, 0.35f, 0.7f),
            new Vector3(0.5f, 0.5f, 0.5f), 0.28f, amber * 1.05f, 0.4f, 0.7f, binTarget: true, throwMultiplier: 1.35f);
        AddDynamicBox(world, "Toss4", new Vector3(8.8f, 0.35f, 1.3f),
            new Vector3(0.5f, 0.5f, 0.5f), 0.28f, amber * 0.85f, 0.4f, 0.7f, binTarget: true, throwMultiplier: 1.35f);
        AddDynamicBox(world, "Toss5", new Vector3(9.5f, 0.35f, 2.1f),
            new Vector3(0.5f, 0.5f, 0.5f), 0.28f, amber * 0.95f, 0.4f, 0.7f, binTarget: true, throwMultiplier: 1.35f);

        // A slow kinematic platform in the back of the bay. Riding it is the
        // simplest way to prove continuous kinematic motion still carries a body.
        var platform = AddStaticBox(world, "TravelPlatform", new Vector3(0f, 0.9f, -7f),
            new Vector3(3f, 0.3f, 3f), Vector3.Zero, steel, 0.55f, 0.45f);
        platform.GetComponent<PhysicsBodyComponent>()!.BodyType = PhysicsBodyType.Kinematic;
        platform.GetComponent<PhysicsBodyComponent>()!.ContinuousKinematicMotion = true;
        var motor = new MovingPlatformComponent
        {
            Axis = Vector3.UnitZ,
            Travel = 2.4f,
            Speed = 0.8f,
            Phase = 0f,
        };
        platform.AddComponent(motor);
        _platforms.Add(motor);
        MovingPlatform = motor;
        MovingPlatformElement = platform;

        // Warm work lights. The sun is cool dusk; these carry the bay.
        AddPointLight(world, "WorkLight", new Vector3(0f, 5.2f, 0.5f),
            new Vector3(1f, 0.82f, 0.58f), 18f, 2.2f);
        AddPointLight(world, "BinLight", new Vector3(8f, 3.8f, -5f),
            new Vector3(1f, 0.55f, 0.18f), 10f, 1.4f);

        GameRoot.Log?.Information("[PhysicsPlayground] built {PropCount} grabbable props", _props.Count);
    }

    private Element AddStaticBox(
        World world, string name, Vector3 position, Vector3 size, Vector3 rotationDegrees,
        Vector3 color, float roughness, float metallic = 0f)
    {
        var element = new Element(name);
        element.SetTransform(position, rotationDegrees);
        element.AddCollider(MeshShape.Box, size.X, size.Y, size.Z);
        element.AddPhysics(PhysicsBodyType.Static);
        element.AddMesh(MeshShape.Box, size.X, size.Y, size.Z);
        AddMaterial(element, color, roughness, metallic);
        world.AddElement(element);
        return element;
    }

    private void AddDecorBox(
        World world, string name, Vector3 position, Vector3 size, Vector3 rotationDegrees,
        Vector3 color, float roughness, float metallic = 0f)
    {
        var element = new Element(name);
        element.SetTransform(position, rotationDegrees);
        element.AddMesh(MeshShape.Box, size.X, size.Y, size.Z);
        AddMaterial(element, color, roughness, metallic);
        world.AddElement(element);
    }

    private void AddDynamicBox(
        World world, string name, Vector3 position, Vector3 size, float mass, Vector3 color,
        float metallic, float roughness, bool binTarget, float throwMultiplier = 1f)
    {
        var element = new Element(name);
        element.SetTransform(position);
        element.AddCollider(MeshShape.Box, size.X, size.Y, size.Z);
        element.AddPhysics(PhysicsBodyType.Dynamic);
        element.AddComponent(new RigidBodyComponent
        {
            Mass = mass,
            LinearDamping = 0.08f,
            AngularDamping = 0.12f,
            CanSleep = true,
        });
        element.AddMesh(MeshShape.Box, size.X, size.Y, size.Z);
        element.AddComponent(new GrabbableComponent { ThrowMultiplier = throwMultiplier });
        AddMaterial(element, color, roughness, metallic);
        world.AddElement(element);
        _props.Add(new PropHome(element, position, Quaternion.Identity, binTarget));
        if (binTarget) BinTargetCount++;
    }

    private void AddDynamicSphere(
        World world, string name, Vector3 position, float radius, float mass, Vector3 color,
        float metallic, float roughness, bool binTarget, float throwMultiplier = 1f)
    {
        var element = new Element(name);
        element.SetTransform(position);
        element.AddCollider(MeshShape.Sphere, radius * 2f, radius * 2f, radius * 2f);
        element.AddPhysics(PhysicsBodyType.Dynamic);
        element.AddComponent(new RigidBodyComponent
        {
            Mass = mass,
            LinearDamping = 0.05f,
            AngularDamping = 0.08f,
            CanSleep = true,
        });
        element.AddMesh(MeshShape.Sphere, radius * 2f, radius * 2f, radius * 2f);
        element.AddComponent(new GrabbableComponent { ThrowMultiplier = throwMultiplier });
        AddMaterial(element, color, roughness, metallic);
        world.AddElement(element);
        _props.Add(new PropHome(element, position, Quaternion.Identity, binTarget));
        if (binTarget) BinTargetCount++;
    }

    private void AddDynamicCylinder(
        World world, string name, Vector3 position, float radius, float height, float mass,
        Vector3 color, float metallic, float roughness, bool binTarget, float throwMultiplier = 1f)
    {
        var element = new Element(name);
        element.SetTransform(position);
        element.AddCollider(MeshShape.Cylinder, radius * 2f, height, radius * 2f);
        element.AddPhysics(PhysicsBodyType.Dynamic);
        element.AddComponent(new RigidBodyComponent
        {
            Mass = mass,
            LinearDamping = 0.08f,
            AngularDamping = 0.15f,
            CanSleep = true,
        });
        element.AddMesh(MeshShape.Cylinder, radius * 2f, height, radius * 2f);
        element.AddComponent(new GrabbableComponent { ThrowMultiplier = throwMultiplier });
        AddMaterial(element, color, roughness, metallic);
        world.AddElement(element);
        _props.Add(new PropHome(element, position, Quaternion.Identity, binTarget));
        if (binTarget) BinTargetCount++;
    }

    private void AddBin(World world, Vector3 center, Vector3 accent)
    {
        // Four low steel walls leave the mouth open for a throw.
        const float half = 1.5f;
        const float wallHeight = 0.9f;
        AddStaticBox(world, "BinWallN", center + new Vector3(0f, wallHeight / 2f, -half),
            new Vector3(3.0f, wallHeight, 0.18f), Vector3.Zero, accent * 0.55f, 0.7f, 0.3f);
        AddStaticBox(world, "BinWallS", center + new Vector3(0f, wallHeight / 2f, half),
            new Vector3(3.0f, wallHeight, 0.18f), Vector3.Zero, accent * 0.55f, 0.7f, 0.3f);
        AddStaticBox(world, "BinWallW", center + new Vector3(-half, wallHeight / 2f, 0f),
            new Vector3(0.18f, wallHeight, 3.0f), Vector3.Zero, accent * 0.55f, 0.7f, 0.3f);
        AddStaticBox(world, "BinWallE", center + new Vector3(half, wallHeight / 2f, 0f),
            new Vector3(0.18f, wallHeight, 3.0f), Vector3.Zero, accent * 0.55f, 0.7f, 0.3f);

        AddTrigger(world, BinTriggerName, center + new Vector3(0f, 1.0f, 0f),
            new Vector3(2.7f, 1.6f, 2.7f), accent, visible: false);
    }

    private void AddTrigger(
        World world, string name, Vector3 position, Vector3 size, Vector3 color, bool visible = false)
    {
        var element = new Element(name);
        element.SetTransform(position);
        element.AddCollider(MeshShape.Box, size.X, size.Y, size.Z, isTrigger: true);
        element.AddPhysics(PhysicsBodyType.Static);

        if (visible)
        {
            element.AddMesh(MeshShape.Box, size.X, size.Y, size.Z);
            var material = new MaterialComponent(color.X, color.Y, color.Z, 0.1f, 0f, 0.6f);
            element.AddComponent(material);
        }

        world.AddElement(element);
    }

    private void AddPointLight(
        World world, string name, Vector3 position, Vector3 color, float range, float energy)
    {
        var element = new Element(name);
        element.SetTransform(position);
        element.AddComponent(new PointLightComponent(color.X, color.Y, color.Z, range, energy));
        world.AddElement(element);
    }

    private static void AddMaterial(IWorldElement element, Vector3 color, float roughness, float metallic)
    {
        element.AddComponent(new MaterialComponent(color.X, color.Y, color.Z, 1f, metallic, roughness));
    }
}
