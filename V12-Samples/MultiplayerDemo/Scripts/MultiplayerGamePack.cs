using V12;
using V12.Basic;
using V12.Core;

namespace MultiplayerDemo;

/// <summary>
/// Registers the basic gameplay systems. Networking is started by the local
/// <c>MultiplayerBootstrap</c> component so the same pak works in host, client,
/// and offline modes.
/// </summary>
public sealed class MultiplayerGamePack : IV12Gamepack
{
    public string Name => "Multiplayer Demo";

    public void Initialize()
    {
        var root = GameRoot.Instance
            ?? throw new InvalidOperationException("No active GameRoot when the gamepak initialized.");

        BasicRegistry.RegisterAll(root);
    }

    public void OnStart()
    {
        GameRoot.Log.Information("[MultiplayerDemo] Started");
    }
}
