# Scene switching and world sessions

> Last updated: 2026-11-26
>
> How V12 loads, focuses, hides and unloads worlds, how a
> button -> loading scene -> game transition is put together, and what happens to
> the player on desktop and in XR. The renderer contract itself stays in
> [`RendererContract.md`](RendererContract.md).

## 1. A scene is a `World`

V12 does not have a separate scene object. A scene is a `World`, and a running
game can have several loaded at once:

- `GameRoot.Worlds` is the world list.
- `GameRoot.WorldManager` owns the live `WorldSession` around each world.
- `GameRoot.PersistentWorld` is a special world for content that must survive
  world switches, usually the local player and camera.
- `Scene` is a static convenience facade over the focused world
  (`V12-Engine/Core/Scene.cs`). It is not the scene unit.

`World.Id` is a stable `Guid` and travels on the wire, so a world can be
addressed across a network link.

### 1.1 Session states

Each `WorldSession` derives four states from its inputs
(`V12-Engine/Core/Worlds/WorldSession.cs`):

| State | Meaning |
|---|---|
| `Visible` | Focused or pinned, and hydrated. Captured for rendering and UI. |
| `Simulated` | In `ActiveWorlds`; its components tick locally. |
| `Subscribed` | A remote world that is applying host state. |
| `Hydrated` | The local world tree is present (for a remote world, a snapshot has arrived). |

The useful distinction is:

- **Focus** decides which world is the active scene for `Scene.Current`,
  `SelectedWorld`, default view targets, and default scene lookups.
- **Visible** is what gets captured. A pinned world can stay visible while
  another world is focused.
- **Simulated** is what ticks. A hidden hosted world with players keeps
  simulating; a remote world can be visible without being in `ActiveWorlds`.

`GameRoot.CaptureFrame` captures `VisibleWorlds` plus `PersistentWorld`
(`V12-Engine/Core/GameRoot.cs`). `GameRoot.CaptureUI` does the same, which is why
a loading overlay placed in the persistent world survives a switch.

## 2. What is supported

| Need | Support | API |
|---|---|---|
| Load WorldML from a string | Yes, synchronous | `GameRoot.LoadSceneFromXml` (`V12.Basic/Scene/SceneLoader.cs`) |
| Load WorldML from a file | Yes, synchronous | `GameRoot.LoadSceneFromFile` |
| Load WorldML from an embedded resource | Yes, synchronous | `GameRoot.LoadSceneFromResource` |
| Load a `.v12pak` world | Yes, synchronous | `root.LoadPak(...)` (`V12.Pak`) or `GameRoot.LoadArchiveWorld` |
| Create an empty or template-built world | Yes | `GameRoot.CreateWorld`, `GameRoot.Templates` |
| Keep several worlds loaded | Yes | `WorldManager.OpenWorld`, `EnsureSession` |
| Focus a different world | Yes | `FocusWorld` / `SelectWorld`, `FocusSession` |
| Hide a world without closing it | Yes | `WorldSession.HiddenPolicy`, `Pinned` |
| Close and unload a world | Yes, with caveats | `WorldManager.Close` / `GameRoot.CloseWorld` |
| Replace a world's contents in place | Yes | `WorldContentSwap.Replace` |
| A loading screen, fade or transition manager | No built-in | Game code |
| Asynchronous loading or progress reporting | No | Everything parses synchronously |
| Automatic player spawn placement | Partial | `World.SpawnPosition`, `SpawnPointComponent`, manual teleport |
| A player prefab stamped into every world | No | Use the persistent player, or author a player per world |

The short version: the world model is solid and already supports the hard part
(multiple loaded worlds with independent focus, visibility and simulation). The
transition flow itself is game code.

## 3. Loading a world

### 3.1 WorldML

`SceneLoader` parses WorldML into a `World`, adds it to `GameRoot.Worlds`, and
optionally focuses it:

```csharp
using V12.Basic.Scene;

// Load and focus immediately.
World world = gameRoot.LoadSceneFromFile("Scenes/Level1.worldml");

// Load without focusing, so the caller can place the player first.
World next = gameRoot.LoadSceneFromFile("Scenes/Level1.worldml", select: false);
```

`LoadSceneFromXml` uses the registered `ITemplateProvider` when one is present,
so `template="..."` and `<Instance template="...">` work in runtime-loaded
scenes. A pak loader registers its own template provider from the pak's
`templates/` folder.

With `select: false`, the world sits in `GameRoot.Worlds` and gets a paused local
session on the next reconcile, until something focuses or pins it. That is the
safe way to stage a load.

### 3.2 Pak worlds

`V12.Pak` loads declared worlds, parses them, adds them to `GameRoot.Worlds`, and
returns them in `V12PakLoadResult.LoadedWorlds`. It does **not** focus them:

```csharp
var pak = gameRoot.LoadPak("Content/Game.v12pak"); // keep this result alive
gameRoot.FocusWorld(pak.LoadedWorlds[0]);
```

Keep the `V12PakLoadResult` alive for as long as the pak's worlds and assets are
in use.

`GameRoot.LoadArchiveWorld` is the older `.v12world` path. It focuses the loaded
world automatically.

### 3.3 Empty and template-built worlds

`GameRoot.CreateWorld(name, templateName)` builds a world from a registered
factory and focuses it:

```csharp
gameRoot.Templates["Arena"] = name =>
{
    var world = new World(name);
    world.AddElement(new Element("ArenaRoot"));
    return world;
};

World arena = gameRoot.CreateWorld("Arena_01", "Arena");
```

`CreateWorld` returns the existing world if the name is already used, and focuses
it. Built-in templates are `"empty"` and `"HotReload"`.

### 3.4 The synchronous constraint

`LoadSceneFromFile`, `LoadSceneFromXml`, `WorldMLParser.Parse`, `LoadPak` and
`LoadArchiveWorld` are all synchronous. There is no built-in async scene
streamer, progress callback, or background parser.

A loading scene therefore works like this:

1. Show the loading UI and request a transition.
2. Let at least one rendered frame pass.
3. Parse the next world.
4. Place the player, focus the new world, close or hide the old one.

Step 3 blocks the frame for as long as the parse takes. Small scenes are fine. A
large pak extraction or a big WorldML tree will freeze the frame, so keep the
loading screen simple and do not expect a progress bar unless the game builds its
own incremental loader.

## 4. Switching and unloading

### 4.1 Focus

```csharp
gameRoot.FocusWorld(world);       // by reference
gameRoot.FocusWorld("Level1");    // by name
gameRoot.SelectWorld(world);      // compatibility alias
gameRoot.FocusSession(session);   // by session
gameRoot.FocusSession(null);      // clear focus
```

Focusing a world reconciles every session. A focused local world becomes visible
and simulated. A focused remote world becomes visible and subscribed, but not
simulated.

`WorldManager.OpenWorld(world, focus: false, hiddenPolicy: HiddenWorldPolicy.Paused)`
loads a world without making it visible or ticking it.

### 4.2 Hidden worlds

A hidden world can be paused, apply network state without simulating, or keep
simulating:

| `HiddenWorldPolicy` | Behaviour while hidden |
|---|---|
| `Paused` | Do not tick or subscribe. |
| `NetworkOnly` | Apply host state, do not tick gameplay locally. |
| `Simulate` | Keep ticking. |

A hidden **hosted** world with connected players is forced to `Simulate`, so
hiding a world does not freeze other players. `Pinned` is the other way to keep a
world visible, for example a picture-in-picture view.

Hiding a world is not the same as unloading it. Its element tree and any backend
resources already created for it still exist.

### 4.3 Closing

```csharp
var session = gameRoot.FindWorldSession(world);
gameRoot.CloseWorld(session, new WorldCloseOptions
{
    Mode = WorldCloseMode.Shutdown,
    Confirmed = true,
    ClearWorld = true,
});
```

`WorldCloseMode` covers `Leave` (leave a remote world while the host continues),
`Shutdown`, `HandoffTo`, `MoveToHeadless` and `DiscardDraft`. A hosted world with
connected players returns `WorldCloseResult.RequiresConfirmation` unless the
caller confirms or hands off authority.

`ClearWorld = false` keeps the world's element tree but removes the session and
world from `GameRoot.Worlds`. Add it back later with `OpenWorld` or
`Worlds.Add` if you want a return trip without reparsing.

The persistent world cannot be closed. `WorldManager.Close` returns
`WorldCloseResult.Unsupported` for it.

### 4.4 In-place content swap

When the world object, session, focus and camera should stay put and only the
contents change, use `WorldContentSwap.Replace`:

```csharp
using V12.WorldML;

var incoming = new World(current.WorldName);
incoming.AddElement(new WorldMLParser().ParseFile("Scenes/Level1.worldml"));

WorldContentSwap.Replace(current, incoming);
```

This is the path used by NovaV2 hot reload. It detaches and destroys the outgoing
world's components before replacing the tree. `World.ReplaceFrom` alone drops the
old tree without calling `OnDetach`/`OnDestroy`, so prefer `WorldContentSwap`
when components own backend resources.

## 5. A complete transition flow

The two shapes are a dedicated loading world and a persistent overlay.

The overlay is usually better because the player and camera never vanish. A
dedicated loading world is useful when the loading screen is a real 3D scene with
its own camera, sky or environment.

### 5.1 Recommended shape

1. At boot, create the player and the loading overlay in `PersistentWorld`.
2. The menu button calls `Request(scenePath)`.
3. The request shows the overlay and stores the path.
4. On a later host frame, `Pump` loads the new world with `select: false`.
5. Place the player at the new world's spawn.
6. Focus the new world and close the previous world.
7. Hide the overlay.

A small transition object:

```csharp
using System.Numerics;
using V12.Basic.Components;
using V12.Basic.Scene;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;

public sealed class SceneTransition
{
    private readonly GameRoot _root;
    private readonly IWorldElement _loadingOverlay;
    private string? _pending;
    private int _framesToWait;

    public SceneTransition(GameRoot root, IWorldElement loadingOverlay)
    {
        _root = root;
        _loadingOverlay = loadingOverlay;
    }

    /// <summary>Call from a UI callback. Do the actual load in Pump.</summary>
    public void Request(string scenePath)
    {
        _pending = scenePath;
        _framesToWait = 1;
        _loadingOverlay.Active = true;
        _root.MarkRenderDirty();
    }

    /// <summary>Call once per host frame, after the frame has been captured.</summary>
    public void Pump()
    {
        if (_pending is null) return;
        if (_framesToWait-- > 0) return;

        string path = _pending;
        _pending = null;

        World? previous = _root.FocusedWorld;
        World next = _root.LoadSceneFromFile(path, select: false); // synchronous

        MovePlayerTo(_root, next);
        _root.FocusWorld(next);

        if (previous is not null)
            _root.CloseWorld(_root.FindWorldSession(previous));

        _loadingOverlay.Active = false;
        _root.MarkRenderDirty();
    }

    public static void MovePlayerTo(GameRoot root, World world)
    {
        IWorldElement? player = root.Player;
        if (player is null) return;

        Vector3 spawn =
            world.FindElementWithComponent<SpawnPointComponent>()?.LocalTransform.Position
            ?? world.SpawnPosition;

        // A split rig puts the capsule on PlayerBody; a one-element player owns it.
        IWorldElement bodyElement =
            player.GetRoot().FindChildByNameRecursive("PlayerBody") ?? player;
        Quaternion rotation = Quaternion.Identity;

        bodyElement.LocalTransform = new TRS(spawn, rotation, Vector3.One);

        if (bodyElement.GetComponent<PhysicsBodyComponent>() is { Body: { } body })
        {
            body.Position = spawn;
            body.Rotation = rotation;
            body.LinearVelocity = Vector3.Zero;
            body.AngularVelocity = Vector3.Zero;
        }
    }
}
```

Wire the button, either in C# or through `SceneBinder` for an authored UI:

```csharp
using V12.Basic.Scene;
using V12.Components.UI;

var transition = new SceneTransition(gameRoot, loadingOverlayElement);

menuWorld.Bind()
    .On("PlayButton", element =>
    {
        var button = element.GetComponent<ButtonComponent>();
        button.OnClick = () => transition.Request("Scenes/Level1.worldml");
    })
    .ThrowOnUnmatched();
```

The important timing rule is: **do not parse the next scene from the button
callback**. `Pump` is the tick/frame boundary where the world tree, sessions and
render capture are not in the middle of one world's update.

If the host only gives you `GameRoot.OnFixedUpdate`, the same pump works there;
wrap it in a `void OnStep(float dt) => Pump();` handler. On a frame that runs
several catch-up fixed steps, the overlay may not get a rendered frame before the
load; accept that or drive `Pump` from the host's real frame loop after
`V12Tick`.

### 5.2 Dedicated loading world

```csharp
World loading = gameRoot.CreateWorld("Loading"); // creates and focuses
loading.AddElement(BuildLoadingScreen());

// Later, from the transition pump:
World next = gameRoot.LoadSceneFromFile(path, select: false);
SceneTransition.MovePlayerTo(gameRoot, next); // the helper from 5.1
gameRoot.FocusWorld(next);
gameRoot.CloseWorld(gameRoot.FindWorldSession(loading));
```

If the loading world has a 3D presentation, give it a `RenderViewComponent`
that targets the loading world and its camera. When a view exists, the renderer
uses explicit views and skips the "first current camera" fallback. That is the
clean way to own the frame during a transition.

## 6. Player continuity

### 6.1 The intended model: one persistent player

Put the local player and its camera in `GameRoot.PersistentWorld` and leave
worlds as content only:

```csharp
using System.Numerics;
using V12.Basic.Building;

gameRoot.PersistentWorld.SpawnPlayer3D(new Vector3(0f, 1.5f, 0f));
```

`SpawnPlayer3D` documents this explicitly. The persistent world:

- has a session that ticks even when the world is not focused,
- is always captured for rendering and UI,
- cannot be closed,
- is never replaced by a network snapshot.

`GameRoot.Player` looks for an element named `Player` in `PersistentWorld`. That
is the local player shortcut.

For the split desktop/XR rig, use `PlayerMotorComponent` on a floor rig named
`Player`, with a body named `PlayerBody` in the same scene root (the motor
resolves it from the rig's root), and the desktop camera / XR head and hands under
the rig. The motor drives the body, follows it with the floor rig, and switches
between mouse look and XR look based on whether the host registered an XR tracking
service. See `docs/BUILDING_GAMES.md` for the component setup.

### 6.2 Worlds do not get a player template

There is no automatic player injection per world. Two things are called
"templates" and neither is a player templater:

- `GameRoot.Templates` is a `string -> Func<string, World>` map used by
  `CreateWorld`. It creates worlds.
- WorldML `template="..."` and `<Instance template="...">` expand a prefab
  subtree at parse time. They clone XML and interpolate attributes; they do not
  create a live player manager or bind a rig per world.

If a game deliberately uses one player per world, it authors that player in each
world's WorldML or builds it with `SpawnPlayer3D` / a C# builder. The game then
owns the bookkeeping:

- keep the element named `Player` for the XR lookup (`GameRoot.Player` itself only
  looks in `PersistentWorld`), or adapt the lookup,
- remove the old rig before or while unloading its world,
- avoid leaving multiple `CameraComponent.IsCurrent` cameras across visible
  worlds.

For networked worlds, a local-only rig can be kept inside the world and marked
`NonPersistentComponent`. `NetworkWorldLink` preserves such subtrees across a
host snapshot replacement. `MultiplayerDemo` uses this for its local player rig.
It is still per-world bookkeeping, not engine templating.

### 6.3 Spawn placement is game code

`World.SpawnPosition` exists (default `(0, 1.5, 0)`), and
`SpawnPointComponent` is a marker whose element transform is the intended spawn.
Nothing applies either automatically:

- `GameRoot.TeleportPlayerToSpawn` is commented out in `GameRoot.cs`. The comment
  says it belongs in game-specific code.
- `WorldMLParser` does not apply a `<SpawnPosition>` block to
  `World.SpawnPosition`, even though the SDK's generated `world.xml` still writes
  one.
- `PlayerMotorComponent` seeds its private spawn position at `OnAttach` and has
  no public setter. `ResetToSpawn` returns to that seeded point, not to the newly
  focused world's spawn.

So on a world switch, move the player yourself. If the game uses a split rig,
move the body and let the motor bring the floor rig with it. If the game wants
fall/reset to follow the new world, recreate the motor or add a small
`SetSpawn` API in game code.

## 7. Rendering and UI

- `CaptureFrame` collects the visible worlds plus `PersistentWorld`.
- `CaptureUI` collects screen-space canvases from the visible worlds plus
  `PersistentWorld`.
- `RenderViewComponent` defines an explicit view: target world, camera, layers,
  clear mode, priority and optional target size.
- If any `RenderViewComponent` is active, `RenderFrameProducer.BuildViews` uses
  those views and does not run the fallback that picks the first
  `CameraComponent.IsCurrent`.
- A view can target any hydrated world, including a world that is not itself
  visible. That is how picture-in-picture, editor previews and a dedicated
  loading view can work.

Practical consequences:

- A persistent loading overlay is captured regardless of which world is focused.
- A HUD in a game world disappears when that world stops being visible.
- Multiple visible worlds with current cameras are ambiguous in the fallback
  path. Use explicit views during transitions, or keep one player camera.

## 8. Desktop and XR

The world/scene path is renderer-agnostic, so switching worlds is the same code
on desktop and in XR. The differences are camera and input:

- **Desktop:** `PlayerComponent` creates a `PlayerCamera3D` child with
  `CameraComponent.IsCurrent = true`. `PlayerMotorComponent` finds
  `DesktopCamera` or `PlayerCamera3D` and uses mouse, keyboard and gamepad.
- **XR:** `NovaV2XRService` mirrors OpenXR head and hand nodes onto
  `XRHeadComponent` / `XRHandComponent` under the local rig.
  `PlayerMotorComponent` uses the XR left stick, head-relative movement, smooth
  turn and room-scale offsets. Hand meshes are hidden on desktop by default.
- **XR rendering:** `NovaV2Renderer.ApplyViewsLocked` deliberately ignores
  snapshot cameras while XR is active and lets the headset camera own the view.
  Do not fight it with a scene camera.
- **Entering XR:** `NovaV2Runtime.EnableXR` / `DisableXR` switch in process. The
  OpenXR session stays up when disabled. Desktop test launches use
  `--xr-mode off`.

The XR service resolves the local player by the literal name `"Player"`,
searching the focused world first and then every hydrated session. It
periodically re-caches, and `InvalidateElements` forces a re-resolve. If a
transition destroys and recreates the player, call `InvalidateElements`; otherwise
pose mirroring can stall briefly or keep writing to the old element.

If the player is persistent, a scene switch is invisible to both modes: the XR
rig and the desktop camera keep pointing at the same elements.

### Loading UI in VR

Screen-space canvases are flat overlays. Do not make one the only loading
feedback in a headset. Prefer:

- a world-space canvas attached to the player rig,
- a short transition that keeps the current world visible,
- or a dedicated loading world with its own camera and world-space UI.

## 9. Networking notes

- A network snapshot replaces the session world. It does not touch
  `PersistentWorld`, so the local player survives joining or leaving a server.
- `ApplyWorldSync` preserves subtrees marked `NonPersistentComponent` under the
  same parent path, which is how a local player rig stays put in a replica
  world.
- The session's `SpawnPosition` is copied from the snapshot.
- `FindElement` / `FindElements` search only `ActiveWorlds` (the simulated set).
  `FindElementInLoadedWorlds` searches every hydrated session, which matters for
  a remote world that is visible but not simulated.
- A focused client world is `WorldAuthority.Remote`: visible, subscribed, but not
  part of `ActiveWorlds`. Systems that must find its elements need the
  loaded-world query or the session directly.

## 10. Known gaps and caveats

1. **No async loading.** Scene parsing, pak extraction and world creation are
   synchronous. A loading screen gives you a frame, not a progress bar.
2. **No automatic spawn placement.** `SpawnPosition` and `SpawnPointComponent`
   exist, but the teleport path is commented out and `<SpawnPosition>` is not
   parsed. `PlayerMotorComponent` has no public spawn setter.
3. **Unloading is not a complete teardown.** `CloseWorld` with `ClearWorld` calls
   `World.ReplaceFrom`, which drops the tree without `OnDetach`/`OnDestroy`.
   `WorldContentSwap.Replace` does detach, but backend resources still need
   explicit cleanup. In particular, physics bodies live on `PhysicsBodyComponent`
   and are not walked on either path. The SpaceFlight sample notes that the
   engine does not yet expose a world clear that destroys old physics bodies.
4. **Physics is one global simulation.** All worlds share `PhysicsService`.
   Hiding a world stops its update pass but does not remove bodies that already
   exist. A paused world can therefore still collide with the active one if their
   geometry overlaps.
5. **Camera fallback is simple.** Any explicit `RenderViewComponent` disables the
   current-camera fallback. During a transition, decide which view owns the
   frame instead of relying on several `IsCurrent` cameras.
6. **XR lookup is name-based.** The XR service looks for `"Player"`. Per-world
   players must keep that name or the game must adjust the lookup.

If your levels own physics colliders, destroy those bodies before dropping their
world, or keep level content in one world and swap its contents in place.

## 11. API summary

| API | Purpose |
|---|---|
| `GameRoot.LoadSceneFromXml/File/Resource` | Parse WorldML into a `World`, optionally focus it. |
| `GameRoot.CreateWorld(name, template)` | Build and focus a world from a factory. |
| `GameRoot.LoadArchiveWorld(path)` | Load an older `.v12world` archive and focus it. |
| `GameRoot.LoadPak(path)` | Load a `.v12pak`; worlds are added but not focused. |
| `GameRoot.WorldManager.OpenWorld` | Add a world with explicit focus, authority and hidden policy. |
| `GameRoot.FocusWorld` / `SelectWorld` | Make a world the active scene. |
| `GameRoot.FocusSession` | Focus by session, or clear focus with `null`. |
| `GameRoot.CloseWorld` | Remove a session, with optional content clear and handoff. |
| `WorldSession.Pinned` | Keep a world visible while another is focused. |
| `WorldSession.HiddenPolicy` | Choose paused, network-only or simulating while hidden. |
| `WorldSession.Authority` | Local, hosted or remote simulation ownership. |
| `WorldContentSwap.Replace` | Detach old components and replace a world's tree in place. |
| `SceneBinder` | Bind C# handlers to authored elements by name, path, tag or predicate. |
| `RenderViewComponent` | Explicit world/camera/layer view for transitions and previews. |

## 12. Related docs

- [`BUILDING_GAMES.md`](BUILDING_GAMES.md): transforms, components, player rigs,
  WorldML and common gameplay recipes.
- [`HOT_RELOAD_NOVAV2.md`](HOT_RELOAD_NOVAV2.md): live C# compile and
  `WorldContentSwap` in NovaV2.
- [`PEER_HOSTING_AND_AUTHORITY.md`](PEER_HOSTING_AND_AUTHORITY.md): world
  sessions, authority and handoff.
- [`SIMULATING_USER.md`](SIMULATING_USER.md): who simulates an element and what
  clients may write.
- [`RendererContract.md`](RendererContract.md): what a renderer may consume from
  a captured frame.
