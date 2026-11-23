using System.Linq;
using V12.Components.UI;
using V12.Core;
using V12.Core.Rendering;
using V12.Core.UI.Paper;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// World-space canvases (<c>CanvasComponent.ScreenSpace == false</c>) must be
/// stepped by <see cref="PaperWorldCanvasSystem"/> into independent Paper
/// surfaces the host can bake onto world quads, and must not leak into the
/// screen-space overlay frame.
/// </summary>
public sealed class PaperWorldCanvasTests
{
    private static (GameRoot Root, ProwlPaperRenderer Paper) CreateHost()
    {
        var root = new GameRoot();
        var paper = new ProwlPaperRenderer(640, 360, 1f);
        paper.WorldCanvasRendererFactory = (_, _, _) => new PaperCanvasRenderer();
        root.Registry.RegisterOrReplace("IUIRenderer", paper);
        return (root, paper);
    }

    private static World CreateWorld(GameRoot root)
    {
        var world = new World("WorldUI") { TickingEnabled = false };
        root.Worlds.Add(world);
        root.SelectWorld(world);
        return world;
    }

    private static int VertexCount(PaperFrame? frame)
        => frame?.Meshes.Sum(m => m.Positions.Length) ?? 0;

    [Fact]
    public void WorldCanvas_GetsOwnSurface_WithLaidOutContent()
    {
        var (root, paper) = CreateHost();
        var world = CreateWorld(root);

        var canvas = new Element("Monitor");
        canvas.AddComponent(new CanvasComponent { ScreenSpace = false, Width = 400, Height = 225 });
        var label = new Element("Status");
        label.AddComponent(new LabelComponent("REACTOR ONLINE"));
        canvas.AddChild(label);
        world.AddElement(canvas);

        root.Initialize();
        root.MarkRenderDirty();
        root.V12Tick(0f);

        Assert.NotNull(paper.WorldCanvases);
        Assert.True(paper.WorldCanvases!.Surfaces.TryGetValue(canvas.Id, out var surface));
        Assert.Equal(400, surface!.PixelWidth);
        Assert.Equal(225, surface.PixelHeight);
        Assert.True(VertexCount(surface.LastFrame) > 0, "world surface should contain the label's glyphs");

        // The world canvas must not also draw into the screen overlay.
        Assert.Equal(0, VertexCount(paper.LastPaperFrame));
    }

    [Fact]
    public void ScreenCanvas_StillRendersIntoOverlay()
    {
        var (root, paper) = CreateHost();
        var world = CreateWorld(root);

        var canvas = new Element("Hud");
        canvas.AddComponent(new CanvasComponent { ScreenSpace = true });
        var label = new Element("Score");
        label.AddComponent(new LabelComponent("100"));
        canvas.AddChild(label);
        world.AddElement(canvas);

        root.Initialize();
        root.MarkRenderDirty();
        root.V12Tick(0f);

        Assert.True(VertexCount(paper.LastPaperFrame) > 0, "screen-space label should draw into the overlay");
        Assert.Empty(paper.WorldCanvases!.Surfaces);
    }

    [Fact]
    public void WorldCanvas_SurfaceRemoved_WhenElementRemoved()
    {
        var (root, paper) = CreateHost();
        var world = CreateWorld(root);

        var canvas = new Element("Monitor");
        canvas.AddComponent(new CanvasComponent { ScreenSpace = false, Width = 320, Height = 180 });
        var label = new Element("Status");
        label.AddComponent(new LabelComponent("OK"));
        canvas.AddChild(label);
        world.AddElement(canvas);

        root.Initialize();
        root.MarkRenderDirty();
        root.V12Tick(0f);
        Assert.Single(paper.WorldCanvases!.Surfaces);

        world.RemoveElement(canvas);
        root.MarkRenderDirty();
        root.V12Tick(0f);

        Assert.Empty(paper.WorldCanvases.Surfaces);
    }

    [Fact]
    public void WorldCanvas_PointerPressAndRelease_ClicksButton()
    {
        var (root, paper) = CreateHost();
        var world = CreateWorld(root);

        int clicks = 0;
        var canvas = new Element("Panel");
        canvas.AddComponent(new CanvasComponent { ScreenSpace = false, Width = 400, Height = 225 });
        var button = new Element("Press");
        button.AddComponent(new ButtonComponent("Press", () => clicks++));
        // Fill the whole surface so any interior pointer position hits the button.
        button.AddComponent(new LayoutElementComponent { PreferredWidth = 400, PreferredHeight = 225 });
        canvas.AddChild(button);
        world.AddElement(canvas);

        root.Initialize();
        root.MarkRenderDirty();
        root.V12Tick(0f);

        var surface = paper.WorldCanvases!.Surfaces[canvas.Id];
        surface.FeedPointer(200f, 100f, true);
        paper.step();
        surface.FeedPointer(200f, 100f, false);
        paper.step();

        Assert.Equal(1, clicks);
    }
}
