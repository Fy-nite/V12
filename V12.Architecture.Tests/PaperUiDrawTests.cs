using System.Numerics;
using V12.Core.Rendering;
using V12.Core.UI.Paper;
using V12.Rendering;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// UI resources are pulled from <see cref="RendererResourceCache"/> every frame.
/// Retaining a GPU handle across frames is unsafe because the cache retires
/// entries that go a frame unused, so these tests drive the builder through a
/// gap in UI output.
/// </summary>
public sealed class PaperUiDrawTests
{
    private const string ShaderUri = "shader://test/paper-ui";

    [Fact]
    public void WhiteTextureIsRecreatedAfterAnEmptyUiFrame()
    {
        RegisterShader();

        using var renderer = new NullRenderer();
        var cache = new RendererResourceCache(renderer);
        var builder = new PaperUiDrawBuilder(cache);
        var pool = new RenderFramePool(8);

        // A frame with content creates the white fallback texture and a material.
        cache.BeginFrame();
        var first = pool.Rent();
        builder.Append(QuadFrame(), target: null, first);
        Assert.Single(first.Items);

        // An empty UI frame leaves the white texture unused, so the cache
        // retires it at the next frame boundary.
        cache.BeginFrame();
        var empty = pool.Rent();
        builder.Append(EmptyFrame(), target: null, empty);
        cache.BeginFrame();

        // UI returns. A retained handle would reach CreateMaterial stale and
        // throw RendererContractException here.
        var second = pool.Rent();
        builder.Append(QuadFrame(), target: null, second);
        Assert.Single(second.Items);
    }

    private static void RegisterShader()
    {
        ShaderAssetRegistry.Instance.Register(ShaderUri, new ShaderProgram(
            BuiltIn: BuiltInShader.None,
            Reflection: ShaderReflection.Empty,
            AssetUri: ShaderUri,
            Source: "shader_type spatial;",
            SourceLanguage: ShaderSourceLanguage.GodotSpatial));
    }

    private static UiFrameContext QuadFrame()
    {
        var mesh = new PaperMesh
        {
            Positions = new[] { new Vector2(0f, 0f), new Vector2(10f, 0f), new Vector2(10f, 10f) },
            Indices = new uint[] { 0, 1, 2 },
            Color = Vector4.One,
            Brush = new UiBrush { Color1 = Vector4.One },
        };

        return FrameWith(mesh);
    }

    private static UiFrameContext EmptyFrame() => FrameWith(null);

    private static UiFrameContext FrameWith(PaperMesh? mesh)
    {
        var frame = new PaperFrame
        {
            Width = 100,
            Height = 100,
            ShapeShaderUri = ShaderUri,
            TextShaderUri = ShaderUri,
        };

        if (mesh is not null)
            frame.Meshes.Add(mesh);

        return new UiFrameContext(default, frame, 100, 100);
    }
}
