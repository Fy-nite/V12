using V12.Pak;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Covers the stream-backed pak path used by hosts that embed the archive in
/// their own package (Godot's <c>res://</c> on Android).
/// </summary>
public sealed class PakStreamTests : IDisposable
{
    private readonly string _root;
    private readonly string _pakPath;
    private readonly string _stem;

    public PakStreamTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "v12pak-stream-" + Guid.NewGuid().ToString("N"));
        _stem = "v12pak-stream-" + Guid.NewGuid().ToString("N");

        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllText(
            Path.Combine(_root, "assets", "hello.txt"),
            "hello from a stream");

        _pakPath = Path.Combine(_root, _stem + ".v12pak");

        var manifest = V12PakManifestBuilder.Create()
            .AddAsset("assets/hello.txt", "assets/hello.txt")
            .Build();

        V12PakBuilder.Build(_root, _pakPath, manifest);
    }

    [Fact]
    public void StreamReaderExtractsAndReusesTheCache()
    {
        string resource = "res://" + _stem + ".v12pak";

        using (var reader = new V12PakReader(resource, () => File.OpenRead(_pakPath)))
        {
            Assert.Contains("assets/hello.txt", reader.Manifest.Assets.Keys);
            Assert.Contains("assets/hello.txt", reader.EntryNames);
            Assert.Equal("hello from a stream", reader.ReadEntryText("assets/hello.txt"));
            Assert.False(reader.ReusedCache);
        }

        using (var reader = new V12PakReader(resource, () => File.OpenRead(_pakPath)))
            Assert.True(reader.ReusedCache);
    }

    [Fact]
    public void ReadManifestOnlyWorksFromAStream()
    {
        var manifest = V12PakReader.ReadManifestOnly(() => File.OpenRead(_pakPath));

        Assert.NotNull(manifest);
        Assert.Contains("assets/hello.txt", manifest!.Assets.Keys);
    }

    [Fact]
    public void HashStreamIsStableForTheSameBytes()
    {
        string first = V12PakCache.HashStream(() => File.OpenRead(_pakPath));
        string second = V12PakCache.HashStream(() => File.OpenRead(_pakPath));

        Assert.Equal(first, second);
    }

    public void Dispose()
    {
        V12PakCache.Purge(_stem);

        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // Best effort; the OS temp dir is disposable.
        }
    }
}
