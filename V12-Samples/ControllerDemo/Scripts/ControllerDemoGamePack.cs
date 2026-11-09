// ControllerDemo - V12 game pak entry point.
using V12;
using V12.Basic;
using V12.Core;

namespace ControllerDemo
{
    /// <summary>
    /// Registers the gameplay systems the demo needs. The scene itself lives in
    /// <c>Scenes/Main.worldml</c>; the interactive props are built by
    /// <see cref="Components.ControllerDemoController"/>.
    /// </summary>
    public sealed class ControllerDemoGamePack : IV12Gamepack
    {
        public string Name => "Controller Demo";

        public void Initialize()
        {
            var root = GameRoot.Instance
                ?? throw new InvalidOperationException("No active GameRoot when the gamepak initialized.");

            BasicRegistry.RegisterAll(root);
        }

        public void OnStart()
        {
            GameRoot.Log?.Information("[ControllerDemo] Started");
        }
    }
}
