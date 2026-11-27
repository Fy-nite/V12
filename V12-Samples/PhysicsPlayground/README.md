# Physics Playground

A yard full of dynamic crates, balls, a moving platform and a grabber, used to
exercise V12's physics, grabbing and (optionally) per-object networking.

Solo is the default: just run the sample and move around.

## Multiplayer

The level is host-authoritative. A crate becomes yours when you grab it: the host
delegates that element to you, your machine steps its physics, and every other
peer follows. Release it and the host takes it back. The rig (`Player`,
`PlayerBody`) is local to each peer and never replicated.

| Variable | Values | Default |
|---|---|---|
| `V12_MP_MODE` | `host`, `client`, `off` | `off` |
| `V12_MP_HOST` | host name or IP for a client | `127.0.0.1` |
| `V12_MP_PORT` | UDP port | `7777` |

Start the host:

```powershell
$env:V12_MP_MODE="host"
cd NovaV2\NovaV2.Host
Godot_v4.8-beta3_mono_win64_console.exe --path . --xr-mode off -- --game ..\..\V12-Samples\PhysicsPlayground --xr 0
```

Start a client in another terminal:

```powershell
$env:V12_MP_MODE="client"
$env:V12_MP_HOST="127.0.0.1"
$env:V12_MP_PORT="7777"
cd NovaV2\NovaV2.Host
Godot_v4.8-beta3_mono_win64_console.exe --path . --xr-mode off -- --game ..\..\V12-Samples\PhysicsPlayground --xr 0
```

Always pass `--xr-mode off` (Godot) and `--xr 0` (NovaV2) for desktop testing.

## Build

```powershell
dotnet build V12-Samples/PhysicsPlayground
vcsdk pack V12-Samples/PhysicsPlayground -o bin/PhysicsPlayground.v12pak --no-renderer
```

## What this exercises

- `EntityAuthorityStore`: per-element simulating user, epochs, disconnect reclaim.
- `PhysicsLocomotionSystem`: dynamic body for the simulator, kinematic follower
  for everyone else, rebuilt when the role flips.
- `GrabberComponent` → `RequestSimulatingUser`: first claim wins, host arbitrates.
- Client pose replication and value gating (`CanWrite`).

The rig is marked `NonPersistentComponent`, so it is never in the snapshot. On a
client, `PhysicsPlaygroundNet` makes the rig its own simulating user locally so
the controller, motor and camera keep ticking on a replica world.
