using System.Linq;
using System.Numerics;
using V12.Components;
using V12.Core;
using V12.Core.Interfaces.Renderer;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Render interpolation must smooth the presented pose without touching the
/// simulated one. These tests pin both halves: gameplay reads the exact pose,
/// and the captured snapshot reads the interpolated one.
/// </summary>
public sealed class RenderInterpolationTests
{
    [Fact]
    public void RenderPose_InterpolatesWhileGameplayStaysExact()
    {
        GameRoot root = CreateRoot(out World world, out Element element);
        element.AddComponent(new MeshComponent(MeshShape.Box));

        // One step at rest primes the pose snapshot.
        root.Update(1f / 60f);
        Assert.Equal(0f, element.WorldTransform.M41, 4);

        // Move, then advance one full step plus half a step.
        element.LocalTransform = new TRS(new Vector3(10f, 0f, 0f));
        root.Update(1.5f / 60f);

        Assert.Equal(0.5f, root.InterpolationAlpha, 3);
        Assert.Equal(10f, element.WorldTransform.M41, 4);
        Assert.Equal(5f, element.RenderWorldTransform.M41, 2);

        var snapshot = root.CaptureFrame();
        var renderable = snapshot.Renderables.First(r => r.ElementId == element.Id);
        Assert.Equal(5f, renderable.Transform.M41, 2);
    }

    [Fact]
    public void RenderPose_DisabledKeepsTheSimulatedPose()
    {
        GameRoot root = CreateRoot(out _, out Element element);
        root.RenderInterpolation = false;

        root.Update(1f / 60f);
        element.LocalTransform = new TRS(new Vector3(10f, 0f, 0f));
        root.Update(1.5f / 60f);

        Assert.Equal(10f, element.RenderWorldTransform.M41, 4);
    }

    [Fact]
    public void RenderPose_ComposesThroughTheParentChain()
    {
        GameRoot root = CreateRoot(out World world, out Element parent);
        parent.LocalTransform = new TRS(new Vector3(0f, 0f, 0f));

        var child = new Element("Child");
        parent.AddChild(child);

        root.Update(1f / 60f);

        parent.LocalTransform = new TRS(new Vector3(10f, 0f, 0f));
        child.LocalTransform = new TRS(new Vector3(0f, 0f, 4f));
        root.Update(1.5f / 60f);

        Matrix4x4 render = child.RenderWorldTransform;
        Assert.Equal(5f, render.M41, 2);
        Assert.Equal(2f, render.M43, 2);
    }

    private static GameRoot CreateRoot(out World world, out Element element)
    {
        var root = new GameRoot { RenderInterpolation = true };
        world = new World("Interpolation");
        root.Worlds.Add(world);
        root.SelectWorld(world);

        element = new Element("Mover");
        element.LocalTransform = new TRS(Vector3.Zero, Quaternion.Identity, Vector3.One);
        world.AddElement(element);

        return root;
    }
}
