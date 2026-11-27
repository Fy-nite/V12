using System;
using System.IO;
using V12.Hosting;
using V12.Pak;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// The engine-side host owns pak selection and the root lifecycle, so renderers
/// do not have to. These cover the resolution order (argument, dev inbox,
/// descriptor, executable name), world selection and shutdown.
/// </summary>
public sealed class V12HostTests : IDisposable
{
    private readonly string _root;

    public V12HostTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "v12host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private static readonly V12PakOptions ContentOnly = new()
    {
        LoadDlls = false,
        LoadContractScripts = false,
        LoadCSharpScripts = false,
    };

    private string NewDirectory(string name)
    {
        string directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string BuildPak(string directory, string pakName, string worldName)
    {
        string worlds = Path.Combine(directory, "worlds", worldName);
        Directory.CreateDirectory(worlds);
        File.WriteAllText(
            Path.Combine(worlds, "world.xml"),
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <World name="{worldName}">
               <Element name="Marker" />
             </World>
             """);

        string pak = Path.Combine(directory, pakName);
        V12PakBuilder.Build(directory, pak);
        return pak;
    }

    // InitializeRoot stays false: GameRoot.Initialize starts the networking
    // thread, and spawning extra ones in a test run perturbs the timing
    // sensitive sync tests. NovaV2 exercises the initialized path in the host.
    private V12HostOptions OptionsFor(string content, string user) => new()
    {
        ContentDirectory = content,
        UserDirectory = user,
        InitializeRoot = false,
        UseExecutableNameFallback = false,
        PakOptions = ContentOnly,
    };

    [Fact]
    public void ExplicitPakWinsOverDescriptorAndInbox()
    {
        string content = NewDirectory("content");
        string other = NewDirectory("other");
        string user = NewDirectory("user");

        BuildPak(content, "Shipped.v12pak", "ShippedWorld");
        string explicitPak = BuildPak(other, "Other.v12pak", "OtherWorld");
        File.WriteAllText(Path.Combine(content, "game.json"), """{ "pak": "Shipped.v12pak" }""");

        var options = OptionsFor(content, user);
        options.PakPath = explicitPak;

        using var host = new V12Host(options).Start();

        Assert.NotNull(host.Source);
        Assert.Equal("argument", host.Source!.Origin);
        Assert.Equal(Path.GetFullPath(explicitPak), host.Source.Path);
        Assert.Equal("OtherWorld", host.Root.SelectedWorld?.WorldName);
    }

    [Fact]
    public void DescriptorNamesThePakAndTheWorld()
    {
        string content = NewDirectory("content");
        string user = NewDirectory("user");
        BuildPak(content, "Shipped.v12pak", "ShippedWorld");
        File.WriteAllText(
            Path.Combine(content, "game.json"),
            """{ "pak": "Shipped.v12pak", "world": "ShippedWorld" }""");

        using var host = new V12Host(OptionsFor(content, user)).Start();

        Assert.NotNull(host.Source);
        Assert.Equal("descriptor", host.Source!.Origin);
        Assert.Equal("ShippedWorld", host.Root.SelectedWorld?.WorldName);
        Assert.Contains(host.Root.Worlds, w => w.WorldName == "ShippedWorld");
    }

    [Fact]
    public void DevInboxOutranksTheShippedBuild()
    {
        string content = NewDirectory("content");
        string user = NewDirectory("user");

        BuildPak(content, "Shipped.v12pak", "ShippedWorld");
        string pushed = BuildPak(NewDirectory("push"), "Pushed.v12pak", "PushedWorld");

        string inbox = Path.Combine(user, "v12inbox");
        Directory.CreateDirectory(inbox);
        File.Copy(pushed, Path.Combine(inbox, V12PakResolver.InboxFileName));

        File.WriteAllText(Path.Combine(content, "game.json"), """{ "pak": "Shipped.v12pak" }""");

        using var host = new V12Host(OptionsFor(content, user)).Start();

        Assert.NotNull(host.Source);
        Assert.Equal("dev-inbox", host.Source!.Origin);
        Assert.Equal("PushedWorld", host.Root.SelectedWorld?.WorldName);
    }

    [Fact]
    public void NoPakStartsAnEmptyRoot()
    {
        string content = NewDirectory("content");
        string user = NewDirectory("user");

        using var host = new V12Host(OptionsFor(content, user)).Start();

        Assert.Null(host.Source);
        Assert.Null(host.Pak);
        Assert.Empty(host.Root.Worlds);
        Assert.True(host.IsHeadless);
    }

    [Fact]
    public void ShutdownUnloadsThePakAndClearsWorlds()
    {
        string content = NewDirectory("content");
        string user = NewDirectory("user");
        BuildPak(content, "Shipped.v12pak", "ShippedWorld");
        File.WriteAllText(Path.Combine(content, "game.json"), """{ "pak": "Shipped.v12pak" }""");

        var host = new V12Host(OptionsFor(content, user)).Start();
        Assert.NotEmpty(host.Root.Worlds);

        host.Shutdown();

        Assert.Null(host.Pak);
        Assert.Empty(host.Root.Worlds);
        Assert.False(host.IsRunning);
    }

    public void Dispose()
    {
        V12PakCache.Purge();

        try { Directory.Delete(_root, true); }
        catch
        {
            // Best effort; the OS temp directory is disposable.
        }
    }
}
