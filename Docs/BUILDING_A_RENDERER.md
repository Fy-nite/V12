# Building a Renderer

> **Current contract:** new renderers should target `V12.Rendering.IRenderer`
> (`Submit(RenderFrameLease)`, resource handles, `IRendererLifecycle`). That is
> what `vcsdk new renderer` scaffolds, and NovaV2 is the reference
> implementation. This guide describes the legacy packet-based
> `V12.Core.Interfaces.Renderer.IRenderer` that MonoGame and Rockstar still use;
> it is kept for those backends until they are ported.

A V12 renderer turns engine packets into backend objects. It does not own the world, does not decide placement, and does not read components from the render thread.

This guide uses the Godot/Nova backend as the reference, but the same shape applies to MonoGame, OpenTK, a headless recorder, or a test renderer.

## The rule

V12 decides placement. The renderer mirrors the tree and draws it.

Allowed inputs:

- `RenderPacket` (`Meshes`, `Nodes`, `Sprites`, `Texts`, `Lights`, `Cameras`, `Environment`)
- `FrameSnapshot` (`RenderableSnapshot`, `AudioSourceSnapshot`)
- `UIFrame` (`UINode` tree)
- `ViewportInfo` (`IViewportRegistry`)
- `IRenderTarget` and its opaque `NativeHandle`

Forbidden:

- `IWorldElement`, `World`, `GameRoot`, `ComponentBase`, `V12.Components.*`
- `GetComponent<T>()` on a live element
- Reading or writing element `LocalTransform`, `MaterialComponent`, `ToggleComponent`, and friends
- Holding live components in renderer state or packet DTOs

Use `long` ids. `ElementId` and `ViewportId` are the only identity a renderer needs.

## Registration

A host creates the engine, then registers the renderer before `root.Initialize()` finishes:

```csharp
var root = new GameRoot();
var renderer = new MyRenderer();

root.Registry.Register("IRenderer", renderer);
root.Registry.Register("IUIRenderer", renderer);          // optional
root.Registry.Register("IViewportRegistry", renderer);    // required, needed for editor/tools
```

If the renderer also hosts the UI, either implement `IUIRenderer` or register the Paper renderer:

```csharp
V12.Paper.PaperRegistration.Register(root);
```

Registration is explicit. The engine does not choose a UI stack for the host.

## What to implement

```csharp
public sealed class MyRenderer : IRenderer, IUIRenderer, IViewportRegistry
{
    public int GetFPS() => 60;

    public RendererInfo GetAllInfo() => new();

    public void QueueItems(RenderPacket packet)
    {
        // Keep the latest packet, or merge it. Do not walk the world.
        _pendingPacket = packet;
    }

    public void RemoveItems(RenderPacket packet) { }

    public int GetScreenWidth() => _width;
    public int GetScreenHeight() => _height;

    public bool LockMouse { get; set; }

    public void step()
    {
        // Called by the host frame loop. Apply pending work on the render thread.
        ApplyPendingPacket();
    }

    public void ApplySnapshot(FrameSnapshot snapshot)
    {
        // Optional alternative to QueueItems. Entries are keyed by ElementId.
        ApplySnapshotEntries(snapshot);
    }

    public void ApplyUI(UIFrame frame)
    {
        // Reconcile controls by UINode.Id. Never read V12 components here.
        ApplyUiNodes(frame);
    }

    public IReadOnlyList<ViewportInfo> Viewports => _viewports;
}
```

Keep `step()` cheap. Do the work on the render thread, not inside `QueueItems` if the queue is filled from a worker thread.

## Packet fields

`RenderPacket.Nodes` mirrors the hierarchy:

- `ElementId`
- `ParentId`
- `Local` (element-local matrix)
- `World` (precomposed, when `HasWorld` is true)

`RenderPacket.Meshes` carries:

- `ElementId`, `ViewportId`, `Active`, `World`
- material fields (`Albedo`, `Metallic`, `Roughness`, `BlendMode`, `Unlit`, `NoDepthTest`, texture paths)
- `Mesh` (`IMeshRenderable`)

`MeshDraw.Mesh` is still a live interface in the current engine. Treat it as geometry only: read `MeshPoints`, `Indices`, and `Material`. Do not cast it to a component type, do not read an owner, and do not mutate it. The target is to replace it with raw arrays in the packet.

`RenderPacket.Lights`, `Cameras`, and `Environment` are already in the packet. Do not call `FindElementsWithComponent<...>` in the renderer.

## Placement

- Mirror the tree from `Nodes` (`ElementId` + `ParentId`).
- Apply `Local` to the backend node, or `World` if `HasWorld` and your backend needs absolute transforms.
- Meshes carry no transforms. A mesh's world is its element's world.
- Missing parent: fall back to identity, never crash.

## Materials

Use the material fields on `MeshDraw`. Do not read `MaterialComponent` from the world.

A backend that needs a native material builds it from the packet fields and caches it. Texture paths are in the packet.

## UI

`IUIRenderer.ApplyUI` receives a `UIFrame` keyed by `UINode.Id`.

- Reconcile create/update/destroy by id.
- Do not walk the world to find widgets.
- Do not read or write `ToggleComponent.IsOn`, `SliderComponent.Value`, or `ButtonComponent.OnClick`. Use the `UINode` callbacks and values.

For Paper, the correct shape is:

```
V12.Paper.ProwlPaperRenderer : IUIRenderer
  Prowl/Quill builds a PaperFrame
  backend uploads PaperFrame vertex/index/draw data
```

Godot's `GodotPaperCanvasRenderer` and MonoGame's `MonogamePaperFrameRenderer` are the reference implementations.

## Editor interaction

Editor interaction is not renderer code. A renderer may implement the seams:

- `IViewportRegistry` - viewport rects and camera matrices
- `IInputSource` - per-frame input snapshot
- `IDirectTransformProvider` - latency-free transform writes by element id

The viewport service and gizmo belong in `V12.Editor`. A game renderer should not implement editor orbit, picking, or gizmo logic.

## Picking

Use the engine's `SceneQuery` on the game side. Do not read back meshes or RIDs from the renderer.

The renderer exposes viewport and camera data through `IViewportRegistry`; the engine casts the ray against its own geometry.

## Threading

A typical host has two threads:

- Worker thread: `GameRoot.Update`, packet/snapshot capture.
- Render thread: `ApplySnapshot`, `QueueItems` drain, `ApplyUI`, backend uploads.

Treat packets and snapshots as immutable after capture. Do not touch backend objects from the worker thread.

## Checklist

- [ ] Renderer registers `"IRenderer"` before the runtime starts.
- [ ] `ApplySnapshot` / `QueueItems` never walk the world tree.
- [ ] No `V12.Components.*` import in the renderer project.
- [ ] No `GetComponent<T>()` or `WorldTransform` reads.
- [ ] Ids are the only cross-thread identity.
- [ ] `ApplyUI` reconciles by `UINode.Id`.
- [ ] Editor behavior is not implemented in the renderer.
- [ ] `dotnet test V12.Architecture.Tests` passes.
