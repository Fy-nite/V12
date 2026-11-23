using System;
using System.Numerics;
using V12.Core.Input;
using V12.Core.Interfaces.Renderer;
using V12.Core.Rendering;
using V12.Core.UI.Paper;
using V12.Paper;
using V12.Rendering;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Engine-side world-UI picking: camera plus canvas placement only, so it works
/// with no renderer attached.
/// </summary>
public sealed class WorldUiPickingTests
{
    private static FrameSnapshot Snapshot(params WorldCanvasSnapshot[] canvases)
    {
        var snapshot = new FrameSnapshot();
        snapshot.Renderables.Add(new RenderableSnapshot
        {
            ElementId = 1,
            NodeType = SnapshotNodeType.Camera,
            Transform = Matrix4x4.CreateTranslation(0f, 1f, 5f),
            IsCurrentCamera = true,
            Fov = 60f,
            NearClip = 0.1f,
            FarClip = 100f,
            Projection = CameraProjection.Perspective,
            LayerMask = RenderLayers.Default,
        });
        snapshot.Canvases.AddRange(canvases);
        return snapshot;
    }

    private static WorldCanvasSnapshot Canvas(long id, Vector3 position, bool interactive = true, int pixelWidth = 400, int pixelHeight = 200)
        => WorldCanvasSnapshot.Create(
            id,
            Guid.Empty,
            pixelWidth,
            pixelHeight,
            Matrix4x4.CreateTranslation(position),
            interactive: interactive);

    [Fact]
    public void PointerAtCenter_HitsCanvasCenterPixel()
    {
        var snapshot = Snapshot(Canvas(10, new Vector3(0f, 1f, -2f)));

        Assert.True(WorldUiPicking.TryPick(snapshot, new Vector2(640f, 360f), 1280, 720, out var hit));
        Assert.Equal(10, hit.CanvasId);
        Assert.Equal(200f, hit.PixelX, 1);
        Assert.Equal(100f, hit.PixelY, 1);
        // Unprojection starts at the near plane, so the distance is depth - near.
        Assert.Equal(6.9f, hit.Distance, 1);
    }

    [Fact]
    public void PointerOutsideCanvas_Misses()
    {
        var snapshot = Snapshot(Canvas(10, new Vector3(0f, 1f, -2f)));

        Assert.False(WorldUiPicking.TryPick(snapshot, new Vector2(1200f, 360f), 1280, 720, out _));
    }

    [Fact]
    public void NearestCanvasWins()
    {
        var snapshot = Snapshot(
            Canvas(10, new Vector3(0f, 1f, -2f)),
            Canvas(11, new Vector3(0f, 1f, -5f)));

        Assert.True(WorldUiPicking.TryPick(snapshot, new Vector2(640f, 360f), 1280, 720, out var hit));
        Assert.Equal(10, hit.CanvasId);
    }

    [Fact]
    public void NonInteractiveCanvas_IsIgnored()
    {
        var snapshot = Snapshot(Canvas(10, new Vector3(0f, 1f, -2f), interactive: false));

        Assert.False(WorldUiPicking.TryPick(snapshot, new Vector2(640f, 360f), 1280, 720, out _));
    }

    [Fact]
    public void CanvasBehindCamera_Misses()
    {
        var snapshot = Snapshot(Canvas(10, new Vector3(0f, 1f, 9f)));

        Assert.False(WorldUiPicking.TryPick(snapshot, new Vector2(640f, 360f), 1280, 720, out _));
    }
}

/// <summary>
/// World-canvas input routing: hover, click-to-focus, keyboard focus and the
/// capture flags gameplay reads. No renderer is involved at any point.
/// </summary>
public sealed class WorldUiInputTests
{
    private const long CanvasId = 50;
    private const long ButtonId = 51;
    private const int ScreenWidth = 1280;
    private const int ScreenHeight = 720;

    private static (InputService Input, RenderFrameProducer Producer, NullRenderer Renderer) CreateHost()
    {
        V12.Paper.PaperShaderAssets.EnsureRegistered();
        var renderer = new NullRenderer();
        var input = new InputService();
        var producer = new RenderFrameProducer(
            renderer, ScreenWidth, ScreenHeight, input: input, uiSource: new PaperUiSource());
        return (input, producer, renderer);
    }

    private static FrameSnapshot Snapshot()
    {
        var snapshot = new FrameSnapshot();
        snapshot.Renderables.Add(new RenderableSnapshot
        {
            ElementId = 1,
            NodeType = SnapshotNodeType.Camera,
            Transform = Matrix4x4.Identity,
            IsCurrentCamera = true,
            Fov = 60f,
            NearClip = 0.1f,
            FarClip = 100f,
            Projection = CameraProjection.Perspective,
            LayerMask = RenderLayers.Default,
        });
        snapshot.Canvases.Add(WorldCanvasSnapshot.Create(
            CanvasId,
            Guid.Empty,
            400,
            200,
            Matrix4x4.CreateTranslation(0f, 0f, -3f)));
        return snapshot;
    }

    private static UiFrameContext Ui(Action? onClick)
    {
        var captured = new UIFrame();
        captured.Nodes.Add(new UINode
        {
            Id = CanvasId,
            ParentId = 0,
            Kind = UIWidgetKind.Canvas,
            ScreenSpace = false,
            Width = 400,
            Height = 200,
        });
        captured.Nodes.Add(new UINode
        {
            Id = ButtonId,
            ParentId = CanvasId,
            Kind = UIWidgetKind.Button,
            PreferredWidth = 400,
            PreferredHeight = 200,
            Text = "OK",
            OnClick = onClick,
        });
        return new UiFrameContext(
            captured,
            new PaperFrame { Width = ScreenWidth, Height = ScreenHeight },
            ScreenWidth,
            ScreenHeight);
    }

    private static void Build(RenderFrameProducer producer, UiFrameContext context)
    {
        Assert.True(producer.TryBuild(Snapshot(), null, 0f, 0f, context, out var lease));
        Assert.NotNull(lease);
        lease!.Release();
    }

    private static InputEvent PointerMove(float x, float y) => new()
    {
        Type = InputEventType.PointerMove,
        Pointer = new Vector2(x, y),
    };

    private static InputEvent LeftButton(bool down, float x = 640f, float y = 360f) => new()
    {
        Type = down ? InputEventType.ButtonDown : InputEventType.ButtonUp,
        Button = PointerButton.Left,
        IsPressed = down,
        Pointer = new Vector2(x, y),
    };

    [Fact]
    public void DirectSurfaceFeed_ClicksButton()
    {
        V12.Paper.PaperShaderAssets.EnsureRegistered();
        using var system = new PaperWorldCanvasSystem((_, _, _) => new PaperCanvasRenderer());

        int clicks = 0;
        var ui = Ui(() => clicks++);
        system.Update(ui.Captured, null);

        var surface = system.Surfaces[CanvasId];
        surface.FeedPointer(200f, 100f, true);
        system.Step();
        surface.FeedPointer(200f, 100f, false);
        system.Step();

        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Hover_FeedsCanvas_AndCapturesPointer()
    {
        var (input, producer, renderer) = CreateHost();
        using var _ = renderer;
        using var __ = producer;

        var ui = Ui(null);
        Build(producer, ui);

        input.SendEvent(PointerMove(640f, 360f));
        Build(producer, ui);

        Assert.Equal(CanvasId, producer.WorldUi!.HoveredCanvasId);
        Assert.True(input.UiCapturesPointer);

        input.SendEvent(PointerMove(1200f, 360f));
        Build(producer, ui);

        Assert.Equal(0, producer.WorldUi.HoveredCanvasId);
        Assert.False(input.UiCapturesPointer);
    }

    [Fact]
    public void Click_InvokesWidget_AndFocusesCanvas()
    {
        var (input, producer, renderer) = CreateHost();
        using var _ = renderer;
        using var __ = producer;

        int clicks = 0;
        var ui = Ui(() => clicks++);
        Build(producer, ui);

        input.SendEvent(PointerMove(640f, 360f));
        input.SendEvent(LeftButton(true));
        Build(producer, ui);

        Assert.Equal(CanvasId, producer.WorldUi!.FocusedCanvasId);
        Assert.True(input.UiCapturesKeyboard);

        input.SendEvent(LeftButton(false));
        Build(producer, ui);

        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Escape_ClearsFocus()
    {
        var (input, producer, renderer) = CreateHost();
        using var _ = renderer;
        using var __ = producer;

        var ui = Ui(null);
        Build(producer, ui);

        input.SendEvent(PointerMove(640f, 360f));
        input.SendEvent(LeftButton(true));
        Build(producer, ui);
        Assert.Equal(CanvasId, producer.WorldUi!.FocusedCanvasId);

        input.SendEvent(new InputEvent
        {
            Type = InputEventType.ButtonDown,
            Key = InputKey.Escape,
            IsPressed = true,
        });

        Assert.Equal(0, producer.WorldUi.FocusedCanvasId);
        Assert.False(input.UiCapturesKeyboard);
    }

    [Fact]
    public void ClickingEmptySpace_ClearsFocus()
    {
        var (input, producer, renderer) = CreateHost();
        using var _ = renderer;
        using var __ = producer;

        var ui = Ui(null);
        Build(producer, ui);

        input.SendEvent(PointerMove(640f, 360f));
        input.SendEvent(LeftButton(true));
        Build(producer, ui);
        Assert.Equal(CanvasId, producer.WorldUi!.FocusedCanvasId);

        input.SendEvent(LeftButton(false));
        input.SendEvent(PointerMove(1200f, 360f));
        input.SendEvent(LeftButton(true, 1200f, 360f));
        Build(producer, ui);

        Assert.Equal(0, producer.WorldUi.FocusedCanvasId);
        Assert.False(input.UiCapturesKeyboard);
    }
}
