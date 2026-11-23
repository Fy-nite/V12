using System.Numerics;
using V12.Components.UI;
using V12.Core;
using V12.Core.Input;
using V12.Core.Rendering;
using V12.Core.UI;
using V12.Paper;
using V12.WorldML;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Screen-space Paper widgets are driven by real pointer events. Tabs in
/// particular used to look dead because a click changed the component but
/// nothing requested the repaint that would show the new page.
/// </summary>
public sealed class ScreenUiTabsTests
{
    [Fact]
    public void ScreenTabs_ClickSelectsTabAndRequestsRepaint()
    {
        _ = new GameRoot();
        var input = new InputService();
        using var ui = new PaperUiSystem(input, 480, 240);

        var world = BuildWorld(out var tabs);
        UiActions.Register("test-tab", args => _selected = args.Index);

        try
        {
            ui.Apply(GameRoot.Instance!.CaptureUI(world));

            // Find where the second tab sits by clicking across the strip.
            (int X, int Y) point = default;
            bool found = false;
            for (int y = 0; y <= 90 && !found; y += 2)
            {
                for (int x = 2; x <= 300 && !found; x += 2)
                {
                    Click(input, ui, world, x, y);
                    if (_selected == 1)
                    {
                        point = (x, y);
                        found = true;
                    }
                }
            }

            Assert.True(found, "no pointer position selected the second tab");

            // From a clean slate, one click must both select the tab and ask
            // for a repaint so the new page is captured next frame.
            tabs.SelectedIndex = 0;
            ui.Apply(GameRoot.Instance!.CaptureUI(world));
            GameRoot.Instance!.ConsumeRenderDirty();
            _selected = -1;

            Click(input, ui, world, point.X, point.Y);

            Assert.Equal(1, _selected);
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.True(GameRoot.Instance!.ConsumeRenderDirty(), "a click must request a repaint");

            var next = GameRoot.Instance!.CaptureUI(world);
            Assert.Equal(1, Assert.Single(next.Nodes, n => n.Name == "Tabs").SelectedIndex);
            Assert.Contains(next.Nodes, n => n.Name == "P1" && n.TabIndex == 1);
        }
        finally
        {
            UiActions.Unregister("test-tab");
        }
    }

    [Fact]
    public void TogglingAWidget_RequestsRepaint()
    {
        _ = new GameRoot();

        var toggle = new ToggleComponent { Label = "Spin", Action = "test-toggle" };
        new Element("Toggle").AddComponent(toggle);

        GameRoot.Instance!.ConsumeRenderDirty();
        toggle.SetToggled(true);

        Assert.True(toggle.IsOn);
        Assert.True(GameRoot.Instance!.ConsumeRenderDirty());
    }

    private static int _selected = -1;

    private static World BuildWorld(out TabsComponent tabs)
    {
        const string xml = """
            <World name="W">
              <Element name="Canvas">
                <Component type="CanvasComponent" ScreenSpace="true" AnchorX="0" AnchorY="0" />
                <Element name="Tabs">
                  <Component type="TabsComponent" Tabs="Alpha,Beta" SelectedIndex="0" Action="test-tab" />
                  <Element name="P0">
                    <Component type="TabPageComponent" Index="0" Title="Alpha" />
                    <Element name="L0"><Component type="LabelComponent" Text="page-zero" /></Element>
                  </Element>
                  <Element name="P1">
                    <Component type="TabPageComponent" Index="1" Title="Beta" />
                    <Element name="L1"><Component type="LabelComponent" Text="page-one" /></Element>
                  </Element>
                </Element>
              </Element>
            </World>
            """;

        var contents = new WorldMLParser().Parse(xml);
        var world = new World("W");
        world.AddElement(contents);

        tabs = world.Root[0].FindChildByNameRecursive("Tabs")!.GetComponent<TabsComponent>()!;
        return world;
    }

    private static void Click(InputService input, PaperUiSystem ui, World world, int x, int y)
    {
        var pointer = new Vector2(x, y);
        input.SendEvent(new InputEvent
        {
            Type = InputEventType.PointerMove,
            Pointer = pointer,
        });
        input.SendEvent(new InputEvent
        {
            Type = InputEventType.ButtonDown,
            Button = PointerButton.Left,
            IsPressed = true,
            Pointer = pointer,
        });
        ui.Apply(GameRoot.Instance!.CaptureUI(world));
        input.SendEvent(new InputEvent
        {
            Type = InputEventType.ButtonUp,
            Button = PointerButton.Left,
            Pointer = pointer,
        });
        ui.Apply(GameRoot.Instance!.CaptureUI(world));
    }
}
