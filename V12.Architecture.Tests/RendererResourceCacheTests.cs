using V12.Rendering;
using Xunit;

namespace V12.Architecture.Tests;

public sealed class RendererResourceCacheTests
{
    [Fact]
    public void Cache_ReusesHandles_AndRetiresUnusedAfterGrace()
    {
        using var renderer = new NullRenderer();
        using var cache = new RendererResourceCache(renderer);

        cache.BeginFrame();
        var shader = cache.GetOrCreateShader(BuiltInShader.Lit);
        var texture = cache.GetOrCreateTexture("texture", NewTexture(), 1);
        var mesh = cache.GetOrCreateMesh("mesh", MeshData.Empty, 1);
        var material = cache.GetOrCreateMaterial(
            "material",
            shader,
            new MaterialDesc(Textures: new[] { new MaterialTextureBinding(0, texture) }),
            1);

        Assert.Equal(1, cache.MeshCount);
        Assert.Equal(1, cache.TextureCount);
        Assert.Equal(1, cache.MaterialCount);
        Assert.Equal(1, cache.ShaderCount);

        Assert.Equal(mesh, cache.GetOrCreateMesh("mesh", MeshData.Empty, 1));
        Assert.Equal(material, cache.GetOrCreateMaterial(
            "material",
            shader,
            new MaterialDesc(Textures: new[] { new MaterialTextureBinding(0, texture) }),
            1));

        // One frame of grace: the entries were used on the previous frame.
        cache.BeginFrame();
        Assert.Equal(1, cache.MeshCount);
        Assert.Equal(1, cache.TextureCount);
        Assert.Equal(1, cache.MaterialCount);

        // Not requested again, so they are retired in dependency order.
        cache.BeginFrame();
        Assert.Equal(0, cache.MeshCount);
        Assert.Equal(0, cache.TextureCount);
        Assert.Equal(0, cache.MaterialCount);
        Assert.Equal(1, cache.ShaderCount);
    }

    [Fact]
    public void Cache_DropsHandles_WhenDeviceEpochChanges()
    {
        using var renderer = new NullRenderer();
        using var cache = new RendererResourceCache(renderer);

        cache.BeginFrame();
        var oldMesh = cache.GetOrCreateMesh("mesh", MeshData.Empty, 1);
        Assert.True(oldMesh.IsValid);

        renderer.LoseDevice();
        renderer.RecoverDevice();

        cache.BeginFrame();
        Assert.Equal(0, cache.MeshCount);

        var newMesh = cache.GetOrCreateMesh("mesh", MeshData.Empty, 1);
        Assert.True(newMesh.IsValid);
        Assert.NotEqual(oldMesh.Value.Epoch, newMesh.Value.Epoch);
        Assert.NotEqual(oldMesh.Value.Id, newMesh.Value.Id);
    }

    [Fact]
    public void Cache_UpdatesMeshInPlace_WhenVersionChanges()
    {
        using var renderer = new NullRenderer();
        using var cache = new RendererResourceCache(renderer);

        cache.BeginFrame();
        var first = cache.GetOrCreateMesh("mesh", MeshData.Empty, 1);
        var second = cache.GetOrCreateMesh("mesh", MeshData.Empty, 2);

        Assert.Equal(first, second);
        Assert.Equal(1, cache.MeshCount);
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
