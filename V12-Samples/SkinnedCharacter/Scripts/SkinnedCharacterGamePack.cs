// SkinnedCharacter - V12 game pak entry point.
using V12;
using V12.Basic;
using V12.Core;

namespace SkinnedCharacter
{
    /// <summary>
    /// Registers the engine systems this sample needs. The scene lives in
    /// <c>Scenes/Main.worldml</c>; camera and playback controls are driven by
    /// <see cref="Components.SkinnedCharacterController"/>.
    /// </summary>
    public sealed class SkinnedCharacterGamePack : IV12Gamepack
    {
        public string Name => "Skinned Character";

        public void Initialize()
        {
            var root = GameRoot.Instance
                ?? throw new InvalidOperationException("No active GameRoot when the gamepak initialized.");

            BasicRegistry.RegisterAll(root);
        }

        public void OnStart()
        {
            GameRoot.Log?.Information("[SkinnedCharacter] Started");
        }
    }
}
