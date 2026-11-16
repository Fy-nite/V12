# Changelog

All notable engine and tooling changes. The gameplay-facing rules live in
[`BUILDING_GAMES.md`](BUILDING_GAMES.md).

## [Unreleased]

### Added

- Skeletal animation module `V12.Skeletal` (`Finite.V12.Skeletal`): swappable
  `ISkeletalAnimator`/`ISkeletalBackend` and `ISkeletalAssetImporter` interfaces,
  a neutral `SkeletonAsset`, a Prowl.Motion backend and Clay importer, manager /
  player / bone-attachment / mesh-part components, and time-based seeking
  (`player.Time = t`). See `Docs/ANIMATION.md`.
- `IMouseCapture` in `V12.Rendering`, surfaced as `GameRoot.MouseCapture`.
  NovaV2 registers a Godot implementation that owns the window mouse mode, so
  gameplay can lock the pointer for relative look and release it for menus
  without referencing a windowing API.
- `V12-Samples/SkinnedCharacter`: a runnable skinned-character sample with a
  ground plane, clip selection, pause/restart and a captured-mouse orbit camera.
- `MaterialComponent.ShaderUri` plus `RenderFrameProducer` custom-material
  binding: a registered `shader://` program compiles per surface, with texture
  slots and uniforms matched by reflection name.
- `ShaderAssetLoader` for `*.v12shader` manifests; NovaV2 scans a project's
  `shaders` folder and a pak's shader assets at startup.
- Particle emitters: `ParticleItem` in the render contract,
  `RendererFeature.Particles`, snapshot capture of `ParticleEmitterComponent`,
  `RenderFrameProducer` emission, and a Godot `GPUParticles3D` backend with
  tint, size and gravity.
- Skybox loading in NovaV2: `EnvironmentComponent.Mode`/`SkyboxPath` now upload
  an equirectangular panorama instead of always falling back to a solid colour.
- Fixed-step simulation clock with pause, time scale, max-step catch-up,
  interpolation alpha, tick counters, and `OnFixedUpdate`.
- Seeded `DeterministicRandom` source exposed as `GameRoot.Random`, with
  `SetSimulationSeed` and `ResetSimulation`.
- `World.Id` and `WorldId` on `WorldSyncDTO`, `ElementCreateDTO`,
  `ElementUpdateDTO`, `ComponentSnapshot`, and `ComponentRemovalDTO`.
- `World.FindElementById(long)` for world-scoped element lookups.
- `TransformHandle` in `V12.Basic.Building` as a view over `LocalTransform`.
- `V12-Engine/Docs/SIMULATION_TIMING.md` and
  `V12-Engine/Docs/SYNC_VALUE_GUIDE.md`.

### Changed

- `GameRoot.Update(deltaTime)` now feeds a fixed-step accumulator. Gameplay
  receives a constant delta, and one rendered frame can run zero or more fixed
  steps.
- `CameraComponent` moved from `V12.Basic.Components` to
  `V12.Components` in `V12-Engine/Components`.
- WorldML now writes `<LocalTransform>` blocks instead of TransformComponent.
- WorldML rotation is authored as three-value XYZ degrees. Legacy four-value
  quaternions are still read.
- `SyncValue<T>` and delegate members are ignored by the BSON component path
  through a serialization convention. `[BsonIgnore]` is no longer required on
  those properties.
- `WorldSyncHandler` prefers wire `WorldId` before the legacy name and global
  scan.
- `V12PakLoader` now counts disabled DLL, C#, and Contract gamepaks in
  `SkippedGamepaks`.

### Removed

- `TransformComponent` from the engine and V12.Basic. Elements own their pose
  through `IWorldElement.LocalTransform`.
- TransformComponent mirroring from `Element.LocalTransform`, renderer
  placement, SVG, lights, and physics locomotion.

### Fixed

- WorldML Euler extraction now round-trips combined pitch, yaw, and roll
  without drifting.
- WorldML serializer no longer synthesizes TransformComponent.
- Pak template tests now search recursively for expanded template children.

## Migration notes

### Transforms

Replace:

```csharp
element.AddComponent(new TransformComponent(4f, 0.5f, 0f));
```

with:

```csharp
element.SetPosition(4f, 0.5f, 0f);
```

or set `element.LocalTransform` directly. `GetOrAddTransform()` now returns a
`TransformHandle` view and does not attach a component.

### WorldML

Replace:

```xml
<Component type="TransformComponent" x="4" y="0.5" z="0" rotationY="45" />
```

with:

```xml
<LocalTransform>
  <Position>4,0.5,0</Position>
  <Rotation>0,45,0</Rotation>
</LocalTransform>
```

Old TransformComponent nodes are still read as pose data. They are not attached
as components.

### SyncValue

Remove `[BsonIgnore]` from `SyncValue<T>` properties. The BSON convention and
WorldML serializer skip them by type.

### Fixed step

Code that assumed one `GameRoot.Update(dt)` call equals one simulation tick must
be adjusted. Use `SimulationTick`, `SimulationTime`, or `FixedUpdate` / `StepOnce`
when exact tick control is required.

### Camera

Update fully qualified references:

```csharp
V12.Basic.Components.CameraComponent // old
V12.Components.CameraComponent        // new
```

## Related documents

- [`BUILDING_GAMES.md`](BUILDING_GAMES.md)
- [`../V12-Engine/Docs/SIMULATION_TIMING.md`](../V12-Engine/Docs/SIMULATION_TIMING.md)
- [`../V12-Engine/Docs/SYNC_VALUE_GUIDE.md`](../V12-Engine/Docs/SYNC_VALUE_GUIDE.md)
- [`../V12-Engine/Docs/WORLDML_BINDINGS.md`](../V12-Engine/Docs/WORLDML_BINDINGS.md)
