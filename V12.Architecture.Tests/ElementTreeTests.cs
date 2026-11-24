using System.Linq;
using V12.Components;
using V12.Core;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Tree invariants the module and tooling layers rely on: the id index covers
/// whole subtrees on add and remove, reparenting cannot leave a child in two
/// parents or cross worlds, and visibility toggles re-capture the frame.
/// </summary>
public sealed class ElementTreeTests
{
    private static (GameRoot Root, World World) NewRoot(string name = "Scene")
    {
        var root = new GameRoot();
        var world = new World(name);
        root.Worlds.Add(world);
        root.SelectWorld(world);
        return (root, world);
    }

    [Fact]
    public void AddElement_IndexesEveryDescendant()
    {
        var (_, world) = NewRoot();

        var top = new Element("Root");
        var child = new Element("Child");
        var grandchild = new Element("Grandchild");
        child.AddChild(grandchild);
        top.AddChild(child);

        world.AddElement(top);

        Assert.Same(top, world.FindElementById(top.Id));
        Assert.Same(child, world.FindElementById(child.Id));
        Assert.Same(grandchild, world.FindElementById(grandchild.Id));
    }

    [Fact]
    public void AddChild_OnAnElementInAWorld_IndexesTheSubtree()
    {
        var (_, world) = NewRoot();

        var parent = new Element("Parent");
        world.AddElement(parent);

        var child = new Element("Child");
        var grandchild = new Element("Grandchild");
        child.AddChild(grandchild);
        parent.AddChild(child);

        Assert.Same(child, world.FindElementById(child.Id));
        Assert.Same(grandchild, world.FindElementById(grandchild.Id));
    }

    [Fact]
    public void RemoveChild_UnindexesTheSubtree()
    {
        var (_, world) = NewRoot();

        var parent = new Element("Parent");
        var child = new Element("Child");
        var grandchild = new Element("Grandchild");
        child.AddChild(grandchild);
        parent.AddChild(child);
        world.AddElement(parent);

        parent.RemoveChild(child);

        Assert.Same(parent, world.FindElementById(parent.Id));
        Assert.Null(world.FindElementById(child.Id));
        Assert.Null(world.FindElementById(grandchild.Id));
    }

    [Fact]
    public void RemoveElement_UnindexesTheSubtree()
    {
        var (_, world) = NewRoot();

        var top = new Element("Root");
        var child = new Element("Child");
        var grandchild = new Element("Grandchild");
        child.AddChild(grandchild);
        top.AddChild(child);
        world.AddElement(top);

        world.RemoveElement(top);

        Assert.Null(world.FindElementById(top.Id));
        Assert.Null(world.FindElementById(child.Id));
        Assert.Null(world.FindElementById(grandchild.Id));
    }

    [Fact]
    public void Reparent_MovesBetweenParentsInOneWorld()
    {
        var (_, world) = NewRoot();

        var first = new Element("First");
        var second = new Element("Second");
        var child = new Element("Child");
        world.AddElement(first);
        world.AddElement(second);
        first.AddChild(child);

        second.AddChild(child);

        Assert.Same(second, child.Parent);
        Assert.DoesNotContain(child, first.Children);
        Assert.Contains(child, second.Children);
        Assert.Same(child, world.FindElementById(child.Id));
    }

    [Fact]
    public void CrossWorldReparent_IsRefused()
    {
        var root = new GameRoot();
        var worldA = new World("A");
        var worldB = new World("B");
        root.Worlds.Add(worldA);
        root.Worlds.Add(worldB);
        root.SelectWorld(worldA);

        var parent = new Element("Parent");
        var child = new Element("Child");
        worldA.AddElement(parent);
        worldB.AddElement(child);

        parent.AddChild(child);

        Assert.Null(child.Parent);
        Assert.DoesNotContain(child, parent.Children);
        Assert.Same(child, worldB.FindElementById(child.Id));
    }

    [Fact]
    public void Cycle_IsRefused()
    {
        var (_, world) = NewRoot();

        var top = new Element("Top");
        var child = new Element("Child");
        world.AddElement(top);
        top.AddChild(child);

        child.AddChild(top);

        Assert.Null(top.Parent);
        Assert.Same(top, child.Parent);
        Assert.DoesNotContain(top, child.Children);
    }

    [Fact]
    public void ElementActive_MarksTheFrameDirty()
    {
        var (root, world) = NewRoot();
        var element = new Element("Thing");
        world.AddElement(element);
        root.ConsumeRenderDirty();

        element.Active = false;

        Assert.False(element.Active);
        Assert.True(root.ConsumeRenderDirty());
    }

    [Fact]
    public void ComponentActive_MarksTheFrameDirty()
    {
        var (root, world) = NewRoot();
        var element = new Element("Thing");
        var light = new GenericLightComponent();
        element.AddComponent(light);
        world.AddElement(element);
        root.ConsumeRenderDirty();

        light.Active = false;

        Assert.False(light.Active);
        Assert.True(root.ConsumeRenderDirty());
    }

    [Fact]
    public void AddComponent_OnANonFocusedWorldElement_Attaches()
    {
        var root = new GameRoot();
        var focused = new World("Focused");
        var background = new World("Background");
        root.Worlds.Add(focused);
        root.Worlds.Add(background);
        root.SelectWorld(focused);

        var element = new Element("BackgroundThing");
        background.AddElement(element);

        var light = new GenericLightComponent();
        element.AddComponent(light);

        Assert.Contains(light, element.Components);
    }
}
