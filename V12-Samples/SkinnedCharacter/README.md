# SkinnedCharacter

A skinned, animated character in NovaV2, driven by `Prowl.Clay` (import) and
`Prowl.Motion` (skeleton, clips, fades).

## What you see

- The Khronos **CesiumMan** walking on a ground plane, slowly turning on a
  turntable.
- The camera orbits the character; right mouse captures the pointer for free
  orbit.
- The model is skinned on the GPU: one skeleton drives one `Skin`, with bone
  poses uploaded every frame.

## Run it

From the repository root:

```powershell
dotnet build V12-Samples\SkinnedCharacter
cd NovaV2\NovaV2.Host
Godot_v4.7-stable_mono_win64_console.exe --path . -- --game ..\..\V12-Samples\SkinnedCharacter
```

Or capture a single frame without a human at the window:

```powershell
$env:V12_SCREENSHOT = "$PWD\skinned.png"
Godot_v4.7-stable_mono_win64_console.exe --path . -- --game ..\..\V12-Samples\SkinnedCharacter
```

If you change engine code, rebuild `NovaV2\NovaV2.Host` as well: the host loads
its own copy of the engine assemblies, and a game assembly built against newer
engine code will throw `MissingMethodException` against a stale host.

## Controls

| Input | Action |
| --- | --- |
| Right mouse | Capture / release the pointer |
| Mouse (captured) | Orbit the camera |
| A / D | Turn the character |
| Space | Pause / resume the clip |
| R | Restart the current clip |
| `[` / `]` | Seek 0.1 s back / forward |
| 1 - 9 | Play the Nth clip (CesiumMan has one) |
| Escape | Release the pointer (host shortcut) |

## How it is wired

- `Scenes/Main.worldml` holds the ground, camera, sun and a
  `SkeletalManagerComponent` pointing at `v12://Assets/CesiumMan.glb`, plus a
  `SkeletalPlayerComponent` for playback.
- The manager imports the model once, builds its skeleton and clips, and creates
  one child element per skinned submesh, each with its material.
- `SkinnedCharacterController` drives the camera orbit and the player
  (`Play`, `Seek`, `Pause`, `Resume`), and uses `IMouseCapture` for the pointer
  lock.
- `SkinnedCharacterGamePack` registers the engine services the sample needs.

See [`../../Docs/ANIMATION.md`](../../Docs/ANIMATION.md) for the module
architecture, components and current limitations.

## Asset

`Assets/CesiumMan.glb` comes from the Khronos glTF Sample Assets repository
(`Models/CesiumMan`), copyright Cesium, licensed CC-BY 4.0. See
[`Assets/CesiumMan.ATTRIBUTION.md`](Assets/CesiumMan.ATTRIBUTION.md).
