using System;
using System.IO;
using System.Linq;
using V12;
using V12.Bindings;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.GamePak;
using V12.WorldML;
using Xunit;

namespace V12.Architecture.Tests;

public sealed class HotReloadTests
{
    [Fact]
    public void ComponentResolver_WinsOverAssemblyScan()
    {
        var parser = new WorldMLParser();
        ComponentTypeResolver.SetResolver(
            name => string.Equals(name, "ResolverProbeComponent", StringComparison.OrdinalIgnoreCase)
                ? typeof(SecondProbe.ResolverProbeComponent)
                : null);

        try
        {
            var root = parser.Parse(
                "<World name=\"W\"><Element name=\"E\"><Component type=\"ResolverProbeComponent\" /></Element></World>");

            var element = root.FindChildByNameRecursive("E");
            Assert.NotNull(element);
            Assert.NotNull(element!.GetComponent<SecondProbe.ResolverProbeComponent>());
            Assert.Null(element.GetComponent<FirstProbe.ResolverProbeComponent>());
        }
        finally
        {
            ComponentTypeResolver.SetResolver(null);
        }
    }

    [Fact]
    public void WorldContentSwap_DetachesOldComponents()
    {
        var oldWorld = new World("W");
        var oldElement = new Element("Old");
        var probe = new DetachProbeComponent();
        oldElement.AddComponent(probe);
        oldWorld.AddElement(oldElement);

        var incoming = new World("W");
        incoming.AddElement(new Element("New"));

        WorldContentSwap.Replace(oldWorld, incoming);

        Assert.True(probe.Detached);
        Assert.Single(oldWorld.Root);
        Assert.Equal("New", oldWorld.Root[0].Name);
    }

    [Fact]
    public void GamepackLoader_RemoveFromAssembly_StopsLifecycle()
    {
        _ = new GameRoot();
        GamepackProbe.ProbeGamepack.Stopped = false;

        var loader = new GamepackLoader();
        var loaded = loader.LoadFromAssembly(typeof(GamepackProbe.ProbeGamepack).Assembly);
        Assert.Equal(1, loaded);

        var removed = loader.RemoveFromAssembly(typeof(GamepackProbe.ProbeGamepack).Assembly);

        Assert.Equal(1, removed);
        Assert.True(GamepackProbe.ProbeGamepack.Stopped);
        Assert.DoesNotContain(loader.Gamepaks, pak => pak is GamepackProbe.ProbeGamepack);
    }

    [Fact]
    public void CSharpScriptHost_Reload_ReplacesComponentTypes()
    {
        _ = new GameRoot();
        var directory = Path.Combine(Path.GetTempPath(), "v12-hotreload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        CSharpScriptHost? host = null;

        try
        {
            string source = """
                using V12.Components;

                namespace HotProbe;

                public sealed class HotProbeComponent : ComponentBase
                {
                    public int Value { get; set; } = 1;
                    public override V12.Core.Core.Interfaces.IWorldElement BuildUI() => new Element();
                }
                """;

            string file = Path.Combine(directory, "HotProbe.cs");
            File.WriteAllText(file, source);

            host = new CSharpScriptHost(directory, "HotReloadProbe");
            var first = host.Reload();
            Assert.True(first.Success, first.ErrorText);

            var firstAssembly = host.Assembly;
            Assert.NotNull(firstAssembly);

            var registry = CSharpComponentRegistry.FromGameRoot();
            Assert.NotNull(registry);
            Assert.True(registry!.TryGet("HotProbeComponent", out var firstEntry));
            Assert.Same(firstAssembly, firstEntry.Type.Assembly);

            File.WriteAllText(file, source.Replace("Value { get; set; } = 1;", "Value { get; set; } = 2;"));
            var second = host.Reload();
            Assert.True(second.Success, second.ErrorText);

            var secondAssembly = host.Assembly;
            Assert.NotNull(secondAssembly);
            Assert.NotSame(firstAssembly, secondAssembly);

            Assert.True(registry.TryGet("HotProbeComponent", out var secondEntry));
            Assert.Same(secondAssembly, secondEntry.Type.Assembly);
            Assert.Same(secondEntry.Type, ComponentTypeResolver.Resolve("HotProbeComponent"));
        }
        finally
        {
            ComponentTypeResolver.SetResolver(null);
            host?.Dispose();
            try { if (host is not null && File.Exists(host.OutputPath)) File.Delete(host.OutputPath); } catch { }
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private sealed class DetachProbeComponent : ComponentBase
    {
        public bool Detached { get; private set; }

        public override IWorldElement BuildUI() => new Element();

        public override void OnDetach(IWorldElement element)
        {
            Detached = true;
            base.OnDetach(element);
        }
    }
}
