# Building NovaV2 for Android

How to ship a V12 game as an Android APK or AAB with NovaV2. The game content
stays a `.v12pak`; the difference is that the pak is embedded in the app package
(`res://`) instead of sitting beside the executable.

This covers NovaV2's Godot export pipeline. `vcsdk` still produces desktop
renderer distributions, so use it for the pak and Godot for the Android host.

Commands are PowerShell; the flags are the same on macOS and Linux.

## Quick path

1. Point Godot at the Android SDK/JDK and install the Android build template.
2. Build the pak: `vcsdk pack <game> --no-renderer -o <game>\build\MyGame.v12pak`.
3. Copy `MyGame.v12pak` and the `game.json` written beside it to
   `NovaV2/NovaV2.Host/`.
4. Set the Android preset's package id and (for release) keystore.
5. Export: `godot --headless --path NovaV2/NovaV2.Host --export-release "Android" build\MyGame.apk`.
6. Install: `adb install -r build\MyGame.apk`, then `adb logcat -s godot:V`.

---

## 1. Prerequisites

| Tool | Notes |
|---|---|
| Godot 4.8 mono | Same editor used to open `NovaV2/NovaV2.Host`. |
| .NET SDK | The project targets `net10.0`. |
| OpenJDK 17 | Higher versions work; 17 is the recommended baseline. |
| Android SDK | `platform-tools`, `build-tools`, a `platforms/android-*`, `cmdline-tools/latest`, and the NDK/CMake components Godot asks for. |
| Android build template | Installed into the project (below). Required for Gradle and for .NET. |

As of Godot 4.8, the documented SDK components are `build-tools/36.1.0`,
`platforms/android-36`, `cmdline-tools/latest`, `ndk/29.0.14206865` and
`cmake/3.22.1`. If your Godot version lists different versions, use those.

Do not use an Android SDK from a Linux distribution package; it is usually too
old. Use the Android CLI or Android Studio's SDK Manager.

### Point Godot at the SDK

`Editor > Editor Settings > Export > Android`:

- **Java SDK Path**: the OpenJDK 17 directory.
- **Android SDK Path**: the SDK directory that contains `platform-tools/adb`.

### Install the Android build template

`Project > Install Android Build Template` in the editor, or run an export with
`--install-android-build-template` (see section 5). This unpacks
`android_source.zip` into the project and is what allows Gradle builds. .NET
Android exports will not work without it.

---

## 2. Build the game pak

```powershell
New-Item -ItemType Directory -Force D:\git\MyGame\build | Out-Null
vcsdk pack D:\git\MyGame --no-renderer -o D:\git\MyGame\build\MyGame.v12pak
```

- `--no-renderer` matters: the Android host is the APK, so copying a desktop
  renderer next to the pak is wasted work.
- Do not pass `--runtime` for Android. The pak's managed DLLs are portable; pak
  native libraries are ignored on device (section 8).
- The pak's manifest decides XR: `"Xr": true` starts XR at launch, otherwise the
  game starts flat and can call `NovaV2Runtime.EnableXR()` later.
- `--split 2g` works the same way: copy every `MyGame.partNNN.v12pak` and the
  `game.json` that lists them. The preset's `*.v12pak` filter ships the parts.
  Splitting does not get around Play's base-module size limit; that needs Play
  Asset Delivery. `game.json` records each part's size and SHA-256, so a custom
  updater can resume or re-fetch a single part.

## 3. Put the pak in `res://`

Copy the pak into the Godot project root:

```
NovaV2/NovaV2.Host/MyGame.v12pak
```

The Android preset already has `include_filter="*.v12pak,*.json"`, which packs
raw `.v12pak` files and the launch descriptor. Keep them at the project root;
folders whose name starts with `_` are excluded by Godot's Gradle export.

Pak selection belongs to `V12.Host`, not the renderer. NovaV2 asks
`V12PakResolver` first, which checks:

1. An explicit `--pak` that is a real file path.
2. The dev inbox (`<user>/v12inbox/dev.v12pak`, and the app's external
   `Android/data/<package>/files/v12inbox` on device).
3. `game.json` next to the host, when it is a real file.
4. `<executable-name>.v12pak`, for compatibility with older builds.

Inside an APK `System.IO` cannot see content, so the shell then falls back to the
Godot resources:

5. `--pak res://MyGame.v12pak` (or a bare `--pak MyGame.v12pak`).
6. `res://<executable-name>.v12pak`.
7. The first `*.v12pak` at the package root, alphabetically.

So a single pak at the root needs no command line.

## 4. Configure the Android preset

The preset lives in `NovaV2/NovaV2.Host/export_presets.cfg` and is named
`Android`. The essentials:

| Setting | Value |
|---|---|
| `package/unique_name` | Your reverse-DNS id, e.g. `com.finite.mygame`. Not `com.example.*` for a real release. |
| `package/name` | Display name. |
| `version/code` / `version/name` | Increment for each upload. |
| `gradle_build/use_gradle_build` | `true` (already set; required for .NET). |
| `gradle_build/export_format` | `0` = APK, `1` = AAB. |
| `architectures/arm64-v8a` | `true`. Add `x86_64` only for an emulator. |
| `xr_features/xr_mode` | `0` = Regular (phone), `1` = OpenXR (Quest-style headset). |
| `package/signed` | `true`; supply a release keystore for anything you distribute. |
| `command_line/extra_args` | Optional. `--xr-mode off` suppresses the startup OpenXR probe on a flat phone build. Leave empty for XR. |

### Release keystore

```powershell
keytool -v -genkey -keystore mygame.keystore -alias mygame -keyalg RSA -validity 10000
```

`keytool` ships with the JDK. If it is not on `PATH`, run it from the JDK's
`bin` directory.

In the preset's Android export settings, fill **Release**, **Release User** and
**Release Password**. Godot currently requires the keystore and key passwords to
be the same. Use letters and digits only; special characters cause failures.
Keep the keystore out of source control.

Debug exports use a debug keystore automatically.

### Permissions

Only enable what the game uses. A single-player offline game needs nothing
extra; networking needs `permissions/internet=true`. Custom permissions go in
`permissions/custom_permissions`.

## 5. Export

### From the editor

`Project > Export > Android > Export Project`, pick APK or AAB, and choose an
output path.

### From the command line

The output directory must already exist. A relative path is resolved against the
project directory, so absolute paths are less surprising.

```powershell
$godot = "C:\Users\charlie\Desktop\Godot_v4.8-beta3_mono_win64\Godot_v4.8-beta3_mono_win64.exe"
New-Item -ItemType Directory -Force D:\git\V12-Monorepo\build | Out-Null

& $godot --headless --path "D:\git\V12-Monorepo\NovaV2\NovaV2.Host" `
  --export-release "Android" "D:\git\V12-Monorepo\build\MyGame.apk"
```

- Use `--export-debug` for a debug-signed build.
- The APK/AAB format comes from the preset's `gradle_build/export_format`, not
  from the output extension.
- If the template is not installed yet (or you removed the generated `android/`
  folder), install it in the same command:

```powershell
# $godot is the path set in the previous block
& $godot --headless --path "D:\git\V12-Monorepo\NovaV2\NovaV2.Host" `
  --install-android-build-template `
  --export-release "Android" "D:\git\V12-Monorepo\build\MyGame.apk"
```

### What the export includes

- The Godot project, NovaV2 assemblies and the V12 engine packages.
- Every raw `*.v12pak` at `res://`, including the game pak.

The pak keeps its file name; discovery handles it. The APK's package id comes
from the preset.

## 6. Install and inspect

```powershell
adb devices
adb install -r D:\git\V12-Monorepo\build\MyGame.apk
```

Launch from the device, then watch the log:

```powershell
adb logcat -s godot:V
```

A successful embedded load logs:

```
[NovaV2.Host] Using embedded pak: res://MyGame.v12pak
V12Pak 'MyGame.v12pak': extraction written at /data/user/0/<pkg>/files/v12paks/MyGame_<hash>
V12Pak 'MyGame.v12pak': 1 world(s), 1 pak DLL(s)
[NovaV2] Loaded pak 'MyGame.v12pak' (1 world(s), 1 gamepak(s)).
```

The next launch should say `extraction cache hit` instead of `written`.

Filter by pid if the log is noisy:

```powershell
$gamePid = (adb shell pidof com.finite.mygame).Trim()
adb logcat --pid=$gamePid
```

## 7. Storage and cache

- The APK contains the pak once.
- The first launch extracts it to `user://v12paks/<stem>_<hash>/`, which on
  Android is under the app's private files directory.
- Later launches reuse that directory. The OS does not clear it as readily as
  the temp directory.
- Clear it with Android's *App info > Storage > Clear storage*, by uninstalling,
  or from game code with `V12PakCache.Purge()`.
- `V12_PAK_CACHE` overrides the location on desktop; on device, leave the
  default.

Budget for the pak size in the APK plus the extracted size in user storage.

## 8. Native libraries

Android (10+) refuses to `dlopen` a native library from app-writable storage.
Anything a pak ships in `bin/` or `runtimes/<rid>/native/` is therefore skipped
on Android and iOS, and a pak with natives logs a warning.

Ship native dependencies inside the app instead:

- a Gradle/AAR dependency, or
- a NuGet runtime asset that the Godot Android build packages under `lib/<abi>/`.

Managed gamepak DLLs are unaffected; they load from the extraction directory as
usual.

## 9. XR headsets

For a Quest-style build:

1. In the Android preset, set `xr_features/xr_mode = 1` (OpenXR) and keep
   `architectures/arm64-v8a = true`.
2. Leave `xr/openxr/enabled=true` in `project.godot` (it is set).
3. Do not add `--xr-mode off` to `command_line/extra_args`.
4. Start XR either with `"Xr": true` in the pak manifest, `--xr on`, or from
   gameplay with `NovaV2Runtime.EnableXR()`.

The host never relaunches the process for XR, so the same APK can run flat and
enter VR later.

## 10. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `No export template found at ... android_source.zip` | Android build template not installed | `Project > Install Android Build Template`, or export with `--install-android-build-template`. |
| `Unable to find Android SDK package 'cmdline-tools/latest'` | SDK incomplete or wrong path | Install `cmdline-tools;latest` with the Android CLI/SDK Manager and set **Android SDK Path**. |
| `Exporting to Android when using C#/.NET is experimental.` | Informational | No action; it is the current Godot state. |
| JDK errors (`JAVA_HOME`, unsupported class file version) | Wrong or missing JDK | Install OpenJDK 17 and set **Java SDK Path**. |
| Gradle build fails | Build template missing, Gradle disabled, or dependencies unreachable | Install the template, confirm `use_gradle_build=true`, allow network access for the first build. |
| `xrCreateInstance failed` / "OpenXR was requested but failed to start" at launch | `openxr/enabled=true` probes at startup and no runtime is installed | Expected on a phone; the engine continues flat. Add `--xr-mode off` to `command_line/extra_args` to silence it. |
| App installs to a blank frame | Pak not found or failed to load | Check the `[NovaV2.Host]` and `V12Pak` log lines. Confirm the pak was in the project root at export time and matched `include_filter`. |
| `Pak not found: ...` | Bad `--pak` value or name mismatch | Use `--pak res://MyGame.v12pak` via `command_line/extra_args` while debugging. |
| Missing native symbol / `DllNotFoundException` | Native library was only in the pak | Put it in the APK (section 8). |
| Pak extracts on every launch | `user://` was cleared, or the pak changed | Expected after an app update; otherwise check for `cache hit` in the log. |
| Quest build stays flat | XR mode is Regular, or the game never requested XR | Set `xr_features/xr_mode=1` and start XR from the manifest or code. |
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE` | Existing install signed with a different key | Uninstall the old package first. |
| `INSTALL_FAILED_NO_MATCHING_ABIS` | Device ABI not in the APK | Enable `arm64-v8a` (or the emulator ABI) and re-export. |

## 11. Testing the embedded path without a device

From a source run, `res://` is the project folder, so the same streamed
extraction runs on desktop:

```powershell
$godot = "C:\Users\charlie\Desktop\Godot_v4.8-beta3_mono_win64\Godot_v4.8-beta3_mono_win64.exe"
Copy-Item D:\git\MyGame\build\MyGame.v12pak D:\git\V12-Monorepo\NovaV2\NovaV2.Host\
& $godot --path "D:\git\V12-Monorepo\NovaV2\NovaV2.Host" --xr-mode off -- --xr off
```

You should see `Using embedded pak: res://MyGame.v12pak` and the extraction
lines. Delete the pak from the project root afterwards so it is not committed or
packed by accident.

## 12. Device reload loop

`vcsdk serve --reload` replaces the per-save adb routine. Install the APK once,
then:

```powershell
vcsdk serve D:\git\MyGame --reload --package com.finite.mygame
```

On every successful build it pushes `bin/MyGame.v12pak` to
`/storage/emulated/0/Android/data/<package>/files/v12inbox/dev.v12pak`, calls
`am force-stop`, and starts the launcher activity again. NovaV2 checks that
inbox before the pak in its own package, so the headset runs the pushed build.

| Option | Default |
|---|---|
| `--device <serial>` | The only connected adb device |
| `--package <id>` | `V12_ANDROID_PACKAGE`, then `package/unique_name` in `export_presets.cfg` |
| `--inbox <dir>` | `/storage/emulated/0/Android/data/<package>/files/v12inbox` |

The pak is always written as `dev.v12pak`, so repeated pushes overwrite it and
nothing needs cleaning up on the device. The extraction cache still grows one
directory per build; clear it from the app or call `V12PakCache.Purge()`.

The reload relaunches the app instead of swapping the pak in place. Pak DLLs
load into the host's non-collectible assembly load context, so a second build
cannot replace the first inside the same process. A relaunch is the cheap,
honest unit of "apply a pak", and because the APK does not change it costs one
app start rather than a reinstall. `V12PakLoadResult.Unload()` covers the
content-only and `CSharpScripts` cases for hosts that want to reload in-process;
the tool does not use it yet.

Requirements: `adb` on `PATH` or under `ANDROID_HOME`/`ANDROID_SDK_ROOT`, at
least one connected device, and a real package id (Godot's generated
`com.example.$genname` is rejected, so pass `--package`). A reload failure never
fails the build; the watch loop keeps packing.

## 13. Not covered yet

- `vcsdk` does not build Android host distributions; it produces the pak and a
  desktop host.
- Touch input has no explicit mapping in `NovaV2Runtime.HandleInputEvent`.
  Godot's mouse-from-touch emulation covers basic taps and drags.
- Play Asset Delivery / OBB packaging for builds that outgrow the base module.
- `vcsdk pack` cannot produce an Android-only pak that strips desktop natives;
  they are ignored at load time on device.
