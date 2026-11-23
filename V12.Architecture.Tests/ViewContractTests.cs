using System.Drawing;
using System.Numerics;
using V12.Core.Interfaces.Renderer;
using V12.Core.Rendering;
using V12.Core.UI.Paper;
using V12.Rendering;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Covers the explicit render-view contract: views are cameras with their own
/// world, rect, layer mask and target. Scene content is not owned by a view.
/// </summary>
public sealed class ViewContractTests
{
    [Fact]
    public void Producer_BuildsOneTargetPerExplicitView()
    {
        using var renderer = new NullRenderer();
        var producer = new RenderFrameProducer(renderer, 800, 300);
        try
        {
            var worldId = Guid.NewGuid();
            var snapshot = new FrameSnapshot();

            snapshot.Renderables.Add(Camera(1, worldId, new Vector3(0f, 2f, 6f)));
            snapshot.Renderables.Add(Camera(2, worldId, new Vector3(4f, 2f, 6f)));

            snapshot.Views.Add(View(10, 20, 1, worldId));
            snapshot.Views.Add(View(11, 21, 2, worldId));

            var uiFrame = new PaperFrame { Width = 800, Height = 300 };
            uiFrame.Viewports.Add(new PaperViewportInfo
            {
                Id = 20,
                ViewId = 10,
                Rect = new Vector4(0f, 0f, 400f, 300f),
            });
            uiFrame.Viewports.Add(new PaperViewportInfo
            {
                Id = 21,
                ViewId = 11,
                Rect = new Vector4(400f, 0f, 400f, 300f),
            });

            var context = new UiFrameContext(new UIFrame(), uiFrame, 800, 300);

            Assert.True(producer.TryBuild(
                snapshot,
                V12.Rendering.EnvironmentData.Default,
                0f,
                0f,
                context,
                out var lease));

            Assert.NotNull(lease);
            try
            {
                var views = lease!.Frame.Views.Span;
                var sceneViews = new List<RenderView>();
                foreach (var view in views)
                {
                    if (view.Id > 0) sceneViews.Add(view);
                }

                Assert.Equal(2, sceneViews.Count);

                Assert.Equal(10, sceneViews[0].Id);
                Assert.NotNull(sceneViews[0].Target);
                Assert.Equal(worldId, sceneViews[0].WorldId);
                Assert.Equal(1, sceneViews[0].CameraElementId);
                Assert.Equal(0f, sceneViews[0].Viewport.X);
                Assert.Equal(400f, sceneViews[0].Viewport.Width);

                Assert.Equal(11, sceneViews[1].Id);
                Assert.NotNull(sceneViews[1].Target);
                Assert.Equal(2, sceneViews[1].CameraElementId);
                Assert.Equal(400f, sceneViews[1].Viewport.X);
                Assert.Equal(400f, sceneViews[1].Viewport.Width);
            }
            finally
            {
                lease!.Release();
            }
        }
        finally
        {
            producer.Dispose();
        }
    }

    private static RenderableSnapshot Camera(long id, Guid worldId, Vector3 position)
    {
        return new RenderableSnapshot
        {
            ElementId = id,
            Name = $"Camera{id}",
            NodeType = SnapshotNodeType.Camera,
            WorldId = worldId,
            LayerMask = RenderLayers.Default,
            Transform = Matrix4x4.CreateTranslation(position),
            IsCurrentCamera = true,
            Fov = 60f,
            NearClip = 0.05f,
            FarClip = 200f,
            Projection = CameraProjection.Perspective,
        };
    }

    private static RenderViewSnapshot View(long id, long viewportId, long cameraId, Guid worldId)
    {
        return new RenderViewSnapshot
        {
            ViewId = id,
            ViewportId = viewportId,
            CameraElementId = cameraId,
            WorldId = worldId,
            LayerMask = RenderLayers.Default,
            Clear = ClearMode.Color,
            ClearColor = Color.Black,
            Priority = 0,
            IsActive = true,
        };
    }

}
