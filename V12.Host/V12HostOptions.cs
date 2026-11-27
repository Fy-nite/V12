using V12.Pak;
using V12.Rendering;

namespace V12.Hosting;

/// <summary>
/// Everything a <see cref="V12Host"/> needs. Defaults are the "run the game next
/// to me" case; a shell that embeds V12 should set <see cref="ContentDirectory"/>
/// and <see cref="UserDirectory"/> to its own paths.
/// </summary>
public sealed class V12HostOptions
{
    /// <summary>
    /// Explicit pak to load. An explicit path always wins over the dev inbox,
    /// the descriptor and the executable-name fallback.
    /// </summary>
    public string? PakPath { get; set; }

    /// <summary>An already-parsed descriptor, for shells that read it themselves (for example <c>res://game.json</c>).</summary>
    public V12GameDescriptor? Descriptor { get; set; }

    /// <summary>Descriptor path. Defaults to <c>game.json</c> in <see cref="ContentDirectory"/>.</summary>
    public string? DescriptorPath { get; set; }

    /// <summary>
    /// Directory that holds the game build (pak, descriptor, exe). Defaults to
    /// the process base directory.
    /// </summary>
    public string? ContentDirectory { get; set; }

    /// <summary>
    /// The shell's user-data directory. The host looks for a pushed build under
    /// <c>v12inbox/</c> here; on Android the matching external directory is
    /// checked too. Defaults to <c>%APPDATA%/V12</c> (or the OS equivalent).
    /// </summary>
    public string? UserDirectory { get; set; }

    /// <summary>Explicit dev inbox directory. Overrides the <see cref="UserDirectory"/> derivation.</summary>
    public string? DevInbox { get; set; }

    /// <summary>
    /// Look for <c>dev.v12pak</c> in the dev inbox before the shipped build.
    /// This is the <c>vcsdk serve --reload</c> hand-off.
    /// </summary>
    public bool UseDevInbox { get; set; } = true;

    /// <summary>
    /// Legacy discovery: <c>&lt;process name&gt;.v12pak</c> next to the host.
    /// Off for new hosts, on for compatibility with builds that still rename
    /// the renderer executable.
    /// </summary>
    public bool UseExecutableNameFallback { get; set; }

    /// <summary>
    /// Call <see cref="V12.Core.GameRoot.Initialize"/> after content loads.
    /// Disable in tests that only exercise pak resolution.
    /// </summary>
    public bool InitializeRoot { get; set; } = true;

    /// <summary>
    /// Configure Serilog through <see cref="V12.Core.GameRoot.ConfigureLogging"/>
    /// when the process has not done it. Off by default so an embedding shell
    /// keeps its own logging setup.
    /// </summary>
    public bool ConfigureLogging { get; set; }

    /// <summary>
    /// Verify each split part against the descriptor's recorded size and hash
    /// before loading. Off by default: a local build is not a delivery.
    /// </summary>
    public bool VerifyParts { get; set; }

    /// <summary>
    /// Renderer backend. Null runs headless, which the engine already supports
    /// (<c>GameRoot.Initialize</c> tolerates a missing <c>IRenderer</c>).
    /// </summary>
    public IRenderer? Renderer { get; set; }

    /// <summary>
    /// What to import from the pak. Defaults match a shipped game (code
    /// included). Turn the code loaders off for anything untrusted.
    /// </summary>
    public V12PakOptions PakOptions { get; set; } = new()
    {
        LoadDlls = true,
        LoadContractScripts = true,
        LoadCSharpScripts = true,
    };
}
