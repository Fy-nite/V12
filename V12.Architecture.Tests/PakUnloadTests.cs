using System;
using System.IO;
using V12.Core;
using V12.Core.Interfaces;
using V12.Pak;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// <see cref="V12PakLoadResult.Unload"/> is the inverse of loading a pak: it
/// removes the worlds, world sessions, asset resolver and loaded-pak entry the
/// pak added, so a host can load another build in the same process.
/// <see cref="V12PakLoadResult.Dispose"/> deliberately does not do that; it only
/// releases the archive reader.
/// </summary>
public sealed class PakUnloadTests : IDisposable
{
    private readonly string _root;

    public PakUnloadTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "v12pak-unload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private static readonly V12PakOptions ContentOnlyOptions = new()
    {
        LoadDlls = false,
        LoadContractScripts = false,
        LoadCSharpScripts = false,
    };

    private string BuildWorldPak()
    {
        string worlds = Path.Combine(_root, "worlds", "UnloadWorld");
        Directory.CreateDirectory(worlds);

        File.WriteAllText(
            Path.Combine(worlds, "world.xml"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <World name="UnloadWorld">
              <Element name="Marker" />
            </World>
            """);

        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllText(Path.Combine(_root, "assets", "hello.txt"), "hello");

        string pak = Path.Combine(_root, "UnloadWorld.v12pak");
        V12PakBuilder.Build(_root, pak);
        return pak;
    }

    [Fact]
    public void UnloadRemovesWorldsAndResolverThenReloads()
    {
        string pakPath = BuildWorldPak();
        var root = new GameRoot();

        var first = V12PakLoader.Load(pakPath, ContentOnlyOptions, root);
        var world = Assert.Single(first.LoadedWorlds);
        Assert.Contains(root.Worlds, w => ReferenceEquals(w, world));
        Assert.NotNull(root.Registry.Get<IAssetResolver>("AssetResolver"));

        // A focused world owns a session, so the unload has to close it rather
        // than just removing the tree from the root's world list.
        Assert.True(root.FocusWorld(world));
        Assert.NotNull(root.FindWorldSession(world));

        first.Unload();

        Assert.True(first.IsUnloaded);
        Assert.DoesNotContain(root.Worlds, w => ReferenceEquals(w, world));
        Assert.Null(root.FindWorldSession(world));
        Assert.Null(root.Registry.Get<IAssetResolver>("AssetResolver"));

        // The point of the fix: a second build loads into the same process.
        var second = V12PakLoader.Load(pakPath, ContentOnlyOptions, root);
        var reloaded = Assert.Single(second.LoadedWorlds);
        Assert.Contains(root.Worlds, w => ReferenceEquals(w, reloaded));

        second.Unload();
        Assert.Empty(root.Worlds);
    }

    [Fact]
    public void DisposeReleasesTheReaderButKeepsTheWorld()
    {
        string pakPath = BuildWorldPak();
        var root = new GameRoot();

        var result = V12PakLoader.Load(pakPath, ContentOnlyOptions, root);
        var world = Assert.Single(result.LoadedWorlds);

        result.Dispose();

        Assert.False(result.IsUnloaded);
        Assert.Contains(root.Worlds, w => ReferenceEquals(w, world));
    }

    [Fact]
    public void UnloadIsIdempotent()
    {
        string pakPath = BuildWorldPak();
        var root = new GameRoot();

        var result = V12PakLoader.Load(pakPath, ContentOnlyOptions, root);
        result.Unload();
        result.Unload();

        Assert.True(result.IsUnloaded);
        Assert.Empty(root.Worlds);
    }

    public void Dispose()
    {
        V12PakCache.Purge("UnloadWorld");

        try { Directory.Delete(_root, true); }
        catch
        {
            // Best effort; the OS temp directory is disposable.
        }
    }
}
