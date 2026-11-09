using System.Reflection;

namespace NovaV2.Hosting;

/// <summary>Startup options for the NovaV2 host runtime.</summary>
public sealed class NovaV2RuntimeOptions
{
    /// <summary>Directory scanned for gamepak DLLs.</summary>
    public string GamepakDirectory { get; set; } = "gamepaks";

    /// <summary>Optional .v12pak archive loaded before directory gamepaks.</summary>
    public string? PakPath { get; set; }

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

    /// <summary>Bring up OpenXR. When no headset is present the runtime keeps
    /// rendering flat and retries, so the same build runs on desktop.</summary>
    public bool EnableXR { get; set; }
}
