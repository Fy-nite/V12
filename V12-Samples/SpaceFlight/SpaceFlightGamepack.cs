using System.IO;
using V12;
using V12.Basic;
using V12.Core;
using V12.Core.Interfaces;

namespace SpaceFlight;

/// <summary>
/// NovaV2 gamepak entry point. Registers the basic gameplay systems, then builds
/// the orbital scene once the engine has finished initialising its services.
/// </summary>
public sealed class SpaceFlightGamepack : IV12Gamepack
{
    private GameRoot _root = null!;

    public string Name => "Space Flight";

    public void Initialize()
    {
        _root = GameRoot.Instance
            ?? throw new InvalidOperationException("No active GameRoot when SpaceFlight initialized.");

        BasicRegistry.RegisterAll(_root);

        // Dev gamepaks are loaded from a folder and the host does not mount a
        // resolver for that mode. The build copies Assets beside the gamepak
        // assembly, so register it here. Pak and --game modes already have a
        // resolver, and replacing it would hide their mounts.
        if (_root.Registry.Get<IAssetResolver>("AssetResolver") is null)
        {
            var resolver = new V12AssetResolver();
            string? assemblyDir = Path.GetDirectoryName(
                typeof(SpaceFlightGamepack).Assembly.Location);
            string assets = Path.Combine(assemblyDir ?? "", "Assets");
            if (Directory.Exists(assets))
                resolver.Mount("Assets", assets);
            _root.Registry.RegisterOrReplace("AssetResolver", resolver);
        }
    }

    public void OnStart()
    {
        SpaceFlightScene.Build(_root);
    }
}
