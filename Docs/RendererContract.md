# V12 Renderer Contract

How a renderer backend (MonoGame, Godot/nova, anything else) plugs into V12.
One rule governs everything: **V12 determines placement; renderers mirror the
tree and set locations.** No renderer computes placement from components.

## Hard boundary

A renderer may consume:

- `RenderPacket`, `FrameSnapshot`, `UIFrame`, `ViewportInfo`
- `IRenderer`, `IUIRenderer`, `IViewportRegistry`, `IDirectTransformProvider`
- opaque `IRenderTarget.NativeHandle`

A renderer must not:

- reference `IWorldElement`, `World`, `GameRoot`, `ComponentBase`, or
  `V12.Components.*`
- call `GetComponent<T>()` on live elements
- hold live components in packet DTOs
- mutate live component state from the render thread

Ids are the only identity: `long ElementId`, `long ViewportId`.

however, marking your renderer as Direct Drive compatibled allow's you to access that information.

See `BUILDING_A_RENDERER.md` for an implementation walkthrough.
or `BUILDING_A_DIRECT_DRIVE_RENDERER.md` for that implementations walkthrough.

## Feeds

Two feeds carry the same data in different shapes. Either is sufficient.

- **Packet** (`RenderPacket`, dirty-driven): `Meshes` (`ElementId`, mesh,
  viewport) + `Nodes` (every element: id, parent id, element-local matrix).
- **Snapshot** (`FrameSnapshot`, compared per field): entries carry
  `ElementId`, `ParentId`, `LocalTransform`, world `Transform`, mesh
  points/indices/dims, material fields. Unchanged entries are skipped.

## Placement

- Element-local matrix comes from one definition
  (`ElementPlacement.ElementLocalMatrix`): the `TransformComponent` when
  present, else `LocalTransform × ScaleComponent`.
- World matrices compose inner-to-outer, row-vector order:
  `world = local × parentWorld` (`Element.WorldTransform` is the authority).
- **Meshes carry no transforms.** A mesh's world is its element's world;
  dims live in the vertices (or the backend's mesh resource for
  `MeshComponent` shapes). There is no mesh-local matrix anywhere.
- Mirror the parent/child tree from V12 data (ids + locals), exactly like the
  Godot backend parents nodes by `ParentId`. Missing parent → identity
  fallback, never a crash. Reparent when links change.

## Mesh data

- Vertices are absolute (dims baked in). New array object = new geometry —
  never mutate in place; backends key GPU caches on array identity.
- Indices must be in range; malformed data is skipped, not crashed on.
- `MeshComponent` shapes may additionally map to backend-native primitives
  (sized from dims); everything else renders as a triangle soup.

## Materials

Use the material fields on `MeshDraw`: albedo/emissive colors, metallic,
roughness, blend mode, texture paths, and the `Unlit` (skip scene lighting) and
`NoDepthTest` (draw on top) flags. No material → backend default.

Do not read `MaterialComponent` from the world. The engine bakes material data
into the packet so the renderer stays world-agnostic.

## Transient editor objects

Two opt-in markers cover editor plumbing:

- `NonPersistentComponent`: renders and picks normally, but is excluded from
  saves and network sync. Applies to descendants.
- `EditorOnlyComponent`: renders and behaves normally, but hierarchy panels and
  scene selection skip it. Applies to descendants.

Use `NonPersistentComponent` for gizmo trees and other throwaway visuals. Use
`EditorOnlyComponent` for editor infrastructure such as the editor camera and
the game-view router.

## Picking

- Pick against the same matrices you draw with, or picking and pixels diverge.
- Gizmo handles are grabbable, never selectable: V12 hit-tests them
  analytically (`TransformGizmo.TryHitHandles`, fat invisible shapes — never
  the visual mesh) before world picking, and world picking skips handle parts.

## Viewports

- A viewport is a camera rendering into a texture. The renderer creates one
  render target per viewport element id, routes `ViewportId` renderables into
  it, and publishes the rect and camera matrices through `IViewportRegistry`.
- Meshes carry a viewport id; groups render per viewport (0 = main screen).
  Hierarchy never crosses a viewport boundary.
- The renderer does not pick, orbit, or draw gizmos. `V12.Editor` builds a ray
  from the published camera, calls `SceneQuery`, and turns the hit into a
  selection or gizmo drag.
- There is no overlay draw path. Editor visuals are transient meshes through
  the normal pipeline (unlit, no-depth).

## Implementing a renderer

1. Create the engine and register `"IRenderer"` before the runtime starts.
2. Implement `ApplySnapshot(FrameSnapshot)` or `QueueItems(RenderPacket)`.
3. Mirror the hierarchy from `Nodes` (`ElementId`, `ParentId`, `Local`/`World`).
4. Build materials from `MeshDraw` fields, not from `MaterialComponent`.
5. Implement `IUIRenderer.ApplyUI(UIFrame)` if the renderer draws UI.
6. Implement `IViewportRegistry` (required): viewport rects and camera matrices for tools and the editor.
7. Keep packets immutable after capture and apply them on the render thread.
8. Never walk the world tree from the render thread.

For Paper, register `V12.Paper.PaperRegistration.Register(root)` in the host and
let `V12.Paper.ProwlPaperRenderer` produce the `PaperFrame` your backend uploads.
Do not auto-register Paper from the engine.

## Per-backend notes

- **MonoGame:** rebuild the id→world mirror on every packet; one shared
  effect, per-mesh lighting/depth/material state; GPU cache keyed on array
  identity + color.
- **Godot:** one node per entry (`MeshInstance3D`, lights, cameras…),
  reparented by `ParentId` with local transforms; `ArrayMesh` rebuilt when
  the points/indices references change; snapshots interpolate unless the node
  is editor-driven (gizmo drags, orbit camera write transforms directly).
