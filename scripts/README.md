# V12 launch scripts

PowerShell wrappers for the built V12/NovaV2 apps and the multiplayer demo.
They set the multiplayer environment only for the launched process and restore
the caller's environment afterwards.

| Script | Use |
|---|---|
| `run-server.ps1` | Alias for `run-host.ps1` (`V12_MP_MODE=host`). |
| `run-host.ps1` | Start a demo as the multiplayer host. |
| `run-client.ps1` | Start a demo as a client. |
| `run-pair.ps1` | Start a host and a client locally, then stop both on exit. |
| `run-app.ps1` | Launch any built app, sample exe, `.v12pak`, or project folder. |
| `v12-common.ps1` | Shared resolution, env, and logging helpers. Do not run directly. |

## Quick start

From the repo root:

```powershell
# One host in this terminal
.\scripts\run-host.ps1 -Port 7777

# One client in another terminal
.\scripts\run-client.ps1 -HostAddress 127.0.0.1 -Port 7777
```

`-Server` is an alias for `-HostAddress`.

For a local desktop smoke test, start both and clean up together:

```powershell
.\scripts\run-pair.ps1 -Port 7777
```

`run-pair` waits until either side exits, or until `Ctrl+C`, then stops both.
Use `-KeepRunning` to leave both up and return to the prompt.

## Exported exe vs current code

By default the demo scripts prefer the exported exe:

`V12-Samples/<Demo>/bin/<Demo>.exe`

That exe auto-loads `<Demo>.v12pak` from the same folder. The scripts warn when
the exe or pak is older than the source. Exports are snapshots: if the warning
appears, re-export the app, or use the current code through Godot:

```powershell
.\scripts\run-host.ps1 -UseGame -DebugInput
.\scripts\run-client.ps1 -UseGame
```

`-UseGame` runs `NovaV2/NovaV2.Host` with the demo project folder, so it picks up
the current Debug build. It is also the right mode while running:

```powershell
vcsdk serve V12-Samples/MultiplayerDemo --no-renderer
```

To refresh the demo pak without re-exporting the exe:

```powershell
dotnet build V12-Samples/MultiplayerDemo
vcsdk pack V12-Samples/MultiplayerDemo -o bin/MultiplayerDemo.v12pak --no-renderer
```

The exported `.exe` is a Godot export and is not updated by `vcsdk pack`; rebuild
or re-export it separately when the host or engine changed.

When `-UseGame` is used, Godot's console wrapper may keep its output in its own
window, so the `logs/` files can be empty. The exported exe writes to `logs/`
normally.

## Hot reload (code + content)

`-HotReload` requires `-UseGame`; it compiles the project's C# sources and
rebuilds the world when a script or `.worldml` file changes:

```powershell
.\scripts\run-host.ps1 -UseGame -HotReload -Xr off
```

Edit `V12-Samples/MultiplayerDemo/Scripts/*.cs` or
`V12-Samples/MultiplayerDemo/Scenes/Main.worldml` and save. The log shows
`[HotReload] C# scripts reloaded` and `[HotReload] World reloaded`.

This is a scene reload for local development: the player/XR rig is recreated and
a live network session resets. Assets and shaders are not reloaded yet. See
`Docs/HOT_RELOAD_NOVAV2.md`.

## VR

The exported demo manifest requests XR. On a desktop machine, force flat:

```powershell
.\scripts\run-host.ps1 -Xr off
.\scripts\run-client.ps1 -Xr off
```

When the host should use the headset and the client should stay flat, run them
separately:

```powershell
# Terminal 1
.\scripts\run-host.ps1 -Xr auto -DebugInput

# Terminal 2
.\scripts\run-client.ps1 -Xr off -HostAddress 127.0.0.1
```

`-Xr auto` lets the manifest decide. `-Xr on` forces XR and `-Xr off` forces
flat. Desktop launches add both `--xr-mode off` (Godot) and `--xr 0` (NovaV2),
per the repo rule for desktop testing.

`run-pair` defaults to `-HostXr off -ClientXr off` so automated desktop runs do
not grab a headset. Override either side:

```powershell
.\scripts\run-pair.ps1 -HostXr auto -ClientXr off
```

## Logs and debugging

Every launch writes two files under `logs/` unless `-NoLog` is passed:

- `<name>-<stamp>.out` - stdout and V12 log lines
- `<name>-<stamp>.err` - stderr

The scripts print the exact paths before starting. On exit, they show the last
20 stdout lines.

`-DebugInput` sets `V12_MP_DEBUG=1`. The demo controller then logs one line per
second with XR state, move/look/turn axes, yaw, position, and pointer capture.
The XR service logs raw left-stick values when they change, and the host logs
player commands when their direction changes.

## Generic app launcher

List the known presets:

```powershell
.\scripts\run-app.ps1 -List
```

Examples:

```powershell
# A sample exe
.\scripts\run-app.ps1 -App MultiplayerDemo -Mode off -Xr off

# The generic NovaV2 app with a pak
.\scripts\run-app.ps1 -App NovaV2 -Pak V12-Samples\MultiplayerDemo\bin\MultiplayerDemo.v12pak -Xr off

# Any project folder through NovaV2
.\scripts\run-app.ps1 -App NovaV2 -Game V12-Samples\MultiplayerDemo -Xr off

# A project folder through the current Godot host
.\scripts\run-app.ps1 -App MultiplayerDemo -UseGame

# A sample as a client or host
.\scripts\run-app.ps1 -App MultiplayerDemo -Mode client -HostAddress 192.168.1.50 -Port 7777

# Any exe with app-specific arguments
.\scripts\run-app.ps1 -App .\path\to\Game.exe -Arguments '--pak','.\game.v12pak','--xr','0'
```

`-Mode` accepts `auto` (leave the app default), `off`, `host`, or `client`.
`-Arguments` is appended for demo/project launches and is used directly for
plain exe launches.

`-Game <dir>` overrides the app preset and launches that project folder. Add
`-UseGame` to run it through the current Godot host instead of the exported
NovaV2 app.

Known presets:

- Demo: `MultiplayerDemo`, `ControllerDemo`, `HelloDotnet`, `SkinnedCharacter`
- App: `NovaV2`, `Nova`, `V12Studio`, `V12StudioAlpha1`, `V12StudioProwl`,
  `V12StudioTracy`, `Rockstar`, `VNode`, `V12Consumer`

## Overrides

| Variable | Purpose |
|---|---|
| `V12_RELEASES` | Root containing `NovaV2\NovaV2.exe`, `Nova\Nova.exe`, etc. Defaults to `D:\releases`, then `<repo>\releases`. |
| `V12_NOVAV2_EXE` | Explicit path to the generic NovaV2 host exe. |
| `GODOT_EXE` | Explicit path to the Godot 4.8 console exe. |

## Common switches

- `-DryRun` - resolve and print the command without starting anything.
- `-NoWait` - start and return immediately, leaving the PID running.
- `-NoLog` - do not create log files.
- `-DebugInput` - set `V12_MP_DEBUG=1`.
- `-Headless` - pass `--headless` for automated runs.
- `-HotReload` - compile and watch a `--game` project (requires `-UseGame`).
