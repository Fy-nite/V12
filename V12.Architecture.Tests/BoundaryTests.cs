using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Boundary guardrails for the V12 split.
///
/// These tests are intentionally written against the assemblies and project files,
/// not against namespaces. A project/package reference is the thing that actually
/// breaks the boundary and ships in the build, so that is what we assert.
///
/// Rules that are temporarily skipped describe the target state and are enabled as
/// each extraction phase lands (see Docs/BOUNDARY_REFACTOR_PLAN.md).
/// </summary>
public sealed class BoundaryTests
{
    private static readonly Assembly Engine = typeof(V12.Core.GameRoot).Assembly;
    private static readonly Assembly Basic = typeof(V12.Basic.BasicRegistry).Assembly;
    private static readonly Assembly Pak = typeof(V12.Pak.V12PakLoader).Assembly;
    private static readonly Assembly Editor = typeof(V12.Editor.IViewportInteraction).Assembly;
    private static readonly Assembly Bindings = typeof(V12.Bindings.V12LinkedAssemblies).Assembly;

    private static readonly string[] RendererAndToolDependencies =
    {
        "Godot",
        "GodotSharp",
        "MonoGame.Framework",
        "Nova",
        "V12TwoDog",
        "V12Studio",
        "V12StudioPak",
    };

    [Fact]
    public void Engine_ShouldNotDependOn_RendererOrToolAssemblies()
        => AssertNoDependency(Engine, RendererAndToolDependencies);

    [Fact]
    public void Basic_ShouldNotDependOn_RendererOrToolAssemblies()
        => AssertNoDependency(Basic, RendererAndToolDependencies);

    [Fact]
    public void Pak_ShouldNotDependOn_RendererOrToolAssemblies()
        => AssertNoDependency(Pak, RendererAndToolDependencies);

    [Fact]
    public void Editor_ShouldNotDependOn_RendererOrToolAssemblies()
        => AssertNoDependency(Editor, RendererAndToolDependencies);

    [Fact]
    public void Bindings_ShouldNotDependOn_AuthoringTools()
        => AssertNoDependency(Bindings, "V12Studio", "V12StudioPak", "Nova", "V12TwoDog", "Godot", "GodotSharp", "MonoGame.Framework");

    [Fact]
    public void Engine_ProjectFile_ShouldNotReference_RenderersOrTools()
    {
        var csproj = ReadRepoFile("V12-Engine", "V12.csproj");

        foreach (var forbidden in RendererAndToolDependencies)
        {
            Assert.DoesNotContain(forbidden, csproj, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Bindings_Source_ShouldNotHardcode_AuthoringToolNames()
    {
        var source = ReadRepoFile("V12.Basic", "libs", "V12.Bindings", "CSharpScriptCompiler.cs");

        Assert.DoesNotContain("V12Studio", source, StringComparison.Ordinal);
        Assert.DoesNotContain("V12StudioPak", source, StringComparison.Ordinal);
    }

    [Fact(Skip = "Engine still references Prowl until the V12.Paper extraction (Phase 3).")]
    public void Engine_ShouldNotDependOn_Prowl()
        => AssertNoDependency(Engine, "Prowl", "Prowl.Paper", "Prowl.Origami", "Prowl.Quill", "Prowl.Vector");

    [Fact]
    public void Engine_ShouldNotDependOn_EditorAssembly()
        => AssertNoDependency(Engine, "V12.Editor");

    [Fact]
    public void Nova_ShouldNotReference_EditorAssembly()
    {
        var csproj = ReadRepoFile("Nova", "Nova.Shared", "Nova.Shared.csproj");

        Assert.DoesNotContain("V12EditorProject", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("V12.Editor", csproj, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoDependency(Assembly assembly, params string[] forbidden)
    {
        var result = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbidden)
            .GetResult();

        Assert.True(result.IsSuccessful, BuildFailureMessage(assembly, result));
    }

    private static string BuildFailureMessage(Assembly assembly, TestResult result)
    {
        var failing = result.FailingTypes is null
            ? "<unknown>"
            : string.Join(Environment.NewLine, result.FailingTypes.Select(t => "  " + t.FullName));

        return $"{assembly.GetName().Name} has forbidden dependencies:{Environment.NewLine}{failing}";
    }

    private static string ReadRepoFile(params string[] relativeParts)
    {
        var root = FindMonorepoRoot();
        var path = Path.Combine(new[] { root }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"Expected repository file at '{path}'.");
        return File.ReadAllText(path);
    }

    private static string FindMonorepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "V12-Monorepo.slnx")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find V12-Monorepo.slnx above '{AppContext.BaseDirectory}'.");
    }
}
