using System.Linq;
using V12.Components;
using V12.Core;
using V12.Core.Rendering;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Regression coverage for the editor's non-ticking scene world. A light or
/// visual change must still mark the frame dirty and appear in the next
/// snapshot even when <see cref="World.Update"/> is skipped.
/// </summary>
public sealed class NonTickingWorldTests
{
    [Fact]
    public void NonTickingWorld_LightChange_IsCaptured()
    {
        var root = new GameRoot();
        var world = new World("Scene") { TickingEnabled = false };
        root.Worlds.Add(world);
        root.SelectWorld(world);

        var element = new Element("Lamp");
        var light = new GenericLightComponent { Energy = 1f };
        element.AddComponent(light);
        world.AddElement(element);

        var initial = root.CaptureFrame();
        var initialLight = initial.Renderables.Single(r => r.ElementId == element.Id);
        Assert.Equal(SnapshotNodeType.LightDirectional, initialLight.NodeType);
        Assert.Equal(1f, initialLight.LightIntensity);

        // Drain the add-element dirty flag, then change the light.
        root.ConsumeRenderDirty();
        light.Energy = 2f;

        Assert.True(root.ConsumeRenderDirty(), "changing a light on a non-ticking world must mark the frame dirty");

        var updated = root.CaptureFrame();
        var updatedLight = updated.Renderables.Single(r => r.ElementId == element.Id);
        Assert.Equal(2f, updatedLight.LightIntensity);
    }
}
