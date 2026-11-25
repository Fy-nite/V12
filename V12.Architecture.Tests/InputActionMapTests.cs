using V12.Core.Input;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Covers the key-based binding path on <see cref="InputActionMap"/>: physical
/// <see cref="InputKey"/> buttons, held-key axes, name fallback and rebinding.
/// </summary>
public sealed class InputActionMapTests
{
    [Fact]
    public void KeyButton_TracksHeldAndEdges()
    {
        var map = new InputActionMap();
        map.BindButton("Jump", InputKey.Space);

        map.OnInputEvent(Button(InputEventType.ButtonDown, InputKey.Space));
        map.Update(0f);

        Assert.True(map.GetButton("Jump"));
        Assert.True(map.GetButtonDown("Jump"));
        Assert.False(map.GetButtonUp("Jump"));

        map.Update(0f);

        Assert.True(map.GetButton("Jump"));
        Assert.False(map.GetButtonDown("Jump"));

        map.OnInputEvent(Button(InputEventType.ButtonUp, InputKey.Space));
        map.Update(0f);

        Assert.False(map.GetButton("Jump"));
        Assert.False(map.GetButtonDown("Jump"));
        Assert.True(map.GetButtonUp("Jump"));
    }

    [Fact]
    public void KeyAxis_ReadsHeldKeys()
    {
        var map = new InputActionMap();
        map.BindAxis("MoveX", InputKey.D, InputKey.A);

        map.OnInputEvent(Button(InputEventType.ButtonDown, InputKey.D));
        map.Update(0f);
        Assert.Equal(1f, map.GetAxis("MoveX"));

        map.OnInputEvent(Button(InputEventType.ButtonUp, InputKey.D));
        map.Update(0f);
        Assert.Equal(0f, map.GetAxis("MoveX"));

        map.OnInputEvent(Button(InputEventType.ButtonDown, InputKey.A));
        map.Update(0f);
        Assert.Equal(-1f, map.GetAxis("MoveX"));

        map.OnInputEvent(Button(InputEventType.ButtonDown, InputKey.D));
        map.Update(0f);
        Assert.Equal(0f, map.GetAxis("MoveX"));
    }

    [Fact]
    public void KeyBinding_FallsBackToEventName()
    {
        var map = new InputActionMap();
        map.BindButton("Jump", InputKey.Space);

        map.OnInputEvent(new InputEvent { Type = InputEventType.ButtonDown, Name = "Space" });
        map.Update(0f);

        Assert.True(map.GetButton("Jump"));
    }

    [Fact]
    public void RebindButton_SwitchesKey()
    {
        var map = new InputActionMap();
        map.BindButton("Jump", InputKey.Space);
        map.RebindButton("Jump", InputKey.F);

        Assert.Equal(InputKey.F, map.GetButtonKey("Jump"));

        map.OnInputEvent(Button(InputEventType.ButtonDown, InputKey.Space));
        map.Update(0f);
        Assert.False(map.GetButton("Jump"));

        map.OnInputEvent(Button(InputEventType.ButtonDown, InputKey.F));
        map.Update(0f);
        Assert.True(map.GetButton("Jump"));
    }

    [Fact]
    public void NameBinding_StillWorks()
    {
        var map = new InputActionMap();
        map.BindButton("Jump", "jump");
        map.BindAxis("MoveX", "move_right", "move_left");

        map.OnInputEvent(new InputEvent { Type = InputEventType.ButtonDown, Name = "jump" });
        map.OnInputEvent(new InputEvent { Type = InputEventType.Axis, Name = "move_right", Value = 0.5f });
        map.Update(0f);

        Assert.True(map.GetButtonDown("Jump"));
        Assert.Equal(0.5f, map.GetAxis("MoveX"));
    }

    [Fact]
    public void DeltaAxis_KeepsTravelAboveOne()
    {
        var map = new InputActionMap();
        map.BindDeltaAxis("LookX", "mouse_dx", "");

        map.OnInputEvent(new InputEvent
        {
            Type = InputEventType.Axis,
            Name = "mouse_dx",
            Value = 42.5f,
        });
        map.Update(0f);

        Assert.Equal(42.5f, map.GetAxis("LookX"));
    }

    [Fact]
    public void NameAxis_ClampsToStickRange()
    {
        var map = new InputActionMap();
        map.BindAxis("LookX", "mouse_dx", "");

        map.OnInputEvent(new InputEvent
        {
            Type = InputEventType.Axis,
            Name = "mouse_dx",
            Value = 42.5f,
        });
        map.Update(0f);

        Assert.Equal(1f, map.GetAxis("LookX"));
    }

    [Fact]
    public void NameAxis_KeepsNegativeDirection()
    {
        var map = new InputActionMap();
        map.BindAxis("MoveX", "xr_left_stick_x", "");

        map.OnInputEvent(new InputEvent
        {
            Type = InputEventType.Axis,
            Name = "xr_left_stick_x",
            Value = -0.75f,
        });
        map.Update(0f);

        Assert.Equal(-0.75f, map.GetAxis("MoveX"));
    }

    [Fact]
    public void NameAxis_NegativeBindingMapsStickUpToForward()
    {
        var map = new InputActionMap();
        map.BindAxis("MoveZ", "", "xr_left_stick_y");
        map.AddAxis("MoveZ", InputKey.S, InputKey.W);

        map.OnInputEvent(new InputEvent
        {
            Type = InputEventType.Axis,
            Name = "xr_left_stick_y",
            Value = 1f,
        });
        map.Update(0f);

        // The stick reports +1 when pushed forward; the game's forward is -Z
        // (same sign as W), so the negative binding must produce -1.
        Assert.Equal(-1f, map.GetAxis("MoveZ"));

        map.OnInputEvent(new InputEvent
        {
            Type = InputEventType.Axis,
            Name = "xr_left_stick_y",
            Value = -1f,
        });
        map.Update(0f);

        Assert.Equal(1f, map.GetAxis("MoveZ"));
    }

    [Fact]
    public void NameButton_QuickTapBetweenUpdatesLatchesBothEdges()
    {
        var map = new InputActionMap();
        map.BindButton("Capture", "mouse_right");

        // A quick click can arrive and release before the next fixed step.
        map.OnInputEvent(new InputEvent { Type = InputEventType.ButtonDown, Name = "mouse_right" });
        map.OnInputEvent(new InputEvent { Type = InputEventType.ButtonUp, Name = "mouse_right" });
        map.Update(0f);

        Assert.False(map.GetButton("Capture"));
        Assert.True(map.GetButtonDown("Capture"));
        Assert.True(map.GetButtonUp("Capture"));

        map.Update(0f);

        Assert.False(map.GetButtonDown("Capture"));
        Assert.False(map.GetButtonUp("Capture"));
    }

    [Fact]
    public void KeyButton_QuickTapBetweenUpdatesLatchesBothEdges()
    {
        var map = new InputActionMap();
        map.BindButton("Jump", InputKey.Space);

        map.OnInputEvent(Button(InputEventType.ButtonDown, InputKey.Space));
        map.OnInputEvent(Button(InputEventType.ButtonUp, InputKey.Space));
        map.Update(0f);

        Assert.False(map.GetButton("Jump"));
        Assert.True(map.GetButtonDown("Jump"));
        Assert.True(map.GetButtonUp("Jump"));

        map.Update(0f);

        Assert.False(map.GetButtonDown("Jump"));
        Assert.False(map.GetButtonUp("Jump"));
    }

    private static InputEvent Button(InputEventType type, InputKey key) => new()
    {
        Type = type,
        Name = key.ToString(),
        Key = key,
    };
}
