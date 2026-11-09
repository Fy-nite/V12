# V12 Boundary Refactor Plan

Status: draft, ready to execute

Scope: `D:\git\V12-Monorepo` (super-repo + submodules) and `D:\git\V12Studio`

## Decisions locked

- **Prowl stays.** Do not rewrite the UI stack. Keep Prowl as the house UI implementation.
- **`V12.Paper` becomes its own repo** (submodule), not a project inside `V12-Engine` and not a folder move into engine core.
- **Origami components do not move into `V12-Engine`.** They are editor-grade UI model, so they move to `V12.Paper` (or a neutral `V12.UI` model project) instead of bloating core.
- **`V12.Basic` is safe to change.**
- **The viewport Godot coupling is cursed and needs fixing.** Fix it in the Viewport quarantine phase.
- **Architecture guardrail is an xUnit project.**
- **Delete `hosts/v-12-studio-4.8`.** It predates engine Tracy support and is dead.
- **Studio UI and build stay as-is.** Only minimal `csproj` reference changes are allowed while the viewport work stabilizes.

## Goal

Restore explicit boundaries between the engine, the optional UI stack, the editor, the renderer, and the authoring tool, without destabilizing V12Studio or `vcsdk`.

The engine must be renderer-agnostic and library-agnostic:

- No Godot, MonoGame, Nova, Prowl, or V12Studio references in `V12-Engine`.
- No editor interaction implementation in `V12-Engine`.
- Renderers only consume packet/snapshot DTOs.

## Target architecture

```
V12-Engine (Finite.V12)
  Contracts:
    Core/Interfaces/Renderer/IRenderer.cs
    Core/Interfaces/Renderer/IUIRenderer.cs
    Core/Interfaces/Renderer/RenderPackets.cs
    Core/Interfaces/Renderer/RendererInfo.cs
    Core/Rendering/FrameSnapshot.cs
    Core/Rendering/UIFrame.cs
    Core/Rendering/IViewportRegistry.cs
    Core/Rendering/IDirectTransformProvider.cs
    Core/Rendering/IRenderTarget.cs
    Core/Rendering/IRenderTargetFactory.cs
    Core/Rendering/SceneQuery.cs
    Core/Rendering/ViewportMath.cs
  Model:
    Components/*
    Components/UI/*
    WorldML/*

V12.Paper (new repo, Finite.V12.Paper)
  Prowl.Paper / Prowl.Quill / Prowl.Origami
  Engine-side implementation:
    Core/UI/Paper/*
    UI/PaperUIProvider.cs
    PaperInspectorAdapter.cs
    Origami components (CodeEditor, DockSpace, DockPanel)
    ProwlUIProvider / PaperUIProvider / IProwlHost
  Renderer bridges:
    Nova.Shared/Paper/GodotPaperCanvasRenderer.cs
    V12.Monogame/Pulsar.Core/Paper/*

V12.Editor (new repo or V12-Engine/V12.Editor project)
  Editor semantics:
    ViewportInteractionService
    IViewportInteraction
    GizmoMode
    GizmoEntitySync
    GizmoHandleComponent
    ViewportFileDialog
    IInspectorHost
    TransformGizmo interaction

V12.Basic (Finite.V12.Basic)
  Gameplay components and systems only. No editor-grade UI.

Nova.Shared (Godot renderer)
  ApplySnapshot(FrameSnapshot) / ApplyUI(UIFrame) only.
  Godot bridge: GodotPaperCanvasRenderer, PaperInput, CanvasShader.

V12Studio
  Studio.Core + Studio.Editor. No Prowl implementation. References V12.Paper / V12.Editor.
```

## Renderer law

A renderer may only consume:

- `RenderPacket` / `NodePlacement` / `MeshDraw` / `CameraData` / `LightData` / `EnvironmentData`
- `FrameSnapshot` / `RenderableSnapshot` / `AudioSourceSnapshot`
- `UIFrame` / `UINode`
- `ViewportInfo`
- `IRenderTarget` via an opaque `object NativeHandle`
- `IRenderer`, `IUIRenderer`, `IViewportRegistry`, `IDirectTransformProvider` signatures

Everything is keyed by `long ElementId` / `long ViewportId`.

A renderer must not:

- Walk `IWorldElement`, `ComponentBase`, `GameRoot`, `World`, or `V12.Components.*`.
- Call `GetComponent<T>()` on live engine objects.
- Mutate live component state (`LocalTransform`, `ToggleComponent.IsOn`, `SliderComponent.Value`).
- Hold live component references in packet DTOs (`IMeshRenderable`, `TreeComponent`, `List<object> Targets`, live `Action` closures).
- Pass `IWorldElement` across the renderer boundary. Use element ids.

Existing references for this law:

- `Docs/RendererContract.md`
- `V12-Engine/Core/Interfaces/Renderer/RenderPackets.cs`
- `V12-Engine/Core/Interfaces/Renderer/IUIRenderer.cs`
- `V12-Engine/Core/Rendering/IViewportRegistry.cs`

## Phases

### Phase 0 - Preparation and safe cleanup

No behavior change. No Studio UI changes.

- [x] **0a. Create `V12.Paper` repo scaffold**
  - New repo: `V12.Paper/V12.Paper.csproj`, `net10.0`, `Finite.V12.Paper`.
  - References `Finite.V12` plus `Prowl.Paper` / `Prowl.Quill` / `Prowl.Origami` `3.9.3`.
  - Registered in `V12-Monorepo/.gitmodules` (`https://github.com/fy-nite/V12.Paper`; push the repo before others clone).
  - Added `V12PaperProject` to `Directory.Build.props` and `Directory.Build.targets`.
  - Added to `V12-Monorepo.slnx`.
  - Added the submodule `Directory.Build.props` shim used by `V12.Basic` and `Nova`.
  - `V12Studio/Directory.Build.props` inherits `$(V12PaperProject)` through the monorepo root props.
  - [ ] Teach `V12-Sdk/Purr.cs` and `pack-feed.ps1` to resolve and publish `V12.Paper` (pending).

- [x] **0b. Remove the dead Godot 4.8 host**
  - Deleted `V12Studio/hosts/v-12-studio-4.8/`.
  - Deleted `V12Studio/hosts/V12Studio.Godot/V12Studio.Godot.csproj.old`.
  - Deleted `V12Studio/hosts/V12Studio.Godot/V12Studio.Godot.csproj2`.
  - Deleted `V12Studio/hosts/V12Studio.Godot/V12Studio.Godot.sln`.
  - Verified `Studio.Core` still builds.

- [x] **0c. Trash and dead code**
  - Deleted `V12-Engine/Core/UI/poo.txt`.
  - Deleted `V12-Engine/nul`.
  - Deleted fully commented dead files:
    - `Nova/Nova.Shared/PortalBinding.cs`
    - `Nova/Nova.Shared/WorldInspector.cs`
    - `Nova/Nova.Shared/NetworkInspector.cs`
    - `V12Studio/src/Studio.Core/DefaultDashboard.cs`
  - Deleted the commented dashboard block in `V12-Engine/Core/GameRoot.cs`.
  - Deleted the unregistered legacy control components and their `.uid` files:
    - `ButtonControlComponent.cs`
    - `CheckboxControlComponent.cs`
    - `SliderControlComponent.cs`
    - `TextInputControlComponent.cs`
    - `UIPanelComponent.cs`
  - Kept `UILabelComponent.cs`: WorldML and the Nova SDK exporter still emit it.
  - Also removed the `V12Studio` / `V12StudioPak` hardcode from `V12.Basic/libs/V12.Bindings/CSharpScriptCompiler.cs`.
  - [ ] Optional: remove the 149 `.uid` files in `V12-Engine` and 17 in `V12.Basic` if those repos are never opened as Godot projects.

Verification:

- `dotnet build V12-Monorepo.slnx`
- `dotnet build V12Studio.slnx`
- Launch `hosts/V12Studio.Godot` with and without `--GodotUI`.
- `vcsdk doctor`

### Phase 1 - Architecture guardrail (xUnit)

Create `V12-Monorepo/V12.Architecture.Tests/V12.Architecture.Tests.csproj`.

This is the first xUnit project in the repo. Existing "tests" are `Exe` harnesses.

- [x] Project settings: `net10.0`, `IsPackable=false`, `IsTestProject=true`.
- [x] Packages:
  - `Microsoft.NET.Test.Sdk`
  - `xunit`
  - `xunit.runner.visualstudio` with `PrivateAssets=all`
  - `NetArchTest.Rules`
- [x] Project references:
  - `$(V12EngineProject)`
  - `$(V12BasicProject)`
  - `$(V12PakProject)`
  - `$(V12PaperProject)`
  - `$(V12BindingsProject)`
  - `Contract.Runtime` comes transitively through the engine.
- [x] Rules currently passing:
  - `Engine_ShouldNotDependOn_RendererOrToolAssemblies`
  - `Basic_ShouldNotDependOn_RendererOrToolAssemblies`
  - `Pak_ShouldNotDependOn_RendererOrToolAssemblies`
  - `Bindings_ShouldNotDependOn_AuthoringTools`
  - `Engine_ProjectFile_ShouldNotReference_RenderersOrTools`
  - `Bindings_Source_ShouldNotHardcode_AuthoringToolNames`
- [x] Editor boundary rules now active:
  - `Editor_ShouldNotDependOn_RendererOrToolAssemblies`
  - `Engine_ShouldNotDependOn_EditorAssembly`
  - `Nova_ShouldNotReference_EditorAssembly`
- [ ] Rules to enable as phases land (currently `Skip`):
  - `Engine_ShouldNotDependOn_Prowl` (Phase 3)
  - `UIFrame_ShouldNotHold_LiveComponents` (packet cleanup)
  - `RenderPacket_ShouldBePureData` (packet cleanup)
  - `Renderers_ShouldOnlyUse_PacketTypes` (Phase 4)
- [ ] Add a root CI workflow or extend an existing one to run `dotnet test` for the architecture project.
- Test command: `dotnet test V12.Architecture.Tests/V12.Architecture.Tests.csproj`

### Phase 2 - Viewport quarantine

Fix the cursed viewport coupling without redesigning the Studio shell.

- [x] Create `V12.Editor` as its own repo and submodule (`Finite.V12.Editor`).
- [x] Move editor implementation out of `V12-Engine` into `V12.Editor`:
  - `ViewportInteractionService.cs`
  - `IViewportInteraction.cs`
  - `GizmoMode.cs`
  - `GizmoEntitySync.cs`
  - `GizmoHandleComponent.cs`
  - `TransformGizmo.cs` (pure geometry)
- [x] Keep in `V12-Engine`:
  - `Core/Rendering/IViewportRegistry.cs`
  - `Core/Rendering/IDirectTransformProvider.cs`
  - `Core/Rendering/SceneQuery.cs`
  - `Core/Rendering/ViewportMath.cs`
- [x] Change `IDirectTransformProvider` to use `long elementId` instead of `IWorldElement`.
- [x] Split `Nova.Shared/WorldCanvasSystem.cs`:
  - Removed editor orbit / picking / gizmo input.
  - Deleted `Nova.Shared/SceneGizmo.cs`.
  - Kept the render-only `SubViewport` path for the Godot Control fallback.
- [x] Update `V12Studio/hosts/V12Studio.Godot/StudioHost.cs` to construct `V12.Editor.ViewportInteractionService`.
- [x] Removed the two-gizmo path by deleting `SceneGizmo`; only the Studio service spawns a gizmo.
- [x] `ProwlPaperRenderer` now renders `UIWidgetKind.Viewport` nodes: draws the host texture, reports the laid-out rect, and releases viewports that disappear.
- [x] Add `GodotViewportSurfaceProvider`: one camera -> texture surface per viewport element, created/resized/released on the main thread.
- [x] `V12Runtime` exposes `EnsureViewportSurface` / `ReleaseViewportSurface`; `StudioHost` uses them instead of creating a `SubViewport` by hand.
- [ ] Move `IInspectorHost` and `PaperInspectorAdapter` to their final home (`V12.Paper` for the adapter, then decide on the inspector contract).
- [x] Give the game view its own `Game` dock tab (driven by the `EditorViewport` `ViewportComponent` id through the provider).
- [x] Removed the duplicate placeholder scene tab; the editor now has one 3D `Game` view plus the `Play` controls tab.
- [x] Size the game-view `SubViewport` from the Paper pane rect (fixes the stretched / low-res viewport).
- [x] Editor camera is current only in edit mode; a play-world camera wins in Play mode when the scene has one.
- [x] Inspector edits now call `MarkRenderDirty` explicitly, so visual changes refresh even when the editor scene world is not ticking.
- [x] Add a non-ticking world regression test for light capture (`NonTickingWorldTests`).
- [ ] Verify the edit/play visual fix in the running editor (the engine capture path is covered; the remaining risk is backend node reparenting).

### Phase 3 - V12.Paper extraction

- [ ] Remove Prowl package references from `V12-Engine/V12.csproj`.
- [ ] Move out of `V12-Engine`:
  - `Core/UI/Paper/*` (7 files)
  - `UI/PaperUIProvider.cs`
  - Any remaining Prowl-specific helper code
  - The `Prowl.Vector` special case in `WorldML/WorldMLSerializer.cs` (replace with engine-only color serialization)
- [ ] Move out of `V12.Basic`:
  - `Components/Origami/CodeEditorComponent.cs`
  - `Components/Origami/DockSpaceComponent.cs`
  - `UI/ProwlUIProvider.cs`
  - `UI/PaperUIProvider.cs`
  - `UI/ProwlHost.cs`
  - `UI/ProwlContextMenu.cs`
  - `UI/ComponentIcons.cs`
- [ ] Replace `GameRoot`'s string/reflection block for `"CodeEditorComponent"`, `"DockSpaceComponent"`, `"DockPanelComponent"` with a typed widget-adapter registry populated by `V12.Paper`.
- [ ] Remove `GameRoot`'s auto-registration of `ProwlPaperRenderer` as the default `IUIRenderer`.
- [ ] Add `V12.Paper.PaperRegistration.Register(root)` and call it from:
  - `Nova.Shared/V12Runtime.cs`
  - `V12.Monogame/Pulsar.Core/V12Game.cs`
  - `V12.Monogame/Pulsar.Core/Paper/MonogamePaperUIRenderer.cs`
  - Studio host, through its existing bootstrap
- [ ] Make hidden dependencies explicit:
  - `V12.Basic` gets an explicit `V12.Paper` / Prowl reference instead of relying on transitive references from the engine.
  - `V12.Monogame/Pulsar.Core` gets an explicit `V12.Paper` reference.
  - `V12Studio/src/Studio.Editor` gets an explicit `V12.Paper` reference.

### Phase 4 - Nova.Shared renderer-only hardening

- [x] Fixed snapshot interpolation freezing continuous motion: a new keyframe now retargets the active glide instead of restarting it at `t=0` every frame.
- [x] Fixed `V12-Samples/HelloDotnet` to rotate at degrees/second (`RY` is radians; the sample was adding degrees).
- [x] `RenderPacket.MeshDraw` now carries the element's `MaterialComponent` (albedo, metallic, roughness, unlit/depth flags, UVs, albedo/normal/metallic/roughness/emission paths). Packet-only renderers (Rockstar) and packet-driven texture paths (MonoGame) now see materials.
- [x] Packet mesh submission is one `MeshDraw` per element (a `MeshRenderer` and its wrapped mesh no longer double-submit geometry).
- [x] MonoGame texture paths resolve through any `IAssetResolver` (including `V12PakAssetResolver`) and handle `v12://`, `res://`, and relative paths.
- [x] MonoGame draws packet materials directly instead of reading `MaterialComponent` from the world.
- [x] MonoGame Paper bridge updated to the simplified `PaperFrame`/`PaperMesh` API (brush/scissor defaults, texture/font-atlas lookup by id).
- [x] `ProwlPaperRenderer` publishes laid-out viewport rects into `PaperFrame.Viewports`, so MonoGame can size and fill viewport render targets.
- [x] MonoGame wires viewport textures both ways: the 3D pass fills `PaperFrame.Viewports`, and the Paper pass resolves those render targets for viewport UI nodes.

- [ ] Delete the duplicate Godot-coupled `Nova.Shared/Paper/ProwlUIProvider.cs`.
- [ ] Keep `V12.Basic.UI.ProwlUIProvider` plus `IProwlHost`.
- [ ] Make Nova implement `IProwlHost`.
- [ ] Keep only the Godot bridge in `Nova.Shared/Paper/`:
  - `GodotPaperCanvasRenderer.cs`
  - `PaperInput.cs`
  - `CanvasShader.cs`
- [ ] Remove the `WorldCanvasSystem` widget graph walk in favor of `ProwlPaperRenderer.ApplyUI(UIFrame)`.
- [ ] Narrow `Nova.Shared/Renderer.cs` to `ApplySnapshot`-only.
- [ ] Move snapshot synthesis back to `GameRoot.CaptureFrame` on the worker thread.
- [ ] Remove `MonogameV12Renderer` live-world fallbacks (`FindElementsWithComponent<EnvironmentComponent/ILightRenderable/ICameraRenderable>`) once the packet carries environment and camera data.
- [ ] Remove `SceneGizmo` once `V12.Editor` owns the path.

### Phase 5 - Documentation

- [x] New `V12-Monorepo/Docs/ARCHITECTURE.md`:
  - Repo and submodule map.
  - Ownership diagram: `V12-Engine`, `V12.Basic`, `V12.Paper`, `V12.Editor`, `V12.Pak`, `Nova.Shared`, `V12Studio`.
  - Data flow: world capture, packet/snapshot, renderer apply, UI frame.
- [x] Moved `RendererContract.md` into `Docs/` and added a normative "Building a renderer" section:
  - Allowed packet types.
  - `long` id rule.
  - `IDirectTransformProvider` shape.
  - Explicit list of forbidden imports.
- [x] New `V12-Monorepo/Docs/BUILDING_A_RENDERER.md`:
  - Step-by-step `IRenderer`, `IUIRenderer`, `IViewportRegistry` wiring.
  - Threading model: worker thread vs render thread.
  - Minimal renderer skeleton.
- [x] New `V12-Monorepo/Docs/WORKING_ON_V12.md`:
  - Submodule checkout.
  - `dotnet build` vs `vcsdk build -r nova`.
  - Pack feed and local publishing.
  - Where to add components and systems.
  - How the architecture tests gate changes.

### Phase 6 - Follow-up hygiene

- [ ] Set `IsPackable=false` on all existing test projects.
- [ ] Add the test projects to an appropriate solution or CI.
- [ ] Rename `V12.Core.Core.Interfaces` to `V12.Core.Interfaces`.
- [ ] Rename `V12.Core.Systems` usage inside `V12.Basic` to `V12.Basic.Systems`.
- [ ] Clean `V12Studio` stray artifacts: `bin`, `obj`, `feed`, `tmp`, `test-vcsdk` (only if untracked and regenerable).
- [ ] Remove engine `.uid` files if unused.

## Verification per phase

- `dotnet build V12-Monorepo.slnx`
- `dotnet build V12Studio.slnx`
- `dotnet test V12.Architecture.Tests`
- `vcsdk build -r nova`
- Launch `hosts/V12Studio.Godot` with and without `--GodotUI`
- Round-trip `Nova/V12.SampleGame/DemoScene.xml` and `V12Studio/src/Studio.Editor/UI/V12StudioUI.worldml`
- Confirm no new `ViewportInteractionService` copy lands back in `V12-Engine` (the guardrail test catches this)

## Risks and mitigations

- **vcsdk / pack breakage.** New `V12.Paper` artifact and explicit references change packaging. Mitigate by landing the repo scaffolding first, publishing to the local feed, and smoke-testing the `blank-game` template.
- **Submodule coordination.** Moves span `V12-Engine`, `V12.Basic`, `Nova`, new `V12.Paper`, and the super-project. Land in lockstep, one commit per repo plus a submodule bump.
- **Studio freeze.** All Studio changes are reference-only until the viewport work is stable. No `Studio.Core` / `Studio.Editor` / `StudioHost` UI logic changes in this plan.
- **Transitive dependency surprises.** `V12.Basic` and `V12.Monogame` currently compile against Prowl only through `V12-Engine`. Add explicit references before removing the engine reference.

## Open questions

1. Should `V12.Editor` be its own repo (symmetric with `V12.Paper`) or a second project `V12-Engine/V12.Editor/` to avoid a third submodule?
2. Package names: `Finite.V12.Paper` and `Finite.V12.Editor`, matching `Finite.V12` / `Finite.V12.Basic`?
3. Should the architecture tests also validate `V12Studio` assemblies (requires a `V12MonorepoRoot -> ../V12Studio` path), or only the monorepo assemblies?
4. For the legacy `*ControlComponent` set, delete outright or keep `[Obsolete]` for one release?

## Appendix: key file references

Engine boundary leaks:

- `V12-Engine/Core/UI/ViewportInteractionService.cs` (self-declares editor code)
- `V12-Engine/Core/UI/IViewportInteraction.cs`
- `V12-Engine/Core/UI/GizmoMode.cs`
- `V12-Engine/Core/UI/GizmoEntitySync.cs`
- `V12-Engine/Components/GizmoHandleComponent.cs`
- `V12-Engine/UI/IInspectorHost.cs`
- `V12-Engine/Core/UI/Paper/*`
- `V12-Engine/UI/PaperUIProvider.cs`
- `V12-Engine/V12.csproj` Prowl package references
- `V12-Engine/Core/GameRoot.cs` Paper auto-registration and Origami reflection block
- `V12-Engine/Core/Rendering/IDirectTransformProvider.cs` live `IWorldElement`
- `V12-Engine/Core/Rendering/UIFrame.cs` live `TreeComponent` / `object Targets` / `Action`
- `V12-Engine/Core/Interfaces/Renderer/RenderPackets.cs` live `IMeshRenderable`

Renderer leaks:

- `Nova/Nova.Shared/Renderer.cs` live component graph walk in `step()`
- `Nova/Nova.Shared/WorldCanvasSystem.cs` widget graph walk and editor orbit/gizmo
- `Nova/Nova.Shared/SceneGizmo.cs` live element mutation
- `Nova/Nova.Shared/GodotViewportAdapter.cs` `IWorldElement` keyed transform dictionary
- `Nova/Nova.Shared/Paper/ProwlUIProvider.cs` duplicate Godot-coupled provider
- `V12.Monogame/Pulsar.Core/MonogameV12Renderer.cs` live environment / camera lookups

Lower-layer leaks:

- `V12.Basic/libs/V12.Bindings/CSharpScriptCompiler.cs` hardcodes `V12Studio` / `V12StudioPak`

Stale content:

- `V12Studio/hosts/v-12-studio-4.8/`
- `V12Studio/hosts/V12Studio.Godot/V12Studio.Godot.csproj.old`
- `V12Studio/hosts/V12Studio.Godot/V12Studio.Godot.csproj2`
- `V12Studio/hosts/V12Studio.Godot/V12Studio.Godot.sln`
- `V12-Engine/Core/UI/poo.txt`
- `V12-Engine/nul`
