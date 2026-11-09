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

    private static InputEvent Button(InputEventType type, InputKey key) => new()
    {
        Type = type,
        Name = key.ToString(),
        Key = key,
    };
}
