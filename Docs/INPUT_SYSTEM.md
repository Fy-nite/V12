# V12 Input System

This guide explains how input moves through V12: how hosts produce it, how the
engine distributes it, and how gameplay code consumes it with `InputActionMap`.

## Layers

V12 input has two independent paths:

1. **Event path** (push): a host bridge polls the OS/controller and calls
   `InputService.SendEvent`. The service fans each `InputEvent` out to every
   registered `IInputHandler`. `InputActionMap` is one such handler.
2. **Snapshot path** (pull): a host implements `IInputSource` and exposes a
   per-frame `InputSnapshot`. Consumers such as the editor viewport poll it
   directly.

Gameplay should usually use the event path through `InputActionMap`. Use the
snapshot path for editor/tooling code that wants raw per-frame state.

```
keyboard / mouse / gamepad / VR
        │
        ▼
host bridge  (MonogameInputBridge, Nova InputHandler, XRTrackingService)
        │  InputService.SendEvent(InputEvent)
        ▼
   InputService ──► IInputHandler[] ──► InputActionMap (actions)
        │                            └─► scripts / systems
        │
        └─ optional: IInputSource.GetSnapshot() ──► editor viewport
```

## Core types

Located in `V12-Engine/Core/Input/`.

| Type | Role |
|---|---|
| `InputEvent` | One input event: `Type`, `Name`, `Key`, `Value`, `Pointer`, `Timestamp`, `Meta`. |
| `InputEventType` | `ButtonDown`, `ButtonUp`, `Axis`, `PointerMove`, `Text`, `TouchStart`, `TouchEnd`, `Char`. |
| `InputKey` | Renderer-agnostic physical key enum (`W`, `Space`, `LeftShift`, ...). |
| `InputService` / `IInputService` | Event bus. `SendEvent`, `RegisterHandler`, `UnregisterHandler`. |
| `IInputHandler` | One method: `OnInputEvent(InputEvent evt)`. |
| `InputActionMap` | Optional binding layer that turns events into named axes, buttons and vectors. |
| `IInputSource` / `InputSnapshot` / `InputKeys` | Polling path used by editor tooling. |

An `InputEvent` carries either:

- a **logical name** in `Name` (for example `"move_right"`, `"jump"`), or
- a **physical key** in `Key` (an `InputKey`).

Hosts may set both. `InputActionMap` accepts either.

## Sending events

A host bridge sends events into the engine:

```csharp
inputService.SendEvent(new InputEvent
{
    Type = InputEventType.Axis,
    Name = "move_right",
    Value = 1f,
});

inputService.SendEvent(new InputEvent
{
    Type = InputEventType.ButtonDown,
    Name = "jump",
});
```

`SendEvent` copies the handler list under a lock and calls each handler
synchronously. Exceptions from a handler are swallowed, so one bad handler does
not stop the others.

## Using InputActionMap

`InputActionMap` is the recommended surface for gameplay. It maps raw events to
stable action names so gameplay code does not care which key or device produced
the input.

### Lifecycle

```csharp
var actions = new InputActionMap { Name = "PlayerActions" };

actions.BindVector2("Move", "move_right", "move_left", "move_forward", "move_backward");
actions.BindButton("Jump", "jump");

actions.Attach(inputService);      // starts receiving events

// once per frame, before reading
actions.Update(deltaTime);

// teardown
actions.Detach(inputService);
```

Rules:

- Call `Update` exactly once per frame, before reading state.
- `Attach`/`Detach` take the concrete `InputService`.
- `GetButtonDown`/`GetButtonUp` are valid only for the frame processed by
  `Update`. They clear on the next call.
- Bindings can be changed at any time; binding writes lock internally.

### Name-based bindings

Use these when the host already emits logical actions (Nova/Godot, VR, the
MonoGame bridge's named actions).

```csharp
actions.BindAxis("FlyUp", "fly_up", "");        // one-directional axis
actions.BindButton("Sprint", "run");
actions.BindVector2("Look", "look_right", "look_left", "look_up", "look_down");
```

- `BindAxis(action, positive, negative)` computes `clamp(positive - negative, -1, 1)`.
  Within a frame the largest value seen for each side is kept, so a name-based
  axis is a peak-hold until the next `Update`. A source must send axis events
  every frame, including explicit `0` on release.
- `BindButton(action, inputName)` latches on `ButtonDown` and releases on
  `ButtonUp`.
- `BindVector2(action, +X, -X, +Y, -Y)` creates `action_X` and `action_Y`, read
  back with `GetVector2(action)`.

### Key-based bindings

Use these for direct, host-agnostic key mappings and rebindable controls.

```csharp
actions.BindButton("Jump", InputKey.Space);
actions.BindAxis("MoveX", InputKey.D, InputKey.A);
actions.BindAxis("FlyUp", InputKey.Q);                 // one-directional
actions.BindVector2("Move", InputKey.D, InputKey.A, InputKey.W, InputKey.S);
```

- Key buttons follow the key's `ButtonDown`/`ButtonUp` edges.
- Key axes derive `+1`/`-1` from held keys. They need only edge events, not
  per-frame axis events, because the map tracks held keys internally.
- The map resolves a key from `InputEvent.Key` first, then falls back to parsing
  `InputEvent.Name` as an `InputKey`. That makes key bindings work with hosts
  that forward only key names.

### Reading state

```csharp
float x       = actions.GetAxis("MoveX");      // -1..1, 0 if unbound
Vector2 move  = actions.GetVector2("Move");
bool held     = actions.GetButton("Sprint");
bool pressed  = actions.GetButtonDown("Jump");
bool released = actions.GetButtonUp("Jump");
```

Unknown actions read as `0` / `false`.

### Rebinding and introspection

The `Bind*` methods overwrite an existing binding, so rebinding is just binding
again. Explicit helpers make intent clear and let a settings UI show the current
mapping.

```csharp
actions.RebindButton("Jump", InputKey.F);
actions.RebindAxis("MoveX", InputKey.Right, InputKey.Left);

InputKey? jumpKey = actions.GetButtonKey("Jump");
var (pos, neg) = actions.GetAxisKeys("MoveX");   // each side may be null

actions.UnbindButton("Jump");
actions.UnbindAxis("MoveX");
actions.ClearBindings();
```

`GetButtonKey` / `GetAxisKeys` return `null` for name-based or unbound actions.

### Threading

`OnInputEvent` and `Update` lock internally because events can arrive on a
different thread than the frame loop. The `Get*` readers do **not** lock. Query
them from the same thread that calls `Update`.

### Edge-case behavior

- If a press and release for the same button both arrive between two `Update`
  calls, the release wins and `GetButtonDown` is not reported. Treat very fast
  taps as host-side edge events if you need both.
- Key-based axis state is latched from edges. If a host does not send a release
  (for example on focus loss), the key stays held. Hosts should resync on focus
  changes.

## Using IInputHandler directly

For scripts or systems that want raw events, implement `IInputHandler`. This is
how Nova's `WorldCanvasSystem` listens for `"interact"`.

```csharp
public sealed class MySystem : IInputHandler
{
    public void OnInputEvent(InputEvent evt)
    {
        if (evt.Type == InputEventType.ButtonDown && evt.Name == "interact")
            DoInteract();

        if (evt.Key == InputKey.Tab)
            ToggleMenu();
    }
}

inputService.RegisterHandler(this);
// ...
inputService.UnregisterHandler(this);
```

Prefer `InputActionMap` when you need held/edge state or rebinding. Use a raw
handler for one-off keys, text, pointer data, or event names outside the map.

## Physical keys

`InputKey` is the engine's renderer-agnostic key enum. Raw key edges are sent as:

```csharp
new InputEvent
{
    Type = InputEventType.ButtonDown,   // or ButtonUp
    Name = InputKey.Space.ToString(),   // "Space"
    Key  = InputKey.Space,
}
```

### Host support

| Host | Raw `InputKey` edges | Notes |
|---|---|---|
| `MonogameInputBridge` | Yes | Forwards every key with `Name` and `Key`. |
| Nova / Godot `InputHandler` | Yes | Forwards mapped keys with `Name` and `Key`. Letters, digits, F-keys, numpad, navigation, modifiers, punctuation. |
| VR `XRTrackingService` | No | Sends named buttons/axes only (`xr_left_*`, `xr_right_*`, plus `jump`, `interact`, `esc`). |
| `IInputSource` | Pull only | `InputSnapshot.PressedKeys` uses `InputKeys` codes. |

Godot cannot distinguish left/right modifiers when polling, so `Shift`, `Ctrl`,
`Alt` and `Meta` map to `LeftShift`, `LeftControl`, `LeftAlt` and `LeftWindows`.

## Built-in event names

Host bridges emit these names. Bind them directly or use them in raw handlers.

| Source | Names |
|---|---|
| Movement | `move_right`, `move_left`, `move_forward`, `move_backward` |
| Look | `look_right`, `look_left`, `look_up`, `look_down` |
| Fly | `fly_up`, `fly_down`, `fly_toggle` |
| Buttons | `jump`, `run`, `crouch`, `interact`, `esc`, `lean_left`, `lean_right` |
| Mouse | `mouse_left`, `mouse_right`, `mouse_wheel`, `mouse_dx`, `mouse_dy` |
| VR | `xr_left_<btn>`, `xr_right_<btn>`, `xr_<side>_thumbstick_x/y`, where `<btn>` is `trigger_click`, `grip_click`, `primary`, `secondary`, `menu`, `thumbstick_click` |

`move_*` and `look_*` are axes. The rest are buttons unless noted.

## Polling path

Editor and tooling code can poll a per-frame snapshot instead of handling
events:

```csharp
InputSnapshot snapshot = inputSource.GetSnapshot();

if (snapshot.PressedKeys.Contains(InputKeys.Escape))
    CancelModal();

Vector2 mouse = snapshot.MousePosition;
bool left = snapshot.LeftDown;
bool active = snapshot.WindowActive;
```

`InputKeys` codes are ASCII for letters and digits (`InputKeys.A == 'A'`) and a
distinct range for named keys. This path is used by `ViewportInteractionService`
and is independent of `InputActionMap`.

## Worked example

A component with key bindings, name fallback for gamepads, and rebinding:

```csharp
private InputActionMap _actions = null!;
private InputService _input = null!;

public override void OnAttach(IWorldElement owner)
{
    _input = GameRoot.Instance.Registry.Get<InputService>();

    _actions = new InputActionMap { Name = "PlayerActions" };
    _actions.BindVector2("Move", InputKey.D, InputKey.A, InputKey.W, InputKey.S);
    _actions.BindVector2("Look", "look_right", "look_left", "look_up", "look_down");
    _actions.BindButton("Jump", InputKey.Space);
    _actions.BindButton("Sprint", InputKey.LeftShift);
    _actions.BindAxis("FlyUp", InputKey.Q);
    _actions.BindAxis("FlyDown", InputKey.C);
    _actions.Attach(_input);
}

public override void Update(float deltaTime)
{
    _actions.Update(deltaTime);

    Vector2 move = _actions.GetVector2("Move");
    if (_actions.GetButtonDown("Jump"))
        Jump();
    if (_actions.GetButton("Sprint"))
        move *= _sprintMultiplier;

    ApplyMove(move);
}

public override void OnDetach(IWorldElement owner)
{
    _actions.Detach(_input);
}
```

## Checklist

- Call `Update` once per frame before reading state.
- Send axis events every frame (name-based axes only).
- Query `Get*` from the `Update` thread.
- Use `InputKey` bindings for rebindable controls; use names for device-agnostic
  gameplay actions.
- Detach handlers and maps on teardown.
