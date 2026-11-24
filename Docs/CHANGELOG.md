# Changelog

All notable engine and tooling changes. The gameplay-facing rules live in
[`BUILDING_GAMES.md`](BUILDING_GAMES.md).

## [Unreleased]

### Added

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

### Changed

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

- `TransformComponent` from the engine and V12.Basic. Elements own their pose
  through `IWorldElement.LocalTransform`.
- TransformComponent mirroring from `Element.LocalTransform`, renderer
  placement, SVG, lights, and physics locomotion.
- Implicit `ViewportComponent.RenderWorld` world claiming, the first-match
  viewport scan, and `FrameSnapshot.ActiveViewportIds`. Views are explicit
  `RenderViewComponent` entries now.

### Fixed

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
- [`RENDERER_AGNOSTIC_UI.md`](RENDERER_AGNOSTIC_UI.md)
- [`../V12-Engine/Docs/SIMULATION_TIMING.md`](../V12-Engine/Docs/SIMULATION_TIMING.md)
- [`../V12-Engine/Docs/SYNC_VALUE_GUIDE.md`](../V12-Engine/Docs/SYNC_VALUE_GUIDE.md)
- [`../V12-Engine/Docs/WORLDML_BINDINGS.md`](../V12-Engine/Docs/WORLDML_BINDINGS.md)
