using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using V12.Core;
using V12.Pak;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Mod paks are ordinary .v12pak files with a module.json at the root. These
/// cover manifest validation, content loading through the normal pak pipeline,
/// dependency refusal, duplicate handling and persisted enable/disable state.
/// </summary>
public sealed class ModPakTests : IDisposable
{
    private readonly List<string> _tempDirectories = new();

    private string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "v12-mod-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }

    private string NewSettingsPath() => Path.Combine(NewDirectory(), "mods.json");

    private string BuildMod(
        string id,
        int apiVersion = 1,
        string[]? dependencies = null,
        string? worldXml = null)
    {
        string directory = NewDirectory();

        var manifest = new
        {
            apiVersion,
            id,
            name = id,
            version = "1.0.0",
            author = "tests",
            dependencies = dependencies ?? Array.Empty<string>(),
        };

        File.WriteAllText(
            Path.Combine(directory, "module.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        if (worldXml is not null)
        {
            string worlds = Path.Combine(directory, "worlds", "ModWorld");
            Directory.CreateDirectory(worlds);
            File.WriteAllText(Path.Combine(worlds, "world.xml"), worldXml);
        }

        string pak = Path.Combine(directory, id + ".v12pak");
        V12PakBuilder.Build(directory, pak);
        return pak;
    }

    private string BuildPlainPak()
    {
        string directory = NewDirectory();
        File.WriteAllText(Path.Combine(directory, "readme.txt"), "not a mod");

        string pak = Path.Combine(directory, "plain.v12pak");
        V12PakBuilder.Build(directory, pak);
        return pak;
    }

    [Fact]
    public void LoadPak_ReadsTheManifest()
    {
        var root = new GameRoot();
        using var host = new PakModHost(root, NewSettingsPath());

        Assert.True(host.LoadPak(BuildMod("test.mod")));

        var mod = Assert.Single(host.Mods);
        Assert.Equal("test.mod", mod.Id);
        Assert.Equal("1.0.0", mod.Version);
        Assert.Equal(1, mod.ApiVersion);
        Assert.True(mod.Loaded);
        Assert.True(mod.Enabled);
        Assert.Null(mod.Error);
    }

    [Fact]
    public void ContentMod_LoadsWorldsThroughThePakPipeline()
    {
        const string world = """
            <?xml version="1.0" encoding="utf-8"?>
            <World name="ModWorld">
              <Element name="Marker" />
            </World>
            """;

        var root = new GameRoot();
        using var host = new PakModHost(root, NewSettingsPath());

        Assert.True(host.LoadPak(BuildMod("content.mod", worldXml: world)));

        Assert.Contains(root.Worlds, w => w.WorldName == "ModWorld");
        Assert.True(Assert.Single(host.Mods).Loaded);
    }

    [Fact]
    public void PakWithoutModuleJson_IsSkipped()
    {
        var root = new GameRoot();
        using var host = new PakModHost(root, NewSettingsPath());

        Assert.False(host.LoadPak(BuildPlainPak()));
        Assert.Empty(host.Mods);
    }

    [Fact]
    public void ApiMismatch_IsRejected()
    {
        var root = new GameRoot();
        using var host = new PakModHost(root, NewSettingsPath());

        Assert.False(host.LoadPak(BuildMod("api.mod", apiVersion: 99)));

        var mod = Assert.Single(host.Mods);
        Assert.False(mod.Loaded);
        Assert.Contains("apiVersion", mod.Error);
    }

    [Fact]
    public void MissingDependency_IsRejected()
    {
        var root = new GameRoot();
        using var host = new PakModHost(root, NewSettingsPath());

        Assert.False(host.LoadPak(BuildMod("needs.mod", dependencies: new[] { "other.mod" })));

        var mod = Assert.Single(host.Mods);
        Assert.Contains("missing dependency", mod.Error);
        Assert.Contains("other.mod", mod.Error);
    }

    [Fact]
    public void DuplicateMod_IsRejected()
    {
        var root = new GameRoot();
        using var host = new PakModHost(root, NewSettingsPath());
        string pak = BuildMod("dup.mod");

        Assert.True(host.LoadPak(pak));
        Assert.False(host.LoadPak(pak));

        Assert.Equal(2, host.Mods.Count);
        Assert.Contains("already loaded", host.Mods[1].Error);
    }

    [Fact]
    public void DisableModFlag_SkipsTheMod()
    {
        var root = new GameRoot();
        using var host = new PakModHost(root, NewSettingsPath());
        string pak = BuildMod("disabled.mod");

        int loaded = host.EnableFromArgs(new[] { "--mod", pak, "--disable-mod", "disabled.mod" });

        Assert.Equal(0, loaded);
        var mod = Assert.Single(host.Mods);
        Assert.False(mod.Enabled);
        Assert.False(mod.Loaded);
        Assert.Contains("disabled", mod.Error);
    }

    [Fact]
    public void SetEnabled_PersistsAcrossHosts()
    {
        var root = new GameRoot();
        string settings = NewSettingsPath();
        string pak = BuildMod("persist.mod");

        using (var first = new PakModHost(root, settings))
            Assert.True(first.SetEnabled("persist.mod", false));

        using var second = new PakModHost(root, settings);
        Assert.False(second.LoadPak(pak));
        Assert.Contains("disabled", Assert.Single(second.Mods).Error);
    }

    [Fact]
    public void LoadDirectory_FindsNestedModPaks()
    {
        string pak = BuildMod("nested.mod");
        string directory = Path.GetDirectoryName(pak)!;

        var root = new GameRoot();
        using var host = new PakModHost(root, NewSettingsPath());

        Assert.Equal(1, host.LoadDirectory(directory));
        Assert.True(Assert.Single(host.Mods).Loaded);
    }

    [Fact]
    public void Describe_ShowsLoadedAndRejected()
    {
        var root = new GameRoot();
        using var host = new PakModHost(root, NewSettingsPath());

        host.LoadPak(BuildMod("ok.mod"));
        host.LoadPak(BuildMod("bad.mod", apiVersion: 99));

        string text = host.Describe();
        Assert.Contains("[ok] ok.mod", text);
        Assert.Contains("[--] bad.mod", text);
        Assert.Contains("apiVersion", text);
    }

    public void Dispose()
    {
        foreach (string directory in _tempDirectories)
        {
            try { Directory.Delete(directory, true); }
            catch { }
        }
    }
}
