// HelloXR - V12 game pak entry point.
using V12;
using V12.Basic;
using V12.Core;

namespace HelloXR
{
    /// <summary>
    /// Registers the gameplay systems. Tracking is owned by the renderer host
    /// (NovaV2's XR service), not by the game.
    /// </summary>
    public sealed class HelloXRGamePack : IV12Gamepack
    {
        public string Name => "Hello XR";

        public void Initialize()
        {
            var root = GameRoot.Instance
                ?? throw new InvalidOperationException("No active GameRoot when the gamepak initialized.");

            BasicRegistry.RegisterAll(root);
        }

        public void OnStart()
        {
            GameRoot.Log?.Information("[HelloXR] Started");
        }
    }
}
