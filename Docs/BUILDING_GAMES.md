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
