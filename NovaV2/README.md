# NovaV2

Godot 4 renderer and host for V12.

- `NovaV2.Host` is the runnable project: it boots a `.v12pak`, a `--game`
  project folder, or the built-in demo scene.
- `NovaV2.Shared` is the runtime, renderer, input bridge, presentation host and
  XR service.

## Desktop

```powershell
dotnet build NovaV2/NovaV2.Host/NovaV2.Host.csproj
Godot --path NovaV2/NovaV2.Host --xr-mode off -- --game <dir>
Godot --path NovaV2/NovaV2.Host --xr-mode off -- --pak <file.v12pak>
```

`--xr-mode off` suppresses Godot's startup OpenXR probe when no headset is
attached; it does not disable NovaV2's XR service, which stays available for
`NovaV2Runtime.EnableXR()`.

`--audio-device <name>` selects the audio output device (case-insensitive, a
substring is accepted). It is the fallback when the system default is wrong or
discarded: on Linux, ALSA can enumerate a `null` sink while the real card is
still openable, and the startup warning lists the names if none can be
resolved.

For a packaged desktop game, rename the exported executable to the game name and
put `<game>.v12pak` beside it. No arguments are needed.

## Android

Full prerequisites, signing, Quest setup and troubleshooting live in
[`docs/BUILDING_FOR_ANDROID.md`](../docs/BUILDING_FOR_ANDROID.md). The short
version:

An APK cannot hold a writable folder beside its executable, so the pak is baked
into the package (`res://`) and opened through Godot's `FileAccess`.

1. In Godot, install the Android build template (`Project > Install Android
   Build Template`) and configure the Android SDK/JDK. .NET Android exports use
   the Gradle build path; the preset already enables it.
2. Put the game pak in `NovaV2/NovaV2.Host/`, for example `MyGame.v12pak`. The
   preset's `include_filter="*.v12pak"` packs it as a raw resource.
3. Set the Android preset's package name, export path and (for release) keystore,
   then export an APK or AAB.

Discovery order, no command line required:

1. `--pak res://MyGame.v12pak` (or a bare `--pak MyGame.v12pak`).
2. `res://<executable-name>.v12pak`.
3. The first `*.v12pak` at the package root, alphabetically.

The archive is extracted once to `user://v12paks/<stem>_<hash>/` and reused on
later launches. `V12_PAK_CACHE` overrides that location. Expect the pak's size
in the APK plus the extracted size in user storage.

A pak this large eventually exceeds Google Play's base-module size limits. At
that point keep the APK small and download the pak to `user://` on first run
(`--pak user://game.v12pak` works), or move the content to Play Asset Delivery.

### Native libraries

Android refuses to `dlopen` a library from app-writable storage, so a pak's
`bin/` natives cannot be loaded on device. Put native dependencies in the APK
(Gradle/AAR or a NuGet runtime asset that the Godot Android build packages). A
pak that ships natives logs a warning on mobile.

### XR headsets

Set the Android preset's `XR Features > XR Mode` to OpenXR for a Quest-style
build. XR can start at launch (`"Xr": true` in the pak manifest, or `--xr on`) or
later from gameplay through `NovaV2Runtime.EnableXR()`.

### Testing the embedded path on desktop

From a source run `res://` is the project folder, so dropping a pak into
`NovaV2/NovaV2.Host/` exercises the same streamed extraction the APK uses.

## Limitations

- `vcsdk pack` still produces the `.v12pak` and a desktop renderer distribution
  (win/linux/osx). For Android, pack the content as usual and export the host
  through Godot's pipeline instead of letting `vcsdk` copy a desktop host.
- A stream-backed pak is cache-keyed by its length plus head/tail bytes, not a
  full-content hash, so a same-length update that only changes middle bytes
  would reuse the previous extraction.
