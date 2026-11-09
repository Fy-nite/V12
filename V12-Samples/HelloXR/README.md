# HelloXR

A minimal XR sample for NovaV2: headset tracking, two tracked controllers, and
a box you can pick up.

## What it does

- The **headset** is the camera; looking around looks around.
- Two **controller cubes** follow your hands (optical hand tracking is used when
  you are not holding a controller).
- **Squeeze the trigger** while your hand is near the orange box to grab it,
  move it, and release the trigger to drop it.
- **A / X** (right primary) or **Space** resets the box.
- **Left thumbstick** moves the rig.

Without a headset the sample still runs: OpenXR fails to initialise, the
renderer stays flat, and the desktop camera shows the box and the two hands so
the scene can be inspected.

## Run it

```powershell
dotnet build V12-Samples\HelloXR
cd NovaV2\NovaV2.Host
Godot_v4.7-stable_mono_win64_console.exe --path . -- --game ..\..\V12-Samples\HelloXR
```

The game declares `"Xr": true` in its manifest, so the host relaunches itself
with `--xr-mode on` and brings OpenXR up. Flat games never touch the headset:
`project.godot` keeps `openxr/enabled=false`, so XR only starts when a game asks
for it.

Pass `--xr 0` to force the flat preview.

## How it is wired

The host owns tracking, not the game:

- `NovaV2XRService` (in `NovaV2.Shared`) creates the `XROrigin3D`, an
  `XRCamera3D` and two `XRController3D`s, initialises OpenXR, and sets
  `Viewport.UseXR`.
- Every frame it mirrors the tracked poses onto the V12 elements that carry
  `XRRootComponent` / `XRHeadComponent` / `XRHandComponent`, and forwards
  controller input as `xr_<side>_<action>` events (`xr_right_trigger`,
  `xr_left_stick_x`, `xr_right_primary_click`, …).
- While XR is active the renderer stops creating snapshot cameras, so the
  headset camera owns the view.

**Tracked elements must not carry a `TransformComponent`.** The pose is written
as a quaternion into `LocalTransform`; a `TransformComponent` stores rotation as
yaw/pitch/roll, which clamps at ±90° and gimbal-locks a hand pointed straight up
or down. The service warns if it finds one.

The game only binds those events:

```csharp
actions.BindAxis("RightTrigger", "xr_right_trigger", "");
actions.BindAxis("MoveX", "xr_left_stick_x", "");
actions.BindButton("Reset", "xr_right_primary_click");
actions.AddButton("Reset", InputKey.Space);   // desktop fallback
```

`Scenes/Main.worldml` holds the rig: `Player` → `XRRig` (`XRRootComponent`) →
`Head`, `LeftHand`, `RightHand`. The XR service finds that shape by component,
so the element names only matter to the game script.

## Requirements

- `NovaV2.Host/project.godot` has `[xr] openxr/enabled=true` and ships
  `openxr_action_map.tres` (the default Godot action map).
- An OpenXR runtime on the machine. On Windows that is usually the SteamVR or
  Oculus/Meta runtime; without one, `xrCreateInstance` fails and the sample runs
  flat.
