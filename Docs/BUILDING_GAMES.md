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
| `InterpolationAlpha` | Fraction of a step left, for render interpolation. |
| `OnFixedUpdate` | Runs after worlds and services update. |
| `Random` | Seeded gameplay random source. |
| `SetSimulationSeed(ulong)` | Reset the random sequence. |
| `ResetSimulation(ulong)` | Reset the clock and random sequence. |

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

`SyncValue<T>` is the explicit value-level sync channel. BSON automatically
ignores `ISyncValue` and delegate members, so no `[BsonIgnore]` is needed.

```csharp
[Sync]
public SyncValue<float>? HealthValue { get; set; }
```

That is enough for BSON and WorldML serialization to skip it. Use
`SyncAutoCreator.EnsureSyncValues` on attach when you want the value created
automatically.

## 7. Networking quick notes

- `DirtyTracker` handles component and element state.
- `SyncValue<T>` handles individual hot values.
- `World.Id` is a stable `Guid`.
- `World.FindElementById` scopes a lookup to one world.
- `WorldSyncDTO`, element updates, component snapshots, and removals carry
  `WorldId`.
- Use `Uid` or selector references when an element must survive renames.

## 8. Common recipes

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
```

The host resolves and uploads the panorama once per path; a failed or empty path
falls back to the solid sky colour.

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




## 9. Pitfalls

- Do not add a TransformComponent. It no longer exists.
- Do not use render delta in gameplay.
- Do not use `System.Random` for gameplay.
- Do not assume `Update(dt)` runs exactly once per rendered frame.
- Do not write quaternions in WorldML unless you are reading an old file.
- Do not add `[BsonIgnore]` to `SyncValue<T>` properties.
- Do not rely on structural changes made mid-tick being visible in the same tick.

## 10. Related docs

- [`CHANGELOG.md`](CHANGELOG.md)
- [`../V12-Engine/Docs/SIMULATION_TIMING.md`](../V12-Engine/Docs/SIMULATION_TIMING.md)
- [`../V12-Engine/Docs/SYNC_VALUE_GUIDE.md`](../V12-Engine/Docs/SYNC_VALUE_GUIDE.md)
- [`../V12-Engine/Docs/WORLDML_BINDINGS.md`](../V12-Engine/Docs/WORLDML_BINDINGS.md)
- [`FEDERATED_INSTANCES.md`](FEDERATED_INSTANCES.md)
