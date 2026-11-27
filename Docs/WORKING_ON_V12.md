# Working on V12

This guide covers the day-to-day workflow for changing V12, building it, and keeping the repo boundaries intact.

## Prerequisites

- .NET SDK 10
- Godot 4.7 with C# support when working on `Nova` or `V12Studio`
- `vcsdk` for packaging and running gamepaks
- The sibling checkout layout if you build `V12Studio`:

```
D:\git\V12-Monorepo
D:\git\V12Studio
```

`V12Studio/Directory.Build.props` points at `..\V12-Monorepo` by default. Override `V12MonorepoRoot` or the individual project paths with `-p:` if your layout differs.

## Checkout

Clone the super-repo with submodules:

```powershell
git clone --recurse-submodules https://github.com/fy-nite/V12-Monorepo.git
```

If the submodules are already present but stale:

```powershell
git -C D:\git\V12-Monorepo submodule update --init --recursive
```

A change to an engine, Basic, Nova or Paper file is a change in that submodule. Commit it in the submodule first, then bump the gitlink in the super-repo.

## Build

From the monorepo root:

```powershell
dotnet build V12-Monorepo.slnx
```

Run the boundary tests:

```powershell
dotnet test V12.Architecture.Tests/V12.Architecture.Tests.csproj
```

Build the editor through the SDK:

```powershell
vcsdk build -r nova
```

`vcsdk build -r nova` is the supported editor build. It packages `V12Studio` into `V12Studio.v12pak` and places the exe and pak in `V12Studio/bin`.

If you add a new package (for example `V12.Paper`), publish it to the local feed:

```powershell
# Stable version, from Directory.Build.props V12Version
pwsh tools/pack-purr.ps1

# Unique local build: <version>-dev.<UTC timestamp>
pwsh tools/pack-purr.ps1 -Dev

# Explicit version
pwsh tools/pack-purr.ps1 -Version 1.0.1
```

`vcsdk sync --dev` does the same from the SDK.

NuGet caches packages by id + version and will not re-extract a rebuilt package
with an unchanged version. Both pack paths delete the matching
`~/.nuget/packages/<id>/<version>` entries after packing, so a plain restore
picks up the new assemblies. If you use `-Dev`, reference a floating prerelease
range in the consuming project (for example `Version="1.0.0-*"`) or the exact
stamped version; a `1.0.0` reference still resolves to the stable package.

## Repo boundaries

Keep these rules in mind before adding a reference:

- `V12-Engine` must not reference Godot, MonoGame, Nova, Prowl or V12Studio.
- `V12-Basic` must not reference Godot, MonoGame, Nova or V12Studio.
- `V12.Paper` is the only project that should reference Prowl.
- Renderers consume packet DTOs, not live engine components.

`V12.Architecture.Tests` checks the assembly and project graph. If you need to cross a boundary, add a contract to the engine instead of a direct reference.

## Adding a component

Components live in `V12-Engine/Components` (engine-level) or the game code (content-level).

- Derive from `ComponentBase`.
- Add `[ComponentEntry(category, label, ...)]` if it should appear in editors.
- Call `MarkDirty()` after state changes that should render or sync.
- Implement `BuildInspector(IInspector)` if it needs a custom inspector.
- Keep editor-only fields out of serialized state, or mark the element with `EditorOnlyComponent` / `NonPersistentComponent` as appropriate.

Engine UI components live in `V12-Engine/Components/UI`. They are UI model, not Prowl code. `GameRoot` maps them to `UIFrame` nodes; a renderer draws the frame.

## Adding a system or gameplay helper

Gameplay systems live in `V12.Basic/Systems` or in a game assembly, not in `V12-Engine`.

A system that needs a per-frame tick implements `IGameService` and registers with the `Registry`. Use `V12Profiler.BeginZone` for profiling zones, matching the existing systems.

## Adding Paper UI

Paper-specific code belongs in `V12.Paper`, not the engine:

- Paper renderer and canvas bridge
- Paper/Origami widget providers
- Origami components
- Paper palette and inspector helpers

A host opts in by calling `V12.Paper.PaperRegistration.Register(root)`. Do not make the engine auto-register Paper.

## Renderer changes

Read `RendererContract.md` before touching `Nova` or `V12.Monogame`.

- Consume `FrameSnapshot`, `RenderPacket`, `UIFrame`, and `ViewportInfo`.
- Never walk the world tree from the render thread.
- Never read or write live components from the renderer.
- Use `SceneQuery` for picking, not mesh readback.
- Key GPU and node caches on array identity and element ids.

Installed renderer slots (`~/.purr/renderers/<id>/<rid>`) supply the V12
assemblies that gamepaks resolve at load time. After adding or changing an
engine package, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/sync-v12.ps1
```

It packs every auto-discovered root-level `Finite.V12.*` module through
`vcsdk sync` and copies any missing package assembly into every renderer slot,
so a new package needs no SDK or renderer-list edit. `-DryRun` previews,
`-Force` overwrites, and `-Renderer <id>` limits the install. Re-export the
renderer with `tools/install-novav2-renderer.ps1` only when its own host
assemblies changed.

Verify with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-v12-deps.ps1 -Renderer novav2 -IncludePackages
```

`vcsdk sync --dev` updates the NuGet feed; it does not refresh an installed
renderer slot.

## Testing

The repo has two test styles:

- `V12.Architecture.Tests` uses xUnit and NetArchTest to protect boundaries.
- Older harnesses (`V12.Bindings.Tests`, `V12.Pak.Tests`, Contract tests) are console `Exe` programs that print PASS/FAIL and return an exit code.

Run the architecture tests before sending a change. If a boundary test fails, fix the reference or update the contract; do not add an exception without a documented reason.

## Hot reload loop

For a game project under `--game`, run NovaV2 with `--hot-reload` to compile C#
and rebuild the world on save:

```powershell
.\scripts\run-host.ps1 -UseGame -HotReload -Xr off
```

This is a scene reload for local development; see `Docs/HOT_RELOAD_NOVAV2.md`
for the collectible assembly, gamepak lifecycle, and current limits.

## Common pitfalls

- Adding a project from two different paths creates two assembly identities. Reference the same path everywhere.
- The Paper renderer/UI implementation lives in `V12.Paper`; backends that draw Paper frames or use world-canvas surfaces must reference `V12.Paper`. `V12-Engine` still references Prowl only for the Origami code-editor contract (`ICodeEditor`, `CodeLanguages`), which is tracked for removal.
- Godot's `PluginLoadContext` requires the same assembly instance at runtime. Do not load a second copy via `Assembly.LoadFrom`.
- `.uid` files are Godot artifacts. They are not needed by the .NET build.
