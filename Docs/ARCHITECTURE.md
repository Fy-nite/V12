# V12 Architecture

V12 is split into a small renderer-agnostic engine, optional extension packages, backend renderers, and authoring tools. The split exists so the engine can run with any renderer, with or without the Paper UI stack, and with or without an editor.

## Repo map

| Repo | Assembly / package | Role |
|---|---|---|
| `V12-Engine` | `V12` / `Finite.V12` | World, ECS, components, rendering contracts, UI contracts, WorldML |
| `V12.Basic` | `V12.Basic` / `Finite.V12.Basic` | Gameplay components, systems, building helpers |
| `V12.Paper` | `V12.Paper` / `Finite.V12.Paper` | Prowl Paper, Origami and Quill UI implementation (optional) |
| `V12.Editor` | `V12.Editor` / `Finite.V12.Editor` | Editor interaction: viewport service, gizmo, inspector (optional) |
| `V12.Pak` | `V12.Pak` / `Finite.V12.Pak` | `.v12pak` archive loading and packing |
| `Nova` | `Nova.Shared` | Godot 4 renderer and frontend |
| `V12.Monogame` | `V12.Monogame` | MonoGame/Pulsar renderer |
| `V12Studio` | `Studio.Core`, `Studio.Editor` | Authoring tool and Godot editor host |

`V12.Editor` is a separate assembly and repo. The engine and renderers do not reference it; only the Studio host and the editor session do.

## Ownership rules

| Layer | May know about | Must not know about |
|---|---|---|
| `V12-Engine` | `System.*`, `Contract.Runtime`, its own contracts | Godot, MonoGame, Nova, Prowl, V12Studio |
| `V12.Basic` | `V12-Engine` | Godot, MonoGame, Nova, V12Studio |
| `V12.Paper` | `V12-Engine`, Prowl | Godot, MonoGame, Nova, V12Studio |
| `V12.Editor` | `V12-Engine` | Godot, MonoGame, Nova (backend specifics) |
| `Nova` / `V12.Monogame` | `V12-Engine` packet contracts, backend SDK | Editor internals, `V12.Paper` internals |
| `V12Studio` | `V12-Engine`, `V12.Basic`, `V12.Paper`, `V12.Editor`| `Nova` , Backend implementation details |

These rules are enforced by `V12.Architecture.Tests` and the project reference graph.

## Renderer boundary

The engine decides placement. Renderers mirror the tree and draw it.

A renderer may consume only these contracts:

- `RenderPacket` (`Core/Interfaces/Renderer/RenderPackets.cs`)
- `FrameSnapshot` (`Core/Rendering/FrameSnapshot.cs`)
- `UIFrame` (`Core/Rendering/UIFrame.cs`)
- `ViewportInfo` (`Core/Rendering/IViewportRegistry.cs`)
- `IRenderTarget` and its opaque `object NativeHandle`
- The interfaces `IRenderer`, `IUIRenderer`, `IViewportRegistry`, `IDirectTransformProvider`

Ids are the only identity a renderer should pass around: `long ElementId`, `long ViewportId`.

A renderer must not:

- Walk `IWorldElement`, `World`, `GameRoot`, or `V12.Components.*`.
- Call `GetComponent<T>()` on live engine objects.
- Hold live component references in packet DTOs.
- Mutate live component state. Use `IDirectTransformProvider`, fired callbacks, or the command/queue path instead.

See `RendererContract.md` for the full contract and `BUILDING_A_RENDERER.md` for a walkthrough.

## Viewports

A viewport is a camera rendering into a texture. The renderer owns that:

- create a render target for each viewport element id
- draw scene renderables into it (`RenderableSnapshot.ViewportId`)
- publish `ViewportInfo` (rect plus camera matrices) through `IViewportRegistry`

The renderer does not pick, orbit, or draw gizmos. `V12.Editor` builds a ray from
the published camera, calls `SceneQuery`, and turns the hit into selection or a
gizmo drag. Hotkeys come from the input/action system, not the renderer.

## UI boundary

The engine owns the UI model and capture:

- UI components (`Core/Components/UI/*`)
- `GameRoot` capture into `UIFrame`
- `IUIRenderer` and `IUIProvider` contracts

`V12.Paper` owns the Prowl implementation:

- `ProwlPaperRenderer`, `PaperCanvasRenderer`
- `PaperUIProvider`, `ProwlUIProvider`
- Origami components (`CodeEditor`, `DockSpace`, `DockPanel`)
- Paper palette, mesh and inspector helpers

A host that wants Paper calls `V12.Paper.PaperRegistration.Register(root)` before the runtime starts. A host that wants a different UI implements `IUIRenderer` and does not reference `V12.Paper`.

## Data flow

```
Game thread (60 Hz)
  GameRoot.Update
  GameRoot.CaptureFrame -> FrameSnapshot / RenderPacket
  GameRoot.CaptureUI    -> UIFrame

Render thread
  IRenderer.ApplySnapshot(FrameSnapshot)
  IRenderer.QueueItems(RenderPacket)
  IUIRenderer.ApplyUI(UIFrame)
```

The render thread never reads the world tree. Snapshot and packet synthesis is the engine's job.

## Where to add code

| Change | Location |
|---|---|
| A new engine component | `V12-Engine/Components`, with `[ComponentEntry]` and `BuildInspector` if it should appear in editors |
| A new gameplay system | `V12.Basic/Systems` or game code |
| A new UI widget model | `V12-Engine/Components/UI` plus a `UIFrame` mapping |
| A new Paper widget | `V12.Paper` |
| A new editor tool | `V12.Editor` |
| A new renderer | Its own repo, referencing `V12-Engine` only |

## Related docs

- `BOUNDARY_REFACTOR_PLAN.md` - the staged split plan
- `RendererContract.md` - normative renderer contract
- `WORKING_ON_V12.md` - contribution and build guide
- `BUILDING_A_RENDERER.md` - renderer implementation walkthrough
