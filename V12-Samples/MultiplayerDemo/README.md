# Multiplayer Demo

A small V12 gamepak with a shared host-authoritative cube, replicated player
avatars, and one player controller that works on desktop and in XR.

The host owns the simulation and spawns one avatar per player. Desktop clients
send movement intent to the host; the host moves that client's avatar and
replicates the transform through the normal world update path. An XR client moves
its own rig for responsiveness and sends the resulting floor pose, so the host
places that player's body under their headset. Every peer sees the other avatars,
while the local camera follows its own assigned avatar.

The same pak runs as a host, a client, or a local solo sandbox. Mode is read
from environment variables, so there are no separate builds:

| Variable | Values | Default |
|---|---|---|
| `V12_MP_MODE` | `host`, `client`, `off` | `host` |
| `V12_MP_HOST` | host name or IP for a client | `127.0.0.1` |
| `V12_MP_PORT` | UDP port | `7777` |

## Controls

**Desktop**

- `WASD` moves. A gamepad left stick also moves.
- Mouse or the right stick looks.
- Right mouse or `Escape` captures/releases the pointer.

**XR**

- The headset owns the view.
- Left stick moves the rig, relative to where the headset is looking.
- Right stick turns the rig smoothly around the headset.
- Tracked hands render on the rig. On the host, the replicated avatar supplies
  the hands and the local hand meshes are hidden so they do not double up.
- The replicated body sits on the floor under the headset, so walking around
  the play space moves the avatar with you.

Without a headset the same world runs flat using the desktop camera. For desktop
testing pass both `--xr-mode off` (Godot) and `--xr 0` (NovaV2) to force flat
even when the machine has a headset.

## Build with vcsdk

From the repo root:

```powershell
dotnet build V12-Samples/MultiplayerDemo
vcsdk pack V12-Samples/MultiplayerDemo -o bin/MultiplayerDemo.v12pak --no-renderer
```

The pak contains `paks/MultiplayerDemo.dll` and `Scenes/Main.worldml`.

## Run from a project folder

Start the host:

```powershell
$env:V12_MP_MODE="host"
$env:V12_MP_PORT="7777"

cd NovaV2\NovaV2.Host
Godot_v4.8-beta3_mono_win64_console.exe --path . --xr-mode off -- --game ..\..\V12-Samples\MultiplayerDemo --xr 0
```

Start a client in another terminal:

```powershell
$env:V12_MP_MODE="client"
$env:V12_MP_HOST="127.0.0.1"
$env:V12_MP_PORT="7777"

cd NovaV2\NovaV2.Host
Godot_v4.8-beta3_mono_win64_console.exe --path . --xr-mode off -- --game ..\..\V12-Samples\MultiplayerDemo --xr 0
```

For desktop testing, always pass `--xr-mode off` (Godot) and `--xr 0`
(NovaV2). Omit both for VR, or pass `--xr on`.

## Run from the pak

After packing, point the host at the archive:

```powershell
$env:V12_MP_MODE="host"
Godot_v4.8-beta3_mono_win64_console.exe --path . --xr-mode off -- --pak ..\..\V12-Samples\MultiplayerDemo\bin\MultiplayerDemo.v12pak --xr 0
```

The client uses the same `--pak` path with `V12_MP_MODE=client`.

## XR

`manifest.json` sets `"Xr": true`. NovaV2 starts OpenXR in-process when a
headset runtime is available. If OpenXR fails to initialise, the demo continues
on the desktop camera.

For desktop testing pass `--xr-mode off` (Godot) and `--xr 0` (NovaV2) to force
the flat path without starting OpenXR.

## What is local and what is shared

- `SharedCube` is host-owned. Its `SyncValue<float>` angle streams to clients.
- `Player` is marked `NonPersistentComponent`, so it stays local to each peer.
  That keeps the desktop camera, XR head and hands from being overwritten by the
  host snapshot.
- The host spawns one `Avatar_<playerId>` per peer, with a body, head, and two
  hand blocks under an avatar XR rig.
- Clients send `PlayerInputCommand` messages to the host. In XR they also send
  the body floor pose plus head and hand poses relative to that body.
- The host applies input and XR poses to the sender's avatar and replicates the
  transforms as normal world element updates.
- In XR the body follows the headset, so stepping around the play space moves the
  visible avatar instead of leaving it at the spawn point.
- The host's own avatar is visible to it, so the local XR hand meshes are hidden
  to avoid drawing a second pair of hands over the replicated ones. A client
  hides its own avatar and keeps its local rig hands.
- Clients receive `PlayerWelcome` with the avatar assigned to them, hide their
  own visual subtree, and follow it with the local camera or headset.

## Hot iteration

Code and content together, without restarting the process:

```powershell
.\scripts\run-host.ps1 -UseGame -HotReload -Xr off
```

This compiles `Scripts/*.cs` with Roslyn, swaps the live script generation, and
reloads `Scenes/Main.worldml` when either changes. It is a scene reload: the
player/XR rig is recreated and a network session resets. Details and limits:
`Docs/HOT_RELOAD_NOVAV2.md`.

For a pak build loop:

```powershell
vcsdk serve V12-Samples/MultiplayerDemo --no-renderer
```

`vcsdk serve` repacks on changes. The current NovaV2 pak loader still needs a
restart for pak content; use `--game --hot-reload` for the live loop.

## Input debugging

Set `V12_MP_DEBUG=1` before launching. The controller logs one line per second
with the resolved XR state, movement/look/turn axes, yaw, and position. The XR
service logs raw left-stick values when they change, and the host logs player
commands when their movement direction changes. That is enough to tell whether
an input problem is upstream (stick/keys), in the controller, or in the network
command path.
