// HelloDotnet - V12 game pak entry point
using V12;
using V12.Components;
using V12.Core;
using hellodotnet.Components;
using V12.Components.Renderables;
using V12.Basic.Building;

namespace hellodotnet
{
    /// <summary>
    /// Entry point for the HelloDotnet game pak. The generic V12 renderer discovers
    /// this class in the packaged assembly and drives the two-phase lifecycle:
    /// <see cref="Initialize"/> then <see cref="OnStart"/>.
    /// </summary>
    public sealed class GamePack : IV12Gamepack
    {
        private GameRoot _root = default!;

        public string Name => "HelloDotnet";

        public void Initialize()
        {
            _root = GameRoot.Instance
                ?? throw new InvalidOperationException("No active GameRoot when the game pak initialized.");
        }

        public void OnStart()
        {
            // Gamepak mode starts with no selected world, so create one.
            var world = _root.CreateWorld("HelloDotnet");
      
            var spinner = new Element("Spinner");
            spinner.SetPosition(0,1,0);
            var mesh = new BoxMesh();
            spinner.AddComponent(mesh);
            spinner.AddComponent(new MeshRenderer() {Mesh = mesh});
            spinner.AddComponent(new DefaultScript());
            world.AddElement(spinner);
        }
    }
}
