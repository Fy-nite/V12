using System.Drawing;
using System.Numerics;
using V12.Core.Interfaces.Renderer;
using V12.Core.Rendering;
using V12.Rendering;
using Xunit;
using NewEnv = V12.Rendering.EnvironmentData;

namespace V12.Architecture.Tests;

public sealed class RenderFrameProducerTests
{
    [Fact]
    public void Producer_EmitsViewsItemsLightsAndOverrides()
    {
        using var renderer = new NullRenderer();
        using var producer = new RenderFrameProducer(renderer, 1280, 720);

        var snapshot = new FrameSnapshot();
        snapshot.Renderables.Add(new RenderableSnapshot
        {
            ElementId = 1,
            NodeType = SnapshotNodeType.Camera,
            Transform = Matrix4x4.CreateTranslation(0f, 1f, 5f),
            IsCurrentCamera = true,
            Projection = CameraProjection.Perspective,
            Fov = 60f,
            NearClip = 0.1f,
            FarClip = 100f,
        });

        snapshot.Renderables.Add(new RenderableSnapshot
        {
            ElementId = 2,
            NodeType = SnapshotNodeType.MeshCustom,
            Transform = Matrix4x4.CreateTranslation(1f, 2f, 3f),
            MeshPoints = new double[]
            {
                0, 0, 0,
                1, 0, 0,
                0, 1, 0,
            },
            MeshIndices = new uint[] { 0, 1, 2 },
            MatR = 1f,
            MatG = 0.25f,
            MatB = 0.5f,
            MatA = 1f,
            MatMetallic = 0.1f,
            MatRoughness = 0.8f,
        });

        snapshot.Renderables.Add(new RenderableSnapshot
        {
            ElementId = 3,
            NodeType = SnapshotNodeType.LightSpot,
            Transform = Matrix4x4.Identity,
            LightColor = Color.White,
            LightIntensity = 2f,
            LightRange = 12f,
            LightAngle = 45f,
            LightSpotSoftness = 0.5f,
        });

        var environment = NewEnv.Default with
        {
            HasValue = true,
            Sky = NewEnv.Default.Sky with { Color = new Vector3(0.1f, 0.2f, 0.3f) },
        };

        Assert.True(producer.TryBuild(snapshot, environment, 3f, 0.016f, out var lease));
        Assert.NotNull(lease);

        var frame = lease!.Frame;
        Assert.Equal(1, frame.Views.Length);
        Assert.Equal(1, frame.Items.Length);
        Assert.Equal(1, frame.Lights.Length);
        Assert.Equal(1, frame.DrawOverrides.Length);
        Assert.Same(environment, frame.Environment);
        Assert.Equal(3f, frame.Time);
        Assert.Equal(0.016f, frame.DeltaTime);

        var view = frame.Views.Span[0];
        Assert.Equal(1, view.CameraElementId);
        Assert.Equal(ProjectionKind.Perspective, view.ProjectionMetadata.Kind);
        Assert.NotEqual(Matrix4x4.Identity, view.Projection);
        Assert.Equal(1280f, view.Viewport.Width);
        Assert.Equal(720f, view.Viewport.Height);

        var item = frame.Items.Span[0];
        Assert.Equal(2, item.ElementId);
        Assert.True(item.Mesh.IsValid);
        Assert.True(item.Material.IsValid);
        Assert.Equal(0, item.OverridesIndex);
        Assert.Equal(3, frame.DrawOverrides.Span[item.OverridesIndex].Values.Length);

        var light = frame.Lights.Span[0];
        Assert.Equal(LightKind.Spot, light.Kind);
        Assert.Equal(2f, light.Intensity);
        Assert.Equal(45f, light.OuterCone);
        Assert.Equal(22.5f, light.InnerCone);
        Assert.Equal(1, producer.Resources.MeshCount);
        Assert.Equal(1, producer.Resources.MaterialCount);

        renderer.Submit(lease);
        Assert.NotNull(renderer.LastFrame);
    }

    [Fact]
    public void Producer_BindsMaterialTextures_WhenLoaderProvidesData()
    {
        using var renderer = new NullRenderer();
        using var producer = new RenderFrameProducer(
            renderer,
            textureLoader: _ => NewTexture());

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
        });
        snapshot.Renderables.Add(new RenderableSnapshot
        {
            ElementId = 2,
            NodeType = SnapshotNodeType.MeshCustom,
            Transform = Matrix4x4.Identity,
            MeshPoints = new double[]
            {
                0, 0, 0,
                1, 0, 0,
                0, 1, 0,
            },
            MeshIndices = new uint[] { 0, 1, 2 },
            MatA = 1f,
            MatTexturePath = "textures/albedo.png",
            MatNormalTexturePath = "textures/normal.png",
        });

        Assert.True(producer.TryBuild(snapshot, NewEnv.Default, 0f, 0.016f, out var lease));
        Assert.Equal(2, producer.Resources.TextureCount);
        Assert.Equal(1, producer.Resources.MaterialCount);

        renderer.Submit(lease!);
    }

    [Fact]
    public void Producer_ReusesMeshAcrossFrames_WhenGeometryIsUnchanged()
    {
        using var renderer = new NullRenderer();
        using var producer = new RenderFrameProducer(renderer);

        var points = new double[]
        {
            0, 0, 0,
            1, 0, 0,
            0, 1, 0,
        };
        var indices = new uint[] { 0, 1, 2 };

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
        });
        snapshot.Renderables.Add(new RenderableSnapshot
        {
            ElementId = 2,
            NodeType = SnapshotNodeType.MeshCustom,
            Transform = Matrix4x4.Identity,
            MeshPoints = points,
            MeshIndices = indices,
            MatA = 1f,
        });

        Assert.True(producer.TryBuild(snapshot, NewEnv.Default, 0f, 0.016f, out var first));
        Assert.True(producer.TryBuild(snapshot, NewEnv.Default, 0.016f, 0.016f, out var second));

        var firstMesh = first!.Frame.Items.Span[0].Mesh;
        var secondMesh = second!.Frame.Items.Span[0].Mesh;
        Assert.Equal(firstMesh, secondMesh);

        renderer.Submit(first);
        renderer.Submit(second);
    }

    [Fact]
    public void Producer_ReportsPoolBackPressure()
    {
        using var renderer = new NullRenderer();
        var pool = new RenderFramePool(1);
        using var producer = new RenderFrameProducer(renderer, pool: pool);

        var snapshot = new FrameSnapshot();
        Assert.True(producer.TryBuild(snapshot, NewEnv.Default, 0f, 0f, out var lease));
        Assert.False(producer.TryBuild(snapshot, NewEnv.Default, 0f, 0f, out var blocked));
        Assert.Null(blocked);

        lease!.Release();
        Assert.True(producer.TryBuild(snapshot, NewEnv.Default, 0f, 0f, out var next));
        next!.Release();
    }

    private static TextureData NewTexture() => new(
        TextureDimension.Texture2D,
        1,
        1,
        1,
        1,
        TextureFormat.Rgba8Unorm,
        TextureColorSpace.Linear,
        TextureUsage.Sampled,
        ReadOnlyMemory<TextureSubresource>.Empty);
}
