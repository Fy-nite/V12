using System.Linq;
using V12.Components;
using V12.Components.Renderables;
using V12.Core;
using V12.Core.Interfaces;
using V12.Core.Interfaces.Renderer;
using V12.Core.Rendering;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// The packet is the renderer-facing contract. It must carry the element's
/// MaterialComponent, or packet-only renderers (Rockstar) and packet-driven
/// texture paths (MonoGame) render default grey.
/// </summary>
public sealed class RenderPacketTests
{
    [Fact]
    public void MeshDraw_CarriesMaterialAndTexturePaths()
    {
        var root = new GameRoot();
        var renderer = new CapturingRenderer();
        root.Registry.Register("IRenderer", renderer);

        var world = new World("Packet") { TickingEnabled = false };
        root.Worlds.Add(world);
        root.SelectWorld(world);

        var element = new Element("Crate");
        var mesh = new BoxMesh();
        element.AddComponent(mesh);
        element.AddComponent(new MeshRenderer { Mesh = mesh });
        element.AddComponent(new MaterialComponent
        {
            AlbedoTexture = "textures/crate.png",
            NormalTexture = "textures/crate_normal.png",
            MetallicTexture = "textures/crate_metal.png",
            RoughnessTexture = "textures/crate_rough.png",
            EmissionTexture = "textures/crate_emit.png",
            R = 0.5f,
            G = 0.25f,
            B = 0.125f,
            A = 1f,
            Metallic = 0.3f,
            Roughness = 0.4f,
            Uv1ScaleX = 2f,
            Uv1ScaleY = 3f,
            Uv1OffsetX = 0.1f,
            Uv1OffsetY = 0.2f,
        });
        world.AddElement(element);

        root.Initialize();
        root.MarkRenderDirty();
        root.V12Tick(0f);

        var draw = renderer.LastPacket!.Meshes.Single(m => m.ElementId == element.Id);

        Assert.Equal("textures/crate.png", draw.TexturePath);
        Assert.Equal("textures/crate_normal.png", draw.NormalTexturePath);
        Assert.Equal("textures/crate_metal.png", draw.MetallicTexturePath);
        Assert.Equal("textures/crate_rough.png", draw.RoughnessTexturePath);
        Assert.Equal("textures/crate_emit.png", draw.EmissionTexturePath);
        Assert.Equal(0.3f, draw.Metallic, 3);
        Assert.Equal(0.4f, draw.Roughness, 3);
        Assert.Equal(2f, draw.UvScale.X, 3);
        Assert.Equal(3f, draw.UvScale.Y, 3);
        Assert.Equal(0.1f, draw.UvOffset.X, 3);
        Assert.Equal(0.2f, draw.UvOffset.Y, 3);
        Assert.Equal(128, draw.Albedo.R);
        Assert.Equal(64, draw.Albedo.G);
        Assert.Equal(32, draw.Albedo.B);
    }

    private sealed class CapturingRenderer : IRenderer
    {
        public RenderPacket? LastPacket;

        public int GetFPS() => 0;
        public RendererInfo GetAllInfo() => new();
        public void QueueItem(IRenderable item) { }
        public void RemoveItem(IRenderable item) { }
        public void QueueItems(RenderPacket packet) => LastPacket = packet;
        public void RemoveItems(RenderPacket packet) { }
        public int GetScreenWidth() => 1280;
        public int GetScreenHeight() => 720;
        public void step() { }
        public bool LockMouse { get; set; }
        public void ApplySnapshot(FrameSnapshot snapshot) { }
    }
}
