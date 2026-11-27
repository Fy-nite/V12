# Changelog

All notable engine and tooling changes. The gameplay-facing rules live in
[`BUILDING_GAMES.md`](BUILDING_GAMES.md).

## [Unreleased]

### Added

- `V12.Host` (`Finite.V12.Host`): a renderer-agnostic host that owns pak
  selection and the `GameRoot` lifecycle, so renderer backends no longer load
  games. `V12PakResolver` picks the archive in a fixed order (explicit path, dev
  inbox, `game.json` descriptor, executable-name compatibility), and `V12Host`
  loads it, starts its gamepaks, selects a world, and runs headless when no
  `IRenderer` is registered. `V12GameDescriptor` replaces name-based discovery,
  so a build is selected by data instead of by what the executable is called.
  NovaV2's shell now delegates pak resolution to it and keeps only Godot
  resource-path handling.
- `vcsdk pack`, `vcsdk build` and `vcsdk serve` write a `game.json` launch
  descriptor beside each pak (`{ "pak", "world", "engine", "xr" }`). The host
  reads it, so the executable name is no longer what selects a game, and a
  shipped build can be renamed freely. NovaV2 reads an embedded `res://game.json`
  through `FileAccess` before falling back to the archive at the package root.
- Split paks: `vcsdk pack`, `build` and `serve --split <size>` write
  `<name>.partNNN.v12pak` parts plus a `game.json` `parts` list.
  `V12PakPartStream` rolls the parts while packing, `V12PakMultiStream` joins
  them for the loader (seek included), and pak discovery never mistakes a part
  for a whole archive. A one-part build is renamed to the plain `.v12pak`, so
  nothing changes for small games. Each part's `bytes` and `sha256` are recorded
  in `game.json` (`partInfo`) as the writer streams it; `V12PakParts.Verify` and
  `V12HostOptions.VerifyParts` use them for integrity and resumable delivery.

- `V12PakLoadResult.Unload()` is the inverse of loading a pak. It closes the
  pak's world sessions (or detaches the trees when a world was never focused),
  removes its worlds from `GameRoot.Worlds`, restores the previous
  `AssetResolver`, stops and removes its gamepaks, drops its compiled component
  source keys, unloads its collectible `CSharpScriptLoadContext` generations,
  unlinks its assemblies from `V12LinkedAssemblies`/`CSharpScriptImages`, drops
  its `bin/` and native probe directories, and forgets the pak in the per-root
  loaded set. `Dispose()` keeps its documented meaning (release the reader,
  leave the content alone). A pak that ships `paks/*.dll` still cannot be
  replaced in-process because those load into the host's non-collectible
  assembly context; `Unload()` makes the content-only and `CSharpScripts` cases
  re-loadable and makes teardown testable.
- `vcsdk serve --reload` pushes each successful build to a connected Android or
  Quest device and relaunches the app, replacing the manual `adb push` /
  `am force-stop` / relaunch sequence. Options: `--device <serial>` (defaults to
  the only connected device), `--package <id>` (defaults to
  `V12_ANDROID_PACKAGE` or `package/unique_name` in `export_presets.cfg`) and
  `--inbox <dir>` (defaults to
  `/storage/emulated/0/Android/data/<pkg>/files/v12inbox`). The pak is written
  as a fixed `dev.v12pak`, so no cleanup is needed on device. NovaV2 prefers
  that inbox over the pak in its own package, which means one installed APK and
  a fresh process per build instead of an in-place pak swap. Reload failures
  never fail the watch loop.

- `Finite.V12.All`: a dependency-only metapackage covering `Finite.V12`,
  `.Basic`, `.Physics`, `.Gravity`, `.Rendering`, `.Audio`, `.Audio.OwnAudio`,
  `.Paper`, `.Editor`, `.Pak`, `.Skeletal` and `.Voxel`. It ships no assembly;
  its ProjectReferences become package dependencies. `vcsdk sync` and
  `tools/pack-purr.ps1` publish it, so a project can reference one package
  instead of listing every module. `V12.Bindings` stays separate (Roslyn) and
  arrives transitively through `Finite.V12.Pak`.
- `V12.Bindings` is now published to the Purr feed from its existing home at
  `V12.Basic/libs/V12.Bindings`. It is a first-class package without moving the
  project, so `V12.Basic`'s standalone layout and `V12.Bindings.Tests` keep
  working.
- `tools/pack-purr.ps1` packs the full module set: Physics, Gravity,
  `V12.Bindings`, Skeletal and the `Finite.V12.All` metapackage are no longer
  missing from a pack-purr-only feed.

- Value writes are now authority-gated. `SyncNetworkBridge` resolves the sending
  peer to its host-assigned player id and `SyncChannel.AcceptIncomingKey` drops a
  key when that player does not simulate the element; the host relays only the
  surviving entries. `SyncKey.TryParseElement` maps the scoped key
  (`world/{w}/element/{uid}/component/{uid}/{field}`) back to an element. This
  closes the hole where any peer could write any `SyncValue<T>` or `[Sync]` field.
  `SyncBatchBuilder.BuildBatch` gained an overload that rebuilds a batch from
  filtered serialized entries.
- Ownership epochs are enforced and replayed. `EntityAuthorityStore.Apply`
  rejects a stale or duplicate epoch, release keeps the epoch history instead of
  deleting the entry, `HasDelegatedEntities` became a counter, and a joining
  client receives the current assignment map after the snapshot.
- `SyncValue<T>` has native codecs for `byte[]`, `int[]`, `uint[]`, `float[]` and
  `double[]` (length + raw values) instead of the JSON fallback, which expanded
  them roughly tenfold. `MeshComponent`'s buffers are the first beneficiary.
- `V12-Samples/PhysicsPlayground` has optional multiplayer (`V12_MP_MODE`,
  default `off`). Host replicates the yard; a grabbed crate is delegated to the
  grabbing client, which steps it; everyone else follows. The local rig is
  `NonPersistentComponent` and a client makes it its own simulating user locally
  so the controller, motor and camera keep ticking on a replica world. Grabbable
  props got stable `Uid`s (delegation requires one).

- Wire compression. `AncientCompressor` actually compresses now: payloads of
  `MinBytesToCompress` (512 B) or more go through Brotli (quality 3, window 22)
  behind a self-describing `V12C` header carrying the codec and the original
  length. Small payloads, incompressible payloads and already-compressed payloads
  are returned as raw BSON, so a 60 Hz value batch pays no header and a second
  compression pass adds nothing. Raw BSON still decodes, so the reader is
  backward compatible with uncompressed payloads from the same build.
  The delivery decision uses the serialized size, so a large element update that
  compresses under the MTU now stays on the unreliable channel instead of
  escalating to reliable. Documented under "Compression and delivery" in
  `V12-Engine/Docs/NETWORKING_GUIDE.md`. Tuning:
  `AncientCompressor.MinBytesToCompress`, `BrotliQuality`, `BrotliWindow`, plus a
  512 MB decompression cap.
  Tests: `V12.Architecture.Tests/WireCompressionTests` covers the shrink+round
  trip, the no-header small path and the incompressible path.

- `[Sync]` field binding (`SyncFieldBinder`, `ISyncValueProvider`). Marking a
  field or property on a component now replicates it without any per-component
  plumbing: a cached per-type schema creates one `SyncValue<T>` per member
  (key = member name, or `[Sync("key")]`), polls it once per fixed tick on the
  peer that simulates the element, and writes remote updates back into the
  member. A property setter that calls `MarkDirty` no longer echoes the update
  onto the wire (`ComponentBase.IsApplyingRemote`); the render refresh still
  runs. Accessors are compiled once per member.
  Explicit `SyncValue<T>` members keep working unchanged and are discovered
  alongside the bound fields — `[Sync]` for ordinary user-facing values,
  `SyncValue<T>` when you want to control when a large value is sent.
  `SyncValue<T>` gained an opt-out from the process-wide registry so bound fields
  do not pollute it, `ISyncValue.MarkDirty` lets a session re-send everything,
  and `NetworkWorldLink` sends that full keyframe when a client joins so values
  excluded from the component snapshot (`[Sync][BsonIgnore]`, explicit values)
  still arrive.
  Tests: `V12.Architecture.Tests/SyncFieldBinderTests` covers schema discovery,
  change-only publishing, the fixed-tick hook, remote write-back with no echo
  through a property setter, and explicit values binding alongside fields.

- `tools/sync-v12.ps1`: one command that runs `vcsdk sync --dev` (which already
  auto-discovers every root-level `Finite.V12.*` module, so a new package needs
  no SDK edit) and then installs any assembly a renderer slot is missing into
  every installed renderer. Supports `-DryRun`, `-Force`, `-Renderer` and
  `-SkipSync`. The matching `tools/install-v12-packages.ps1` extracts the
  runtime `Finite.V12.*` packages from the Purr feed and fills gaps only, so a
  fresh renderer export is never downgraded. Authoring-only packages
  (`Finite.V12.Editor`, `Finite.V12.Sdk`, `V12.MCP.*`) are excluded.
- This closes the failure mode where `vcsdk build -r novav2` copied a renderer
  that was missing `V12.Voxel.dll`; the gamepak resolved but its planet
  component did not, producing the "No VoxelPlanetComponent in the scene"
  error. `tools/install-novav2-renderer.ps1` now also installs missing packages
  and runs the dependency audit with `-IncludePackages`.

- `tools/check-v12-deps.ps1`: audits an installed renderer or exported game
  folder against the real `V12.*.dll` set from a build output. It reports
  missing, stale and extra assemblies, optionally lists the newest
  `Finite.V12.*` packages, and is the generic replacement for hardcoded
  Gravity/Physics checks. `tools/install-novav2-renderer.ps1` now runs this
  audit after export, so a new engine module (first case: `V12.Voxel`) cannot
  be silently absent from `~/.purr/renderers/<id>/<rid>`.
- Documented the renderer-slot workflow in `Docs/VOXEL.md`,
  `Docs/WORKING_ON_V12.md` and the Voxel Planet sample: `vcsdk sync --dev`
  updates the NuGet feed, `vcsdk build -r <id>` copies the installed renderer,
  and only `tools/install-novav2-renderer.ps1` refreshes the slot. A missing
  `V12.Voxel.dll` there is the "No VoxelPlanetComponent in the scene" failure.

- Render interpolation for high-refresh displays. `GameRoot.RenderInterpolation`
  (off by default, on in NovaV2) makes capture read
  `Element.RenderWorldTransform`, which slerps position/rotation/scale between
  the last two fixed-step poses using the clock's `InterpolationAlpha`. The
  world snapshots every element at the start and end of a fixed step
  (`World.BeginRenderPoseSnapshot` / `EndRenderPoseSnapshot`) after physics and
  gameplay services have written their transforms. Gameplay, physics and
  `SceneQuery` keep the exact simulated `WorldTransform`. This removes 60 Hz
  motion judder on a 144/240 Hz display for ~0.05 ms of CPU; the cost is up to
  one fixed step of visual latency. `ElementPlacement.RenderLocalMatrix` and
  `RenderWorldMatrix` are the capture-only helpers.
- `V12_FPS_LOG=1` in the NovaV2 host: opt-in per-frame telemetry with average
  and max CPU/wall time plus managed allocation per frame, printed every 120
  frames. Off by default.
- `V12-Samples/VoxelPlanet`: the terrain tool now applies once per fixed tick
  (60 Hz) with a lower per-tick strength, so digging follows the mouse instead
  of stepping at 18 Hz. The aiming ray uses the presented camera pose.

- `V12.Voxel` (`Finite.V12.Voxel`): a renderer-agnostic deformable terrain
  package. A planet is a signed-distance sphere pushed around by seeded value
  noise; brushes (`Dig`, `Fill`, `Smooth`) write sparse voxel overrides, and
  `VoxelSurfaceNets` meshes only the changed chunks into ordinary
  `IMeshRenderable` geometry. Chunk grids are cached and updated in place, so a
  brush pass costs 8 chunks in ~0.6-0.9 ms, and `VoxelPlanetComponent` rebuilds
  dirty chunks nearest-first under a 4 ms per-tick budget while keeping the
  previous mesh visible. Includes `TryRaycast`, `TryUseTool`,
  `ApplyWorldBrush`, depth-based vertex-colour strata, deterministic
  cross-chunk seams, and `Docs/VOXEL.md`.
- `V12.Voxel.Tests`: headless geometry invariants for the mesher - outward
  winding, no open edges or duplicated seam triangles across chunks, brush
  carving, and deterministic re-meshing.
- `V12-Samples/VoxelPlanet`: a fly-camera voxel planet you can dig, fill and
  smooth, with a pre-carved crater, a Paper HUD and a live remesh-time readout.
- `IMeshRenderable.MeshColors` plus `RenderableSnapshot.MeshColors`: an optional
  per-vertex RGBA stream for procedural and baked geometry (terrain strata, baked
  AO). `MaterialDesc.VertexColors` carries the intent to the backend; NovaV2
  maps it to `VertexColorUseAsAlbedo`. Meshes that publish no colours keep the
  existing opaque-white stream, so nothing renders differently.
- NovaV2 host: references `V12.Voxel` alongside `V12.Physics`/`V12.Gravity` so a
  gamepak's optional package resolves from the host load context.

- `V12.Gravity` patched-conic frames: `GravitySourceComponent.SphereOfInfluence`
  and `GravitySource.SphereOfInfluence` let a gravity field pick the smallest
  containing frame instead of summing every point mass. Inside that frame only
  the local body acts; outside every frame only roots (`SOI == 0`) act.
  `GravitySources.SampleAcceleration` is shared by `GravityField` and the Bepu
  integrator, so telemetry, cameras and physics choose the same body. Fields
  that author no SOIs keep the old all-source sum.
- `V12-Samples/SpaceFlight`: rescaled the system to a big-world layout (Earth
  420,000 units, Moon 115,000, Jupiter 1,800,000) with surface gravity from the
  real planets, authored spheres of influence that nest Moon inside Earth, and
  patched-conic gravity so a kinematic primary's un-cancelled pull cannot drag
  the ship out of its local orbit. Orbit rings and the live conic tube now scale
  with the scene instead of using the old 10,000-unit constants.

- `Docs/RENDERER_CHECKLIST.md`: acceptance checklist for a custom renderer. It
  splits the requirements into the mandatory `V12.Rendering.IRenderer` contract
  (frame leases, validated handles, device epochs, resource dependencies, views,
  draw items, threading, failure policy), normal scene parity, and optional
  capability-gated features. It also covers host wiring with
  `RenderFrameProducer`, the acceptance tests to run, and the common reasons a
  backend is rejected. The checklist is linked from `Docs/README.md`,
  `RendererContract.md` and `BUILDING_A_RENDERER.md`.
- Android/mobile pak loading from the app package. `V12PakReader`,
  `V12PakLoader` (`LoadFromStream`) and `GameRoot.LoadPak` accept a stream-backed
  pak, keyed in the extraction cache by a sampled length/head/tail hash, so an
  archive embedded in `res://` is unpacked straight to the cache instead of
  being copied out first. NovaV2 adds `GodotPakStream`, discovers
  `res://<name>.v12pak` and `res://*.v12pak`, extracts mobile paks into
  `user://v12paks`, and skips pak native probing on Android/iOS because the
  platform will not load libraries from app-writable storage (those belong in
  the APK's `lib/<abi>/`). The Android export preset enables Gradle (required
  for .NET) and includes `*.v12pak`. The step-by-step setup, signing and
  troubleshooting guide is [`BUILDING_FOR_ANDROID.md`](BUILDING_FOR_ANDROID.md).
- `InputActionMap.BindDeltaAxis`: unbounded delta axes for mouse and touch
  travel. Normal axes stay clamped to `[-1, 1]` for sticks and key pairs, while
  a delta axis keeps the host's raw pixel movement. This fixes mouse look that
  was being silently capped to one pixel-equivalent per fixed tick.
- `LocomotionComponent.UseExternalVelocity`: lets a custom controller own
  `LocomotionComponent.Velocity` and keeps the legacy named-action
  `LocomotionSystem` from zeroing it. Needed by gamepaks whose host does not
  register the demo `move_*` actions.
- `V12.Basic.Player.PlayerMotorComponent` and `MovementEnvelope`: the shared
  desktop/XR player motor. It drives a dynamic capsule through
  `LocomotionComponent`, keeps a floor rig named `Player` above a separate
  `PlayerBody`, handles room-scale head offsets and rig yaw, supports desktop
  camera and XR sticks/turn, and shapes input speed with an ADSR envelope
  (`Attack`, `Decay`, `Sustain`, `Release`) for movement with weight. It exposes
  `ExternalVelocity` for carriers, `ResetRequested` for game-level resets, and
  `IsXr` / `Head` / `LeftHand` / `Body` for sample-specific HUDs.
- `V12.Basic.Grabbing`: reusable `GrabberComponent` and `GrabbableComponent`.
  The grabber supports a desktop camera ray and both XR hands, keeps held bodies
  dynamic (velocity-driven toward the hand/aim point), restores gravity on
  release, throws with hand velocity, and releases after a blocked-body leash.
  XR grab state uses the trigger/grip click edges so a held squeeze survives the
  fixed-step clearing of host-frame analog axes. The legacy `PickupSystem` yields
  to it when present.
- `V12-Samples/PhysicsPlayground`: a physics yard with static level geometry, a
  dynamic player capsule, a rotated ramp, a continuous kinematic platform,
  trigger volumes, mass-varied grabbable props and a throw-scoring bin. Runs
  flat and in XR with a desktop HUD and a world-space wrist panel for the
  headset. The floor rig and capsule body are separate elements, and the capsule
  follows the tracked head so it sits under the player rather than the
  play-space centre.
- `Docs/PEER_HOSTING_AND_AUTHORITY.md`: implementation plan for peer-hosted
  worlds and regions without a full simulation mesh. It defines per-entity
  `Host`/`Owner`/`Client`/`Shared` authority, authority epochs, owner avatars
  and held objects, gateway-first clients, a reliable structural operation log,
  and freeze/snapshot/resume host handoff to friends, homeservers, and compute
  nodes. The document maps each change onto the current `WorldSession`,
  `DirtyTracker`, `NetworkWorldLink`, `WorldSync`, and `SyncValue` code and
  sequences the work into local-first phases.
- Double-precision floating origin for worlds. `WorldOffset`, `World.Origin`,
  `World.GlobalPositionOf` and `World.RebaseOrigin` keep a local float bubble
  and a global double offset; `IFloatingOriginAware` lets components move
  cached world-space state with the bubble. `PhysicsBodyComponent` shifts its
  backend body, and `FloatingOriginComponent` re-centres the world around an
  anchor every 2,000 units. `V12-Samples/SpaceFlight` puts one on the ship, so
  the renderer and BEPU see local coordinates while telemetry and MCP report
  the continuous global position. `SceneIntrospection` exposes the world
  `origin` and per-element `globalPosition`; MCP `get_element`/`find_elements`
  show both. Saves and network sync do not carry the origin yet, and real-scale
  surfaces still need camera-relative terrain chunks.
- Live scene introspection (`V12.Core.Introspection`). `GameRoot.Introspection`
  exposes `CaptureSnapshot()` (a flat, parent-linked read of every loaded world
  and its component properties) and an `Enqueue`/`DrainCommands` queue that
  applies scene edits at the tick boundary. Mutations route through
  `World.AddElement`, `IWorldElement.AddComponent` and `LocalTransform`, so
  `DirtyTracker` and replication still see them. Writes to a
  `WorldAuthority.Remote` world are rejected. Commands are idempotent by
  `RequestId`, carry `Actor` for attribution, return the previous value or
  transform for undo, and are kept in a bounded journal
  (`RecentJournal`) that doubles as a replay script on the fixed-step clock.
  Synthetic input (`SceneOp.SendInput`) is tagged `Meta["source"]="introspection"`.
  `WorldMLSerializer.IsExcludedProperty` is now public so live reads use the
  same "authoritative state" definition as saves (mesh/material payloads are
  never serialized into a snapshot).
  Commands drain at the start of `GameRoot.FixedUpdate`, and from `GameRoot.Update`
  while the simulation is paused, so a paused clock does not block editing.
- `SceneIntrospection.RequestSnapshotAsync` answers snapshots on the tick thread,
  so any number of threads can read and write at once without touching live
  components. `CaptureSnapshot` stays the tick-thread fast path.
  `ElementSnapshot.Depth` was added so reads can be depth-limited.
- The module system is a first-class engine feature (see `Docs/MODULES.md`):
  `IV12Module` plus `ModuleContext` (root, command line, assembly/module paths,
  per-module data directory under local app data, logger), dependency ordering
  with missing-dependency and cycle rejection, per-module `ModuleInfo`
  diagnostics and `GameRoot.Modules.Describe()`, and one host entry point,
  `GameRoot.Modules.EnableFromArgs(args, baseDir)`, handling `--modules [dir]`,
  `--module <dll>` and `--no-modules`. Assemblies load through
  `GamepackLoader.AssemblyLoader` so they share the engine's load context, are
  skipped when already present by name (a modules folder may also hold
  dependencies), and dispose in reverse start order from `GameRoot.Shutdown`.
  Nothing loads by default. Modules are the trusted-code half of mods; content
  and components stay in game paks. Module state is exposed at
  `GET /v1/modules` and the `list_modules` MCP tool.
- Mod paks: a `.v12pak` with `module.json` at the root is a mod. `IModHost` and
  `PakModHost` (in `V12.Pak`) validate `apiVersion` and dependencies, load the
  pak through the normal pipeline (worlds, assets, scripts, DLLs), start any
  gamepaks it contributed, and feed its assemblies to `ModuleHost` so
  `IV12Module` code starts as well. `--mods`, `--mod <pak>`, `--disable-mod <id>`
  and `--no-mods`; disabled ids persist in
  `%LOCALAPPDATA%/V12/<exe>/mods.json`. `vcsdk pack` stages `module.json`
  automatically, so one command builds content paks and mods. Mod state is
  exposed at `GET /v1/mods` and the `list_mods` MCP tool. See
  `Docs/MODULES.md`.
- `V12.MCP.Probe`, a loadable probe module for the introspection surface:
  loopback HTTP with `GET /v1/health|worlds|snapshot|journal|receipts/{id}` and
  `POST /v1/commands`, bearer-token auth, `--mcp-readonly`, ephemeral ports, and
  a per-process handshake file at `%TEMP%/v12-mcp/probe-<pid>.json`. It uses a
  `TcpListener`-based HTTP/1.1 server instead of `HttpListener` (needs a URL ACL
  on Windows when non-elevated) or Kestrel (needs the ASP.NET shared framework
  a Godot game does not ship). NovaV2 loads it with `--mcp` from `modules/`, or
  any module with `--module <path>`. The probe also serves `GET /v1/element/{id}`
  and an `active` filter on `/v1/snapshot`.
- `V12.MCP`, a stdio MCP server over the probe. Read tools: `probe_status`,
  `list_worlds`, `get_tree`, `get_element`, `find_elements`, `get_journal`,
  `get_receipt`. Write tools: `set_transform`, `set_active`, `set_property`,
  `spawn_element`, `despawn_element`, `attach_component`, `detach_component`,
  `send_input`, `set_simulation`. It never references `V12-Engine`; it is a JSON
  adapter configured with `V12_MCP_URL` and `V12_MCP_TOKEN`, so engine DTO
  changes do not force a tool rebuild. See `Docs/MCP.md`.
- `IPhysicsTriggerEvents` and real Bepu statics. `PhysicsBodyType.Static` now
  creates a `Simulation.Statics` collidable instead of an immovable kinematic
  body, and trigger colliders suppress contact response and raise
  `TriggerEnter`/`TriggerExit` from the game thread after a step. Overlap is
  tested against broadphase bounds, so keep trigger volumes simple.
- `PhysicsBodyDesc.Mesh` carries the triangle payload for
  `MeshShape.Custom` colliders. `PhysicsLocomotionSystem` supplies the element's
  `MeshComponent` (or another mesh renderable), so a custom collider is no
  longer silently replaced by a box. Custom-mesh inertia stays a box
  approximation.
- NovaV2 hot reload for `--game` project folders. `--hot-reload` compiles the
  project's C# sources into a collectible assembly before the first world parse,
  watches scripts and the WorldML file, swaps the live gamepak generation, and
  replaces the world tree on save. Old components receive `OnDetach`, local-only
  rigs are recreated, and the XR service cache is invalidated.
  Added `ComponentTypeResolver`, `WorldContentSwap`,
  `IV12GamepackLifecycle`, `GamepackLoader.StopAll`/`RemoveFromAssembly`,
  collectible script unload/link cleanup, and the `run-*.ps1 -HotReload` flow.
  See `Docs/HOT_RELOAD_NOVAV2.md`.
- NovaV2 `--game` now takes precedence over the pak resolved beside the
  executable. Previously an exported build (`MyGame.exe` + `MyGame.v12pak`) that
  was launched with `--game` loaded the pak afterwards and selected its world,
  so `--hot-reload` watched and reloaded a world that was not being rendered.
- WorldML list authoring: `List<string>` properties accept one attribute
  (`Tabs="Controls,Data,About"`) or a text block, and object lists accept child
  items whose attributes map to the item's properties, including nested lists:
  `<Columns><Item Header="Signal" Flex="2" /></Columns>` and
  `<Rows><Item><Cells><Item>x</Item></Cells></Item></Rows>`. Tabs, table,
  dropdown and tree data can now be declared in the world file instead of a
  controller.
- Named UI actions: `ButtonComponent`, `ToggleComponent`, `CheckboxComponent`,
  `SliderComponent`, `TabsComponent`, `TableComponent` and
  `DropdownComponent` take an `Action` string, resolved through
  `UiActions.Register(name, handler)` (`V12.Core.UI`). A component's own
  delegate still wins, so code can override authored content.
- `V12-Samples/UiShowcase`: a NovaV2 sample that authors every UI widget in
  WorldML (HUD canvas with labels, buttons, toggle, checkbox, slider, progress
  bar, text input, tabs, table, dropdown, separators, spinner, cards, and an
  Origami property grid) plus a world-space canvas placed in the scene. A small
  controller fills list widgets and wires callbacks. See
  `V12-Samples/UiShowcase/README.md`.
- `V12.Physics` (`Finite.V12.Physics`): the Bepu 2 backend moved out of
  `V12.Basic`, plus shape-derived inertia helpers, body force/torque
  extensions, and a fixed-step `PhysicsService` that uses the engine's supplied
  delta instead of a wall clock. `V12-Engine` no longer references Bepu.
- `V12.Gravity` (`Finite.V12.Gravity`): point-gravity fields
  (`GravityField`, `GravitySource`), a `GravitySourceComponent` and
  `GravityFieldSystem`, and double-precision `OrbitalState` /
  `OrbitalMechanics` (circular and escape speed, apsis, period, and
  universal-variable two-body propagation).
- `IGravityField` and `IGravityFieldBackend` physics contracts, so a backend
  can apply a point field alongside uniform gravity. `Bepu` implements the
  capability; hosts that do not support it simply leave the optional interface
  unimplemented.
- `IPhysicsBody.Mass`, `IPhysicsBody.GravityScale`, `AddImpulse` and
  `AddAngularImpulse`, with default implementations so existing backends keep
  compiling. `PhysicsBodyComponent` exposes `AddImpulse`,
  `AddAngularImpulse` and `BodyMass`.
- `V12-Samples/SpaceFlight`: a NovaV2 gamepak that shows a dynamic ship in a
  local orbit around Earth inside a compact solar system. It builds its scene
  in the gamepak, copies itself into `NovaV2.Host/gamepaks`, and prints live
  orbital elements from `V12.Gravity.OrbitalState`. Controls: `[`/`]` change
  the simulation time scale (0.25x to 8x), `B`/`N` apply prograde/retrograde
  impulses, `C` toggles orbit/chase camera, `T` cycles the attitude mode,
  `WASD` rotates the ship, `Space` is a held nose thrust, the arrow keys pan
  and tilt the chase camera around the ship, `Q`/`E` zoom, and `R` resets the
  ship to a fresh circular orbit around Earth's current position.
- SpaceFlight audio: `Assets/spaceflight.mp3` autoplays as a quiet looped music
  source on the `Music` bus at volume 0.12, and
  `Assets/benvibrant-thruster-loop-562715.mp3` loops while `Space` is held. Both
  use `AudioSourceComponent` with `SpatialBlend = 0` so the far chase/orbit
  cameras still hear them; `SpaceFlightInputComponent.ThrustAudio` starts and
  stops the thruster on the thrust edge.
- `OrbitPathMesh` in the sample: a live predicted orbit tube generated from
  `OrbitalState`, so a burn bends the visible path into the new ellipse. It
  selects the local `GravitySourceComponent` by sphere-of-influence containment
  and builds the conic around that body's position and `Mu`, subtracting the
  body's own track velocity. The tube radius is constant now; scaling it with
  semi-major axis made the line visibly grow after every burn. It hides when
  the state becomes unbound.
- `ProgradeAttitudeComponent` in the sample: a kinematic flight-assist attitude
  hold with Prograde, Retrograde, TidalLock and Free modes (`T` cycles). The
  ship now turns to follow the orbit instead of staying inertially fixed. This
  is a flight assist, not an RCS torque simulation. `ManualRotate` lets `WASD` drop
  the hold into `Free` and turn the hull directly, and `ThrustPlumeComponent`
  scales the exhaust cone while `Space` applies a held nose impulse.
- `NavballComponent` and `NavballCurveMesh` in the sample: a ship-relative
  navball fixed in the lower-right corner. The bone cross is the orbital
  horizon and vertical, projected as screen-space ribbons rather than a
  physical globe; cyan/red rings mark prograde/retrograde. Because the centre
  is the nose, the camera can orbit freely without moving the horizon. Marker
  containers hide by moving behind the camera and clearing every child's
  `Active` flag, since parent-only visibility does not stop child renderers.
- `OrbitCameraComponent` now has `Orbit` and `Follow` modes. Follow captures a
  base direction when the mode is entered, converts it to a world yaw/pitch,
  and then uses a plain spherical orbit around the ship. The camera always
  looks at the ship; Up/Down tilt under/over it, Left/Right orbit around it,
  and `Q`/`E` zoom. Neither the ship's changing velocity nor the gravity field
  rotates the camera. The planet-orbit view targets the locally dominant body
  and scales its radius/height from that body's surface radius, so it frames
  Mars or Luna instead of always framing Earth.
- Camera pan/tilt, zoom, cinematic planet-orbit rotation and manual `WASD`
  rotation now consume real frame time. The engine runs several fixed steps per
  rendered frame under time warp, so applying input every fixed step made those
  controls and the camera speed up with the warp level. `Space` thrust still
  uses simulation time because it is a physical force.
- `FixedStepClock.MaxStepsPerFrame` is held at 8 in the sample. Raising the
  budget with the warp level (`scale * 2`) made 128x run hundreds of physics
  steps in one frame and tank the perceived FPS.
- The sample ship now loads `Assets/rocket.glb` through `ModelInstance`, with
  the GLB's Y-up hull rotated onto local -Z and scaled to the collider. The
  gamepak build copies `Assets` beside the assembly and
  `SpaceFlightGamepack.Initialize` registers a `V12AssetResolver` for the
  folder-loaded dev host, so models and the skybox resolve in both dev and
  packaged runs.
- The sample is now a compact solar system: a textured Sun at the origin, then
  Mercury, Venus, Earth, Mars, Jupiter, Saturn, Uranus and Neptune in order.
  Every planet starts on the +X line and orbits the Sun with its own angular
  rate; the orbit rings make the order visible. Earth carries a 1,500-unit Moon
  orbit. `PlanetOrbitComponent` now supports a moving centre element, so the
  Moon follows Earth while Earth follows the Sun, and exposes its analytic
  `LinearVelocity` for relative orbits.
- `GravityBodySet` now uses the smallest containing sphere of influence, with
  a strongest-root fallback outside every SOI. The orbit path, telemetry,
  navball and cinematic camera all lock to that body. The Moon, Mars and the
  outer planets therefore take over as soon as the ship enters their SOI
  instead of Earth winning from a distance. Planets keep solid sphere colliders
  and the ship is a normal dynamic body; there is no scripted crash, impact
  skin, kinematic handoff or re-parking component. The ship mass is derived
  from the collider volume and a hull density (about 12.2 t for the 28x26x42
  hull) instead of the symbolic 12 kg, so contact responses have real inertia.
  `PlanetRingMesh` is a flat annulus for Saturn's rings. Textures live under
  `Assets/` and resolve through `v12://Assets/...`.
- `SpaceFlightInputComponent.ResetOrbit` now resets relative to Earth's current
  solar position and velocity, so `R` still produces a local Earth orbit after
  Earth has moved around the Sun.
- `V12.Gravity.PlanetProfile`: derived radius/gravity/mu profile with
  `EcoVRWorking` (2,400 km, 1g), Earth and Moon presets, circular/escape speed,
  orbital period and geostationary radius. `GravitySourceComponent.FromProfile`
  copies a profile onto a planet element.
- `OrbitalMechanics.SphereOfInfluence` for patched-conic transitions, using the
  standard `a * (m / M)^(2/5)` approximation.
- `tools/install-novav2-renderer.ps1`: exports `NovaV2.Host` into
  `~/.purr/renderers/novav2/<rid>/`, writes `renderer.json` when missing, and
  checks that `V12.Gravity.dll`/`V12.Physics.dll` made it into the export.
  `vcsdk build V12-Samples/SpaceFlight -r novav2` now produces a runnable
  `SpaceFlight.exe` + `SpaceFlight.v12pak` folder.
- `scripts/` PowerShell launch wrappers: `run-server`/`run-host`, `run-client`,
  `run-pair`, and `run-app` for sample exes, `.v12pak` files, project folders,
  and release app presets. They set the multiplayer environment for each
  child process, write logs under `logs/`, warn when an exported exe or pak is
  older than the source, and support `-UseGame` for running the current Godot
  host instead of a stale export. Desktop launches add both
  `--xr-mode off` and `--xr 0`.
- `Docs/WORLD_SESSIONS_AND_PERMISSIONS.md`: design proposal for multi-world
  sessions, focus switching, hidden-world simulation policies, per-world
  connections, fine-grained permissions (`Read`/`Write`/`Execute`/`Grab`/
  `Move`/`Rotate` plus extensions), temporary draft worlds and presentation
  orbs, and integration with the VNode and federation plan.
- `WorldSession` and `WorldManager`: multi-world session state derived from
  stored inputs (`Authority`, `HiddenPolicy`, `IsFocused`, `Pinned`,
  `PlayersConnected`), `FocusWorld`/`SelectWorld`, `ActiveWorlds` as the
  simulated set, `VisibleWorlds`, `WorldSessions`, hidden-world simulation
  rules, hosted-world player override, close semantics with confirmation and
  handoff/move callbacks, and world-scoped `FindElement`/`FindElementByUid`.
  The persistent world now has a session and `SelectWorld` is a compatibility
  alias for `FocusWorld`.
- Per-session replication primitives: `SyncChannel` with a private
  `SyncRegistry`, world/element/component-scoped `SyncValue` keys,
  `SyncNetworkBridge` overloads for a channel, `DirtyTracker.Sync`, and
  `WorldSession`-owned `NetworkCables`, `SyncChannel`, and `DirtyTracker`.
  `SyncRegistry` is now an instance type with a `Default` compatibility
  registry, and `SyncManager` is a facade over the process-wide default
  channel. Session channels are pumped from `GameRoot.FixedUpdate` only when a
  sender is attached.
- `NetworkWorldLink`: one network connection per `WorldSession`, host/client
  factories, world snapshot on connect, and application of `WorldSync`,
  `WorldElementUpdate`, `WorldDelta`, component batches, and component
  removals. A host tracks `PlayersConnected`; a client runs with
  `WorldAuthority.Remote` so it applies state without simulating locally.
  Added `HostPlayerId`, `ClientJoined`/`ClientLeft`, `CommandReceived`,
  `ServerMessage`, `SendToServer`, and `SendToClient` for game-level commands
  and targeted host-to-client messages. `NetworkHost.SendToClient` routes a
  message to one server-assigned player.
- `V12-Samples/MultiplayerDemo`: a vcsdk-packable gamepak with a host/client
  bootstrap, a host-authoritative shared cube, replicated player avatars, and
  one desktop/XR player controller. The player rig is local-only and is
  preserved across host snapshots; the shared cube streams through a scoped
  `SyncValue`; client input and XR head/hand poses travel to the host as
  commands, and the host replicates each avatar body, head, and hands as world
  elements.
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
- Explicit render views: `RenderViewComponent` (world, camera, layer mask,
  clear mode, priority, optional target size), `FrameSnapshot.Views` with
  `RenderViewSnapshot`, one render target per view created by
  `RenderFrameProducer`, rect-aspect projections, and `PaperTextureRef` so a
  Paper viewport surface samples a render target without a CPU readback.
  NovaV2 publishes view id, rect, world and layer metadata through
  `IViewportRegistry`.
- `RenderFilterComponent` for per-element layer masks and an optional
  single-view restriction (nested 3D UI); NovaV2 isolates those with reserved
  view layer bits.
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
- World-space Paper UI: `ProwlPaperRenderer.WorldCanvasRendererFactory` now
  creates a live `PaperWorldCanvasSystem` (previously a `null` shim). Each root
  canvas with `CanvasComponent.ScreenSpace = false` is laid out into its own
  Paper surface sized in content pixels, stepped every frame, and exposed as
  `WorldCanvases.Surfaces` for hosts to bake onto world quads. World-canvas
  widgets are excluded from the screen overlay when the system is active, and
  `Surface.FeedPointer` routes canvas-local clicks to widget handlers.
  MonoGame/Pulsar is the reference host (bake, quad draw, raycast picking);
  covered by `PaperWorldCanvasTests`.
- `Docs/RENDERER_AGNOSTIC_UI.md`: proposal that moves both screen-space and
  world-space UI entirely into the engine as ordinary meshes and draw items, so
  a renderer only draws triangles, allocates textures/targets when told, and
  feeds input. World canvases become canvas-local meshes placed by
  `DrawItem.World`, with engine-side raycast picking and focus, so layout,
  clicks and keyboard routing also work headless in tests. Phased plan covers
  presentation-surface UI views, mesh world canvases, engine picking/focus,
  legacy backend removal, boundary cleanup, and an optional plain-mesh tier for
  renderers without custom shader support.
- `EnvironmentComponent.SkyEnergy` and `EnvironmentComponent.SkyboxRotation`,
  both honoured by NovaV2. `SkyEnergy` scales a skybox panorama (and the solid
  sky colour) so a dim image can be brought up without editing it, and
  `SkyboxRotation` yaws the panorama in degrees. Previously the contract carried
  a sky energy and rotation that nothing could set, and the host hardcoded
  `Energy = 1`.
- `V12-Samples/SpaceFlight` draws its nebula as an environment skybox
  (`BackgroundMode.Skybox` + `v12://Assets/skybox_space.jpg`) instead of a
  75,000-unit mesh shell, and the equirectangular panorama is extracted from the
  original GLB into `Assets/`, with the model kept in `SourceAssets/` outside
  the pak.

- Per-element simulating user (`EntityAuthorityStore`, `WorldSession.Authorities`).
  Every element has exactly one peer simulating it: the host by default, or the
  player it is delegated to. `NetworkWorldLink.AssignSimulatingUser(element,
  playerId)` applies the assignment and broadcasts `MessageType.EntityAuthority`;
  the new owner starts simulating and every other peer stops. `World.Update` now
  takes the session's ownership map and ticks a component only where its element
  is simulated, so `ScriptComponent`, `ContractComponent` and any ordinary
  component run on the peer that owns the object — the "simulating user" model,
  not a client-side copy of host simulation. A remote world still stays out of
  `ActiveWorlds`, so host physics, audio and capture do not start on a client.
  `ComponentSimulationScope` gained `Simulator` (default, tick wherever the
  element is simulated), `Replica` (tick only where the element is a replica),
  and `Everywhere` (validation/compute: tick even when this peer does not
  simulate the element). `WorldSession.LocalPlayerId` tells a client which
  player it is; a game sets it from its welcome message.
  `ScriptSystem` now also collects scripts from replica sessions, so hot reload
  and `on_action` reach a client-side script.
  Still missing, deliberately: value writes are not gated by
  `EntityAuthorityStore.CanWrite` (poses are), there is no epoch fencing or
  authority snapshot yet, and a hung-but-connected owner keeps its elements. See
  `Docs/SIMULATING_USER.md`.
- Clients can claim an element instead of waiting for the host to notice a grab.
  `NetworkWorldLink.RequestSimulatingUser` / `ReleaseSimulatingUser` send
  `MessageType.EntityAuthorityRequest`; the host grants a claim when the element
  is unowned or already the requester's (first claim wins), and ignores the rest.
- Disconnects reclaim ownership. `NetworkClient.Disconnect` sends a disconnect
  instead of closing silently, so the host no longer waits out its 15 s timeout.
  `NetworkWorldLink` then calls `EntityAuthorityStore.ReleaseAllFor(playerId)`,
  broadcasts an `EntityAuthority` per released element before raising
  `ClientLeft`, and the host resumes simulating them on the next tick.
  `PickupSystem` and `GrabberComponent` now claim on grab, re-resolve their
  physics body after the grant rebuilds it, and release on drop, replacing the
  old `ClientHeld` + `[Remote] Grab/GrabMove/Release` path (still present in
  `PhysicsBodyComponent` for compatibility, no longer used).
- Simulating-user physics. `PhysicsLocomotionSystem` now walks world sessions
  instead of `ActiveWorlds`, so a client builds bodies for a replicated world: a
  dynamic body for the elements it simulates and a kinematic follower driven by
  the element pose for everything else. It destroys and recreates the body when
  the role flips (grab/release), so a follower never keeps the dynamic body's
  inertia. A client's pose for an element it simulates now reaches the host and
  the other peers: `DirtyTracker.CanSendElement` (set from
  `WorldSession.IsSimulatedLocally`) sends only owned elements on a client, and
  the host filters `WorldElementUpdate` with `EntityAuthorityStore.CanWrite`,
  applies it, and relays it with the origin preserved so the sender is not
  echoed. Only pose fields are trusted from a client; name, description, parent
  and uid stay host-authoritative. Tests: client-owned pose reaches the host and
  a second client; an unauthorised client pose never reaches the host.

### Changed

- `WorldSession.SimulateReplicaComponents` is now `SimulateRemoteWorld`, because
  it gates the whole client-side tick pass for a remote world (owned elements and
  replica-only components), not just components.

- `SyncValue<T>` versions are a last-write-wins stamp instead of a bare counter:
  `(hybridClock << 16) | writerId`. The wire size is unchanged (one `uint64`),
  but comparison now orders by `(clock, writer)` where the clock is UTC
  milliseconds bumped to stay monotonic per value. This is what lets two peers
  write the same value and still converge; the old scheme made any two active
  writers reject each other forever. `SyncChannel.WriterId` supplies the 16-bit
  identity, the host uses its server-assigned player id, and `TryApplyRemote`
  remembers the exact accepted stamp so a duplicate cannot re-apply.
  `SyncValue<T>.PackVersion` / `ClockOf` / `WriterOf` expose the layout.
- `NetworkWorldLink` forwards a client's `SyncBatch` to every other peer by
  default (new `RelayRemoteValueWrites`, default `true`). A client write now
  reaches the host and the rest of the session without a round trip; conflicts
  resolve on the version stamp. The host still applies the batch through
  `SyncNetworkBridge` and `NetworkHost.BroadcastMessage` still skips the origin.
  This is the transport half of `docs/PEER_HOSTING_AND_AUTHORITY.md` §5: it does
  not yet ask whether the sender may write that value, so treat it as
  trusted-session behaviour until the `CanWrite` gate lands.

- NovaV2 XR now publishes `xr_<side>_trigger` and `xr_<side>_grip` every frame,
  not only when the analog value changes. `InputActionMap` clears host-frame name
  axes at the end of each fixed step, so a change-only trigger would look held
  for one tick and released immediately after.
- Inactive UI elements are now skipped by `UIFrame` capture and world-canvas
  placement. This lets a host swap a screen-space HUD for a wrist/world canvas
  by toggling `Active` instead of destroying the UI tree, and makes inactive
  hand/wrist panels disappear with their parent.
- Paper's spatial shaders no longer declare `world_vertex_coords`. Brush,
  scissor and text sampling are authored in canvas pixel space, so a world
  canvas was being sampled in metres and looked affinely warped when rotated.
- Paper now has dedicated world-canvas shaders (`shader://v12.paper/world`,
  `world-text`) and an opt-in flat HUD variant (`shader://v12.paper/world-flat`)
  selected with `CanvasComponent.WorldShader = "flat"`. The flat variant keeps
  the font SDF but skips pixel-space brush and scissor masks, which is the right
  fit for wrist panels and small world HUDs viewed at steep angles.
- `WorldCanvasSnapshot.Create` normalizes the transform's row-vector axes
  instead of its columns. A scaled and rotated world canvas was being sheared by
  the old column normalization, which is what produced the corner-warp look on
  the wrist panel. `V12.Architecture.Tests/WorldCanvasTransformTests` guards the
  orthonormal basis.
- `V12-Samples/PhysicsPlayground` rotates the tracked room-scale walking delta
  by the rig yaw before applying it to the body. Physical forward now follows the
  direction the rig is facing after a turn, instead of staying on the old world
  axis.
- `V12-Samples/PhysicsPlayground` folds the moving platform's velocity into the
  player's locomotion target while standing on it. The dynamic capsule is carried
  at platform speed instead of being overwritten to zero each tick, and the
  carry is no longer compounded into a launch velocity.
- `V12-Samples/HelloXR` uses the XR trigger click edge for grab held state, so
  the box stays in hand instead of grabbing and releasing on the first fixed
  tick. The analog trigger remains the threshold fallback.
- `PlayerMotorComponent` XR smooth turn now uses a 0.22 right-stick deadzone and
  scales the remaining travel, so controller rest noise cannot yaw the rig on
  its own.
- `V12-Samples/MultiplayerDemo` uses `V12.Basic.Player.MovementEnvelope` for its
  deterministic movement step. The client samples the envelope per fixed step,
  carries it in `PlayerInputCommand.Envelope`, stores it in pending inputs for
  replay, and the host applies the same value in `ComputeStep`. XR applies the
  envelope to the local rig movement.
- NovaV2 re-applies the player transform to the OpenXR origin after the V12 tick
  and before rendering. The hand meshes and the headset camera now share one
  player pose for the frame instead of the hands leading the view by one tick.
- `V12-Samples/MultiplayerDemo`: desktop clients now predict their own movement
  locally and reconcile against host state instead of waiting for the input round
  trip. `PlayerInputCommand` carries a sequence and fixed-step delta, the host
  applies one deterministic step per command to the client's avatar, and
  `PlayerStateAck` echoes the last processed sequence plus the authoritative
  transform. The client drops acknowledged inputs and replays pending ones from
  the host transform. XR keeps its client-owned rig pose path.
- `vcsdk build -r <id>` prefers an installed renderer whose folder name matches
  the requested id over a legacy install whose `renderer.json` merely claims
  that id. `~/.purr/renderers/nova` still declares `id: novav2` and sorted ahead
  of the fresh `novav2` export, so packages silently shipped the stale host.
- `tools/install-novav2-renderer.ps1` finishes when Godot returns a nonzero
  exit code but still produced the host executable. Godot reports warnings as
  exit 1 after a complete export, which previously aborted the script before
  it wrote `renderer.json`.
- `NovaV2InputHandler` keeps publishing relative mouse look while the pointer
  is captured even if a screen-space UI still reports pointer or keyboard
  capture. A hidden cursor cannot own a hover, so the stale flags were dropping
  look frames. Menus release capture before they use the pointer.
- `V12-Samples/SpaceFlight` camera and attitude controls: entering the chase
  camera captures the pointer (either `C` or a mouse button from the cinematic
  orbit view) so mouse look works immediately, and returning to the orbit view
  releases it. The mouse wheel zooms the chase camera or the cinematic orbit
  view, and `Q`/`E` now roll the hull instead of zooming. Mouse look reads the
  host's raw relative travel directly instead of through an `InputActionMap`
  axis, whose [-1, 1] clamp collapsed a real sweep into a single tiny step;
  `SpaceFlightInputComponent` now registers as an `IInputHandler` for that.
  Pitch follows the screen direction (mouse up looks up).
  The component logs whether capture is available and when it is captured or
  released, which makes a host without `IMouseCapture` obvious.
  `ProgradeAttitudeComponent.ManualRotate` takes a roll input, and
  `OrbitCameraComponent` gains `LookFollow`, `ZoomFollow` and `ZoomOrbit`. The minor planets are scaled 4x through
  `MinorPlanetScale` so they read as worlds instead of dots, and planet
  materials default to lit so the Sun stays the only unlit body.
- `GravityFieldSystem` caches gravity source discovery. The full tree walk now
  runs only on world structure changes or a slow 0.25 s rescan; every fixed tick
  reads positions from the cached `GravitySourceComponent`s instead of walking
  every element in every ticking world.
- Rewrite `COMMERCIAL-EXCEPTION.md` as a complete licensing document at version
  1.0. It now defines the terms it relies on (`Covered Work`, `Studio`, `Net
  Revenue`, `Calendar Year`), states the relationship to the AGPL instead of
  implying a restriction the license does not allow, and separates the two
  products the exception actually offers: free use under the AGPL below the
  revenue threshold, and a closed-source grant from the Indie tier upwards.
  Adds the four-tier fee table (Free under $1,000, Indie $200, Studio $500,
  Enterprise 1% with a $2,000 minimum), per-studio aggregation across all
  titles with an anti-splitting rule, a net-of-fees-and-taxes revenue basis,
  pro-rata billing with a 90-day grace period when a studio crosses a band,
  the two-consecutive-year rule for returning to the Free tier, annual
  declaration and audit terms, termination and cure, an explicit out-of-scope
  section naming `Contract` and `ObjektRT` as MIT, a contribution relicensing
  clause, and a FAQ. Syncs the `readme.md` license section to the same table.
- Add a root `CONTRIBUTING.md`. It carries the contribution license grant from
  Section 14 of the commercial exception, the submodule workflow, the build and
  architecture-test commands, the changelog rules, and the commit message style.
  Without that grant Finite could not relicense contributed code, which would
  make the paid tiers impossible to sell.
- Add the short commercial exception notice to `V12-Engine`, `V12.Basic`, and
  `V12.Monogame`. Those three ship an AGPL `LICENCE` but had no copy of the
  exception, so a clone of one of them on its own had no path to a paid license.
- Kinematic bodies can now be driven as continuous motion.
  `PhysicsBodyComponent.ContinuousKinematicMotion` makes
  `PhysicsLocomotionSystem` publish `delta / deltaTime` as the body's velocity
  and let the backend integrate the pose, so contacts solve against the surface
  speed a rider actually sees. `IPhysicsBackend` gained a
  `MoveKinematic(body, delta, deltaTime)` overload for that path (defaulted to
  the old two-argument call so existing backends keep compiling); Bepu sets the
  velocity and does not write the pose, because Bepu always integrates kinematic
  velocities. The SpaceFlight planets opt in. Teleport and collide-and-slide
  motion (XR player, grabbed props) keeps the old `MoveKinematic(body, delta)`
  path. Before this, a planet was a stationary wall to friction, so a landed
  ship was dragged toward world zero and scraped off the surface instead of
  riding with the planet.
- `V12.Basic` now references `V12.Physics` and `V12.Gravity`. `BasicRegistry`
  registers the gravity field before the physics service so the field is
  current for the tick. The packed `Finite.V12.Basic` declares both as package
  dependencies.
- `vcsdk sync` now packs `Finite.V12.Physics` and `Finite.V12.Gravity` before
  `Finite.V12.Basic`. `sync --list` reports both, and the SDK README documents
  them in the feed and in the Basic bundle.
- `IPhysicsBody.AddForce` and `AddTorque` are now continuous force/torque
  values applied over the next step. Use `AddImpulse` and `AddAngularImpulse`
  for instantaneous changes; the player jump uses the impulse path.
- Physics stepping now follows the engine's fixed clock. `GameRoot.TimeScale`
  runs more fixed steps and therefore speeds up physics; the old stopwatch
  accumulator in the Bepu backend is gone.
- `PhysicsBodyComponent.SyncLocomotionVelocity` lets a dynamic body keep its
  physics velocity when a `LocomotionComponent` is also present. Vessels and
  scripted flight can opt out of the XZ stomp instead of removing the controller.
- `RaycastHit.Body` is nullable. A ray can hit static geometry, which has no
  `IPhysicsBody`; callers that already null-checked are unaffected.
- The NovaV2 host no longer builds its demo world and UI overlay when the
  default `gamepaks` folder contains an assembly. A gamepak gets a clean scene;
  pass `--views-demo 1` to force the demo back.
- `NovaV2.Host/project.godot` no longer defaults the editor run to the old
  `ControllerDemo` folder, so the gamepaks folder is the normal dev loop.
- NovaV2 accepts `--xr-mode off` as the desktop-test spelling for `--xr off`.
  The SpaceFlight sample keeps `FixedStepClock.MaxStepsPerFrame` at 8 so high
  time scales cannot run an unbounded number of physics steps per frame.
- NovaV2 XR is opt-in at runtime instead of a process relaunch. The host
  creates the XR rig by default (`NovaV2RuntimeOptions.EnableXR = true`) but
  never initialises OpenXR or flags the viewport until XR is requested.
  `NovaV2RuntimeOptions.StartXR`, `--xr on` or a manifest `"Xr": true` start it
  in the current process; `NovaV2Runtime.EnableXR()`, `DisableXR()` and
  `ToggleXR()` switch it during gameplay, and `--xr off` omits the rig
  entirely. A device with no OpenXR runtime (an Android phone, a desktop with
  no headset) logs the failed start once and keeps rendering flat; nothing
  polls in the background, so a later `EnableXR()` call is what retries.
  `project.godot` now enables the OpenXR module at startup because Vulkan needs
  the instance extensions before the scene loads; that only makes the runtime
  available and does not put the game into XR. The `--xr-mode on` relaunch and
  the `V12_XR_RELAUNCHED`/`--xr-relaunched` bookkeeping are gone.
- `SyncRegistry` is now an instance class with a `Default` compatibility
  registry. `SyncValue<T>` registers in `SyncRegistry.Default`, while a
  session-owned `SyncChannel` keeps a separate registry. `SyncManager` remains
  a static facade over `SyncChannel.Default` for code that has no session.
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
- `ViewportComponent` is now presentation only (`ViewId`, background, active).
  A new `RenderViewComponent` owns the camera, world, layer mask, clear mode
  and priority. Scene renderables carry `WorldId`/`LayerMask` plus an optional
  `ViewId` restriction instead of being owned by a viewport.
- `DrawItem` carries `WorldId`, `LayerMask` and `ViewId` instead of
  `ViewportId`. NovaV2 isolates worlds with reserved layer bits, renders one
  target per explicit view, and supports the same world in several views.
- `SceneQuery` picks by world id and layer mask, using the view camera for
  unprojection instead of filtering geometry by viewport ownership.
- The auto-registered `UIBuilder` root canvas now defaults to
  `ScreenSpace = true`. It is a free-standing screen UI root, and the old
  `false` default would bake a stray empty world quad at the origin once
  world-space Paper surfaces are enabled.
- Screen-space UI is now engine-owned (renderer-agnostic UI, Phase 1).
  `RenderFrameProducer` declares the UI view itself (`RenderViewIds.ScreenUi`),
  creates and resizes its target when the host selects
  `UiTargetPolicy.OwnTarget`, and names the surface to present in
  `RenderFrame.PresentTarget` (`null` = the host's presentation surface).
  `UiFrameContext` is now `(Captured, Screen, Width, Height)`; hosts no longer
  create, name, resize or destroy UI targets. NovaV2 passes its captured UI
  model and laid-out Paper frame, then presents the target the frame names.
  Covered by `UiFrameProducerTests`; the design is in
  `RENDERER_AGNOSTIC_UI.md`.
- World-space Paper UI is now engine-emitted (renderer-agnostic UI, Phase 2).
  `GameRoot.CaptureFrame` records `FrameSnapshot.Canvases` (`WorldCanvasSnapshot`:
  element id, world id, pixel size, placement without scale, size in metres,
  priority, interactive). `RenderFrameProducer` lays out each canvas headlessly
  with `PaperWorldCanvasSystem` and emits it as ordinary scene meshes through
  `PaperUiDrawBuilder.AppendWorld`: canvas-pixel vertices, `DrawItem.World` =
  pixel-to-metre scale × canvas placement, `ViewId = 0`, the canvas `WorldId`
  and the scene layer, depth-tested but not depth-written. No render target,
  view or renderer code is involved, and `CanvasComponent` gains `Priority` and
  `Interactive`. Covered by `UiFrameProducerTests`.
- Engine-side world-UI input (renderer-agnostic UI, Phase 3).
  `WorldUiPicking` raycasts the captured camera against canvas planes and maps
  hits to canvas pixels; `WorldUiInput` (registered with `InputService`, owned
  by `RenderFrameProducer` when an input service is supplied) feeds every
  surface each frame, focuses the canvas under a left click, routes keys and
  text to the focused canvas, clears focus on Esc or an empty click, and sets
  `UiCapturesPointer`/`UiCapturesKeyboard` for gameplay. `PaperSurfaceInput`
  holds the engine-side key/text mapping. `PaperInputHandler` now decides
  consumption from its own Paper instance so a world canvas owning the pointer
  does not make the screen layer swallow its events. Loopback is headless;
  covered by `WorldUiPickingTests`, `WorldUiInputTests`, and
  `UiFrameProducerTests`.
- `Docs/MODEL_LOADING.md`: guide to static 3D model loading in the engine —
  `ModelInstance` (preserved node hierarchy, per-part materials, triplanar
  fallback) versus `MeshAsset` (flattened single mesh, dominant material),
  asset mounts and `v12://` resolution, material and texture import rules,
  embedded texture extraction, UV-channel selection, import conventions, the
  static/skeletal split, WorldML usage, lifecycle and troubleshooting.
- Renderer-agnostic UI boundary cleanup (Phase 5). The Paper implementation
  (`ProwlPaperRenderer`, `PaperCanvasRenderer`, `PaperWorldCanvasSystem`,
  `PaperSurfaceInput`, `OrigamiDockHost`) moved from `V12-Engine` into
  `V12.Paper`; namespaces are unchanged so existing backend code stays
  source-compatible. `PaperMesh` is now toolkit-free (`System.Numerics` colors
  plus a neutral `UiBrush`), and the engine gained the `IUiSource` contract.
  `RenderFrameProducer` accepts an `IUiSource` (constructor parameter) and no
  longer creates Paper objects; `V12.Paper.PaperUiSource` implements world
  layout, stepping, pointer feeding and key/text routing. NovaV2 wires a
  `PaperUiSource`; MonoGame/Pulsar, Rockstar and Nova v1 reference `V12.Paper`;
  `GameRoot` no longer auto-registers a Paper `IUIRenderer`. `UiBoundaryTests`
  enforce that the engine assembly contains no Paper implementation and that
  the UI contracts contain no toolkit types. Follow-up (5b): the Origami
  code-editor contract (`ICodeEditor`, `CodeLanguages`) still references Prowl,
  so `V12-Engine` keeps its Prowl package reference until that contract moves.

### Removed

- The dead client-grab path: `PhysicsBodyComponent.ClientHeld`, `HeldPosition`,
  `HeldRotation`, `LastGrabMoveMs`, its `[Remote] Grab/GrabMove/Release` methods,
  and the `ClientHeld` branches in `PhysicsLocomotionSystem`. Session links never
  dispatched those RPCs, and grabbing now goes through `RequestSimulatingUser`.

- `TransformComponent` from the engine and V12.Basic. Elements own their pose
  through `IWorldElement.LocalTransform`.
- TransformComponent mirroring from `Element.LocalTransform`, renderer
  placement, SVG, lights, and physics locomotion.
- Implicit `ViewportComponent.RenderWorld` world claiming, the first-match
  viewport scan, and `FrameSnapshot.ActiveViewportIds`. Views are explicit
  `RenderViewComponent` entries now.

### Fixed

- Screen-space canvas input no longer leaks to whole-viewport overlays.
  `CanvasComponent.Interactive` is captured on `UINode`, and Paper marks a
  non-interactive canvas box `IsNotInteractable`, so a decorative crosshair or
  HUD overlay does not report `WantsCapturePointer` and swallow gameplay
  clicks. The VoxelPlanet crosshair opts out with `Interactive="false"`.
- `V12-Samples/VoxelPlanet` now pins `V12.Voxel` in a static constructor.
  A pak loader parses its worlds before `IV12Gamepack.Initialize` runs, so the
  previous load in `Initialize` was one step too late: `WorldMLParser` reported
  `Unknown component type 'VoxelPlanetComponent'` and skipped the planet even
  though `V12.Voxel.dll` was present and loaded moments later.
- `NovaV2.Shared` still requested `Godot.NET.Sdk/4.7.0` while the host and
  `project.godot` target 4.8. MSBuild ignored the older SDK (`MSB4240`) and
  resolved the shared project against the host's SDK; it now declares
  `4.8.0-beta.3` directly, so a standalone build of the shared project matches
  the host instead of silently targeting 4.7.
- Restoring `Finite.V12.Pak` failed with `NU1101: Unable to find package
  V12.Bindings`. The Pak nuspec declares `V12.Bindings` (it uses the bindings
  runtime to load Contract component assemblies), but `vcsdk sync` never packed
  it because it lives below the root-level discovery scan. Both pack paths now
  include it, and the package resolves from the same feed version as the rest of
  the engine.
- `tools/check-v12-deps.ps1 -IncludePackages` required a `V12.All.dll` that does
  not exist. Dependency-only metapackages (no `lib/` assembly) are now skipped
  when building the required-assembly set.
- `tools/pack-purr.ps1` omitted Physics, Gravity and Skeletal even though
  `Finite.V12.Basic` and the Voxel/Skeletal modules depend on them, so a feed
  produced by that script alone could not restore those packages. The list now
  matches the modules `vcsdk sync` packs.

- World teardown leaked components. `WorldManager.Close` (and the direct
  `GameRoot.Worlds` removal path) dropped the world tree without calling
  `OnDetach`/`OnDestroy`, so event subscriptions, script runtimes and backend
  resources outlived the world. There is now one teardown,
  `World.DetachAll()`, which walks the tree and detaches + destroys every
  component; `WorldManager` calls it on close and on direct removal, and
  `WorldContentSwap` (hot reload) reuses it instead of its own copy.
  `Element.RemoveComponent` now also calls `OnDestroy` after `OnDetach`.
  `PhysicsBodyComponent.OnDetach` destroys its backend body, which is the
  reported symptom: a closed world used to leave Bepu bodies alive in
  `PhysicsService` and still colliding. `PhysicsService.BodyCount` /
  `StaticBodyCount` expose the maps for diagnostics.
  `World.RemoveElement` deliberately still does not detach: removing an element
  is also how a reparent works, so detaching there would break moving a live
  subtree between worlds.
  Teardown also releases the component's process-wide `SyncValue` registry
  entries. `SyncRegistry.Unregister(ISyncValue)` removes by instance and refuses
  to delete a replacement that took the same key; `SyncValueScanner` is the one
  enumeration used by both the session channel and teardown, so registration and
  cleanup cannot drift apart.
  Tests: `V12.Architecture.Tests/WorldTeardownTests` checks recursive detach,
  close, direct removal, the physics body count, contract pinning, and that detach
  and `RemoveComponent` unregister values.

- Contract components now release their VM object. `ContractComponentRegistry`
  pins the handle (`ContractRuntime.PinHandle`) before the constructor runs and
  `ContractComponent.OnDetach` unpins it, so a closed world's script instances
  become collectable instead of staying rooted for the life of the session. This
  needed a VM change: the ObjektRT collector only rooted statics and interpreter
  stacks, so a handle held by CLR host code had no root at all and could be swept
  while the component was still calling into it. `Runtime.PinObject` /
  `UnpinObject` / `PinnedObjectCount` / `IsObjectAlive` are the host root API.
  Tests: the ObjektRT runtime suite pins/collects/unpins/collects, and
  `WorldTeardownTests.ContractComponent_DetachReleasesTheVmHandle` compiles a
  `.ct` component, checks it is pinned after allocation, and checks the pin is
  gone after `RemoveComponent`.

- `SyncValue<T>` no-write-wins: two peers writing the same value both advanced to
  the same counter, so `TryApplyRemote` rejected `version <= local` on both sides
  and every remote update was dropped for as long as both kept writing. The
  packed `(clock, writer)` stamp makes the winner total and deterministic, and
  `TryApplyRemote` now compares against the last accepted stamp rather than a
  recomputed one (which let a duplicate from a higher writer id re-apply).
  `V12.Architecture.Tests/ValueSyncMultiWriterTests` covers tie-break, stale
  rejection, clock adoption, and a real three-root loopback relay.
- Element tree consistency. `World.AddElement`/`RemoveElement` and
  `Element.AddChild`/`RemoveChild` now index and unindex whole subtrees, so
  `FindElementById` no longer misses live descendants or returns detached ones.
  `AddChild` detaches from the old parent before attaching (a reparent no longer
  leaves the element in two children lists), refuses cross-world moves, and
  refuses cycles. `AddComponent`/`RemoveComponent` lock the element's own world
  instead of the focused world. `Element.Active` and `ComponentBase.Active` are
  dirty-tracked, so toggling visibility re-captures the frame.
- `vcsdk pack`: every dependency DLL from the publish output is staged, not
  just those whose names avoid the `System.`/`Microsoft.` prefixes. The pack is
  `--no-self-contained`, so the output never contains the shared framework and
  the old name filter only dropped real NuGet dependencies
  (`Microsoft.Extensions.*`, `Microsoft.Data.Sqlite`, `System.Text.Json`, ...).
  A pak that referenced one of those loaded with the assembly missing even
  though the game shipped it. `bin/` now mirrors the publish output and only
  `V12*.dll` stays out, so the renderer's engine copy remains authoritative.
- `V12.Physics`: capsule colliders now use the requested total height.
  Bepu's `Capsule.length` is the length of the internal segment, so the old code
  built a capsule `2 * radius` taller than its visual mesh and its
  `ColliderComponent.Height`. Capsule characters floated above the floor and the
  ground probe stopped short of the surface. `BepuShapeFactory` and `BepuInertia`
  now share one capsule-dimension helper, and `CharacterCapsule_RestsAtItsRequestedTotalHeight`
  covers the regression.
- `V12.Physics`: `DestroyBody` disposes the body's shape with
  `Shapes.RemoveAndDispose` instead of leaking it until the simulation is
  disposed. The gravity snapshot is refreshed before every substep, so a body
  that sleeps mid-step cannot make later substeps use another body's gravity
  scale. Rotation locks and damping now apply before integration, not after.
- `V12.Physics` helpers: `BepuBodyExtensions.SetMass` honors its mass argument,
  and `BepuInertia.WithMass` rescales the inverse inertia tensor as well as the
  inverse mass. A custom `IGravityField` that reports a mismatched source count
  can no longer make the integrator read past its snapshot.
- `V12.Gravity`: `OrbitalMechanics` rejects non-finite Stumpff/Newton
  intermediate values instead of returning a NaN state.
- `V12.Basic`: `PhysicsLocomotionSystem` passes `ColliderComponent.IsTrigger`
  through to the backend and honors the legacy `RigidBodyComponent.IsKinematic`
  flag when `PhysicsBodyComponent.BodyType` is still at its default.
- SpaceFlight: prograde and retrograde burns use velocity relative to the
  dominant body, not the ship's world velocity. The predicted-orbit tube now
  builds local geometry and follows the body with the element transform, so
  planetary motion no longer rebuilds the whole mesh every refresh. The scene is
  idempotent on C# hot reload, the time-warp levels match the 8x fixed-step
  budget, planets are lit while the Sun stays unlit, and navball marker dots
  face the camera instead of turning edge-on.
- SpaceFlight: removed the stale `SpaceFlightScene.cs.orig` and
  `spaceflight-scale.patch` artifacts.
- SpaceFlight: the ship no longer pole-vaults off a planet on touchdown. The
  visible 28 x 26 x 42 hull now uses a 28-unit sphere contact proxy. A box
  collider caught a leading corner on the curved surface and converted forward
  speed into a 74-85 m/s outward hop (about 450-500 units up, repeating as
  skips), which read as an elastic bounce. The planet is kinematic and the ship
  is 12.2 t, so mass cancels out of the response; the contact shape was the
  cause. Telemetry also reports planet-relative speed while the contact used to
  see world speed, so the same fix makes the reported speed match the physics.
- A model's own material flags now survive import. `GameRoot.ApplyModelMaterial`
  rebuilt the ambient-blocking fallback from albedo, metallic, roughness and the
  texture paths only, so `DoubleSided`, `Unlit`, emissive colour and emission
  strength were dropped: a two-sided shell (a GLB skybox sphere) was drawn
  back-face culled from inside and emissive-only materials came out as lit white
  surfaces. `IModelMaterialSource` carries those flags now (defaulted, so an
  existing source still compiles) and `MeshAsset` reports them from the model's
  dominant material.
- UI clicks now repaint. NovaV2 renders on demand, and a click that changed a
  tab, toggle, checkbox, slider or named action only marked network-dirty, so the
  screen kept showing the old state until some other input forced a frame. The UI
  setters and `UiActions` now call `GameRoot.MarkRenderDirty`. Covered by
  `ScreenUiTabsTests`, which drives real Paper pointer events.
- Toggle and checkbox clicks now write their state back to the component.
  `CaptureUI` used to hand the renderer only the component's delegate, so the
  node flipped its own copy, the next capture re-read `IsOn`/`Checked`, and the
  widget snapped back. `SetToggled`/`SetChecked` keep the component authoritative.
- Sliders are click-to-set across `Min`..`Max` and show their current value in
  NovaV2 instead of behaving like a toggle chip. Drag is not wired yet, and the
  value is quantised by `Step` when one is authored.
- NovaV2 Paper UI no longer throws `Texture #N is stale or unknown` after a
  frame with no UI output (a world swap, for example). `PaperUiDrawBuilder`
  cached the 1x1 white fallback texture across frames, but the resource cache
  retires textures that go a frame unused, so the cached handle reached
  `CreateMaterial` after its texture was destroyed. The handle is now requested
  from the cache every frame. Covered by `PaperUiDrawTests`.
- World-space canvases are no longer also drawn into the screen-space overlay on
  NovaV2. `ProwlPaperRenderer` only skipped them when it owned the world-canvas
  system; with `PaperUiSource` owning them it drew a second copy.
  `WorldCanvasesExternal` marks that case.
- `RectComponent.Width`/`Height` are honoured by the Paper renderer, and an
  unsized rect hugs its content. Previously the size was ignored and the box
  stretched over its parent, which made a card behind a layout impossible.
- WorldML parse errors report the original `XmlException` message. The fragment
  retry used to wrap the document and report a misleading "XML declaration must
  be the first node" instead of the real problem. The retry is also skipped for
  documents that already carry a declaration.
- The live C# compiler references package assemblies shipped beside the host,
  not just `V12*` and Serilog. Scripts that call APIs whose signatures use
  package types (`PaperPalette.FromHex` returning `Prowl.Vector.Color`, for
  example) failed with `CS0012` under `--hot-reload`.
- WorldML warns once when a short component type name is ambiguous and reports
  which type won. `ButtonComponent` and `ProgressBarComponent` exist in both
  `V12.Components` and `V12.Components.UI`, so the UI widget silently lost to
  the legacy component.
- Bepu dynamic bodies now get a real inertia tensor from their shape and mass.
  Previously `BodyInertia { InverseMass = 1/m }` left the tensor at zero, so
  torque, tipping and tumbling were no-ops. Runtime mass changes recompute it.
- The Bepu backend now honors `PhysicsBodyDesc.GravityScale` and `CanSleep`;
  both were carried by the description but never applied. `GravityScale = 0`
  is weightless, and `CanSleep = false` keeps a body awake.
- Ground probes follow the local gravity direction instead of always casting
  world `-Y`, so character bodies can ground on a spherical planet.
- Sphere and capsule meshes now use outward-facing triangle winding. The old
  order rendered inside-out on back-face culled backends (NovaV2/Godot), which
  showed the far shell as a crescent on planet-sized spheres.
- `GamepackLoader.LoadFromDirectory` resolves each DLL to an absolute path
  before handing it to `AssemblyLoadContext.LoadFromAssemblyPath`. Relative
  paths from `Directory.EnumerateFiles` made every directory gamepak fail to
  load in NovaV2.
- Gamepak dependency resolution now probes the host's assembly directory, the
  host base directory, the gamepaks root and the requesting assembly's
  directory, instead of only searching already-loaded assemblies. This fixes
  `FileNotFoundException` for `V12.Gravity`/`V12.Physics` when a gamepak's
  `OnStart` is the first code to touch a lazily-loaded transitive dependency.
  NovaV2.Host also touches both assemblies at startup so WorldML can resolve
  gravity components deterministically.
- Renderer deployment skips `*.tmp` files. A Godot export can leave a
  full-size `NovaV2.tmp` beside the host when a run is interrupted; it was
  being copied into the game output as a 98 MB duplicate.
- `InputService.SendEvent` now broadcasts raw keyboard edges to every handler
  even when a UI handler sets `Handled`. Paper/World-UI handlers register before
  game action maps, and a focused element was swallowing every key event, so
  game keybinds stopped working after UI focus. Pointer, wheel and text events
  still stop at the first consumer; games can check
  `InputService.UiCapturesKeyboard` if they want to suppress keybinds while
  typing. Covered by `InputServiceTests`.
- NovaV2 pulls its window to the foreground and grabs focus on launch when not
  running as an editor hint. Launches from a terminal or IDE otherwise left
  keyboard focus on the console, which made every keybind look dead.
- Physics no longer advances from wall-clock time, which fixes time scale,
  pause-adjacent catch-up behavior, and nondeterministic stepping.
- Name-based input axes now keep their sign. `InputActionMap` previously folded
  per-frame name axes through `Math.Max`, so every negative direction (stick
  left/back, motion axes) was clamped to zero and XR sticks could only move one
  way. Each frame now takes the latest signed value.
- `NovaV2XRService` resolves the local `Player` through the focused world and
  every hydrated session, not just `ActiveWorlds`. A client world is
  `WorldAuthority.Remote`, so the old lookup returned null after the cache
  refresh and stopped writing poses and following the rig, even though the
  headset camera kept tracking.
- The multiplayer demo maps XR stick-up to forward (the stick reports +Y when
  pushed forward) and moves relative to the headset yaw.
- Multiplayer avatars in XR now use a body pose captured from the tracked rig:
  the body root sits on the floor under the headset, with head and hands stored
  relative to that body. Host-driven and client-driven VR therefore show the
  same body position, yaw, and tracked parts instead of leaving the body at its
  spawn point.
- An XR peer adopts its host-assigned avatar spawn position once, so two peers
  that loaded the same world file no longer start with their bodies inside each
  other before either one moves.
- XR right-stick turning: the rig rotates smoothly around the headset, and
  movement stays relative to the resulting facing direction.
- The demo controller re-reads the authored `Player` transform on its first
  tick. Components attach before the world parser finishes writing transforms,
  so solo/desktop play previously started at the world origin and overwrote the
  authored spawn position.
- Input button edges now latch across fixed steps. A quick right-click, Escape
  tap, pad tap, or headset button press that goes down and up between two
  `InputActionMap.Update` calls still produces both a pressed and released
  edge, so pointer capture no longer silently misses fast clicks.
- `NovaV2InputHandler.ToInputKey` falls back to the logical keycode when the
  physical keycode is `None` or the generic `Pause` value. Remote desktop and
  synthetic input paths report the real key in `Keycode`, so WASD and capture
  keys were previously dropped on those setups.
- Desktop gamepad movement: the left stick drives the same movement axes as
  WASD, and the demo binds Escape as a pointer-capture fallback.
- `V12_MP_DEBUG=1` adds one-second input diagnostics to the demo and raw
  left-stick logging to the XR service.
- The multiplayer host hides its local XR hand meshes while its own avatar is
  visible, so a second pair of hands no longer renders over the replicated ones.
- The V12 BSON transient-member convention now applies to every `IComponent`,
  not only types in the `V12` namespace. Game components in other namespaces
  no longer serialize `SyncValue` or delegate members by accident.
- WorldML Euler extraction now round-trips combined pitch, yaw, and roll
  without drifting.
- WorldML serializer no longer synthesizes TransformComponent.
- Pak template tests now search recursively for expanded template children.
- Linux audio silence. OwnAudioSharp's ALSA enumeration includes the `null` PCM
  ("Discard all samples (playback) or generate zero samples (capture)"), and the
  own-audio backend took the first device when ALSA had no default, so a machine
  whose real sink could not open ran with a fully initialised, completely silent
  engine. Device selection now skips null/dummy devices while real hardware is
  available, tries every remaining device before failing startup, and reports the
  enumerated list when none opens. `AudioDeviceInfo.IsSilent` exposes the check,
  `AudioSystem` logs a warning when only a silent sink exists, and NovaV2 accepts
  `--audio-device <name>` to choose the output device explicitly.

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

### Wire compression

Payloads of 512 B or more are Brotli-compressed behind a `V12C` header. A build
from before this change cannot read them: update both peers together. Small
payloads (including normal value batches) are unchanged and still raw BSON.
Tune with `AncientCompressor.MinBytesToCompress` / `BrotliQuality` /
`BrotliWindow` if the fixed step shows compression cost.

Version stamps are now `(clock << 16) | writerId` rather than a bare counter. The
payload is still one `uint64`, so nothing changes on disk, but a build from
before this change and one after it must not be mixed in one session: each side
would read the other's stamps as a different ordering. Update both peers.

Code that treated `SyncValue<T>.Version` as "number of writes" should use
`SyncValue<T>.ClockOf` / `WriterOf`, or keep its own counter. Do not assume
`Version` starts at 1 or increments by exactly one: the clock is UTC
milliseconds.

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
- [`RENDERER_AGNOSTIC_UI.md`](RENDERER_AGNOSTIC_UI.md)
- [`../V12-Engine/Docs/SIMULATION_TIMING.md`](../V12-Engine/Docs/SIMULATION_TIMING.md)
- [`../V12-Engine/Docs/SYNC_VALUE_GUIDE.md`](../V12-Engine/Docs/SYNC_VALUE_GUIDE.md)
- [`../V12-Engine/Docs/WORLDML_BINDINGS.md`](../V12-Engine/Docs/WORLDML_BINDINGS.md)
