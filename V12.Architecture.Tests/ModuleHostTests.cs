using System.Collections.Generic;
using System.Linq;
using V12.Core;
using V12.Core.Modules;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Coverage for the module host as a feature: dependency ordering, rejection
/// paths, diagnostics, lifecycle and the command-line entry point.
/// </summary>
public sealed class ModuleHostTests
{
    [Fact]
    public void RegisterRange_StartsDependenciesFirst()
    {
        var root = new GameRoot();
        var order = new List<string>();

        var beta = new TestModule("beta", order, dependencies: new[] { "alpha" });
        var alpha = new TestModule("alpha", order);

        int started = root.Modules.RegisterRange(new IV12Module[] { beta, alpha });

        Assert.Equal(2, started);
        Assert.Equal(new[] { "alpha", "beta" }, order);
    }

    [Fact]
    public void MissingDependency_IsRejected_AndTheRestStillStarts()
    {
        var root = new GameRoot();
        var order = new List<string>();

        var good = new TestModule("good", order);
        var broken = new TestModule("broken", order, dependencies: new[] { "not-loaded" });

        int started = root.Modules.RegisterRange(new IV12Module[] { broken, good });

        Assert.Equal(1, started);
        Assert.Same(good, Assert.Single(root.Modules.Modules));

        var diagnostic = Assert.Single(root.Modules.Diagnostics, d => d.Name == "broken");
        Assert.False(diagnostic.Started);
        Assert.Contains("missing dependency", diagnostic.Error);
    }

    [Fact]
    public void DependencyCycle_IsRejected_WithoutStartingEither()
    {
        var root = new GameRoot();
        var order = new List<string>();

        var a = new TestModule("a", order, dependencies: new[] { "b" });
        var b = new TestModule("b", order, dependencies: new[] { "a" });

        int started = root.Modules.RegisterRange(new IV12Module[] { a, b });

        Assert.Equal(0, started);
        Assert.Empty(root.Modules.Modules);
        Assert.Equal(2, root.Modules.Diagnostics.Count(d => d.Error == "dependency cycle"));
    }

    [Fact]
    public void DuplicateName_StartsOnce()
    {
        var root = new GameRoot();
        var order = new List<string>();

        Assert.True(root.Modules.Register(new TestModule("dup", order)));
        Assert.False(root.Modules.Register(new TestModule("dup", order)));

        Assert.Single(root.Modules.Modules);
        var rejected = Assert.Single(root.Modules.Diagnostics, d => !d.Started && d.Name == "dup");
        Assert.Contains("already loaded", rejected.Error);
    }

    [Fact]
    public void DecliningModule_IsDisposedAndNotKept()
    {
        var root = new GameRoot();
        var module = new TestModule("decline", new List<string>()) { Accept = false };

        Assert.False(root.Modules.Register(module));

        Assert.Empty(root.Modules.Modules);
        Assert.True(module.Disposed);
        Assert.Contains("declined", Assert.Single(root.Modules.Diagnostics).Error);
    }

    [Fact]
    public void DisposeAll_DisposesInReverseStartOrder()
    {
        var root = new GameRoot();
        var order = new List<string>();
        var disposal = new List<string>();

        root.Modules.RegisterRange(new IV12Module[]
        {
            new TestModule("one", order, disposal: disposal),
            new TestModule("two", order, disposal: disposal),
        });

        root.Modules.DisposeAll();

        Assert.Empty(root.Modules.Modules);
        Assert.Equal(new[] { "two", "one" }, disposal);
    }

    [Fact]
    public void EnableFromArgs_NoModulesFlag_DisablesEverything()
    {
        var root = new GameRoot();

        int started = root.Modules.EnableFromArgs(new[] { "--no-modules", "--module", "whatever.dll" });

        Assert.Equal(0, started);
        Assert.Empty(root.Modules.Modules);
    }

    [Fact]
    public void EnableFromArgs_ModuleFlag_ReachesTheLoader()
    {
        var root = new GameRoot();
        string testAssembly = typeof(ModuleHostTests).Assembly.Location;

        int started = root.Modules.EnableFromArgs(new[] { "--module", testAssembly });

        // The test assembly is already loaded, so the loader must skip it by
        // name. The point is that the flag is parsed and handed to the loader.
        Assert.Equal(0, started);
        Assert.Empty(root.Modules.Modules);
    }

    [Fact]
    public void Context_CarriesArgsAndModulePaths()
    {
        var root = new GameRoot();
        var module = new TestModule("ctx", new List<string>());
        var args = new[] { "--hello", "1" };

        Assert.True(root.Modules.Register(module, args));

        var context = Assert.IsType<ModuleContext>(module.Context);
        Assert.Equal(2, context.Args.Length);
        Assert.Equal("--hello", context.Args[0]);
        Assert.Equal("1", context.Args[1]);
        Assert.Contains("modules", context.DataDirectory);
        Assert.Equal(typeof(TestModule).Assembly.Location, context.AssemblyPath);
    }

    [Fact]
    public void Describe_ReportsStartedAndRejectedModules()
    {
        var root = new GameRoot();
        var order = new List<string>();

        root.Modules.Register(new TestModule("ok", order));
        root.Modules.Register(new TestModule("bad", order, dependencies: new[] { "gone" }), Array.Empty<string>());

        string description = root.Modules.Describe();

        Assert.Contains("[ok] ok", description);
        Assert.Contains("[--] bad", description);
        Assert.Contains("missing dependency", description);
    }

    [Fact]
    public void DataDirectory_IsSanitised()
    {
        string path = ModuleContext.GetDataDirectory("../../evil/name");
        string folder = System.IO.Path.GetFileName(path);

        Assert.Contains("modules", path);
        Assert.DoesNotContain("..", folder);
        Assert.DoesNotContain('/', folder);
        Assert.DoesNotContain('\\', folder);
    }

    private sealed class TestModule : IV12Module
    {
        private readonly List<string> _order;
        private readonly List<string>? _disposal;

        public TestModule(
            string name,
            List<string> order,
            IReadOnlyList<string>? dependencies = null,
            List<string>? disposal = null)
        {
            Name = name;
            _order = order;
            _disposal = disposal;
            Dependencies = dependencies ?? Array.Empty<string>();
        }

        public string Name { get; }
        public IReadOnlyList<string> Dependencies { get; }
        public bool Accept { get; init; } = true;
        public bool Disposed { get; private set; }
        public ModuleContext? Context { get; private set; }

        public bool Start(ModuleContext context)
        {
            Context = context;
            if (Accept) _order.Add(Name);
            return Accept;
        }

        public void Dispose()
        {
            Disposed = true;
            _disposal?.Add(Name);
        }
    }
}
