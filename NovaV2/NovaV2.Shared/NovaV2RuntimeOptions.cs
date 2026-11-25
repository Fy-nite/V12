using System.Reflection;

namespace NovaV2.Hosting;

/// <summary>Startup options for the NovaV2 host runtime.</summary>
public sealed class NovaV2RuntimeOptions
{
    /// <summary>Directory scanned for gamepak DLLs.</summary>
    public string GamepakDirectory { get; set; } = "gamepaks";

    /// <summary>Optional .v12pak archive loaded before directory gamepaks.</summary>
    public string? PakPath { get; set; }

    /// <summary>
    /// Godot resource path of an embedded pak, for example
    /// <c>res://MyGame.v12pak</c>. Used when the game ships the archive inside
    /// its own package (an Android APK, or a pak baked into a desktop pck).
    /// Takes precedence over <see cref="PakPath"/>.
    /// </summary>
    public string? PakResource { get; set; }

    /// <summary>
    /// Directory scanned recursively for <c>.v12shader</c> manifests before the
    /// world loads, so <c>MaterialComponent.ShaderUri</c> resolves on frame one.
    /// </summary>
    public string? ShaderDirectory { get; set; }

    /// <summary>Already-referenced assemblies to scan for gamepaks.</summary>
    public IReadOnlyList<Assembly>? GamepakAssemblies { get; set; }

    /// <summary>Gamepak name to launch when no direct assembly pak is started.</summary>
    public string? ForceGamepak { get; set; }

    /// <summary>Start the first discovered gamepak when no name is forced.</summary>
    public bool AutoStartFirstGamepak { get; set; } = true;

    /// <summary>Create an empty world when no gamepak and no existing world is present.</summary>
    public bool CreateEmptyWorldIfNoPak { get; set; }

    /// <summary>Capture the mouse when the runtime starts.</summary>
    public bool LockMouseOnStart { get; set; }

    /// <summary>
    /// Create the XR rig and service. On by default, but this only makes XR
    /// available: OpenXR is not initialised and the viewport is not switched to
    /// XR until <see cref="StartXR"/> is set or gameplay calls
    /// <c>NovaV2Runtime.EnableXR()</c>. Set false to omit the rig entirely.
    /// </summary>
    public bool EnableXR { get; set; } = true;

    /// <summary>
    /// Start OpenXR and render to the headset as soon as the runtime is up.
    /// Driven by <c>--xr on</c> and by a game manifest with <c>"Xr": true</c>.
    /// With no runtime present the app stays flat in this process; it never
    /// relaunches.
    /// </summary>
    public bool StartXR { get; set; }
}
