using V12.Core.Input;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Covers <see cref="InputService"/> dispatch semantics: raw key edges must
/// reach game action maps even when a UI handler consumes them, while pointer
/// events still stop at the first consumer.
/// </summary>
public sealed class InputServiceTests
{
    [Fact]
    public void RawKeyEdge_ReachesGameActionMap_AfterUiHandlerConsumes()
    {
        var input = new InputService();
        input.RegisterHandler(new ConsumingHandler());

        var map = new InputActionMap();
        map.BindButton("Boost", InputKey.B);
        map.Attach(input);

        input.SendEvent(new InputEvent
        {
            Type = InputEventType.ButtonDown,
            Name = nameof(InputKey.B),
            Key = InputKey.B,
        });
        map.Update(0f);

        Assert.True(map.GetButtonDown("Boost"));

        input.SendEvent(new InputEvent
        {
            Type = InputEventType.ButtonUp,
            Name = nameof(InputKey.B),
            Key = InputKey.B,
        });
        map.Update(0f);

        Assert.True(map.GetButtonUp("Boost"));
    }

    [Fact]
    public void PointerEvent_StopsAtHandled()
    {
        var input = new InputService();
        var first = new ConsumingHandler();
        var second = new RecordingHandler();
        input.RegisterHandler(first);
        input.RegisterHandler(second);

        input.SendEvent(new InputEvent { Type = InputEventType.PointerMove });

        Assert.True(first.SawPointer);
        Assert.False(second.SawPointer);
    }

    private sealed class ConsumingHandler : IInputHandler
    {
        public bool SawPointer { get; private set; }

        public void OnInputEvent(InputEvent evt)
        {
            if (evt.Type == InputEventType.PointerMove)
                SawPointer = true;

            if (evt.Key.HasValue ||
                evt.Type is InputEventType.PointerMove)
            {
                evt.Handled = true;
            }
        }
    }

    private sealed class RecordingHandler : IInputHandler
    {
        public bool SawPointer { get; private set; }

        public void OnInputEvent(InputEvent evt)
        {
            if (evt.Type == InputEventType.PointerMove)
                SawPointer = true;
        }
    }
}
