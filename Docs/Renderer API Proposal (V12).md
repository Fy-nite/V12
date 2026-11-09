# V12 Renderer Contract — revised v1

## Status and intent

This is a revision of the original renderer proposal, not a replacement of its architecture. V12 owns simulation, scene hierarchy resolution, input, audio, UI layout and CPU-side resource descriptions. A renderer is a backend-independent sink that owns GPU realization and turns submitted draw data into pixels.

The v1 milestone is **Nova plus the legacy adapter**. MonoGame and Rockstar may remain on the old feeds until their ports. Slang, custom post-processing passes, instancing, skinning, particles, decals and video remain deferred. v1 uses the built-in `Lit`, `Unlit`, `Ui`, and `Text` shader family.

The contract assembly must be backend-free: plain data and `System.Numerics`; no Godot, MonoGame, Prowl, world-component, or Studio types.

## Normative decisions

1. **One declarative frame.** V12 resolves world matrices, produces meshes for sprites/text/SVG/UI, and submits a flat frame. Renderers never read live world components or walk V12 hierarchy.
2. **Frame ownership is executable, not documentary.** `Submit` takes a `RenderFrameLease`; its pooled backing storage is unavailable to V12 until the lease is returned. The renderer may retain the latest accepted lease for presentation.
3. **Materials are shared; draw overrides are not.** `MaterialDesc`, texture bindings and material defaults are shared state. Object tint, UV transform and other object-specific values live in immutable `DrawOverrides` referenced by a `DrawItem`.
4. **Every handle is scoped to a renderer device epoch and has a generation.** A destroyed/reused handle cannot name a new resource, and a handle cannot silently survive renderer replacement or device recreation.
5. **CPU source is retained by V12.** The renderer owns only derived GPU state. On recovery V12 explicitly recreates its resources under the new epoch; the renderer must not expect an implicit GPU resurrection.
6. **Presentation/windowing and rendering are distinct.** A renderer can expose presentation surfaces, but input/window events travel through a separate host/input bridge, not through `IRenderer` or `RenderFrame`. Headless has neither requirement.
7. **Projection matrix is authoritative.** Raw projection fields are descriptive and must agree with it; backends draw with `Projection`, never reconstruct a replacement projection.
8. **`ActiveViewportIds` is removed.** `RenderFrame.Views` is the sole authoritative list of active views. This eliminates two sources of truth.

## Lifetime, threading, and frame pooling

`RenderFrameLease` is a class, intentionally—not a copyable struct. It owns immutable arrays rented from a V12-owned pool. The engine fills it while it is `Building`, seals it, then submits it. `Submit` either accepts the lease or throws/reports a contract failure; it may not retain an unaccepted lease.

The renderer calls `Release()` exactly once when it no longer reads the frame. In normal presentation it retains one completed frame, then releases its predecessor when a new completed frame replaces it. The lease uses an idempotence guard only to diagnose double release; double release remains a renderer bug. A bounded pool (normally at least simulation + render + cached/present slots) gives back-pressure rather than allowing unbounded allocation.

`ReadOnlyMemory<T>` alone is not an immutability guarantee. V12 must not expose the rented arrays after sealing, and the lease's public API exposes only `ReadOnlyMemory<T>`. Debug builds should poison/check returned buffers and assert illegal state transitions.

```csharp
public interface IRenderer : IDisposable
{
    void Submit(RenderFrameLease frame); // ownership transfers only on successful return
    RendererCapabilities Capabilities { get; }

    // Resource calls: valid only for this DeviceEpoch.
    MeshHandle CreateMesh(in MeshData data);
    void UpdateMesh(MeshHandle mesh, in MeshData data);
    void DestroyMesh(MeshHandle mesh);
    TextureHandle CreateTexture(in TextureData data);
    void UpdateTexture(TextureHandle texture, in TextureData data);
    void DestroyTexture(TextureHandle texture);
    ShaderHandle CreateShader(in ShaderProgram program);
    void DestroyShader(ShaderHandle shader);
    MaterialHandle CreateMaterial(ShaderHandle shader, in MaterialDesc desc);
    void UpdateMaterial(MaterialHandle material, in MaterialDesc desc);
    void DestroyMaterial(MaterialHandle material);
    RenderTargetHandle CreateRenderTarget(in RenderTargetDesc desc);
    void ResizeRenderTarget(RenderTargetHandle target, int width, int height);
    void DestroyRenderTarget(RenderTargetHandle target);
    TextureHandle GetRenderTargetTexture(RenderTargetHandle target);
    ValueTask<ReadbackResult> RequestReadbackAsync(RenderTargetHandle? target,
                                                   CancellationToken cancellationToken = default);
}

public sealed class RenderFrameLease : IDisposable
{
    public ulong Sequence { get; }
    public DeviceEpoch Epoch { get; }
    public RenderFrame Frame { get; } // sealed, read-only view of pooled storage
    public void Release();            // renderer → pool; equivalent to Dispose
    public void Dispose() => Release();
}
```

**Validation required:** whether submit and resource calls occur on one render thread, and whether `Submit` may block until a queue slot is available. Define this before Nova implementation; the answer controls pool size, synchronization and shutdown semantics.

## Handles, epochs, and resource dependencies

```csharp
public readonly record struct DeviceEpoch(ulong Value);
public readonly record struct ResourceHandle(uint Id, uint Generation, DeviceEpoch Epoch);
public readonly record struct MeshHandle(ResourceHandle Value);
public readonly record struct TextureHandle(ResourceHandle Value);
public readonly record struct ShaderHandle(ResourceHandle Value);
public readonly record struct MaterialHandle(ResourceHandle Value);
public readonly record struct RenderTargetHandle(ResourceHandle Value);
```

The backend validates type, epoch, id and generation for every resource reference. In release builds an invalid reference is reported through diagnostics and the affected operation is rejected/skipped deterministically—never dereferenced or rebound to another object. Creation after device recovery returns handles in the new epoch. The engine must rebuild all dependent CPU-side mappings before it emits a frame in that epoch.

Dependencies are explicit: a material references a shader and textures; a render-target texture is owned by its render target. Destroying a shader with live materials, a texture with live material references, or a target while its texture is bound is rejected in v1. V12 destroys dependents first. This avoids implicit cascading destruction and makes stale references visible. A backend may defer physical GPU deletion until frames that use the resource are retired, but logical destruction takes effect immediately.

## Device loss and renderer replacement

Device lifecycle is a control-plane contract, not a frame field:

```csharp
public interface IRendererLifecycle
{
    event Action<RendererDeviceEvent> DeviceEvent;
    DeviceEpoch CurrentEpoch { get; }
}
public enum RendererDeviceEventKind { Lost, Recovering, Ready, PermanentFailure }
public readonly record struct RendererDeviceEvent(RendererDeviceEventKind Kind, DeviceEpoch Epoch,
                                                  string? Detail = null);
```

On `Lost`, the renderer stops accepting new work (or explicitly returns `Unavailable`—choose one), releases/invalidates GPU realization, and preserves only the last presentable image if the platform can do so. On `Ready`, it exposes a new `DeviceEpoch`; V12 recreates shaders, meshes, textures, materials and targets from retained CPU data in dependency order, then resumes frames. No old handle is valid. Renderer replacement uses the same rehydrate path.

**Validation required:** platform-specific loss behavior (especially Godot host, mobile, XR compositor, and swapchain recreation) and whether an image can safely remain visible during recovery.

## Resources: extensible descriptions

Keep v1's simple streams, but make layout and image memory explicit so new formats do not require a breaking interface change.

```csharp
public readonly record struct VertexAttribute(VertexSemantic Semantic, VertexFormat Format,
                                              int Offset, int Stream);
public readonly record struct VertexStream(int Stride, ReadOnlyMemory<byte> Data);
public sealed record MeshData(
    ReadOnlyMemory<VertexStream> VertexStreams,
    ReadOnlyMemory<VertexAttribute> Layout,
    IndexFormat IndexFormat, ReadOnlyMemory<byte> Indices,
    ReadOnlyMemory<SubmeshRange> Submeshes);

public sealed record TextureData(
    TextureDimension Dimension, int Width, int Height, int DepthOrLayers, int MipCount,
    TextureFormat Format, TextureColorSpace ColorSpace, TextureUsage Usage,
    ReadOnlyMemory<TextureSubresource> Subresources);
public readonly record struct TextureSubresource(int Mip, int Layer, int RowPitch,
                                                 int SlicePitch, ReadOnlyMemory<byte> Bytes);
```

Provide helpers for the common V12 position/normal/UV0/color layout. This reserves room for tangents, UV1+, skin streams, multiple submeshes, compressed textures, cubemaps/arrays and sRGB without claiming their implementation in v1.

## Shared material state and per-draw overrides

`MaterialDesc` owns shader selection, blending/cull/depth/shadow state and stable default parameter/texture values. It is changed rarely with `UpdateMaterial`. Its parameters use reflection indices later, and fixed built-in tables in v1.

`DrawOverrides` is frame-owned immutable data; it never mutates a `MaterialHandle`:

```csharp
public readonly record struct DrawOverride(int ParameterIndex, MaterialValue Value);
public readonly record struct DrawOverrides(ReadOnlyMemory<DrawOverride> Values);
public struct DrawItem
{
    public long ElementId, ViewportId;
    public MeshHandle Mesh;
    public MaterialHandle Material;
    public Matrix4x4 World;
    public ClipRect Clip;
    public uint SortKey, LayerMask;
    public bool Active;
    public int OverridesIndex; // -1 = none; index into RenderFrame.DrawOverrides
}
public struct RenderFrame
{
    public ReadOnlyMemory<RenderView> Views;
    public ReadOnlyMemory<DrawItem> Items;
    public ReadOnlyMemory<DrawOverrides> DrawOverrides;
    public ReadOnlyMemory<LightItem> Lights;
    public EnvironmentData Environment;
    public float Time, DeltaTime;
}
```

Tint, UV offset/scale, animation values and object-specific emission therefore work correctly when two items share one material. Texture override is allowed only if `MaterialValue` explicitly supports `TextureHandle`; otherwise a distinct material is required. **Validation required:** benchmark override packing and batching in Nova before choosing whether overrides become an indexed table, an inline fixed small buffer, or both.

## Views, routing, and presentation

`RenderView.Id` remains a V12 routing key and is independent from `Target`. An item draws if its `ViewportId == view.Id` and the layer masks overlap. Two eyes or stacked cameras can share an Id. `RenderFrame.Views` alone declares active views.

```csharp
public struct RenderView
{
    public long Id, CameraElementId;
    public Matrix4x4 View;
    public Matrix4x4 Projection; // authoritative
    public ProjectionMetadata ProjectionMetadata; // diagnostic/culling metadata; must match matrix
    public ViewportRect Viewport;
    public ClearSettings Clear;
    public RenderTargetHandle? Target;
    public uint LayerMask;
    public StereoEye Eye;
}
```

`ProjectionMetadata` contains kind, near/far and perspective FOV-Y or orthographic size. It is useful for diagnostics and backend optimizations, but any mismatch is a V12 frame-construction error; the matrix wins.

`IRenderer` has no `WindowEvent`, `GetViewport`, `GetViewports`, or input responsibility. A separate optional `IPresentationHost` owns swapchains/surfaces, resize/focus/close events and reports `ViewportInfo` to V12's input/UI systems. A headless implementation provides no presentation host. A renderer may be packaged with one (Nova likely will), but they remain separate contracts.

## Environment parity

V1 does not accept the placeholder `EffectSettings`. Before freezing the contract, mirror every existing `RenderPacket.Environment` field into typed V12-owned structs, preserving units/defaults/meaning:

```csharp
public sealed record EnvironmentData(
    AmbientSettings Ambient, SkySettings Sky, FogSettings Fog, TonemapSettings Tonemap,
    BloomSettings Bloom, AutoExposureSettings AutoExposure, SsaoSettings Ssao,
    SsilSettings Ssil, SsgiSettings Ssgi, SsrSettings Ssr, DepthOfFieldSettings Dof);
```

Each effect struct must contain the real current parameters (not merely `Enabled` and `Intensity`). This is an API-generation requirement: create a field-by-field mapping table and parity tests from the actual current type before selecting the final names. `RendererCapabilities` declares supported features; unsupported effects are reported diagnostically and ignored only according to the documented fallback. `EnvironmentData` expresses visual intent, not backend-native knobs.

**Validation required:** inventory the current environment source and confirm every post-effect parameter, enum, default and serialization behavior. This document deliberately does not invent those V12 fields.

## Last-frame cache and XR

The cached object is a completed `RenderFrameLease`, not an arbitrary set of mutable V12 buffers. A desktop renderer may continue presenting it until replaced. A deliberate empty submitted frame is distinct from no submission.

XR cached-frame behavior is narrower than the original promise: the renderer/platform owns session state, tracking sampling, compositor submission, late-latching and any supported reprojection. The cached V12 frame supplies the last scene poses and draw data; a presentation backend may use a newer predicted head pose only through its supported compositor path. It must not silently rewrite arbitrary world matrices, claim that it prevents nausea, or promise reprojection where unavailable.

Add an optional `IXrPresentation` beside—not inside—`IRenderer`, with explicit capability flags such as `LateLatch`, `DepthReprojection`, and `CachedFramePresentation`. Define the ownership/timestamp coordinate conventions for head pose, predicted display time, eye views and compositor image lifetime before implementation.

**Validation required:** Nova's XR host and target runtimes must demonstrate the cached-frame path. Until then, support is "re-present last compositor image/last submitted stereo frame where available," not a guarantee of fresh-pose rendering.

## Migration sequence

1. Create the backend-free contract assembly and field-by-field environment parity inventory.
2. Implement generation/epoch handle validation, `RenderFrameLease` pool, and a null renderer that records frames and checks dependencies/lifetime.
3. Add tests: stale generation/epoch rejection, material override isolation, shader dependency rejection, pool back-pressure/release, device recovery rehydrate, projection authority, and empty-frame versus no-submit behavior.
4. Convert engine resource caches and sprite/text/SVG/UI generators to create/update resources and produce ordinary `DrawItem`s. Preserve CPU font/layout ownership.
5. Build the legacy adapter from the same scene sources; it emits the old `RenderPacket`/`FrameSnapshot`/UI feeds for unported backends without reconstructing them from the new frame.
6. Port Nova to `Submit(RenderFrameLease)`, the presentation-host boundary, targets and built-in shaders. Validate environment mapping, resize, headless, and XR claims on actual supported hosts. This completes v1.
7. Bridge Paper directly to draw items; retain `IUIRenderer` only as the temporary adapter. Extract `V12.Paper` and enable boundary tests when its separate migration lands.
8. Port MonoGame then Rockstar; delete legacy feeds only after equivalence and capability tests pass.
9. Add Slang/reflection behind the existing shader/material handles after the stable frame/resource contract is proven.

## V1 acceptance gates

- Null/headless simulation works without a window/GPU; CPU layout and generated mesh tests are deterministic.
- Nova never reads live `IWorldElement`, components, or backend types across the boundary.
- An old handle cannot address a newly allocated or recovered resource.
- Shared materials render different per-draw tint/UV values in the same frame.
- Resource destruction is dependency-safe and physical retirement respects retained frames.
- Device recovery recreates all resources from V12 CPU data under a new epoch.
- Every currently supported environment field has an explicit typed destination and parity test.
- `Views` is the only active-viewport authority; projection matrix wins on disagreement.
- The legacy adapter remains until Nova works on the v1 contract; Slang does not block this milestone.
