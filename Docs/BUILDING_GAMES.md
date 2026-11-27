# Building Games with V12: Cheatsheet

> Last updated: 2026-11-10
>
> A practical starting point for gamepacks, components, worlds, transforms,
> sync values, and deterministic gameplay. The release history lives in
> [`CHANGELOG.md`](CHANGELOG.md).

## 1. The game loop

The host calls `Update` once per rendered frame. Gameplay always receives a
fixed delta.

```csharp
gameRoot.Update((float)deltaTime);
```

Useful `GameRoot` members:

| Member | Use |
|---|---|
| `FixedDeltaTime` | Simulation step size, default `1f / 60f`. |
| `SimulationPaused` | Stop fixed steps while real time continues. |
| `TimeScale` | Slow down or speed up simulation. |
| `SimulationTick` | Fixed-step counter. |
| `SimulationTime` | Simulated time, not wall-clock time. |
| `InterpolationAlpha` | Fraction of a step left, consumed by render interpolation. |
| `RenderInterpolation` | Host opt-in: record and present interpolated poses. NovaV2 enables it. |
| `OnFixedUpdate` | Runs after worlds and services update. |
| `Random` | Seeded gameplay random source. |
| `SetSimulationSeed(ulong)` | Reset the random sequence. |
| `ResetSimulation(ulong)` | Reset the clock and random sequence. |

With `RenderInterpolation` on, capture reads `Element.RenderWorldTransform`:
position, rotation and scale are slerped between the last two fixed-step poses
using `InterpolationAlpha`. Gameplay reads `WorldTransform`, the exact simulated
pose, and `SceneQuery` still picks against it. The trade is up to one fixed step
of visual latency for smooth motion on a display faster than the simulation.
NovaV2 turns it on by default; set `NovaV2RuntimeOptions.RenderInterpolation` to
`false` to restore the old capture behaviour.

## 2. Determinism rules

Follow these in gameplay code:

1. **Use `GameRoot.Random`** for gameplay randomness. Do not use
   `System.Random`, `Guid.NewGuid()`, or wall-clock values.
2. **Do not read wall-clock time** inside simulation code. Use
   `SimulationTick` or `SimulationTime`.
3. **Treat world order as stable and meaningful.** `World.Update` walks roots
   and components in list order. Do not depend on dictionary or hash-set order.
4. **Defer structural changes during a tick when possible.** Adding or removing
   elements mid-walk changes the next tick's order.
5. **Use the fixed delta inside `Update`.** Do not substitute render delta or
   host `Time.deltaTime`.
6. **Capture replay state together.** Save `SimulationTick`, `SimulationTime`,
   and `Random.State` at the same checkpoint.
7. **Use `InterpolationAlpha` for rendering only.** Gameplay must not depend on
   frame interpolation.
8. **Seed deliberately.** Call `SetSimulationSeed` or `ResetSimulation` at
   session or replay start.

```csharp
gameRoot.SetSimulationSeed(1234);

float chance = gameRoot.Random.NextFloat();
int index = gameRoot.Random.NextInt(0, 16);

ulong checkpoint = gameRoot.Random.State;
// later
gameRoot.Random.RestoreState(checkpoint);
```

For strict lockstep, drive `FixedUpdate` or `StepOnce` from a shared tick
source rather than relying on real-time catch-up.

## 3. Elements and transforms

`IWorldElement.LocalTransform` is the only element pose. There is no
TransformComponent.

```csharp
element.LocalTransform = new TRS
{
    Position = new Vector3(4f, 0.5f, 0f),
    Rotation = Quaternion.CreateFromYawPitchRoll(
        yaw: 45f * MathF.PI / 180f,
        pitch: 0f,
        roll: 0f),
    Scale = Vector3.One
};
```

Builder shorthands:

```csharp
element.SetPosition(4f, 0.5f, 0f);
element.SetRotation(0f, 45f, 0f);
element.SetTransform(new Vector3(4f, 0.5f, 0f), new Vector3(0f, 45f, 0f));
```

Script-style handle:

```csharp
Owner.GetOrAddTransform().RY += 0.1f;   // radians
Owner.GetOrAddTransform().RotationY = 45f; // degrees
```

`TransformHandle` reads and writes `LocalTransform`. It does not create a
component and does not clamp rotation.

## 4. WorldML

Canonical transform block:

```xml
<Element name="Crate">
  <LocalTransform>
    <Position>4,0.5,0</Position>
    <Rotation>10,20,30</Rotation>
    <Scale>1,1,1</Scale>
  </LocalTransform>

  <Component type="MeshComponent" Shape="Box" Width="1" Height="1" Depth="1" />
</Element>
```

Rotation is XYZ degrees:

- X is pitch
- Y is yaw
- Z is roll

Yaw only:

```xml
<Rotation>0,45,0</Rotation>
```

Legacy files with `<TransformComponent>` still load, but the node is converted
to `LocalTransform` and is not attached. A legacy four-value quaternion is also
accepted.

## 5. Components

Lifecycle:

```text
OnAttach -> Update / OnUpdate -> OnDetach -> OnDestroy
```

Minimal component:

```csharp
public sealed class SpinComponent : ComponentBase
{
    public float Speed { get; set; } = 45f;

    public override void Update(float deltaTime)
    {
        if (Owner == null) return;
        Owner.GetOrAddTransform().RY += Speed * deltaTime * (MathF.PI / 180f);
    }
}
```

Keep per-tick work deterministic and avoid host-frame assumptions.

## 6. SyncValue

Two ways to replicate a value, and they coexist on the same component.

**`[Sync]` on a field or property** — the no-plumbing path. The binder creates one
`SyncValue<T>` per member, polls it on the peer that simulates the element, and
writes remote updates back. A plain assignment is enough:

```csharp
public sealed class DoorComponent : ComponentBase
{
    [Sync] public float OpenAmount;          // key: OpenAmount
    [Sync("state/locked")] public bool Locked;
    public float AnimationCache;             // local
}
```

**`SyncValue<T>` explicitly** — when you want to decide when a large or derived
value is sent. You own the write-back via `OnRemoteApplied`. Marked `[Sync]` too
so tooling sees it:

```csharp
[Sync]
public SyncValue<float>? HealthValue { get; set; }
```

BSON automatically ignores `ISyncValue` and delegate members, so no `[BsonIgnore]`
is needed. Use `SyncAutoCreator.EnsureSyncValues` on attach when you want the
value created automatically.

Rule of thumb: `[Sync]` for ordinary user-facing state, `SyncValue<T>` for large
or explicitly-timed values. Neither is for meshes, textures or terrain — those go
through the content/asset path. Full details in
[`../V12-Engine/Docs/SYNC_VALUE_GUIDE.md`](../V12-Engine/Docs/SYNC_VALUE_GUIDE.md).

## 7. The simulating user

Every element has exactly one **simulating user**. The host simulates everything
by default; a player becomes the simulating user of an object it holds, wears, or
otherwise drives. The peer simulating an element runs its components; every other
peer treats the element as a replica and only applies what arrives.

Full call sequence, order of operations, physics behaviour and current gaps:
[`SIMULATING_USER.md`](SIMULATING_USER.md).

This is why a grabbed object is smooth and a host prop is authoritative: only one
peer steps it, and the result is replicated.

```csharp
// Host: hand the object to a player, or pass 0 to take it back.
gameLink.AssignSimulatingUser(element, playerId);

// Client: which player am I? Set this once from your welcome message.
session.LocalPlayerId = welcome.PlayerId;

// Client: ask for a grabbed object, and give it back on release.
gameLink.RequestSimulatingUser(element);
gameLink.ReleaseSimulatingUser(element);
```

`AssignSimulatingUser` applies locally and broadcasts `EntityAuthority`, so the
new owner starts simulating and every other peer stops. A claim is granted when
the element is unowned or already yours (first claim wins); a denied claim is not
answered. `PickupSystem` and `GrabberComponent` already claim and release for you.
A client that disconnects hands its elements back to the host automatically.
The assignment is keyed by `element.Uid`, so an element without a stable uid
cannot be delegated.

A component picks where it runs with `SimulationScope`:

| Scope | Runs on |
|---|---|
| `Simulator` (default) | the peer simulating the element — host, or the owning client |
| `Replica` | peers that do **not** simulate the element (interpolation, HUD, local prediction) |
| `Everywhere` | every peer, even those that do not simulate it (validation, compute) |

`ScriptComponent` and Contract (`.ct`) mod components are `Simulator`, so the VM
runs for whoever owns the object and nowhere else.

Rules:

- A client only writes what it simulates. `SyncValue<T>` writes are relayed
  last-write-wins, but ticking is the real gate: a well-behaved client cannot
  produce a write for an object it does not simulate.
- `EntityAuthorityStore.CanWrite` is the policy check for enforcing that against
  a modifier; the host still arbitrates. It is not wired into the value relay
  yet.
- `WorldSession.SimulateRemoteWorld` (default `true`) disables the client
  tick pass for a session when you want a pure viewer.
- The host's tick path stays free when nothing is delegated: the ownership lookup
  is skipped entirely until the first assignment.
- Grab/release does not drive assignment automatically yet. A game calls
  `AssignSimulatingUser` on grab and passes 0 on release.

## 8. Networking quick notes

- `DirtyTracker` handles component and element state.
- `SyncValue<T>` handles individual hot values.
- `SyncValue<T>` writes are not host-only. `NetworkWorldLink` relays a client's
  value batch to the other peers (`RelayRemoteValueWrites`, default on) and
  conflicts resolve last-write-wins on a `(clock, writerId)` stamp. Permission is
  not enforced yet, so use it in trusted sessions until the per-entity
  `CanWrite` gate lands (`Docs/PEER_HOSTING_AND_AUTHORITY.md` §5).
- `World.Id` is a stable `Guid`.
- `World.FindElementById` scopes a lookup to one world.
- `WorldSyncDTO`, element updates, component snapshots, and removals carry
  `WorldId`.
- Use `Uid` or selector references when an element must survive renames.

## 9. Common recipes

Spawn a box:

```csharp
var box = new Element("Crate");
box.SetPosition(0f, 0.5f, 0f);
box.AddMesh(MeshShape.Box, 1f, 1f, 1f);
box.AddCollider(MeshShape.Box, 1f, 1f, 1f);
box.AddPhysics(kinematic: false);
world.AddElement(box);
```

Query by component:

```csharp
var player = root.FindElementWithComponent<PlayerComponent>();
var allLights = root.FindElementsWithComponent<PointLightComponent>();
```

Spawn a player:

```csharp
world.SpawnPlayer3D(new Vector3(0f, 1.5f, 0f));
```

### Shared desktop/XR player motor

For a player that works on desktop and in a headset, use
`V12.Basic.Player.PlayerMotorComponent` instead of the older `PlayerComponent`.
It expects a floor rig named `Player` with a separate capsule body named
`PlayerBody` (the VR-friendly split), drives the body through its
`LocomotionComponent`, and supports a single-element body as a fallback.

```xml
<Element name="Player">
  <Component type="PlayerMotorComponent" BodyElementName="PlayerBody" />
  <!-- DesktopCamera and XRRig children -->
</Element>
<Element name="PlayerBody">
  <Component type="RigidBodyComponent" />
  <Component type="LocomotionComponent" UseExternalVelocity="True" />
  <Component type="ColliderComponent" Shape="Capsule" />
  <Component type="PhysicsBodyComponent" BodyType="Dynamic" LockRotation="True" />
  <!-- capsule mesh -->
</Element>
```

Movement speed is shaped by an ADSR envelope:

| Property | Meaning |
|---|---|
| `Attack` | Seconds to ramp from rest to full input speed. |
| `Decay` | Seconds to settle from full speed to `Sustain`. |
| `Sustain` | Fraction of top speed held while input stays pressed. |
| `Release` | Seconds for the release tail to reach zero; the last direction is kept during the tail. |

The motor also handles mouse/XR look, keyboard/gamepad/XR movement, jump,
smooth XR turn with `TurnDeadzone`, room-scale head offsets, floor-rig sync and
fall reset. `ExternalVelocity` carries a moving platform; `ResetRequested` lets
a game reset its own world state when the motor resets.

`MovementEnvelope` is reusable on its own when a game owns its movement math, as
`MultiplayerDemo` does for its deterministic host/client step.

### Space and point gravity

A planet is an element with a `GravitySourceComponent`. The component publishes
`Mu` and `SurfaceRadius`; `GravityFieldSystem` collects every enabled source in
the ticking worlds and hands the field to the physics backend.

Known bodies can be built from a `PlanetProfile` instead of hand-entering the
numbers. `PlanetProfile.EcoVRWorking` is the 2,400 km, 1g target from
`EcoVR-Planet-Design-Doc.docx`; `GravitySourceComponent.FromProfile` copies it
onto an element.

```csharp
planet.AddComponent(new V12.Gravity.Components.GravitySourceComponent
{
    Mu = 3.986004418e14f,          // Earth
    SurfaceRadius = 6.371e6f,
});
```

For a pure point-gravity scene, zero the uniform field once:

```csharp
gameRoot.PhysicsBackend!.Gravity = Vector3.Zero;
```

### Statics and triggers

`PhysicsBodyType.Static` creates a real Bepu static collidable. It participates
in raycasts and contact solving, can be pose-updated, but never integrates.

A `ColliderComponent` with `IsTrigger = true` creates an overlap-only body. The
Bepu backend suppresses contact response and raises trigger events on the game
thread after each step:

```csharp
if (gameRoot.PhysicsBackend is IPhysicsTriggerEvents triggers)
{
    triggers.TriggerEnter += (trigger, other) => { /* entered */ };
    triggers.TriggerExit += (trigger, other) => { /* exited */ };
}
```

The trigger body is always the first argument. Overlap is tested against the
collider's broadphase bounds, so use a simple volume for gameplay triggers
rather than an exact contact shape.

Custom colliders carry their mesh through `PhysicsBodyDesc.Mesh`;
`PhysicsLocomotionSystem` fills it from the element's mesh component. The
backend uses the triangle mesh for collision but a box approximation for
inertia.

Vessels should be root elements with a `ColliderComponent`, a
`RigidBodyComponent` and a `PhysicsBodyComponent`. A `LocomotionComponent`
owns XZ velocity and would overwrite orbital motion; leave it off a vessel, or
set `PhysicsBodyComponent.SyncLocomotionVelocity = false` so the physics
velocity is preserved.

Use `AddForce` for thrust (applied over the step) and `AddImpulse` for an
instant change such as a jump or a decoupler kick. `BodyMass` can be changed on
staging; the backend recomputes inertia from the collider shape.

For time warp, propagate a `V12.Gravity.OrbitalState` on rails in double
precision and only run local physics for the active vessel.

A runnable NovaV2 example lives in `V12-Samples/SpaceFlight`: it builds a
scaled planet, a dynamic ship on a circular orbit, a camera, and live orbital
telemetry. Build it and launch the NovaV2 host.

`V12-Samples/UiShowcase` shows the UI side: a screen-space HUD and a world-space
canvas authored entirely in WorldML, with every widget kind, list data declared
inline (tabs, table columns/rows, dropdown items), and widgets bound to named
actions (`Action="pulse"`) that a script registers with `UiActions.Register`.
Its README lists which components still need code and which behave as
approximations in the Paper backend.

`V12-Samples/PhysicsPlayground` is the ground-physics sample: a dynamic player
capsule, static level geometry, a rotated ramp, a continuous kinematic platform,
trigger volumes, mass-varied grabbable props and a throw-scoring bin. It uses
`GrabberComponent`/`GrabbableComponent` from `V12.Basic/Grabbing` for both the
desktop ray and XR hands, and sets `LocomotionComponent.UseExternalVelocity` so
its own controller owns the velocity.

Load a pak:

```csharp
using var load = root.LoadPak("game.v12pak", new V12PakOptions
{
    LoadDlls = true,
    LoadContractScripts = true,
});
```

### Mouse capture

The host owns the pointer. Gameplay asks for it through `IMouseCapture`
(`V12.Rendering`), never through a windowing API:

```csharp
var mouse = gameRoot.MouseCapture;
mouse?.Capture();                 // lock for relative look
mouse?.Release();                 // give the cursor back for menus
bool captured = mouse?.IsCaptured == true;
```

The service is optional: a headless or test host leaves it null, so null-check
once and cache the answer. NovaV2 registers it as `IMouseCapture` and wires the
Escape key to the same toggle, so `CaptureChanged` is the single source of truth
for a pause menu. While released, accumulated mouse-look delta is discarded, so
re-capturing never snaps the view.

### Custom material shaders

`MaterialComponent.ShaderUri` compiles a registered V12 shader for that surface:

```csharp
element.AddComponent(new MaterialComponent
{
    ShaderUri = "shader://toon",
    AlbedoTexture = "textures/wood.png",
    Roughness = 0.2f,
});
```

Manifests are registered automatically from a project's `shaders` folder and
from a pak's assets. A shader that declares these uniforms gets the material
fields with no extra plumbing:

| Uniform | Source |
|---|---|
| `albedoTex`, `normalTex`, `metallicTex`, `roughnessTex`, `emissionTex`, `occlusionTex` | the matching material texture |
| `albedoColor` (also `tint`, `baseColor`) | R/G/B/A |
| `metallic`, `roughness`, `normalScale`, `alphaCutoff` | the matching field |
| `emission`, `emissionEnergy` | emission fields |
| `uvOffset`, `uvScale` | UV1 fields |

Other uniforms keep their shader defaults. A URI that is not registered logs a
warning and falls back to the built-in lit shader, so a missing manifest never
blanks a scene.

### Particles

`ParticleEmitterComponent` is simulation data for the renderer; the engine does
not step particles.

```csharp
var sparks = new Element("Sparks");
sparks.SetPosition(0f, 1f, 0f);
sparks.AddComponent(new ParticleEmitterComponent
{
    Amount = 48,
    Lifetime = 0.8f,
    SpeedMin = 1f,
    SpeedMax = 3f,
    SpreadAngle = 35f,
    Size = 0.08f,
    ColorR = 1f,
    ColorG = 0.6f,
    ColorB = 0.1f,
    Gravity = 9.81f,
});
```

`Emitting = false` stops emission without destroying the emitter; flip it back
to true to restart a one-shot burst. Backends that do not advertise
`RendererFeature.Particles` ignore the emitter.

### Skybox

Set the world's `EnvironmentComponent` to `BackgroundMode.Skybox` and point
`SkyboxPath` at an equirectangular panorama:

```csharp
environment.Mode = BackgroundMode.Skybox;
environment.SkyboxPath = "textures/sky.png";   // or v12://..., res://...
environment.SkyEnergy = 1f;                    // brighten a dim panorama
environment.SkyboxRotation = 90f;              // yaw in degrees
```

The host resolves and uploads the panorama once per path; a failed or empty path
falls back to the solid sky colour. It is a 2D image, not a mesh: a GLB skybox
(a sphere with the nebula baked on) has to be unpacked into its panorama first,
because a mesh shell only works while it is between the camera and the far clip
and drawn with both faces.

`SkyEnergy` scales the panorama (and the solid sky colour) and
`SkyboxRotation` rotates it around the world up axis, so the interesting part of
the image can be lined up with the view a game opens on. Both are captured from
the component every frame, so they are safe to animate.

### Static 3D models

Use `ModelInstance` when the file's node hierarchy and per-part materials
should survive as elements, or `MeshAsset` for one flattened mesh:

```csharp
var desk = new Element("Desk");
desk.SetPosition(3f, 0.575f, -3.6f);
desk.SetRotation(0f, -28f, 0f);
desk.AddComponent(new ScaleComponent(0.5f));
desk.AddComponent(new ModelInstance("v12://samples/Desk.glb"));
world.AddElement(desk);
```

Paths resolve through mounts, so `v12://` URIs work for gamepak and sample
content alike. See [`MODEL_LOADING.md`](MODEL_LOADING.md) for hierarchy vs
flattening, materials from the file, embedded textures, triplanar fallback and
import conventions.

### Skinned and animated characters

The `V12.Skeletal` module provides a manager (rig, clips, parts) and a player
(clip, time, speed, pause):

```csharp
var character = new Element("Character");
character.SetPosition(0f, 0f, 0f);

character.AddComponent(new SkeletalManagerComponent
{
    AssetPath = "v12://Assets/CesiumMan.glb",
});

character.AddComponent(new SkeletalPlayerComponent
{
    PlayOnLoad = "Walk",     // empty plays the manager's first clip
    LoopOnLoad = true,
});

world.AddElement(character);
```

Playback lives on the player, including time-based seeking:

```csharp
player.Play("Run", loop: true, blendTime: 0.25f);
player.Time = 0.75f;         // seek to 0.75 s
player.Speed = 0.5f;
player.Pause();
player.Resume();
```

The runtime backend (`ISkeletalAnimator`) and importer (`ISkeletalAssetImporter`)
are swappable by name; the default is Prowl.Motion + Prowl.Clay. See
[`ANIMATION.md`](ANIMATION.md) for the components, bone attachment and the full
asset model.




## 10. Pitfalls

- Do not add a TransformComponent. It no longer exists.
- Do not use render delta in gameplay.
- Do not use `System.Random` for gameplay.
- Do not assume `Update(dt)` runs exactly once per rendered frame.
- Do not write quaternions in WorldML unless you are reading an old file.
- Do not add `[BsonIgnore]` to `SyncValue<T>` properties.
- Do not rely on structural changes made mid-tick being visible in the same tick.

## 11. Related docs

- [`CHANGELOG.md`](CHANGELOG.md)
- [`../V12-Engine/Docs/SIMULATION_TIMING.md`](../V12-Engine/Docs/SIMULATION_TIMING.md)
- [`../V12-Engine/Docs/SYNC_VALUE_GUIDE.md`](../V12-Engine/Docs/SYNC_VALUE_GUIDE.md)
- [`../V12-Engine/Docs/WORLDML_BINDINGS.md`](../V12-Engine/Docs/WORLDML_BINDINGS.md)
- [`FEDERATED_INSTANCES.md`](FEDERATED_INSTANCES.md)
- [`SCENE_SWITCHING.md`](SCENE_SWITCHING.md)
