using Godot;
using V12.Core;
using V12.Pak;

namespace NovaV2.Hosting;

/// <summary>
/// Drop-in Godot host node for the v1 renderer contract. Add it under a scene
/// root, then build or load a world through <see cref="Runtime"/>. If no
/// <see cref="GameRoot"/> exists it creates one; use <see cref="Attach"/> to
/// supply a prepared root instead.
/// </summary>
[GlobalClass]
public partial class NovaV2HostNode : Node3D
{
    [Export] public bool CreateRootIfMissing { get; set; } = true;
    [Export] public bool LockMouseOnStart { get; set; }
    [Export] public string PakPath { get; set; } = "";

    /// <summary>
    /// Embedded pak resource (<c>res://MyGame.v12pak</c>), used when the game
    /// ships the archive inside its own package instead of beside the
    /// executable. Takes precedence over <see cref="PakPath"/>.
    /// </summary>
    [Export] public string PakResource { get; set; } = "";
    [Export] public string ShaderDirectory { get; set; } = "";
    [Export] public string GamepakDirectory { get; set; } = "gamepaks";
    [Export] public string ForceGamepak { get; set; } = "";
    [Export] public bool AutoStartFirstGamepak { get; set; } = true;
    /// <summary>Create the XR rig. On by default; it stays inert until XR is
    /// requested, so one build runs flat or in VR.</summary>
    [Export] public bool EnableXR { get; set; } = true;

    /// <summary>Start OpenXR immediately. Same as <c>--xr on</c>; the game can
    /// also enable XR later through <see cref="NovaV2Runtime.EnableXR"/>.</summary>
    [Export] public bool StartXR { get; set; }

    private NovaV2Runtime? _runtime;
    private PakModHost? _mods;

    public NovaV2Runtime? Runtime => _runtime;
    public GameRoot? Root => _runtime?.Root;

    public override void _Ready()
    {
        SetProcessInput(true);

        // Keyboard events only reach _Input when the game window is focused.
        // Launches from a terminal/IDE otherwise leave focus on the console and
        // make every keybind appear dead. Running as a game is not the editor,
        // so pulling the window forward is the expected behavior here.
        if (!Engine.IsEditorHint())
        {
            DisplayServer.WindowMoveToForeground();
            GetWindow().GrabFocus();
        }

        if (_runtime is not null)
            return;

        var root = GameRoot.Instance;
        if (root is null)
        {
            if (!CreateRootIfMissing)
            {
                GD.PushError("[NovaV2Host] No GameRoot exists and CreateRootIfMissing is false.");
                return;
            }

            root = new GameRoot();
        }

        _runtime = new NovaV2Runtime(this, root, BuildOptions());
        LoadModules(root);
    }

    public override void _EnterTree()
    {
        // `openxr/enabled` can initialise the module before _Ready runs. That
        // is only the runtime being available; the viewport is flagged for XR
        // here only when XR is actually requested now, so a normal launch keeps
        // rendering flat even with a headset connected.
        bool start = StartXR;
        bool enabled = EnableXR;

        if ((CommandLineValue("--xr-mode") ?? CommandLineValue("--xr")) is { } value)
        {
            bool on = IsXrOn(value);
            start = on;
            enabled = on;
        }

        if (!enabled || !start)
            return;

        // Only mark the viewport when OpenXR is already running. Otherwise the
        // XR service marks it once Initialize() succeeds; flagging a viewport
        // for XR with no interface running renders nothing.
        var xrInterface = XRServer.FindInterface("OpenXR");
        if (xrInterface is null || !xrInterface.IsInitialized())
            return;

        var viewport = GetTree()?.Root;
        if (viewport is not null && !viewport.UseXR)
            viewport.UseXR = true;
    }

    public override void _Process(double delta)
    {
        _runtime?.Tick((float)delta);
    }

    public override void _Input(InputEvent @event)
    {
        _runtime?.HandleInputEvent(@event);
    }

    public override void _ExitTree()
    {
        // Modules own process-wide resources (listeners, threads). Stop them
        // before the runtime tears the renderer down.
        Root?.Modules.DisposeAll();
        _mods?.Dispose();
        _mods = null;
        _runtime?.Dispose();
        _runtime = null;
    }

    public NovaV2Runtime Attach(GameRoot root, NovaV2RuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        _runtime?.Dispose();
        _runtime = new NovaV2Runtime(this, root, options ?? BuildOptions());
        return _runtime;
    }

    /// <summary>
    /// Loads opt-in modules. Nothing is loaded unless the user asked with
    /// <c>--module</c>, <c>--modules</c> or <c>--mcp</c>, so a game launched
    /// without a module flag carries no tooling. <c>--mcp</c> is sugar for
    /// "load V12.MCP.Probe.dll from the plugins folder".
    /// </summary>
    private void LoadModules(GameRoot root)
    {
        string[] args = AllCommandLineArgs();

        // Mod paks are ordinary .v12pak files with a module.json; content, scripts
        // and module code all arrive through the normal pak pipeline.
        _mods = new PakModHost(root);
        root.Registry.RegisterOrReplace("Mods", _mods);
        _mods.EnableFromArgs(args, AppContext.BaseDirectory);

        // --modules, --module and --no-modules are handled by the engine.
        root.Modules.EnableFromArgs(args, AppContext.BaseDirectory);

        if (!CommandLineFlag("--mcp") || CommandLineFlag("--no-modules"))
            return;

        string directory = CommandLineValue("--modules") is { Length: > 0 } configured
            ? (Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured))
            : Path.Combine(AppContext.BaseDirectory, "modules");

        string? probe = FindProbe(directory);
        if (probe is null)
        {
            GD.PushWarning(
                "[NovaV2Host] --mcp was passed but V12.MCP.Probe.dll was not found. " +
                $"Searched '{directory}', '{Path.Combine(AppContext.BaseDirectory, "modules")}' " +
                $"and '{Path.Combine(AppContext.BaseDirectory, "plugins")}'. Pass --module <path>.");
            return;
        }

        root.Modules.Load(probe, args);
    }

    /// <summary>
    /// Finds the probe DLL. <c>modules/</c> is the standard folder;
    /// <c>plugins/</c> is the older name and is still checked so an existing
    /// setup keeps working.
    /// </summary>
    private static string? FindProbe(string preferredDirectory)
    {
        string[] candidates =
        {
            Path.Combine(preferredDirectory, "V12.MCP.Probe.dll"),
            Path.Combine(AppContext.BaseDirectory, "modules", "V12.MCP.Probe.dll"),
            Path.Combine(AppContext.BaseDirectory, "plugins", "V12.MCP.Probe.dll"),
        };

        foreach (string candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    /// <summary>Engine and user arguments together, for modules that parse their own switches.</summary>
    private static string[] AllCommandLineArgs()
        => OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();

    private NovaV2RuntimeOptions BuildOptions()
    {
        var options = new NovaV2RuntimeOptions
        {
            PakPath = string.IsNullOrWhiteSpace(PakPath) ? null : PakPath,
            PakResource = string.IsNullOrWhiteSpace(PakResource) ? null : PakResource,
            ShaderDirectory = string.IsNullOrWhiteSpace(ShaderDirectory) ? null : ShaderDirectory,
            GamepakDirectory = string.IsNullOrWhiteSpace(GamepakDirectory) ? string.Empty : GamepakDirectory,
            ForceGamepak = string.IsNullOrWhiteSpace(ForceGamepak) ? null : ForceGamepak,
            AutoStartFirstGamepak = AutoStartFirstGamepak,
            LockMouseOnStart = LockMouseOnStart,
            EnableXR = EnableXR,
            StartXR = StartXR,
        };

        // Godot keeps everything after "--" out of GetCmdlineArgs, so user
        // options have to be read from GetCmdlineUserArgs first.
        if (CommandLineValue("--pak") is { } pak)
        {
            // A Godot virtual path is opened through FileAccess, not System.IO.
            if (pak.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
                pak.StartsWith("user://", StringComparison.OrdinalIgnoreCase))
                options.PakResource ??= pak;
            else
                options.PakPath ??= pak;
        }

        options.ForceGamepak ??= CommandLineValue("--gamepak");

        // `--xr on` enables the rig and starts it; `--xr off` disables the rig
        // entirely. Anything else only changes whether XR starts now, never
        // whether the game can enter XR later.
        // `--xr-mode` is the current desktop-test spelling; `--xr` remains for
        // older launch scripts.
        var xrMode = CommandLineValue("--xr-mode") ?? CommandLineValue("--xr");
        if (xrMode is { } xr)
        {
            bool on = IsXrOn(xr);
            options.EnableXR = on;
            options.StartXR = on;
        }

        return options;
    }

    /// <summary>
    /// True for a <c>--xr</c> value that should start XR. <c>0</c>, <c>false</c>
    /// and <c>off</c> (any case) turn it off; <c>on</c>, <c>1</c> and <c>true</c>
    /// turn it on; an absent value leaves the current default.
    /// </summary>
    public static bool IsXrOn(string? value)
        => value is not null
           && !value.Equals("0", StringComparison.OrdinalIgnoreCase)
           && !value.Equals("false", StringComparison.OrdinalIgnoreCase)
           && !value.Equals("off", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="name"/> appears as a bare switch on the command line.</summary>
    public static bool CommandLineFlag(string name)
        => Array.IndexOf(OS.GetCmdlineUserArgs(), name) >= 0
           || Array.IndexOf(OS.GetCmdlineArgs(), name) >= 0;

    /// <summary>
    /// Reads the value following <paramref name="name"/> from the command line.
    /// User arguments (after <c>--</c>) are checked first, then engine
    /// arguments, so both <c>--pak x</c> and <c>-- --pak x</c> work.
    /// </summary>
    public static string? CommandLineValue(string name)
    {
        var user = OS.GetCmdlineUserArgs();
        int index = Array.IndexOf(user, name);
        if (index >= 0 && index + 1 < user.Length)
            return user[index + 1];

        var engine = OS.GetCmdlineArgs();
        index = Array.IndexOf(engine, name);
        return index >= 0 && index + 1 < engine.Length ? engine[index + 1] : null;
    }
}
