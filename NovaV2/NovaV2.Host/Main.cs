using Godot;
using NovaV2.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using V12.Basic.Building;
using V12.Basic.Components;
using V12.Components;
using V12.Components.Renderables;
using V12.Components.UI;
using V12.Core;
using V12.Core.Interfaces.Renderer;
using V12.Core.Input;
using V12.Core.UI;
using V12.WorldML;
using V12BoxMesh = V12.Components.Renderables.BoxMesh;

namespace NovaV2.Host;

public partial class Main : Node3D
{
    public override void _Ready()
    {
        if (!GameRoot.LoggingConfigured)
            GameRoot.ConfigureLogging();

        // Run a real game instead of the demo scene:
        //   --pak <file.v12pak>     packaged game (world + scripts + assets)
        //   --game <dir>            a V12 project folder, loaded straight from
        //                           its bin output and world file
        //   --world <relative>      world file inside --game (default Scenes/Main.worldml)
        //
        // With no arguments the host also looks for a pak named after its own
        // executable, so renaming the binary to "MyGame.exe" and dropping
        // "MyGame.v12pak" beside it is enough to launch the game.
        string? pakPath = NovaV2HostNode.CommandLineValue("--pak") ?? ResolveExecutablePak();
        string? gameDir = NovaV2HostNode.CommandLineValue("--game");
        string? worldPath = NovaV2HostNode.CommandLineValue("--world");

        // A game declares XR in its manifest. Godot can only bring XR up when
        // started with --xr-mode on, so relaunch once when a game needs it.
        bool xrDisabled = NovaV2HostNode.CommandLineValue("--xr") is "0" or "false";
        bool requiresXr = !xrDisabled && GameRequiresXr(gameDir, pakPath);
        if (requiresXr && !XrRelaunchDone())
        {
            RelaunchWithXrMode();
            return;
        }

        var root = new GameRoot();

        // The WorldML parser resolves component types by scanning loaded
        // assemblies, and .NET loads them lazily. Touch the engine assemblies
        // that carry world components so a scene resolves even when the game
        // ships no gamepak (a gamepak normally pulls these in itself).
        _ = typeof(V12.Basic.Components.CameraComponent);
        _ = typeof(V12.Components.MeshComponent);
        _ = typeof(V12.Components.Renderables.MeshAsset);

        if (gameDir is not null)
        {
            BuildGameWorld(root, gameDir, worldPath);
        }
        else if (pakPath is null)
        {
            EnsureDemoActions();
            BuildDemoWorld(root);
        }

        // NovaV2HostNode._Ready picks up GameRoot.Instance (the root above).
        // In --pak mode the node loads the archive itself; in --game mode the
        // world is already built here, so it must not scan a gamepak folder.
        AddChild(new NovaV2HostNode
        {
            Name = "NovaV2Host",
            PakPath = pakPath ?? "",
            GamepakDirectory = gameDir is null ? "gamepaks" : "",
            // --game already initialised and started the gamepaks above.
            AutoStartFirstGamepak = gameDir is null,
            EnableXR = requiresXr,
        });

        // Dev aid: V12_SCREENSHOT=<path> renders a few frames, writes a PNG and
        // quits. Handy for checking a scene without a human at the window.
        _screenshotPath = System.Environment.GetEnvironmentVariable("V12_SCREENSHOT");

        if (System.Environment.GetEnvironmentVariable("V12_INPUTTEST") == "1")
            RunInputSelfTest();
    }

    private static void RunInputSelfTest()
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            GD.Print((ok ? "PASS  " : "FAIL  ") + what);
            if (!ok) failures++;
        }

        static InputKey Map(Key key, KeyLocation location = KeyLocation.Unspecified) =>
            NovaV2InputHandler.ToInputKey(new InputEventKey { PhysicalKeycode = key, Location = location });

        Check(Map(Key.A) == InputKey.A, "A maps to A");
        Check(Map(Key.W) == InputKey.W, "W maps to W");
        Check(Map(Key.Space) == InputKey.Space, "Space");
        Check(Map(Key.Shift) == InputKey.LeftShift, "Shift -> LeftShift");
        Check(Map(Key.Shift, KeyLocation.Right) == InputKey.RightShift, "right Shift");
        Check(Map(Key.Ctrl, KeyLocation.Right) == InputKey.RightControl, "right Ctrl");
        Check(Map(Key.Alt, KeyLocation.Right) == InputKey.RightAlt, "right Alt");
        Check(Map(Key.F12) == InputKey.F12, "F12");
        Check(Map(Key.Kp5) == InputKey.NumPad5, "numpad 5");
        Check(Map(Key.Bracketleft) == InputKey.OemOpenBrackets, "[");
        Check(Map(Key.Backslash) == InputKey.OemBackslash, "backslash");
        Check(Map(Key.Up) == InputKey.Up, "Up");
        Check(Map(Key.Escape) == InputKey.Escape, "Escape");
        Check(Map(Key.Help) == InputKey.None, "unmapped key is None");

        var service = new InputService();
        var actions = new InputActionMap();
        actions.BindVector2("Move", InputKey.D, InputKey.A, InputKey.W, InputKey.S);
        actions.BindButton("Jump", InputKey.Space);
        actions.Attach(service);

        void Edge(Key key, bool down)
        {
            var mapped = NovaV2InputHandler.ToInputKey(new InputEventKey { PhysicalKeycode = key });
            service.SendEvent(new V12.Core.Input.InputEvent
            {
                Type = down ? InputEventType.ButtonDown : InputEventType.ButtonUp,
                Name = mapped.ToString(),
                Key = mapped,
            });
        }

        Edge(Key.W, true);
        actions.Update(0.016f);
        Check(Math.Abs(actions.GetAxis("Move_Y") - 1f) < 0.001f, "W drives Move_Y +1");
        Edge(Key.W, false);
        Edge(Key.S, true);
        actions.Update(0.016f);
        Check(Math.Abs(actions.GetAxis("Move_Y") + 1f) < 0.001f, "S drives Move_Y -1");
        Edge(Key.S, false);
        actions.Update(0.016f);

        Edge(Key.Space, true);
        actions.Update(0.016f);
        Check(actions.GetButton("Jump") && actions.GetButtonDown("Jump"), "Space presses Jump");
        Edge(Key.Space, false);
        actions.Update(0.016f);
        Check(!actions.GetButton("Jump") && actions.GetButtonUp("Jump"), "Space releases Jump");

        // Gamepad bindings, fed by the same event shape the host emits.
        actions.BindButton("PadJump", GamepadButton.A);
        actions.BindAxis("PadMoveX", GamepadAxis.LeftX);

        service.SendEvent(new V12.Core.Input.InputEvent
        {
            Type = InputEventType.ButtonDown, Name = "A", PadButton = GamepadButton.A, Device = 0,
        });
        actions.Update(0.016f);
        Check(actions.GetButton("PadJump") && actions.GetButtonDown("PadJump"), "pad A presses PadJump");

        service.SendEvent(new V12.Core.Input.InputEvent
        {
            Type = InputEventType.ButtonUp, Name = "A", PadButton = GamepadButton.A, Device = 0,
        });
        actions.Update(0.016f);
        Check(!actions.GetButton("PadJump"), "pad A releases PadJump");

        service.SendEvent(new V12.Core.Input.InputEvent
        {
            Type = InputEventType.Axis, Name = "LeftX", PadAxis = GamepadAxis.LeftX, Value = 0.75f, Device = 0,
        });
        actions.Update(0.016f);
        Check(Math.Abs(actions.GetAxis("PadMoveX") - 0.75f) < 0.001f, "left stick drives PadMoveX +0.75");

        service.SendEvent(new V12.Core.Input.InputEvent
        {
            Type = InputEventType.Axis, Name = "LeftX", PadAxis = GamepadAxis.LeftX, Value = -0.4f, Device = 0,
        });
        actions.Update(0.016f);
        Check(Math.Abs(actions.GetAxis("PadMoveX") + 0.4f) < 0.001f, "left stick drives PadMoveX -0.4");

        GD.Print(failures == 0 ? "INPUT SELF TEST: ALL PASS" : $"INPUT SELF TEST: {failures} FAILURES");
    }

    /// <summary>
    /// Loads a V12 project folder: its compiled game assembly (so the world's
    /// component types resolve), its Assets folder as <c>v12://Assets</c>, and
    /// its world file.
    /// </summary>
    private static void BuildGameWorld(GameRoot root, string gameDir, string? worldPath)
    {
        gameDir = Path.GetFullPath(gameDir);
        if (!Directory.Exists(gameDir))
        {
            GD.PushError($"[NovaV2.Host] --game folder not found: {gameDir}");
            return;
        }

        LoadGameAssembly(root, gameDir);

        var resolver = new V12AssetResolver();
        resolver.Mount("Assets", Path.Combine(gameDir, "Assets"));
        root.Registry.RegisterOrReplace("AssetResolver", resolver);

        string worldFile = Path.Combine(gameDir, worldPath ?? Path.Combine("Scenes", "Main.worldml"));
        if (!File.Exists(worldFile))
        {
            GD.PushError($"[NovaV2.Host] world file not found: {worldFile}");
            return;
        }

        var parser = new WorldMLParser();
        var contents = parser.ParseFile(worldFile);
        var world = new World(contents.Name ?? "Scene");
        world.AddElement(contents);
        world.TickingEnabled = true;
        root.Worlds.Add(world);
        root.SelectWorld(world);

        GD.Print($"[NovaV2.Host] Loaded '{Path.GetFileName(worldFile)}' from {gameDir}");
    }

    /// <summary>
    /// Loads the game's own assembly from its build output and starts any
    /// <c>IV12Gamepack</c> it declares, so gameplay systems (physics, script
    /// system, UI provider) are registered before the world is parsed.
    ///
    /// Only the game assembly is loaded. A publish folder also contains the
    /// engine, the compiler and native libraries; loading all of them would be
    /// slow and can pull in a second copy of something the host already has.
    /// </summary>
    private static void LoadGameAssembly(GameRoot root, string gameDir)
    {
        string? assemblyPath = FindGameAssembly(gameDir);
        if (assemblyPath is null)
        {
            GD.Print("[NovaV2.Host] No game assembly found; run 'dotnet build' first. " +
                     "The scene will still load, but its components will not resolve.");
            return;
        }

        try
        {
            var context = AssemblyLoadContext.GetLoadContext(typeof(GameRoot).Assembly);
            var assembly = context is not null
                ? context.LoadFromAssemblyPath(assemblyPath)
                : Assembly.LoadFrom(assemblyPath);

            GD.Print($"[NovaV2.Host] Game assembly: {assemblyPath}");
            if (root.LoadGamepacksFromAssembly(assembly) > 0)
                GD.Print("[NovaV2.Host] Gamepak started");
            else
                GD.Print("[NovaV2.Host] Loaded game assembly (no gamepak; scene components only)");
        }
        catch (Exception ex)
        {
            GD.PushError($"[NovaV2.Host] Could not load {assemblyPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Finds the game's compiled assembly under <c>bin</c>, by project folder
    /// name or <c>.csproj</c> name. A Debug build wins over a Release publish,
    /// because <c>--game</c> is the development loop and a stale packed output
    /// would otherwise shadow the code that was just built.
    /// </summary>
    private static string? FindGameAssembly(string gameDir)
    {
        string bin = Path.Combine(gameDir, "bin");
        if (!Directory.Exists(bin))
            return null;

        var names = new List<string>
        {
            Path.GetFileName(gameDir.TrimEnd(Path.DirectorySeparatorChar)),
        };

        foreach (string project in Directory.GetFiles(gameDir, "*.csproj"))
            names.Add(Path.GetFileNameWithoutExtension(project));

        var matches = Directory
            .GetFiles(bin, "*.dll", SearchOption.AllDirectories)
            .Where(path => names.Any(name => string.Equals(
                Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matches.Count == 0)
            return null;

        var debug = matches
            .Where(path => path.Contains(
                $"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        return (debug.Count > 0 ? debug : matches)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
    }

    /// <summary>
    /// True when the game's manifest declares <c>"Xr": true</c>. Works for a
    /// project folder and for a packaged archive (the manifest is streamed
    /// without extracting the pak).
    /// </summary>
    private static bool GameRequiresXr(string? gameDir, string? pakPath)
    {
        try
        {
            if (gameDir is not null)
            {
                string manifestPath = Path.Combine(gameDir, "manifest.json");
                if (!File.Exists(manifestPath))
                    return false;

                var manifest = System.Text.Json.JsonSerializer.Deserialize<V12.Pak.V12PakManifest>(
                    File.ReadAllText(manifestPath),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return manifest?.Xr == true;
            }

            if (pakPath is not null)
                return V12.Pak.V12PakReader.ReadManifestOnly(pakPath)?.Xr == true;
        }
        catch (Exception ex)
        {
            GD.Print($"[NovaV2.Host] Could not read the game manifest: {ex.Message}");
        }

        return false;
    }

    /// <summary>True when this process is already the relaunched XR run.</summary>
    private static bool XrRelaunchDone()
    {
        if (System.Environment.GetEnvironmentVariable("V12_XR_RELAUNCHED") == "1")
            return true;

        return Array.IndexOf(OS.GetCmdlineUserArgs(), "--xr-relaunched") >= 0;
    }

    /// <summary>
    /// Restarts the process with <c>--xr-mode on</c>. Godot only creates an XR
    /// instance when that flag is present at startup, and the project keeps
    /// OpenXR off so a flat game never takes over a headset.
    ///
    /// Godot removes engine arguments from <c>OS.GetCmdlineArgs()</c>, so the
    /// project path has to be reconstructed rather than copied.
    /// </summary>
    private void RelaunchWithXrMode()
    {
        try
        {
            string executable = OS.GetExecutablePath();
            var args = new List<string>();

            // Source runs need --path; an exported build already knows its pck.
            string projectDir = ProjectSettings.GlobalizePath("res://");
            if (!string.IsNullOrWhiteSpace(projectDir) &&
                File.Exists(Path.Combine(projectDir, "project.godot")))
            {
                args.Add("--path");
                args.Add(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            args.Add("--xr-mode");
            args.Add("on");
            args.Add("--");
            args.AddRange(OS.GetCmdlineUserArgs());
            args.Add("--xr-relaunched");

            System.Environment.SetEnvironmentVariable("V12_XR_RELAUNCHED", "1");
            GD.Print($"[NovaV2.Host] Game requires XR; relaunching: {executable} {string.Join(' ', args)}");
            OS.CreateProcess(executable, args.ToArray(), openConsole: false);
        }
        catch (Exception ex)
        {
            GD.PushError($"[NovaV2.Host] XR relaunch failed: {ex.Message}");
        }

        GetTree().Quit();
    }

    /// <summary>
    /// Finds a pak named after the running executable, so an exported build can
    /// be shipped as <c>MyGame.exe</c> plus <c>MyGame.v12pak</c> with no
    /// command line at all. Looks beside the executable, then the working
    /// directory, then the Godot project root.
    /// </summary>
    private static string? ResolveExecutablePak()
    {
        string executable = OS.GetExecutablePath();
        if (string.IsNullOrWhiteSpace(executable))
            return null;

        string name = Path.GetFileNameWithoutExtension(executable);
        if (string.IsNullOrWhiteSpace(name))
            return null;

        string pakName = name + ".v12pak";
        foreach (string? directory in new[]
                 {
                     Path.GetDirectoryName(executable),
                     Directory.GetCurrentDirectory(),
                     ProjectSettings.GlobalizePath("res://"),
                 })
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            string candidate = Path.Combine(directory, pakName);
            if (File.Exists(candidate))
            {
                GD.Print($"[NovaV2.Host] Using pak named after the executable: {candidate}");
                return candidate;
            }
        }

        return null;
    }

    private string? _screenshotPath;
    private int _screenshotFrames;

    public override void _Process(double delta)
    {
        if (string.IsNullOrEmpty(_screenshotPath))
            return;

        // Give the runtime a few frames to load textures and draw.
        if (++_screenshotFrames < 60)
            return;

        var image = GetViewport().GetTexture().GetImage();
        if (image is not null)
        {
            var error = image.SavePng(_screenshotPath);
            GD.Print($"[Screenshot] saved '{_screenshotPath}' ({error})");
        }

        _screenshotPath = null;
        GetTree().Quit();
    }

    private static void BuildDemoWorld(GameRoot root)
    {
        var world = root.CreateWorld("NovaV2 Demo");
        world.TickingEnabled = true;

        // Mount the repo's sample models as v12://samples/… so demo elements
        // can reference them by URI instead of a machine-specific path.
        var samplesDir = Path.GetFullPath(Path.Combine(
            ProjectSettings.GlobalizePath("res://"), "..", "..", "V12-Samples"));
        if (Directory.Exists(samplesDir))
        {
            var existing = root.Registry.Get("AssetResolver")?.ServiceInstance as V12AssetResolver;
            var resolver = existing ?? new V12AssetResolver();
            resolver.Mount("samples", samplesDir);
            if (existing is null)
                root.Registry.RegisterOrReplace("AssetResolver", resolver);
        }

        var environment = new Element("Environment");
        environment.AddComponent(new EnvironmentComponent(
            BackgroundMode.SolidColor,
            0.30f,
            0.50f,
            0.80f)
        {
            AmbientR = 0.20f,
            AmbientG = 0.22f,
            AmbientB = 0.28f,
            AmbientEnergy = 1.0f,
        });
        world.AddElement(environment);

        var camera = new Element("Camera");
        camera.SetPosition(0f, 2f, 6f);
        camera.AddComponent(new CameraComponent
        {
            IsCurrent = true,
            Fov = 60f,
            NearClip = 0.05f,
            FarClip = 200f,
        });
        world.AddElement(camera);

        var sun = new Element("Sun");
        sun.SetRotation(-45f, -30f, 0f);
        sun.AddComponent(new GenericLightComponent
        {
            Type = LightType.Directional,
            Energy = 1.2f,
            ColorR = 1f,
            ColorG = 0.95f,
            ColorB = 0.88f,
        });
        world.AddElement(sun);

        var cube = new Element("Cube");
        cube.SetPosition(0f, 0f, 0f);
        cube.SetRotation(22f, 32f, 0f);
        var box = new V12BoxMesh();
        cube.AddComponent(box);
        cube.AddComponent(new MeshRenderer { Mesh = box });
        cube.AddComponent(new MaterialComponent
        {
            R = 1f,
            G = 1f,
            B = 1f,
            A = 1f,
            Metallic = 0.1f,
            Roughness = 0.55f,
            AlbedoTexture = "res://textures/checker.png",
        });
        world.AddElement(cube);

        // Desk.glb loaded straight from the samples mount. ModelInstance rebuilds
        // the file's own node hierarchy as elements and gives each mesh part a
        // MaterialComponent filled from the material the file carries, so the
        // desk arrives textured (wood diffuse + packed roughness + normal map)
        // and stays editable part by part. Raw bounds are 4 x 2.3 x 2.1 units,
        // so a 0.5 scale reads as a desk beside the 1-unit cube and puts its base
        // on the ground plane.
        var desk = new Element("Desk");
        desk.SetPosition(3.0f, 0.575f, -3.6f);
        desk.SetRotation(0f, -28f, 0f);
        desk.AddComponent(new ScaleComponent(0.5f));
        desk.AddComponent(new ModelInstance("v12://samples/Desk.glb"));
        world.AddElement(desk);

        if (root.Registry.Get("UIBuilder")?.ServiceInstance is UIBuilder ui)
        {
            if (ui.Root.GetComponent<CanvasComponent>() is { } canvas)
            {
                canvas.ScreenSpace = true;
                canvas.Width = 1280;
                canvas.Height = 720;
            }

            ui.Label(ui.Root, "Title", "NovaV2 UI");
            var clickButton = ui.Button(ui.Root, "ClickMe", () =>
            {
                GD.Print("[NovaV2] UI button clicked");
                V12.Core.GameRoot.Log?.Information("[NovaV2] UI button clicked");
            });
            clickButton.AddComponent(new LayoutElementComponent
            {
                PreferredWidth = 180f,
                PreferredHeight = 38f,
            });
        }
    }

    private static void EnsureDemoActions()
    {
        AddKey("strafe_left", Key.A);
        AddKey("strafe_right", Key.D);
        AddKey("move_forwards", Key.W);
        AddKey("move_backwards", Key.S);
        AddKey("jump", Key.Space);
        AddKey("crouch", Key.Ctrl);
        AddKey("run", Key.Shift);
        AddKey("lean_left", Key.Q);
        AddKey("lean_right", Key.E);
        AddKey("interact", Key.R);
        AddKey("escape", Key.Escape);
        AddKey("fly_toggle", Key.Backslash);
        AddKey("fly_up", Key.Pageup);
        AddKey("fly_down", Key.Pagedown);

        AddAction("look_left");
        AddAction("look_right");
        AddAction("look_up");
        AddAction("look_down");
    }

    private static void AddKey(string action, Key key)
    {
        AddAction(action);
        InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = key });
    }

    private static void AddAction(string action)
    {
        if (!InputMap.HasAction(action))
            InputMap.AddAction(action);
    }
}
