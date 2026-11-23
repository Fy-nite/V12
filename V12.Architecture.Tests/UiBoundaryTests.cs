using System;
using System.Linq;
using System.Reflection;
using V12.Core;
using V12.Core.Rendering;
using V12.Core.UI.Paper;
using V12.Paper;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// The renderer-agnostic UI boundary: the engine owns the UI model and the
/// neutral contracts, V12.Paper owns the Prowl implementation, and neither the
/// engine nor the renderer contract references the implementation.
/// </summary>
public sealed class UiBoundaryTests
{
    [Fact]
    public void EngineAssembly_HasNoUIImplementation()
    {
        var engine = typeof(GameRoot).Assembly;
        var implementation = new[]
        {
            "ProwlPaperRenderer",
            "PaperCanvasRenderer",
            "PaperWorldCanvasSystem",
            "PaperSurfaceInput",
            "OrigamiDockHost",
        };

        foreach (var type in engine.GetTypes())
        {
            if (type.Namespace != "V12.Core.UI.Paper") continue;
            Assert.DoesNotContain(type.Name, implementation);
        }
    }

    [Fact]
    public void PaperPackage_OwnsTheImplementation()
    {
        var paper = typeof(PaperUiSource).Assembly;

        Assert.NotNull(paper.GetType("V12.Core.UI.Paper.ProwlPaperRenderer"));
        Assert.NotNull(paper.GetType("V12.Core.UI.Paper.PaperCanvasRenderer"));
        Assert.NotNull(paper.GetType("V12.Core.UI.Paper.PaperWorldCanvasSystem"));
        Assert.NotNull(paper.GetType("V12.Paper.PaperUiSource"));
        Assert.NotNull(paper.GetType("V12.Paper.PaperSurfaceInput"));
    }

    [Fact]
    public void UiContracts_HaveNoToolkitTypes()
    {
        var engine = typeof(PaperFrame).Assembly;
        var contracts = new[]
        {
            "V12.Core.UI.Paper.PaperFrame",
            "V12.Core.UI.Paper.PaperMesh",
            "V12.Core.UI.Paper.UiBrush",
            "V12.Core.Rendering.UIFrame",
            "V12.Core.Rendering.IUiSource",
            "V12.Core.Rendering.WorldCanvasSnapshot",
            "V12.Core.Rendering.UiFrameContext",
        };

        foreach (var name in contracts)
        {
            var type = engine.GetType(name);
            Assert.NotNull(type);

            foreach (var field in type!.GetFields(BindingFlags.Public | BindingFlags.Instance))
                Assert.DoesNotContain("Prowl", field.FieldType.FullName ?? "");

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Assert.DoesNotContain("Prowl", property.PropertyType.FullName ?? "");
        }
    }

    [Fact]
    public void RendererContract_DoesNotReferenceTheUiPackage()
    {
        var refs = typeof(V12.Rendering.IRenderer).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain("V12.Paper", refs);
    }
}
