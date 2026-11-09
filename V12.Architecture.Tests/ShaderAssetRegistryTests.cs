using V12.Core.Rendering;
using V12.Rendering;
using Xunit;

namespace V12.Architecture.Tests;

public sealed class ShaderAssetRegistryTests
{
    [Fact]
    public void Registry_ParsesManifestReflectionAndSource()
    {
        const string json = """
        {
          "name": "test",
          "version": 3,
          "parameters": [
            { "name": "brushType", "index": 0, "kind": "Int" },
            { "name": "brushColor", "index": 1, "kind": "Vector4" }
          ],
          "textures": [
            { "name": "tex0", "slot": 0 }
          ],
          "backends": {
            "godot_spatial": "shader_type spatial;"
          }
        }
        """;

        var registry = new ShaderAssetRegistry();
        registry.RegisterJson("shader://test/paper", json);

        Assert.True(registry.TryResolve("shader://test/paper", out var program, out long version));
        Assert.Equal(3, version);
        Assert.Equal(ShaderSourceLanguage.GodotSpatial, program.SourceLanguage);
        Assert.Equal("shader_type spatial;", program.Source);
        Assert.Equal("shader://test/paper", program.AssetUri);
        Assert.NotNull(program.Reflection);
        Assert.Equal(2, program.Reflection!.Parameters.Count);
        Assert.Equal(MaterialValueKind.Int, program.Reflection.Parameters[0].Kind);
        Assert.Single(program.Reflection.Textures);
    }
}
