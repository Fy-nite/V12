using System.Linq;
using V12.Components;
using V12.Components.UI;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Rendering;
using V12.Core.UI;
using UIButton = V12.Components.UI.ButtonComponent;
using V12.WorldML;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// WorldML should carry list data and name-bound actions, so widgets that used
/// to need a C# controller can be authored declaratively.
/// </summary>
public sealed class UiWorldMLTests
{
    [Fact]
    public void ListProperties_AreAuthoredInWorldML()
    {
        const string xml = """
            <World name="W">
              <Element name="E">
                <Component type="V12.Components.UI.TabsComponent" Tabs="One,Two,Three" SelectedIndex="1" />
                <Component type="V12.Components.UI.DropdownComponent" Items="Red,Green,Blue" />
                <Component type="V12.Components.UI.TableComponent" RowHeight="20">
                  <Columns>
                    <Item Header="Signal" Flex="2" />
                    <Item Header="Live" Flex="1" />
                  </Columns>
                  <Rows>
                    <Item>
                      <Cells>
                        <Item>Pulse</Item>
                        <Item>3</Item>
                      </Cells>
                    </Item>
                  </Rows>
                </Component>
              </Element>
            </World>
            """;

        var world = new WorldMLParser().Parse(xml);
        var element = world.FindChildByNameRecursive("E");
        Assert.NotNull(element);

        var tabs = element!.GetComponent<TabsComponent>();
        Assert.NotNull(tabs);
        Assert.Equal(new[] { "One", "Two", "Three" }, tabs!.Tabs.ToArray());
        Assert.Equal(1, tabs.SelectedIndex);

        var dropdown = element.GetComponent<DropdownComponent>();
        Assert.NotNull(dropdown);
        Assert.Equal(new[] { "Red", "Green", "Blue" }, dropdown!.Items.ToArray());

        var table = element.GetComponent<TableComponent>();
        Assert.NotNull(table);
        Assert.Equal(new[] { "Signal", "Live" }, table!.Columns.Select(c => c.Header).ToArray());
        Assert.Equal(2f, table.Columns[0].Flex);
        Assert.Single(table.Rows);
        Assert.Equal(new[] { "Pulse", "3" }, table.Rows[0].Cells.ToArray());
    }

    [Fact]
    public void ButtonAction_InvokesRegisteredHandler()
    {
        int calls = 0;
        int index = -1;

        UiActions.Register("test-pulse", args => { calls++; index = args.Index; });
        try
        {
            var button = new UIButton { Label = "Pulse", Action = "test-pulse" };
            var element = new Element("Button");
            element.AddComponent(button);

            button.InvokeClick();

            Assert.Equal(1, calls);
            Assert.Equal(-1, index);
            Assert.Same(element, button.Owner);
        }
        finally
        {
            UiActions.Unregister("test-pulse");
        }
    }

    [Fact]
    public void Delegate_WinsOverNamedAction()
    {
        int delegateCalls = 0;
        int actionCalls = 0;

        UiActions.Register("test-both", _ => actionCalls++);
        try
        {
            var button = new UIButton
            {
                Label = "Both",
                Action = "test-both",
                OnClick = () => delegateCalls++,
            };
            new Element("Button").AddComponent(button);

            button.InvokeClick();

            Assert.Equal(1, delegateCalls);
            Assert.Equal(0, actionCalls);
        }
        finally
        {
            UiActions.Unregister("test-both");
        }
    }

    [Fact]
    public void Toggle_StateSurvivesCaptureThroughToTheComponent()
    {
        _ = new GameRoot();

        var world = new WorldMLParser().Parse("""
            <World name="W">
              <Element name="Canvas">
                <Component type="CanvasComponent" ScreenSpace="true" />
                <Element name="Toggle">
                  <Component type="ToggleComponent" Label="Spin" Action="nobody-registered-this" />
                </Element>
              </Element>
            </World>
            """);

        var uiWorld = new World(world.Name ?? "W");
        uiWorld.AddElement(world);

        var frame = GameRoot.Instance!.CaptureUI(uiWorld);
        var node = Assert.Single(frame.Nodes, n => n.Kind == UIWidgetKind.Toggle);
        Assert.False(node.Bool);

        // This is what the renderer does on click.
        node.OnToggled!(true);

        var toggle = uiWorld.Root[0].FindChildByNameRecursive("Toggle")!.GetComponent<ToggleComponent>();
        Assert.NotNull(toggle);
        Assert.True(toggle!.IsOn);

        // The next capture reads the component, so the widget no longer snaps back.
        var next = GameRoot.Instance!.CaptureUI(uiWorld);
        Assert.True(Assert.Single(next.Nodes, n => n.Kind == UIWidgetKind.Toggle).Bool);
    }

    [Fact]
    public void Slider_ClampsAndRaisesNamedAction()
    {
        float seen = -1f;
        UiActions.Register("test-power", args => seen = args.Value);
        try
        {
            var slider = new SliderComponent { Min = 0f, Max = 1f, Step = 0.1f, Action = "test-power" };
            new Element("Slider").AddComponent(slider);

            slider.SetValue(1.8f);
            Assert.Equal(1f, slider.Value);
            Assert.Equal(1f, seen);

            slider.SetValue(0.44f);
            Assert.Equal(0.4f, slider.Value, 3);
        }
        finally
        {
            UiActions.Unregister("test-power");
        }
    }
}
