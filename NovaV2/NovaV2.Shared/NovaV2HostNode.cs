using Godot;
using V12.Core;

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
    [Export] public string GamepakDirectory { get; set; } = "gamepaks";
    [Export] public string ForceGamepak { get; set; } = "";
    [Export] public bool AutoStartFirstGamepak { get; set; } = true;
    [Export] public bool EnableXR { get; set; }

    private NovaV2Runtime? _runtime;

    public NovaV2Runtime? Runtime => _runtime;
    public GameRoot? Root => _runtime?.Root;

    public override void _Ready()
    {
        SetProcessInput(true);

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
    }

    public override void _EnterTree()
    {
        // Flag the root viewport for XR as early as possible. Godot may boot
        // OpenXR from `openxr/enabled` before _Ready runs, and a viewport that
        // is not marked renders nothing ("No viewport was marked with use_xr").
        bool xr = EnableXR;
        if (CommandLineValue("--xr") is { } value)
            xr = value is not "0" and not "false";

        if (!xr)
            return;

        // Only mark the viewport when OpenXR is already running (the project
        // setting can boot it before _Ready). Otherwise the runtime's XR
        // service marks it once Initialize() succeeds; flagging it earlier
        // would leave a desktop run rendering nothing.
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

    private NovaV2RuntimeOptions BuildOptions()
    {
        var options = new NovaV2RuntimeOptions
        {
            PakPath = string.IsNullOrWhiteSpace(PakPath) ? null : PakPath,
            GamepakDirectory = string.IsNullOrWhiteSpace(GamepakDirectory) ? string.Empty : GamepakDirectory,
            ForceGamepak = string.IsNullOrWhiteSpace(ForceGamepak) ? null : ForceGamepak,
            AutoStartFirstGamepak = AutoStartFirstGamepak,
            LockMouseOnStart = LockMouseOnStart,
            EnableXR = EnableXR,
        };

        // Godot keeps everything after "--" out of GetCmdlineArgs, so user
        // options have to be read from GetCmdlineUserArgs first.
        options.PakPath ??= CommandLineValue("--pak");
        options.ForceGamepak ??= CommandLineValue("--gamepak");

        if (CommandLineValue("--xr") is { } xr)
            options.EnableXR = xr is not "0" and not "false";

        return options;
    }

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
