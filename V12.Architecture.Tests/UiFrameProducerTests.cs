using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using V12.Core.Interfaces.Renderer;
using V12.Core.Rendering;
using V12.Core.UI.Paper;
using V12.Paper;
using V12.Rendering;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// The producer owns the screen-space UI view and target. Hosts choose a
/// presentation policy; they never create, name or resize a UI target.
/// </summary>
public sealed class UiFrameProducerTests
{
    [Fact]
    public void PresentSurface_EmitsUiViewWithoutTarget()
    {
        using var renderer = new NullRenderer();
        using var producer = new RenderFrameProducer(renderer, 800, 300);

        Assert.True(producer.TryBuild(Snapshot(), null, 0f, 0f, Ui(800, 300), out var lease));
        Assert.NotNull(lease);

        var frame = lease!.Frame;
        Assert.Null(frame.PresentTarget);

        var ui = FindUiView(frame);
        Assert.Equal(RenderViewIds.ScreenUi, ui.Id);
        Assert.Null(ui.Target);
        Assert.Equal(RenderLayers.Ui, ui.LayerMask);
        Assert.Equal(ProjectionKind.Orthographic, ui.ProjectionMetadata.Kind);
        Assert.Equal(800f, ui.Viewport.Width);
        Assert.Equal(300f, ui.Viewport.Height);

        // UI is emitted after every scene view so it composites on top.
        Assert.Equal(RenderViewIds.ScreenUi, frame.Views.Span[frame.Views.Length - 1].Id);

        renderer.Submit(lease);
        Assert.NotNull(renderer.LastFrame);
    }

    [Fact]
    public void OwnTarget_NamesEngineTargetAsPresentTarget()
    {
        using var renderer = new NullRenderer();
        using var producer = new RenderFrameProducer(renderer, 800, 300)
        {
            ScreenUiTargetPolicy = UiTargetPolicy.OwnTarget,
        };

        Assert.True(producer.TryBuild(Snapshot(), null, 0f, 0f, Ui(800, 300), out var lease));
        Assert.NotNull(lease);

        var frame = lease!.Frame;
        var ui = FindUiView(frame);

        Assert.NotNull(frame.PresentTarget);
        Assert.NotNull(ui.Target);
        Assert.Equal(frame.PresentTarget, ui.Target);

        renderer.Submit(lease);
        Assert.NotNull(renderer.LastFrame);
    }

    [Fact]
    public void OwnTarget_IsResizedNotRecreated()
    {
        using var renderer = new NullRenderer();
        using var producer = new RenderFrameProducer(renderer, 800, 300)
        {
            ScreenUiTargetPolicy = UiTargetPolicy.OwnTarget,
        };

        var first = BuildTarget(producer, 800, 300);
        var resized = BuildTarget(producer, 640, 360);
        var again = BuildTarget(producer, 640, 360);

        Assert.Equal(first, resized);
        Assert.Equal(resized, again);
    }

    [Fact]
    public void NoUiFrame_EmitsNoUiView()
    {
        using var renderer = new NullRenderer();
        using var producer = new RenderFrameProducer(renderer, 800, 300);

        Assert.True(producer.TryBuild(Snapshot(), null, 0f, 0f, out var lease));
        Assert.NotNull(lease);

        var frame = lease!.Frame;
        Assert.Null(frame.PresentTarget);
        foreach (var view in frame.Views.Span)
            Assert.NotEqual(RenderViewIds.ScreenUi, view.Id);

        renderer.Submit(lease);
    }

    [Fact]
    public void WorldCanvas_EmitsSceneMeshes_WithPlacement()
    {
        V12.Paper.PaperShaderAssets.EnsureRegistered();

        using var renderer = new NullRenderer();
        using var producer = new RenderFrameProducer(
            renderer, 800, 300, uiSource: new PaperUiSource());

        var worldId = Guid.NewGuid();
        var snapshot = Snapshot(worldId);
        snapshot.Canvases.Add(WorldCanvasSnapshot.Create(
            50,
            worldId,
            400,
            200,
            Matrix4x4.CreateTranslation(0f, 1f, -2f)));

        var captured = new UIFrame();
        captured.Nodes.Add(new UINode
        {
            Id = 50,
            ParentId = 0,
            Kind = UIWidgetKind.Canvas,
            ScreenSpace = false,
            Width = 400,
            Height = 200,
        });
        captured.Nodes.Add(new UINode
        {
            Id = 51,
            ParentId = 50,
            Kind = UIWidgetKind.Button,
            PreferredWidth = 200,
            PreferredHeight = 80,
            Text = "OK",
        });

        var context = new UiFrameContext(
            captured,
            new PaperFrame { Width = 800, Height = 300 },
            800,
            300);

        Assert.True(producer.TryBuild(snapshot, null, 0f, 0f, context, out var lease));
        Assert.NotNull(lease);

        var frame = lease!.Frame;

        // World UI needs no view and no target of its own.
        Assert.Null(frame.PresentTarget);
        var uiView = FindUiView(frame);
        Assert.Null(uiView.Target);

        var items = frame.Items.Span.ToArray().Where(i => i.ElementId == 50).ToArray();
        Assert.NotEmpty(items);

        foreach (var item in items)
        {
            // Scene item: normal views draw it, screen UI does not.
            Assert.Equal(0, item.ViewId);
            Assert.Equal(worldId, item.WorldId);
            Assert.Equal(RenderLayers.Default, item.LayerMask);
            Assert.True(item.Mesh.IsValid);
            Assert.True(item.Material.IsValid);

            // 400 px wide over 2 m, 200 px high over 1 m.
            Assert.Equal(0.005f, item.World.M11, 4);
            Assert.Equal(0.005f, item.World.M22, 4);

            // The mesh is centred on the canvas, so the world matrix origin is
            // the canvas centre.
            var centre = Vector3.Transform(Vector3.Zero, item.World);
            Assert.Equal(0f, centre.X, 3);
            Assert.Equal(1f, centre.Y, 3);
            Assert.Equal(-2f, centre.Z, 3);
        }

        renderer.Submit(lease);
        Assert.NotNull(renderer.LastFrame);
    }

    [Fact]
    public void WorldCanvas_WithoutPlacement_IsSkipped()
    {
        V12.Paper.PaperShaderAssets.EnsureRegistered();

        using var renderer = new NullRenderer();
        using var producer = new RenderFrameProducer(
            renderer, 800, 300, uiSource: new PaperUiSource());

        var captured = new UIFrame();
        captured.Nodes.Add(new UINode
        {
            Id = 60,
            ParentId = 0,
            Kind = UIWidgetKind.Canvas,
            ScreenSpace = false,
            Width = 320,
            Height = 180,
        });
        captured.Nodes.Add(new UINode
        {
            Id = 61,
            ParentId = 60,
            Kind = UIWidgetKind.Button,
            PreferredWidth = 120,
            PreferredHeight = 40,
            Text = "OK",
        });

        var context = new UiFrameContext(
            captured,
            new PaperFrame { Width = 800, Height = 300 },
            800,
            300);

        Assert.True(producer.TryBuild(Snapshot(Guid.NewGuid()), null, 0f, 0f, context, out var lease));
        Assert.NotNull(lease);

        Assert.DoesNotContain(lease!.Frame.Items.Span.ToArray(), i => i.ElementId == 60);
        lease.Release();
    }

    [Fact]
    public void CustomUiSource_EmitsWorldCanvasWithoutAnyPaperImplementation()
    {
        V12.Paper.PaperShaderAssets.EnsureRegistered();

        using var renderer = new NullRenderer();
        var source = new FakeUiSource(70);
        using var producer = new RenderFrameProducer(
            renderer, 800, 300, uiSource: source);

        var worldId = Guid.NewGuid();
        var snapshot = Snapshot(worldId);
        snapshot.Canvases.Add(WorldCanvasSnapshot.Create(
            70, worldId, 400, 200, Matrix4x4.CreateTranslation(0f, 0f, -2f)));

        var context = new UiFrameContext(
            new UIFrame(),
            new PaperFrame { Width = 800, Height = 300 },
            800,
            300);

        Assert.True(producer.TryBuild(snapshot, null, 0f, 0f, context, out var lease));
        Assert.NotNull(lease);

        var items = lease!.Frame.Items.Span.ToArray()
            .Where(i => i.ElementId == 70)
            .ToArray();
        Assert.NotEmpty(items);
        Assert.All(items, item => Assert.Equal(0, item.ViewId));

        lease.Release();
    }

    /// <summary>
    /// Minimal engine-only source: proves the producer consumes nothing but the
    /// neutral contract, so a host with a different UI toolkit works too.
    /// </summary>
    private sealed class FakeUiSource : IUiSource
    {
        private readonly Dictionary<long, PaperFrame> _frames = new();

        public FakeUiSource(long canvasId)
        {
            _frames[canvasId] = BuildFrame();
        }

        public void PrepareWorldCanvases(UIFrame frame) { }

        public void StepWorldCanvases() { }

        public IReadOnlyDictionary<long, PaperFrame> WorldCanvases => _frames;

        public IEnumerable<long> CanvasIds => _frames.Keys;

        public bool FeedCanvasPointer(long canvasId, float x, float y, bool down) =>
            _frames.ContainsKey(canvasId);

        public bool FeedCanvasInput(long canvasId, V12.Core.Input.InputEvent evt) => false;

        public bool ContainsCanvas(long canvasId) => _frames.ContainsKey(canvasId);

        private static PaperFrame BuildFrame()
        {
            var frame = new PaperFrame { Width = 400, Height = 200 };
            frame.Meshes.Add(new PaperMesh
            {
                Positions = new[]
                {
                    new Vector2(-200f, -100f),
                    new Vector2(200f, -100f),
                    new Vector2(200f, 100f),
                    new Vector2(-200f, 100f),
                },
                UVs = new[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(1f, 0f),
                    new Vector2(1f, 1f),
                    new Vector2(0f, 1f),
                },
                Colors = new[] { Vector4.One, Vector4.One, Vector4.One, Vector4.One },
                Indices = new uint[] { 0, 1, 2, 0, 2, 3 },
                IsText = false,
            });
            return frame;
        }
    }

    private static RenderTargetHandle? BuildTarget(RenderFrameProducer producer, int width, int height)
    {
        Assert.True(producer.TryBuild(Snapshot(), null, 0f, 0f, Ui(width, height), out var lease));
        Assert.NotNull(lease);
        var handle = lease!.Frame.PresentTarget;
        lease.Release();
        return handle;
    }

    private static RenderView FindUiView(RenderFrame frame)
    {
        foreach (var view in frame.Views.Span)
        {
            if (view.Id == RenderViewIds.ScreenUi)
                return view;
        }

        throw new Xunit.Sdk.XunitException("Frame has no screen-space UI view.");
    }

    private static UiFrameContext Ui(int width, int height) =>
        new(new UIFrame(), new PaperFrame { Width = width, Height = height }, width, height);

    private static FrameSnapshot Snapshot(Guid? worldId = null)
    {
        var snapshot = new FrameSnapshot();
        snapshot.Renderables.Add(new RenderableSnapshot
        {
            ElementId = 1,
            Name = "Camera",
            NodeType = SnapshotNodeType.Camera,
            WorldId = worldId ?? Guid.Empty,
            LayerMask = RenderLayers.Default,
            Transform = Matrix4x4.CreateTranslation(0f, 1f, 5f),
            IsCurrentCamera = true,
            Fov = 60f,
            NearClip = 0.1f,
            FarClip = 100f,
            Projection = CameraProjection.Perspective,
        });
        return snapshot;
    }
}
