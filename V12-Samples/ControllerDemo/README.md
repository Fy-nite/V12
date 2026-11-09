# ControllerDemo

A small V12 sample that shows off raw gamepad input. Everything is bound
through `InputActionMap`, so each action answers to a pad source **and** a
keyboard fallback at the same time.

| Action | Gamepad | Keyboard |
| --- | --- | --- |
| Move | Left stick | W A S D |
| Look | Right stick | Arrow keys |
| A / B / X / Y | Face buttons | Space / E / Q / R |
| LB / RB | Shoulders | F / G |
| Back / Start | Back / Start | Backspace / Enter |
| D-pad | D-pad | I / K / J / L |
| Triggers | LT / RT | Z / C |

## What you see

- **Orange cube** moves on the ground with the left stick.
- **Blue cube** spins with the right stick.
- **Twelve tiles** light up while their button is held, in Xbox colours
  (A green, B red, X blue, Y yellow, shoulders white, d-pad orange).
- **Two bars** at the ends rise and brighten with the left and right triggers.

## Run it

From the repo root, with the Godot binary:

```powershell
cd NovaV2\NovaV2.Host
Godot_v4.7-stable_mono_win64_console.exe --path . -- --game ..\..\V12-Samples\ControllerDemo
```

Or package it and run the archive:

```powershell
vcsdk pack V12-Samples\ControllerDemo -o ControllerDemo.v12pak
# then rename the Godot binary to ControllerDemo.exe, put ControllerDemo.v12pak
# beside it, and launch it with no arguments
```

## How it is wired

`Scripts/ControllerDemoController.cs` builds the props at runtime and binds one
action map:

```csharp
actions.BindAxis("MoveX", GamepadAxis.LeftX);
actions.AddAxis("MoveX", InputKey.D, InputKey.A);

actions.BindButton("A", GamepadButton.A);
actions.AddButton("A", InputKey.Space);
```

`Bind*` replaces an action's sources, `Add*` appends one. Axes sum their
sources, so a stick and a key pair can drive the same action without fighting.
Pad buttons and keys report 1 while held, pad axes report their analog value,
and name-based axes are a peak-hold over the frame.

The scene itself (`Scenes/Main.worldml`) only holds the camera, sun,
environment and ground; the interactive props are created by the script so the
sample stays readable.
