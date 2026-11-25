using System.IO;
using System.Linq;
using V12.Components.UI;
using V12.Core;
using V12.Core.Rendering;
using V12.WorldML;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Guards the UI showcase sample: every widget it advertises must survive the
/// WorldML-to-UIFrame capture. This is also the regression test for the
/// ambiguous <c>ButtonComponent</c> / <c>ProgressBarComponent</c> names, which
/// have a legacy twin in <c>V12.Components</c>.
/// </summary>
public sealed class UiShowcaseCaptureTests
{
    [Fact]
    public void Showcase_World_CapturesEveryWidgetKind()
    {
        var frame = CaptureShowcase();

        var hud = Assert.Single(frame.Nodes, n => n.Name == "Hud");
        Assert.Equal(UIWidgetKind.Canvas, hud.Kind);
        Assert.True(hud.ScreenSpace);

        var panel = Assert.Single(frame.Nodes, n => n.Name == "Panel");
        Assert.Equal(UIWidgetKind.Canvas, panel.Kind);
        Assert.False(panel.ScreenSpace);

        foreach (var kind in new[]
                 {
                     UIWidgetKind.Label,
                     UIWidgetKind.Button,
                     UIWidgetKind.Toggle,
                     UIWidgetKind.Checkbox,
                     UIWidgetKind.Slider,
                     UIWidgetKind.ProgressBar,
                     UIWidgetKind.TextInput,
                     UIWidgetKind.Tabs,
                     UIWidgetKind.Table,
                     UIWidgetKind.Dropdown,
                     UIWidgetKind.Separator,
                     UIWidgetKind.Spinner,
                     UIWidgetKind.PropertyGrid,
                     UIWidgetKind.Rect,
                     UIWidgetKind.VLayout,
                     UIWidgetKind.HLayout,
                 })
        {
            Assert.Contains(frame.Nodes, n => n.Kind == kind);
        }

        // The fully qualified names must resolve the UI widgets, not the legacy
        // V12.Components.ButtonComponent / ProgressBarComponent.
        var pulse = Assert.Single(frame.Nodes, n => n.Name == "PulseButton");
        Assert.Equal(UIWidgetKind.Button, pulse.Kind);
        Assert.Equal("Pulse", pulse.Text);

        var power = Assert.Single(frame.Nodes, n => n.Name == "PowerBar");
        Assert.Equal(UIWidgetKind.ProgressBar, power.Kind);

        // Tabs are authored as nodes; the strip titles come from C#.
        var tabs = Assert.Single(frame.Nodes, n => n.Name == "Tabs");
        Assert.Equal(UIWidgetKind.Tabs, tabs.Kind);
        Assert.Equal(new[] { "Controls", "Data", "About" }, tabs.Items.ToArray());
        Assert.Equal(
            new[] { 0, 1, 2 },
            frame.Nodes.Where(n => n.TabIndex >= 0).Select(n => n.TabIndex).OrderBy(i => i).ToArray());

        // List data is authored in WorldML, not filled by the controller.
        var table = Assert.Single(frame.Nodes, n => n.Name == "Table");
        Assert.Equal(new[] { "Signal", "Live" }, table.Columns.ToArray());
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("Pulse count", table.Rows[0][0]);

        var dropdown = Assert.Single(frame.Nodes, n => n.Name == "Choice");
        Assert.Equal(new[] { "Amber", "Bone", "Ember", "Linen" }, dropdown.Items.ToArray());
    }

    [Fact]
    public void InactiveCanvas_IsSkippedByCapture()
    {
        _ = new GameRoot();
        var world = new World("Scene");
        var hud = new Element("Hud");
        hud.AddComponent(new CanvasComponent { ScreenSpace = true });
        world.AddElement(hud);

        hud.Active = false;
        var hidden = GameRoot.Instance!.CaptureUI(world);
        Assert.DoesNotContain(hidden.Nodes, n => n.Name == "Hud");

        hud.Active = true;
        var visible = GameRoot.Instance!.CaptureUI(world);
        Assert.Contains(visible.Nodes, n => n.Name == "Hud");
    }

    [Fact]
    public void Showcase_World_ParsesWithoutAmbiguity()
    {
        var frame = CaptureShowcase();

        // Both canvases plus widgets; a name-resolution failure would leave an
        // element as a bare shell with no node.
        Assert.True(frame.Nodes.Count > 20, $"expected a populated UI frame, got {frame.Nodes.Count} nodes");
        Assert.DoesNotContain(frame.Nodes, n => n.Name == "PulseButton" && n.Kind == UIWidgetKind.VLayout);
    }

    private static UIFrame CaptureShowcase()
    {
        string root = FindRepoRoot();
        string worldPath = Path.Combine(root, "V12-Samples", "UiShowcase", "Scenes", "Main.worldml");
        Assert.True(File.Exists(worldPath), worldPath);

        _ = new GameRoot();
        var contents = new WorldMLParser().ParseFile(worldPath);
        var world = new World(contents.Name ?? "Scene");
        world.AddElement(contents);

        return GameRoot.Instance!.CaptureUI(world);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "V12-Monorepo.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? System.AppContext.BaseDirectory;
    }
}
