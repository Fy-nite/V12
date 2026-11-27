using V12;
using V12.Core;

namespace VoxelPlanet;

/// <summary>
/// Pins the voxel package into the process before WorldML parses the scene.
/// The parser resolves component types by scanning assemblies that are already
/// loaded, and a referenced package loads lazily, so it pins the assembly by
/// name and then touches the component types.
/// <para>
/// This has to happen in the static constructor, not <see cref="Initialize"/>:
/// the pak loader instantiates gamepaks while loading their DLLs, then parses
/// the pak's worlds, and only afterwards calls <c>Initialize</c>. Waiting for
/// <c>Initialize</c> is one step too late and the scene silently loses its
/// planet with "Unknown component type 'VoxelPlanetComponent'".
/// </para>
/// </summary>
public sealed class VoxelPlanetGamepack : IV12Gamepack
{
    static VoxelPlanetGamepack()
    {
        System.Reflection.Assembly.Load("V12.Voxel");

        _ = typeof(V12.Voxel.Components.VoxelPlanetComponent);
        _ = typeof(V12.Voxel.Components.VoxelChunkMesh);
    }

    public string Name => "Voxel Planet";

    public void Initialize()
    {
        GameRoot.Log?.Information("[VoxelPlanet] Finite.V12.Voxel loaded");
    }

    public void OnStart()
    {
        GameRoot.Log?.Information("[VoxelPlanet] Started");
    }
}
