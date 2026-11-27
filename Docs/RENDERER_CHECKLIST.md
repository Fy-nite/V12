# Renderer Checklist

A renderer is the backend half of V12. It receives an immutable frame and turns
it into device work. The engine owns simulation, placement, UI layout, input
and picking. The renderer owns devices, resources, draw submission and
presentation.

`RendererContract.md` is the normative contract. This page turns it into a list
you can work through, test against, and sign off. `BUILDING_A_RENDERER.md` is
the older packet-path walkthrough, and `BUILDING_A_DIRECT_DRIVE_RENDERER.md`
covers the opt-in exception to the boundary.

## The five hard requirements

A renderer is V12-compatible only when it:

1. implements `V12.Rendering.IRenderer` and consumes `RenderFrameLease`;
2. validates every resource handle by kind, generation and device epoch;
3. draws every view and draw item the frame gives it, with the placement the
   frame gives it;
4. releases every accepted frame lease exactly once;
5. never reads the world, any component, or any live element state.

Everything below expands those five. Tier 1 is mandatory. Tier 2 is needed to
render a normal V12 scene, and may degrade when a feature is genuinely
unsupported. Tier 3 is optional and advertised through capabilities.

## Which contract

| Path | Interface | Status |
|---|---|---|
| Frame contract | `V12.Rendering.IRenderer` (`Submit(RenderFrameLease)`, resource handles, `IRendererLifecycle`) | Required for new renderers |
| Legacy packet contract | `V12.Core.Interfaces.Renderer.IRenderer` (`RenderPacket`, `FrameSnapshot`, `UIFrame`) | Kept for MonoGame and Rockstar only; do not start new work here |

- Reference implementation: `NovaV2/NovaV2.Shared/NovaV2Renderer.cs`.
- Headless reference and test double: `V12.Rendering.NullRenderer`.
- Scaffold: `vcsdk new renderer --name MyRenderer --backend monogame|godot|none`.
- The host converts engine captures into the frame contract with
  `RenderFrameProducer`. The renderer never sees `FrameSnapshot`,
  `RenderPacket` or a `GameRoot`.

## Tier 1: the contract

### Project and repo boundary

- [ ] The renderer does not need a reference to the whole engine. It references
      `Finite.V12.Rendering` and its backend SDK. If it implements the editor
      viewport seam it also references the engine's `V12.Core.Rendering`
      contract types.
- [ ] The renderer does not reference `V12.Components.*`, `V12.Paper`,
      `V12.Editor`, `V12.Basic`, `V12.Physics` or `V12.Gravity`.
- [ ] The only engine types it touches, if any, are the viewport seam
      (`V12.Core.Rendering.IViewportRegistry` and `ViewportInfo`).
- [ ] The renderer does not register itself under the registry key
      `"IRenderer"`. That key selects `GameRoot`'s legacy packet path and casts
      to the legacy interface. A frame-contract renderer is passed directly
      into `RenderFrameProducer`.
- [ ] No `GetComponent<T>()`, no `IWorldElement`, no `World`, no `GameRoot`
      inside the renderer assembly.

### Renderer object

- [ ] Implements `IRenderer : IDisposable`.
- [ ] `Capabilities` returns an honest, stable `RendererCapabilities`. Never
      advertise a feature you cannot draw.
- [ ] `Submit(RenderFrameLease)` accepts a frame, realizes it, and returns.
- [ ] Every `Create*`, `Update*` and `Destroy*` call is implemented: mesh,
      texture, shader, material, render target.
- [ ] `GetRenderTargetTexture` and `ResizeRenderTarget` are implemented without
      replacing the target handle.
- [ ] `RequestReadbackAsync` returns a `ReadbackResult`. It never hangs.
- [ ] `Dispose` is idempotent, releases a frame it still holds, and destroys
      the resources it owns.
- [ ] Implements `IRendererLifecycle` with `CurrentEpoch` and `DeviceEvent`, so
      `RendererResourceCache` can drop handles from an old epoch. Required for
      any backend whose device can be lost or recreated.
- [ ] Backend-specific presentation (for example NovaV2's
      `PresentRenderTarget`) is an extra method on the renderer or host. It is
      not part of `IRenderer`.

### Frame ownership

- [ ] A frame whose `Epoch` does not match `CurrentEpoch` is rejected with
      `RendererContractException`.
- [ ] A rejected frame is never released by the renderer. The caller still owns
      it and releases it.
- [ ] An accepted frame is released exactly once. Drawing or building backend
      state and releasing before `Submit` returns is the simplest model.
- [ ] `lease.Frame` is never read after `Release`. It throws
      `ObjectDisposedException`.
- [ ] No `ReadOnlyMemory<T>` from a frame is kept past `Release`. Frame arrays
      return to `ArrayPool` and are reused.
- [ ] If a frame is deferred to another thread or frame, the renderer owns a
      copy and owns the release.
- [ ] `Dispose` releases the frame the renderer still holds.

### Handles and device epoch

- [ ] Every handle is validated before use: resource kind, epoch, id and
      generation all match.
- [ ] A stale, unknown or cross-epoch handle throws `RendererContractException`
      and leaves device state untouched.
- [ ] Handle ids are never reused without a new generation. A monotonic id
      counter is enough.
- [ ] Device loss marks the device unavailable, rejects all new work, and raises
      `RendererDeviceEventKind.Lost`.
- [ ] Recovery raises `Recovering`, increments the epoch, clears resource
      tables, then raises `Ready` with the new epoch.
- [ ] No resource is created, updated or destroyed while the device is lost.
- [ ] A resource is only valid for the epoch it was created in.

### Resource dependencies

- [ ] `DestroyShader` is rejected while a material still references it.
- [ ] `DestroyTexture` is rejected while a material references it, including a
      texture bound through a `MaterialValue`.
- [ ] `CreateMaterial` and `UpdateMaterial` reject unknown shader or texture
      handles.
- [ ] A render target owns its texture. `GetRenderTargetTexture` returns it,
      resizing is done with `ResizeRenderTarget`, and destroying or updating
      the texture directly is rejected.
- [ ] `DestroyRenderTarget` is rejected while its texture is still referenced
      by a material.
- [ ] Destroy order is dependencies first: materials, then targets and
      textures, then meshes and shaders.
- [ ] Disposal does not throw for handles that are already gone or from an old
      epoch.

### Views

- [ ] Every view in `RenderFrame.Views` is drawn. A frame with no views draws
      nothing; the renderer does not invent a default view.
- [ ] Views are ordered by ascending `RenderView.Priority`, with a stable order
      for equal priorities.
- [ ] `RenderView.View` and `Projection` are authoritative. They are not
      rebuilt from FOV, a camera component, or the window aspect.
      `ProjectionMetadata` is informational.
- [ ] `Viewport` is in pixels and already reflects the view's aspect. A zero or
      negative rect draws nothing and does not crash.
- [ ] `Clear` is applied per view: `Color` clears to `Clear.Color`, `Sky` uses
      the environment sky, `DepthOnly` clears depth only, `None` draws over the
      existing contents.
- [ ] A non-null `Target` renders into that render target. A null `Target`
      renders into the presentation surface.
- [ ] `WorldId` is a filter. The same world may be drawn by several views with
      different cameras.
- [ ] `LayerMask`, `ViewId` and `Eye` filter what the view draws.

### Draw items

- [ ] Items with `Active == false` are skipped.
- [ ] `DrawItem.World` is applied exactly as given. There is no mesh-local
      transform and no parenting step in the renderer.
- [ ] Items are filtered by `WorldId`, `LayerMask` and the optional `ViewId`
      (`0` matches every view in the world).
- [ ] Items are ordered within a view by `SortKey`, keeping submission order
      for equal keys.
- [ ] `Mesh` and `Material` are bound. An invalid handle rejects the frame.
- [ ] `Clip` is applied as a scissor when `Enabled`. `ClipRect.None` means no
      clip.
- [ ] `OverridesIndex` indexes `RenderFrame.DrawOverrides`, `-1` means none,
      and an out-of-range index rejects the frame.
- [ ] `SkinIndex` indexes `RenderFrame.Skins`, and `-1` means the mesh is not
      skinned.
- [ ] Draw overrides affect that draw only. The shared material is never
      mutated.

### Geometry

- [ ] Layout comes from `MeshData.Layout`, `VertexStreams` and each
      `VertexAttribute` stream and offset. No assumed interleaved layout or
      fixed attribute order.
- [ ] Both `IndexFormat.UInt16` and `IndexFormat.UInt32` are honoured.
- [ ] Triangles are counter-clockwise when seen from the front. Convert to the
      backend convention once.
- [ ] Vertex positions are absolute. Dimensions are baked into the vertices;
      no element scale is applied by the renderer.
- [ ] `Submeshes` ranges are honoured. `SubmeshRange.MaterialSlot` is the mesh's
      own slot. V12 v1 emits one submesh and one material per draw item.
- [ ] `MeshData.Empty`, zero vertices or zero indices draw nothing and do not
      crash.
- [ ] Out-of-range indices or a submesh past the index buffer skip that mesh
      with a diagnostic. Malformed content does not take the process down.
- [ ] A new `MeshData` array means new geometry. Arrays are not mutated, and
      any cache is keyed on identity or version.

### Materials and shaders

- [ ] `BuiltInShader.Lit`, `Unlit`, `Ui` and `Text` are supported. `None` means
      a custom program.
- [ ] Built-in parameter indices and texture slots come from
      `BuiltInShaders.cs`, not from names guessed per backend.
- [ ] `MaterialDesc` is applied: blend, cull, depth test and write, lit, shadow
      flags, parameters and texture bindings.
- [ ] Texture colour space is honoured: `Srgb` for colour, `Linear` for data.
- [ ] Both `MaterialTextureBinding` slots and textures bound through a
      `MaterialValue` are resolved.
- [ ] Custom `ShaderProgram` sources are supported for the languages the
      backend compiles. A language it cannot compile is a diagnostic, not a
      crash.
- [ ] There is no shader update in v1. A changed program destroys and recreates
      the shader, as `RendererResourceCache` already does.

### Lights, environment, skins, particles

- [ ] Inactive lights and particles are skipped.
- [ ] Lights are drawn as directional, point and spot, using `World`, `Color`,
      `Intensity`, `Range`, `InnerCone`, `OuterCone`, `CastShadows` and
      `LayerMask`.
- [ ] `EnvironmentData` is applied: ambient, sky and skybox, fog, tonemap,
      bloom, auto exposure, SSAO/SSIL/SSGI/SSR, depth of field and shadows.
- [ ] An environment feature the backend does not support is skipped, not
      rejected. The frame stays valid.
- [ ] Skins resolve `BoneStart` and `BoneCount` into `BoneTransforms`, with
      `BoneParents` (`-1` is a root), `InverseBindPoses` and the skeleton
      `World`.
- [ ] Particles may be ignored by a backend that cannot run them. If they are
      drawn, every field is a simulation parameter and `World` is the emitter
      placement.
- [ ] `Time` and `DeltaTime` come from the frame, not from a clock the renderer
      owns.
- [ ] Blend modes are applied, with opaque draws before alpha, additive and
      multiply draws within a view.

### UI

- [ ] There is no UI system to implement. Screen-space UI is the
      `RenderViewIds.ScreenUi` view (`-1000`), orthographic, on
      `RenderLayers.Ui`, with no camera.
- [ ] UI arrives as ordinary `DrawItem`s and is drawn like any other mesh.
- [ ] `UiTargetPolicy.PresentSurface` draws the UI view into the presentation
      surface. `OwnTarget` draws it into a target, and `RenderFrame.PresentTarget`
      names that target.
- [ ] When `PresentTarget` is non-null, the host presents that target's
      texture. The renderer does not recreate, rename or destroy it.
- [ ] World-space UI is ordinary scene meshes with `ViewId = 0` and the
      canvas's `WorldId`. There is no canvas texture, canvas view or UI quad.
- [ ] No font loading, text shaping, rounded-corner drawing, widget layout,
      hit-testing or focus logic in the renderer.
- [ ] The UI implementation is host-supplied through `IUiSource`
      (`PaperUiSource` for Paper). The renderer never references `V12.Paper`.

### Threading and lifetime

- [ ] All device access happens on the render thread that owns resource
      creation. The renderer documents that thread and asserts it in debug.
- [ ] Frames are immutable. Frame memory is not mutated and the renderer does
      not call back into the engine from `Submit`.
- [ ] `Submit` does not block on the GPU, a fence, a readback or the next
      frame.
- [ ] `RequestReadbackAsync` either completes or reports `Success = false` with
      a `Detail`.
- [ ] No static mutable state and no finalizer-driven resource release.

### Failure policy

- [ ] Contract violations throw `RendererContractException`: stale handle,
      wrong epoch, unknown resource, broken dependency, frame from another
      epoch, out-of-range table index.
- [ ] Content problems degrade: empty mesh, bad indices, unsupported shader
      language, unsupported environment effect. Skip, record a diagnostic, keep
      the frame.
- [ ] Device loss is a lifecycle event, not an exception storm.
- [ ] `Dispose` after loss, after recovery, and twice is safe.
- [ ] A diagnostics list (`IReadOnlyList<string>`) is exposed for tests and
      tooling. `NullRenderer` and NovaV2 both do this.

## Tier 2: normal scene parity

These are required to render a current V12 scene. If the backend cannot support
one, it must degrade without rejecting the frame, and its capability flag must
say so.

- [ ] Shadows: directional, point and spot casters, plus the global
      `ShadowSettings` distance, bias, normal bias and opacity.
- [ ] Fog: linear, exponential and exponential squared. Volumetric fog is
      Tier 3.
- [ ] Sky: colour, energy, and a texture skybox with blur and rotation.
- [ ] Tonemap modes: linear, Reinhard, filmic, ACES and AgX, with white point,
      exposure, brightness, contrast and saturation.
- [ ] Bloom modes: mix, additive and screen, with intensity and thresholds.
- [ ] Auto exposure, SSAO, SSIL, SSGI, SSR and depth of field. Advertise only
      the ones that actually run.
- [ ] Render targets with colour and depth, transparent targets, mipmaps and
      resize in place.
- [ ] Readback of the presentation surface or a named target.
- [ ] GPU skinning with up to four bone influences per vertex
      (`MeshDataFactory.CreateSkinned`).
- [ ] Clip rects, `NoDepthTest`, `Unlit`, additive and multiply blending.

## Tier 3: optional and capability-gated

- [ ] Particles: point and cone emission, lifetime, speed range, gravity,
      tint, one-shot and emitting state.
- [ ] Instancing: `RendererFeature.Instancing` is reserved. There is no
      instanced draw payload yet, so do not advertise it until there is one.
- [ ] Stereo and XR: `RendererFeature.Stereo`, `RenderView.Eye`, and
      `IXrPresentation.Features` (`LateLatch`, `DepthReprojection`,
      `CachedFramePresentation`). The platform owns the session, tracking and
      compositor.
- [ ] Presentation host: `IPresentationHost`, `ViewportInfo` and
      `WindowEvent` (resize, focus, close).
- [ ] Editor seam: `IViewportRegistry` with one `ViewportInfo` per view, and
      `IDirectTransformProvider` for latency-free transform writes by element
      id.
- [ ] Direct drive: the opt-in exception that lets a renderer read live world
      and component state. It is a separate contract with its own rules, not a
      shortcut for a normal renderer.

### Capability flags

Advertise a flag only when the matching frame data is honoured.

| Flag | Frame data |
|---|---|
| `Shadows` | `LightItem.CastShadows`, `EnvironmentData.Shadows` |
| `Fog` | `EnvironmentData.Fog` |
| `VolumetricFog` | `FogSettings.Volumetric*` |
| `Sky` | `EnvironmentData.Sky` |
| `Stereo` | `RenderView.Eye` |
| `RenderTargets` | `RenderView.Target`, `RenderFrame.PresentTarget` |
| `Readback` | `RequestReadbackAsync` |
| `Skeletons` | `SkinItem`, `RenderFrame.BoneTransforms` |
| `Instancing` | Reserved; no payload today |
| `Tonemap` | `EnvironmentData.Tonemap` |
| `Bloom` | `EnvironmentData.Bloom` |
| `AutoExposure` | `EnvironmentData.AutoExposure` |
| `Ssao` | `EnvironmentData.Ssao` |
| `Ssil` | `EnvironmentData.Ssil` |
| `Ssgi` | `EnvironmentData.Ssgi` |
| `Ssr` | `EnvironmentData.Ssr` |
| `Dof` | `EnvironmentData.Dof` |
| `Particles` | `RenderFrame.Particles` |

## Host wiring

The host owns the engine loop, input, audio, content and UI source. It gives the
renderer a producer and submits frames. A minimal shape:

```csharp
var root = new GameRoot();
var renderer = new MyRenderer();          // IRenderer + IRendererLifecycle
var input = root.Registry.Get<InputService>()
            ?? throw new InvalidOperationException("InputService is not registered.");

// Optional, but required for editor tools and viewport queries.
root.Registry.RegisterOrReplace("IViewportRegistry", renderer);

var producer = new RenderFrameProducer(
    renderer,
    width,
    height,
    textureLoader: MyTextureLoader.Load,
    input: input,
    uiSource: new PaperUiSource());       // only when the host uses Paper

root.Initialize();

// Each frame, on the thread that owns renderer resources:
root.V12Tick(deltaTime);

var snapshot = root.CaptureFrame();
var environment = CaptureEnvironment(root);   // host-owned
var capturedUi = root.CaptureUI();

// Paper hosts lay out capturedUi with PaperUiSystem first:
// uiSystem.Apply(capturedUi);
UiFrameContext? uiContext = null;
// uiContext = new UiFrameContext(capturedUi, uiSystem.LastFrame, width, height);

if (!producer.TryBuild(snapshot, environment, time, deltaTime, uiContext, out var lease)
    || lease is null)
{
    // Pool back-pressure. Keep the dirty flag and retry next tick.
    return;
}

var presentTarget = lease.Frame.PresentTarget;
try
{
    renderer.Submit(lease);
}
catch
{
    // Ownership only transfers on a successful return.
    lease.Release();
    throw;
}

// Present the named target when it changes. The host decides how.
if (presentTarget is { } present)
    PresentNamedTarget(present);          // host-specific: blit, composite or hand to a panel
```

- [ ] The renderer is created after `GameRoot` and before the first frame.
- [ ] `RenderFrameProducer` is created with the renderer, viewport size,
      texture loader, `InputService` and optional `IUiSource`.
- [ ] `CaptureFrame` and `CaptureUI` run on the host thread; `TryBuild` and
      `Submit` run on the render thread that owns resources.
- [ ] `TryBuild` returning `false` skips the frame instead of blocking or
      throwing.
- [ ] `Submit` failure releases the lease and rethrows.
- [ ] `RenderFrame.PresentTarget` is presented when it changes.
- [ ] `producer.Resize(width, height)` runs on window resize, followed by a
      forced capture.
- [ ] The producer is disposed before the renderer, and the renderer before the
      host's device and window.

## Acceptance

### Unit tests

- [ ] Empty frame: one view, no items, submits and releases.
- [ ] Full frame: two views, overlapping worlds, layered items, a light, a draw
      override and a skinned mesh.
- [ ] Pool back-pressure: `TryRent` fails until the accepted lease is released.
- [ ] A frame from an old epoch is rejected, and the caller can still release
      it.
- [ ] A stale handle and a stale generation are rejected with
      `RendererContractException`.
- [ ] A shader, texture or target texture still in use is not destroyed.
- [ ] `ResizeRenderTarget` keeps the handle and invalidates the old contents.
- [ ] A frame with 10k draw items completes without allocation storms.
- [ ] `Dispose` with a held frame neither leaks nor double-releases.
- [ ] Malformed content (empty mesh, bad index, unsupported shader language)
      does not throw.
- [ ] Device loss rejects work, then recovery accepts new-epoch resources.

### Integration

- [ ] A NovaV2 sample runs flat with `--xr-mode off`.
- [ ] Window resize works, including a minimised or zero-size viewport.
- [ ] Device loss and recovery are exercised at least once.
- [ ] The editor viewport, picking and gizmo work, or the renderer honestly
      reports that it does not provide the editor seam.
- [ ] `dotnet test V12.Architecture.Tests` passes.
- [ ] No `V12.Components.*` or live-world access in the renderer assembly
      (`V12.Architecture.Tests` and a project-reference review both catch it).

## Definition of done

Copy this into the PR description and tick it honestly.

- [ ] Targets `V12.Rendering.IRenderer`, not the legacy packet interface.
- [ ] Implements `IRendererLifecycle`.
- [ ] Validates handles by kind, epoch, id and generation.
- [ ] Rejects stale, unknown and cross-epoch handles with
      `RendererContractException`.
- [ ] Releases every accepted lease exactly once and never releases a rejected
      one.
- [ ] Does not read a lease after release or keep pooled memory.
- [ ] Draws every view, in `Priority` order, with the frame's `View` and
      `Projection`.
- [ ] Filters items by `WorldId`, `LayerMask` and `ViewId`.
- [ ] Applies `DrawItem.World` without recomputing placement.
- [ ] Applies `Clip`, `SortKey`, `OverridesIndex` and `SkinIndex`.
- [ ] Honours both index formats, CCW winding and submesh ranges.
- [ ] Applies materials from `MaterialDesc` and built-in parameter indices.
- [ ] Does not mutate shared materials for draw overrides.
- [ ] Applies the environment and lights it claims in `Capabilities`.
- [ ] Handles `PresentTarget` and both `UiTargetPolicy` modes.
- [ ] Never implements UI layout, text, hit-testing, focus or picking.
- [ ] Never reads the world, components or live element state.
- [ ] Disposal is idempotent and dependency-ordered.
- [ ] Diagnostics are exposed.
- [ ] Claims in `Capabilities` match what the tests actually draw.

## Common rejection reasons

| Symptom | Likely cause | Fix |
|---|---|---|
| Crashes or garbage after a device reset | Handles from the old epoch still used | Bump the epoch, clear tables, validate on every call |
| Blank or doubled UI | UI view drawn to the window instead of its target | Honour `RenderView.Target` and `PresentTarget` |
| Pool exhaustion or double-release | Releasing a frame that `Submit` rejected | Release only after a successful submit |
| Flickering or corrupt geometry | Reading pooled frame memory after `Release` | Copy anything you defer |
| Meshes vanish after a hot reload | Cache keyed on array length or contents | Key on array identity plus version |
| Editor picking misses | Viewport matrices missing or stale | Publish `IViewportRegistry` each frame |
| Lighting ignores editor layers | Layer masks dropped | Filter items and lights by `LayerMask` |
| GPU memory grows over time | Unused cached resources never destroyed | Destroy cache entries by version or frame age |
| Renderer "works" but editor is unusable | Gizmos or picking implemented in the renderer | Keep them in `V12.Editor`, expose the seams |
