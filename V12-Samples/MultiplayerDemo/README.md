# Multiplayer Demo

A small V12 gamepak with a shared host-authoritative cube, replicated player
avatars, and one player controller that works on desktop and in XR.

The host owns the simulation and spawns one avatar per player. Desktop clients
predict their own movement locally, then reconcile against the host's
authoritative state, so input responds immediately instead of waiting for a
round trip. The host still applies every input command to the replicated body
and broadcasts the result. An XR client moves its own rig for responsiveness and
sends the resulting floor pose, so the host places that player's body under
their headset. Every peer sees the other avatars, while the local camera follows
its own assigned avatar.

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
- Clients send sequenced `PlayerInputCommand` messages to the host. Desktop
  commands carry the fixed-step delta; XR commands also send the body floor pose
  plus head and hand poses relative to that body.
- The host applies desktop commands as deterministic steps and XR poses directly
  to the sender's avatar, then replicates the transforms as normal world element
  updates.
- Desktop clients run the same step locally before sending it, so the camera
  moves on the input tick. The host returns `PlayerStateAck` with the last
  applied sequence and the authoritative transform; the client drops acknowledged
  inputs and replays the rest, which keeps prediction converged even though the
  host still owns the result.
- In XR the body follows the headset, so stepping around the play space moves the
  visible avatar instead of leaving it at the spawn point.
- The host's own avatar is visible to it, so the local XR hand meshes are hidden
  to avoid drawing a second pair of hands over the replicated ones. A client
  hides its own avatar and keeps its local rig hands.
- Clients receive `PlayerWelcome` with the avatar assigned to them and hide their
  own visual subtree. The desktop camera follows the local prediction while the
  host keeps the replicated body authoritative; XR uses the local headset pose.

## Movement envelope

The demo uses `V12.Basic.Player.MovementEnvelope`, the same ADSR envelope as the
shared `PlayerMotorComponent`: attack, decay to sustain, and release. It shapes
input speed, not collision response, so movement starts and stops with a little
weight while the host remains authoritative.

The envelope value is sampled per fixed step and carried in
`PlayerInputCommand.Envelope`. The host applies that exact value in
`MultiplayerAvatarComponent.ComputeStep`, and the client stores it in each
pending input so prediction replay uses the same speed. Without sending the
value, a client and host with different envelope phases would drift apart.

XR still uses the client-owned rig pose path. The envelope shapes the local rig
movement there; the host mirrors the resulting body/head/hand poses.

## Client prediction

Desktop clients predict movement locally and reconcile through `PlayerStateAck`.
The host is still authoritative: it applies each sequenced command, and an ack
carries the last applied sequence plus the resulting transform. The client drops
acknowledged inputs, replays anything newer from the host transform, and keeps
moving. If the host result differs, the next ack corrects the prediction.

XR uses a different model. The tracked rig is already local and responsive, so
the client sends the captured body, head, and hand pose and the host mirrors it.
There is no desktop-style replay for XR.

The demo trusts the client's command rate and fixed-step delta. A production
host should validate or rate-limit both; prediction does not change who owns the
authoritative body.

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
