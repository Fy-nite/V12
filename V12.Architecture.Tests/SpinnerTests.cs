using System.Linq;
using V12.Basic.Building;
using V12.Components;
using V12.Components.Renderables;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Rendering;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Reproduces the HelloDotnet spinner: a compiled component updates the owning
/// element's LocalTransform every world tick, and the render snapshot must
/// carry the new rotation.
/// </summary>
public sealed class SpinnerTests
{
    [Fact]
    public void SpinnerComponent_UpdatesSnapshotRotation()
    {
        var root = new GameRoot();
        var world = new World("Spin") { TickingEnabled = true };
        root.Worlds.Add(world);
        root.SelectWorld(world);

        var element = new Element("Spinner");
        element.SetPosition(0, 1, 0);
        var mesh = new BoxMesh();
        element.AddComponent(mesh);
        element.AddComponent(new MeshRenderer { Mesh = mesh });
        element.AddComponent(new SpinTestComponent());
        world.AddElement(element);

        var before = root.CaptureFrame().Renderables.Single(r => r.ElementId == element.Id);

        world.Update(1f / 60f);

        var after = root.CaptureFrame().Renderables.Single(r => r.ElementId == element.Id);

        Assert.NotEqual(before.LocalTransform, after.LocalTransform);
        Assert.NotEqual(before.Transform, after.Transform);
    }

    private sealed class SpinTestComponent : ScriptableComponent
    {
        public float Speed { get; set; } = 45f;

        public override void Update(float deltaTime)
        {
            if (Owner == null) return;
            // Same expression as V12-Samples/HelloDotnet/Scripts/hellodotnetScript.cs.
            // RY is radians; Speed is degrees per second.
            Owner.GetOrAddTransform().RY += Speed * deltaTime * (System.MathF.PI / 180f);
        }
    }
}
