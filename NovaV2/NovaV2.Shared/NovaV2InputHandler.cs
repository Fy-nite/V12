using Godot;
using V12.Basic.Components;
using V12.Core;
using V12.Core.Diagnostics;
using V12.Core.Input;

namespace NovaV2.Hosting;

/// <summary>
/// Translates Godot keyboard, mouse and gamepad input into V12 InputService
/// events. This is the host-parity counterpart to the old Nova input bridge.
/// </summary>
public sealed class NovaV2InputHandler
{
    private readonly Dictionary<JoyButton, bool> _prevJoyButtons = new();
    private readonly Dictionary<(int Device, JoyButton Button), bool> _prevPadButtons = new();
    private readonly HashSet<int> _connectedPads = new();
    private Vector2 _accumulatedMouseLook;
    private bool _f10WasPressed;

    public bool MouseCaptured { get; set; }
    public bool DebugMode { get; set; }

    /// <summary>Godot key codes mapped to engine keys. Modifier keys are
    /// resolved separately because left and right share one code.</summary>
    private static readonly Dictionary<Key, InputKey> KeyMap = BuildKeyMap();

    /// <summary>Godot pad buttons mapped to engine buttons by name.</summary>
    private static readonly Dictionary<JoyButton, GamepadButton> PadButtonMap = BuildPadButtonMap();

    /// <summary>Godot pad axes mapped to engine axes by name.</summary>
    private static readonly Dictionary<JoyAxis, GamepadAxis> PadAxisMap = BuildPadAxisMap();

    public void AccumulateMouseLook(Vector2 delta) => _accumulatedMouseLook += delta;

    public bool ProcessFrame(InputService inputService, bool xrAvailable, GameRoot root)
    {
        using var _zone = V12Profiler.BeginZone("NovaV2InputHandler.ProcessFrame", V12Profiler.Colors.Runtime);
        if (inputService is null) return false;

        // Raw key edges are forwarded from the actual InputEventKey stream in
        // NovaV2Runtime.HandleInputEvent, so they reach games even while the UI
        // holds keyboard focus. This frame pass only derives the legacy named
        // axes and gamepad channels below.

        // Every pad button and axis is published as a raw engine event too, so
        // games can bind their own actions instead of the named channels.
        ForwardGamepad(inputService);

        if (inputService.UiCapturesPointer || inputService.UiCapturesKeyboard)
        {
            _accumulatedMouseLook = Vector2.Zero;
            return false;
        }

        float moveX = 0f;
        float moveY = 0f;
        float lookX = 0f;
        float lookY = 0f;

        if (ActionPressed("strafe_right")) moveX += 1f;
        if (ActionPressed("strafe_left")) moveX -= 1f;
        if (ActionPressed("move_forwards")) moveY += 1f;
        if (ActionPressed("move_backwards")) moveY -= 1f;

        if (ActionPressed("look_right")) lookX += 1f;
        if (ActionPressed("look_left")) lookX -= 1f;
        if (ActionPressed("look_up")) lookY += 1f;
        if (ActionPressed("look_down")) lookY -= 1f;

        float stickLX = Input.GetJoyAxis(0, JoyAxis.LeftX);
        float stickLY = Input.GetJoyAxis(0, JoyAxis.LeftY);
        float stickRX = Input.GetJoyAxis(0, JoyAxis.RightX);
        float stickRY = Input.GetJoyAxis(0, JoyAxis.RightY);

        if (!xrAvailable && MouseCaptured && _accumulatedMouseLook != Vector2.Zero)
        {
            var player = root.Player;
            if (player is not null)
            {
                var pc = player.GetComponent<PlayerComponent>();
                if (pc is not null)
                    pc.AddMouseDelta(_accumulatedMouseLook.X, _accumulatedMouseLook.Y);
            }

            inputService.SendEvent(new V12.Core.Input.InputEvent
            {
                Type = InputEventType.Axis,
                Name = "mouse_dx",
                Value = _accumulatedMouseLook.X,
            });
            inputService.SendEvent(new V12.Core.Input.InputEvent
            {
                Type = InputEventType.Axis,
                Name = "mouse_dy",
                Value = _accumulatedMouseLook.Y,
            });

            _accumulatedMouseLook = Vector2.Zero;
        }

        const float deadzone = 0.15f;
        if (Mathf.Abs(stickLX) < deadzone) stickLX = 0f;
        if (Mathf.Abs(stickLY) < deadzone) stickLY = 0f;
        if (Mathf.Abs(stickRX) < deadzone) stickRX = 0f;
        if (Mathf.Abs(stickRY) < deadzone) stickRY = 0f;

        if (Mathf.Abs(stickLX) > 0f || Mathf.Abs(stickLY) > 0f)
        {
            moveX = stickLX;
            moveY = -stickLY;
        }
        if (Mathf.Abs(stickRX) > 0f || Mathf.Abs(stickRY) > 0f)
        {
            lookX = stickRX;
            lookY = -stickRY;
        }

        moveX = Mathf.Clamp(moveX, -1f, 1f);
        moveY = Mathf.Clamp(moveY, -1f, 1f);

        SendAxis(inputService, "move_right", moveX > 0 ? moveX : 0f);
        SendAxis(inputService, "move_left", moveX < 0 ? -moveX : 0f);
        SendAxis(inputService, "move_forward", moveY > 0 ? moveY : 0f);
        SendAxis(inputService, "move_backward", moveY < 0 ? -moveY : 0f);
        SendAxis(inputService, "look_right", lookX > 0 ? lookX : 0f);
        SendAxis(inputService, "look_left", lookX < 0 ? -lookX : 0f);
        SendAxis(inputService, "look_up", lookY > 0 ? lookY : 0f);
        SendAxis(inputService, "look_down", lookY < 0 ? -lookY : 0f);

        if (ActionJustPressed("jump")) SendButton(inputService, "jump", true);
        if (ActionJustPressed("crouch")) SendButton(inputService, "crouch", true);
        if (ActionJustPressed("run")) SendButton(inputService, "run", true);
        if (ActionJustReleased("run")) SendButton(inputService, "run", false);
        if (ActionJustPressed("lean_left")) SendButton(inputService, "lean_left", true);
        if (ActionJustReleased("lean_left")) SendButton(inputService, "lean_left", false);
        if (ActionJustPressed("lean_right")) SendButton(inputService, "lean_right", true);
        if (ActionJustReleased("lean_right")) SendButton(inputService, "lean_right", false);
        if (ActionJustPressed("interact")) SendButton(inputService, "interact", true);
        if (ActionJustReleased("interact")) SendButton(inputService, "interact", false);
        if (ActionJustPressed("fly_toggle")) SendButton(inputService, "fly_toggle", true);
        if (ActionJustReleased("fly_toggle")) SendButton(inputService, "fly_toggle", false);
        if (ActionPressed("fly_up")) SendAxis(inputService, "fly_up", 1f);
        if (ActionPressed("fly_down")) SendAxis(inputService, "fly_down", 1f);

        var currentJoy = new Dictionary<JoyButton, bool>();
        for (int i = 0; i < (int)JoyButton.Max; i++)
        {
            var btn = (JoyButton)i;
            currentJoy[btn] = Input.IsJoyButtonPressed(0, btn);
        }

        void SendCtrlBtn(string name, JoyButton btn)
        {
            bool now = currentJoy.GetValueOrDefault(btn, false);
            bool prev = _prevJoyButtons.GetValueOrDefault(btn, false);
            if (now && !prev) SendButton(inputService, name, true);
            if (!now && prev) SendButton(inputService, name, false);
        }

        SendCtrlBtn("jump", JoyButton.A);
        SendCtrlBtn("crouch", JoyButton.B);
        SendCtrlBtn("interact", JoyButton.X);
        SendCtrlBtn("fly_toggle", JoyButton.Y);
        SendCtrlBtn("lean_left", JoyButton.LeftShoulder);
        SendCtrlBtn("lean_right", JoyButton.RightShoulder);
        SendCtrlBtn("esc", JoyButton.Start);

        float ltAxis = Input.GetJoyAxis(0, (JoyAxis)4);
        if (ltAxis > 0.15f)
            SendAxis(inputService, "run", ltAxis);

        _prevJoyButtons.Clear();
        foreach (var kv in currentJoy)
            _prevJoyButtons[kv.Key] = kv.Value;

        bool escapePressed = false;
        if (ActionJustPressed("escape"))
        {
            SendButton(inputService, "esc", true);
            escapePressed = true;
        }

        if (Input.IsKeyPressed(Key.F10))
        {
            if (!_f10WasPressed)
            {
                DebugMode = !DebugMode;
                GD.Print($"[NovaV2] Debug mode: {(DebugMode ? "ON" : "OFF")}");
                _f10WasPressed = true;
            }
        }
        else
        {
            _f10WasPressed = false;
        }

        return escapePressed;
    }

    /// <summary>
    /// Forwards one physical key edge as a raw engine key event. Every key is
    /// forwarded (letters, digits, function keys, numpad, navigation, both
    /// modifier sides, OEM punctuation), so a game can bind its own actions to
    /// <see cref="InputKey"/> values through <c>InputActionMap</c> instead of
    /// depending on the host to pre-register named actions.
    /// </summary>
    public void ForwardKeyEdge(InputService inputService, InputEventKey keyEvent)
    {
        if (inputService is null || keyEvent is null || keyEvent.Echo)
            return;

        var v12Key = ToInputKey(keyEvent);
        if (v12Key == InputKey.None)
            return;

        inputService.SendEvent(new V12.Core.Input.InputEvent
        {
            Type = keyEvent.Pressed ? InputEventType.ButtonDown : InputEventType.ButtonUp,
            Name = v12Key.ToString(),
            Key = v12Key,
            Value = keyEvent.Pressed ? 1f : 0f,
            IsPressed = keyEvent.Pressed,
        });
    }

    /// <summary>
    /// Maps a Godot key event to an engine key. Physical keycode wins so a
    /// layout change cannot break movement bindings; modifier keys share one
    /// code in Godot and are split by <see cref="InputEventKey.Location"/>.
    /// </summary>
    public static InputKey ToInputKey(InputEventKey keyEvent)
    {
        var key = keyEvent.PhysicalKeycode != Key.None
            ? keyEvent.PhysicalKeycode
            : keyEvent.Keycode;

        if (key == Key.None)
            return InputKey.None;

        bool right = keyEvent.Location == KeyLocation.Right;

        switch (key)
        {
            case Key.Shift: return right ? InputKey.RightShift : InputKey.LeftShift;
            case Key.Ctrl: return right ? InputKey.RightControl : InputKey.LeftControl;
            case Key.Alt: return right ? InputKey.RightAlt : InputKey.LeftAlt;
            case Key.Meta: return right ? InputKey.RightWindows : InputKey.LeftWindows;
        }

        return KeyMap.TryGetValue(key, out var mapped) ? mapped : InputKey.None;
    }

    /// <summary>
    /// Publishes every button edge and axis value from every connected pad as a
    /// raw engine event. Buttons carry <see cref="InputEvent.PadButton"/>, axes
    /// carry <see cref="InputEvent.PadAxis"/> and their analog value, and both
    /// carry the device index, so a game can bind its own actions and still
    /// tell pads apart.
    /// </summary>
    public void ForwardGamepad(InputService inputService)
    {
        if (inputService is null)
            return;

        var connected = new HashSet<int>(Input.GetConnectedJoypads());

        // A pad that vanished while a button was held must release it, or the
        // action stays latched forever.
        foreach (int gone in _connectedPads)
        {
            if (connected.Contains(gone))
                continue;
            ReleasePad(inputService, gone);
        }

        _connectedPads.Clear();
        foreach (int device in connected)
            _connectedPads.Add(device);

        foreach (int device in connected)
        {
            foreach (var (godotButton, padButton) in PadButtonMap)
            {
                bool now = Input.IsJoyButtonPressed(device, godotButton);
                bool was = _prevPadButtons.GetValueOrDefault((device, godotButton));
                if (now == was)
                    continue;

                _prevPadButtons[(device, godotButton)] = now;
                inputService.SendEvent(new V12.Core.Input.InputEvent
                {
                    Type = now ? InputEventType.ButtonDown : InputEventType.ButtonUp,
                    Name = padButton.ToString(),
                    PadButton = padButton,
                    Device = device,
                    Value = now ? 1f : 0f,
                    IsPressed = now,
                });
            }

            foreach (var (godotAxis, padAxis) in PadAxisMap)
            {
                inputService.SendEvent(new V12.Core.Input.InputEvent
                {
                    Type = InputEventType.Axis,
                    Name = padAxis.ToString(),
                    PadAxis = padAxis,
                    Device = device,
                    Value = Input.GetJoyAxis(device, godotAxis),
                });
            }
        }
    }

    private void ReleasePad(InputService inputService, int device)
    {
        foreach (var (key, held) in _prevPadButtons)
        {
            if (key.Device != device)
                continue;

            _prevPadButtons[key] = false;
            if (!held)
                continue;

            inputService.SendEvent(new V12.Core.Input.InputEvent
            {
                Type = InputEventType.ButtonUp,
                Name = key.Button.ToString(),
                PadButton = PadButtonMap.GetValueOrDefault(key.Button, GamepadButton.None),
                Device = device,
            });
        }
    }

    private static Dictionary<JoyButton, GamepadButton> BuildPadButtonMap()
    {
        var map = new Dictionary<JoyButton, GamepadButton>();
        for (int i = 0; i < (int)JoyButton.Max; i++)
        {
            var godotButton = (JoyButton)i;
            if (Enum.TryParse<GamepadButton>(godotButton.ToString(), out var pad) &&
                pad != GamepadButton.None)
            {
                map[godotButton] = pad;
            }
        }
        return map;
    }

    private static Dictionary<JoyAxis, GamepadAxis> BuildPadAxisMap()
    {
        var map = new Dictionary<JoyAxis, GamepadAxis>();
        for (int i = 0; i < (int)JoyAxis.Max; i++)
        {
            var godotAxis = (JoyAxis)i;
            if (Enum.TryParse<GamepadAxis>(godotAxis.ToString(), out var pad))
                map[godotAxis] = pad;
        }
        return map;
    }

    private static bool ActionPressed(string name) =>
        InputMap.HasAction(name) && Input.IsActionPressed(name);

    private static bool ActionJustPressed(string name) =>
        InputMap.HasAction(name) && Input.IsActionJustPressed(name);

    private static bool ActionJustReleased(string name) =>
        InputMap.HasAction(name) && Input.IsActionJustReleased(name);

    private static void SendAxis(InputService inputService, string name, float value) =>
        inputService.SendEvent(new V12.Core.Input.InputEvent
        {
            Type = InputEventType.Axis,
            Name = name,
            Value = value,
        });

    private static void SendButton(InputService inputService, string name, bool down) =>
        inputService.SendEvent(new V12.Core.Input.InputEvent
        {
            Type = down ? InputEventType.ButtonDown : InputEventType.ButtonUp,
            Name = name,
            Value = down ? 1 : 0,
        });

    private static Dictionary<Key, InputKey> BuildKeyMap()
    {
        var map = new Dictionary<Key, InputKey>();
        void Add(Key godot, InputKey v12) => map[godot] = v12;

        for (char c = 'A'; c <= 'Z'; c++)
            Add(Enum.Parse<Key>(c.ToString()), Enum.Parse<InputKey>(c.ToString()));

        for (int i = 0; i <= 9; i++)
            Add(Enum.Parse<Key>("Key" + i), Enum.Parse<InputKey>("D" + i));

        for (int i = 1; i <= 24; i++)
            Add(Enum.Parse<Key>("F" + i), Enum.Parse<InputKey>("F" + i));

        for (int i = 0; i <= 9; i++)
            Add(Enum.Parse<Key>("Kp" + i), Enum.Parse<InputKey>("NumPad" + i));

        Add(Key.KpAdd, InputKey.Add);
        Add(Key.KpSubtract, InputKey.Subtract);
        Add(Key.KpMultiply, InputKey.Multiply);
        Add(Key.KpDivide, InputKey.Divide);
        Add(Key.KpPeriod, InputKey.Decimal);
        Add(Key.KpEnter, InputKey.Enter);
        Add(Key.Numlock, InputKey.NumLock);
        Add(Key.Space, InputKey.Space);
        Add(Key.Enter, InputKey.Enter);
        Add(Key.Escape, InputKey.Escape);
        Add(Key.Tab, InputKey.Tab);
        Add(Key.Backspace, InputKey.Back);
        Add(Key.Capslock, InputKey.CapsLock);
        Add(Key.Delete, InputKey.Delete);
        Add(Key.Insert, InputKey.Insert);
        Add(Key.Home, InputKey.Home);
        Add(Key.End, InputKey.End);
        Add(Key.Pageup, InputKey.PageUp);
        Add(Key.Pagedown, InputKey.PageDown);
        Add(Key.Up, InputKey.Up);
        Add(Key.Down, InputKey.Down);
        Add(Key.Left, InputKey.Left);
        Add(Key.Right, InputKey.Right);
        Add(Key.Print, InputKey.PrintScreen);
        Add(Key.Scrolllock, InputKey.Scroll);
        Add(Key.Pause, InputKey.Pause);
        Add(Key.Semicolon, InputKey.OemSemicolon);
        Add(Key.Equal, InputKey.OemPlus);
        Add(Key.Comma, InputKey.OemComma);
        Add(Key.Minus, InputKey.OemMinus);
        Add(Key.Period, InputKey.OemPeriod);
        Add(Key.Slash, InputKey.OemQuestion);
        Add(Key.Quoteleft, InputKey.OemTilde);
        Add(Key.Bracketleft, InputKey.OemOpenBrackets);
        Add(Key.Backslash, InputKey.OemBackslash);
        Add(Key.Bracketright, InputKey.OemCloseBrackets);
        Add(Key.Apostrophe, InputKey.OemQuotes);

        return map;
    }
}
