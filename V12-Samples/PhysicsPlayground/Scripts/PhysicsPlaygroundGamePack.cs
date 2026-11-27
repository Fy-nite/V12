using V12;
using V12.Basic;
using V12.Core;

namespace PhysicsPlayground;

/// <summary>
/// Registers the standard gameplay services. The yard and the grabber are
/// ordinary components, so there is no sample-specific bootstrap beyond this.
/// XR is declared in manifest.json and started by the host when a runtime is
/// available; the same world falls back to the desktop camera otherwise.
/// </summary>
public sealed class PhysicsPlaygroundGamePack : IV12Gamepack
{
    public string Name => "Physics Playground";

    public void Initialize()
    {
        var root = GameRoot.Instance
            ?? throw new InvalidOperationException("No active GameRoot when PhysicsPlayground initialized.");

        BasicRegistry.RegisterAll(root);

        // Optional host/client mode via V12_MP_MODE; solo is unchanged.
        root.Registry.Register("PhysicsPlaygroundNet", new PhysicsPlaygroundNet(root));
    }

    public void OnStart()
    {
        GameRoot.Log?.Information("[PhysicsPlayground] Started");
    }
}
