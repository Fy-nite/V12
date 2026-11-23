# NovaV2 hot reload (game mode)

This is the local development loop for editing a V12 game while NovaV2 keeps
running. It covers both gameplay C# and world content for `--game` project
folders.

## Usage

From the repo root:

```powershell
.\scripts\run-host.ps1 -UseGame -HotReload -Xr off
```

Or directly:

```powershell
Godot_v4.8-beta3_mono_win64_console.exe `
  --path NovaV2/NovaV2.Host --xr-mode off -- `
  --game V12-Samples/MultiplayerDemo --hot-reload --xr 0
```

`--hot-reload` requires `--game`. A packaged `.v12pak` still needs a restart
until pak reload is wired.

`V12-Samples/UiShowcase` is a small project to try it on: save
`Scenes/Main.worldml` or `Scripts/UiShowcaseController.cs` and watch the log.

`--game` takes precedence over the pak the host finds beside its own executable.
An exported build ships `MyGame.exe` plus `MyGame.v12pak`, and in the past the
host loaded both: the pak's world was selected after the project folder's, so the
watcher reloaded a world that was not on screen. Now an explicit `--game` skips
the exe-adjacent pak (an explicit `--pak` is still honoured). If you are running
an older exported binary, move the `.v12pak` out of `bin` or run the repo's
host directly.

Edit `Scripts/*.cs` or `Scenes/Main.worldml` and save. The host compiles the
sources, swaps the gamepaks, and rebuilds the world in place.

## What happens on a save

### C# source change

1. `CSharpScriptHost` recompiles every `.cs` under the project folder. Build
   folders (`bin`, `obj`, `.v12`, `.git`, `node_modules`) are ignored.
2. The new assembly loads into a **collectible** `CSharpScriptLoadContext`.
3. `CSharpComponentRegistry` removes the previous source generation, registers
   the new component types, and installs them behind
   `ComponentTypeResolver`.
4. Gamepaks from the old assembly are stopped through
   `IV12GamepackLifecycle.OnStop` (when implemented) and removed.
5. Gamepaks from the new assembly load, initialize, and start.
6. The world file is parsed again so components are constructed from the new
   types.

A failed compile leaves the previous assembly and the running world alone. The
error is logged with file/line information.

### World file change

1. The file is re-read after a short debounce.
2. The new tree is parsed into a temporary `World`.
3. `WorldContentSwap.Replace` calls `OnDetach` on every old component, replaces
   the tree on the existing `World`, and marks the render frame dirty.
4. The session's `DirtyTracker` and `SyncChannel` are refreshed against the new
   elements.
5. `NovaV2XRService` drops its cached Player/head/hand elements so the next frame
   resolves the recreated rig.

The `World` object and its `WorldSession` stay alive. Focus, camera routing, and
UI keep pointing at the same world.

## Scope and limits

- **`--game` only.** The pak path loads worlds through `V12PakLoader`; reloading
  a pak in place is a later step.
- **Scene reload, not an in-place patch.** Local-only player rigs and avatars are
  recreated from the world file. Static state in the script assembly resets
  because the assembly is a new generation.
- **Network sessions reset.** A host/client reload stops the old link and starts
  a new one. Clients need to reconnect. Use this while developing solo or as a
  host; the networked hot-reload resync is a later step.
- **Assets and shaders are not reloaded yet.** World/component changes use the
  mounted `Assets` folder; a changed texture may still show the cached GPU
  handle. Shader reload needs a version bump (see `Docs/SHADERS.md`).
- **Failed world parses keep the old tree.** A malformed save logs an error and
  does not replace the running world.

## Pieces

| Piece | Role |
|---|---|
| `CSharpScriptHost` | Watches sources, compiles, loads a collectible generation, unloads the previous one. |
| `CSharpComponentRegistry` | Removes the old source generation and registers the new component types. |
| `ComponentTypeResolver` | Makes WorldML prefer the live registry type over an older assembly still in the AppDomain. |
| `GamepackLoader.RemoveFromAssembly` | Stops/removes gamepaks belonging to the replaced generation. |
| `IV12GamepackLifecycle` | Optional `OnStop` hook for gamepak cleanup. |
| `WorldContentSwap` | Detaches old components and replaces a world's tree while keeping the `World` object. |
| `NovaV2HotReloadService` | Polls scripts and the world file, drives the swap, and invalidates the XR cache. |
| `NovaV2RuntimeOptions` / `Main` | Reads `--hot-reload` and compiles scripts before the first world parse. |

## Next steps

1. Pak hot reload: watch the `.v12pak`, reload its extraction, recompile live
   scripts if present, and swap the world.
2. Network hot reload: after a host reload, force a full snapshot or a world
   revision so connected clients resync without reconnecting.
3. Asset/shader reload: bump `ShaderAssetRegistry` versions on `.v12shader`
   changes and invalidate texture handles.
4. Optional editor trigger: a key or menu entry to force a reload without
   waiting for the debounce.
