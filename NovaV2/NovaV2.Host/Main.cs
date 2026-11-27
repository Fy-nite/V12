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
using V12.Bindings;
using V12.Components;
using V12.Components.Renderables;
using V12.Components.UI;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;
using V12.Core.Input;
using V12.Core.UI;
using V12.Hosting;
using V12.Pak;
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
        //   --pak <file.v12pak>     packaged game, or res://MyGame.v12pak
        //   --game <dir>            a V12 project folder, loaded straight from
        //                           its bin output and world file
        //   --world <relative>      world file inside --game (default Scenes/Main.worldml)
        //
        // With no arguments the host looks for a pak named after its own
        // executable, then any *.v12pak at the package root. The last case is
        // the Android path: the APK has no writable folder beside its
        // executable, so the export ships res://MyGame.v12pak instead.
        //
        // `--game` deliberately wins over the implicit fallback. An exported
        // build ships a pak, and loading it as well would select the pak's
        // world over the project folder's, leaving --hot-reload watching a world
        // that is not the one on screen.
        string? gameDir = NovaV2HostNode.CommandLineValue("--game");
        string? requestedPak = NovaV2HostNode.CommandLineValue("--pak");
        PakLocation pak = requestedPak is not null
            ? ResolvePak(requestedPak)
            : gameDir is null ? ResolvePak(null) : new PakLocation(null, null);
        string? worldPath = NovaV2HostNode.CommandLineValue("--world");

        // XR never reloads the process. The rig exists by default and stays
        // flat until asked to start:
        //   --xr on     start XR now, in this process
        //   --xr off    disable the rig entirely
        //   manifest    "Xr": true starts XR, same as --xr on
        // With neither, the game runs flat and can call runtime.EnableXR()
        // during gameplay (a menu, a level trigger, a paired headset).
        string? xrArgument = NovaV2HostNode.CommandLineValue("--xr-mode")
            ?? NovaV2HostNode.CommandLineValue("--xr");
        bool? xrOn = xrArgument is null ? null : NovaV2HostNode.IsXrOn(xrArgument);
        bool startXr = xrOn ?? GameRequestsXr(gameDir, pak);
        bool xrEnabled = xrOn != false;

        var root = new GameRoot();

        // The WorldML parser resolves component types by scanning loaded
        // assemblies, and .NET loads them lazily. Touch the engine assemblies
        // that carry world components so a scene resolves even when the game
        // ships no gamepak (a gamepak normally pulls these in itself).
        _ = typeof(V12.Components.CameraComponent);
        _ = typeof(V12.Components.MeshComponent);
        _ = typeof(V12.Components.Renderables.MeshAsset);
        // Skeletal components live in their own package; touch one so the
        // WorldML parser can resolve SkeletalManagerComponent/SkeletalPlayerComponent.
        // KeepAlive is deliberate: a bare `typeof` can be optimized away.
        GC.KeepAlive(typeof(V12.Skeletal.Components.SkeletalManagerComponent));

        // Physics and gravity are transitive dependencies of Basic. Touch them
        // here so their assemblies are loaded before any gamepak starts, which
        // also makes GravitySourceComponent resolvable from WorldML.
        GC.KeepAlive(typeof(V12.Physics.PhysicsService));
        GC.KeepAlive(typeof(V12.Gravity.GravityField));

        bool hotReload = HasCommandLineFlag("--hot-reload") || HasCommandLineFlag("--live-scripts");
        if (hotReload && gameDir is null)
        {
            GD.PushWarning(
                "[NovaV2.Host] --hot-reload currently requires --game; pak hot reload is not wired yet.");
        }

        if (gameDir is not null)
        {
            BuildGameWorld(root, gameDir, worldPath, hotReload);
        }
        else if (pak.IsEmpty)
        {
            bool viewsDemo = NovaV2HostNode.CommandLineValue("--views-demo") is "1" or "true";

            // A gamepak folder means the host is running an actual game. Do not
            // build the demo world and its UI overlay on top of it.
            if (!HasDefaultGamepaks() || viewsDemo)
            {
                EnsureDemoActions();
                BuildDemoWorld(root);
                if (viewsDemo)
                    BuildViewsDemo(root);
            }
        }

        // NovaV2HostNode._Ready picks up GameRoot.Instance (the root above).
        // In --pak mode the node loads the archive itself; in --game mode the
        // world is already built here, so it must not scan a gamepak folder.
        AddChild(new NovaV2HostNode
        {
            Name = "NovaV2Host",
            PakPath = pak.Path ?? "",
            PakResource = pak.Resource ?? "",
            // A project folder registers its own shaders before the world
            // loads, so `shader://` URIs resolve on the first frame.
            ShaderDirectory = gameDir is null ? "" : Path.Combine(gameDir, "shaders"),
            GamepakDirectory = gameDir is null ? "gamepaks" : "",
            // --game already initialised and started the gamepaks above.
            AutoStartFirstGamepak = gameDir is null,
            EnableXR = xrEnabled,
            StartXR = startXr,
        });

        // Dev aid: V12_SCREENSHOT=<path> renders a few frames, writes a PNG and
        // quits. Handy for checking a scene without a human at the window.
        _screenshotPath = System.Environment.GetEnvironmentVariable("V12_SCREENSHOT");

        if (System.Environment.GetEnvironmentVariable("V12_INPUTTEST") == "1")
            RunInputSelfTest();
    }

    private static bool HasCommandLineFlag(string name)
        => OS.GetCmdlineUserArgs().Contains(name) || OS.GetCmdlineArgs().Contains(name);

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
    private static void BuildGameWorld(GameRoot root, string gameDir, string? worldPath, bool hotReload)
    {
        gameDir = Path.GetFullPath(gameDir);
        if (!Directory.Exists(gameDir))
        {
            GD.PushError($"[NovaV2.Host] --game folder not found: {gameDir}");
            return;
        }

        CSharpScriptHost? scripts = null;
        Assembly? scriptAssembly = null;

        if (hotReload)
        {
            string mode = System.Environment.GetEnvironmentVariable("V12_MP_MODE") ?? "solo";
            string projectName = Path.GetFileName(gameDir.TrimEnd(Path.DirectorySeparatorChar));
            scripts = new CSharpScriptHost(gameDir, $"NovaV2HotReload_{projectName}_{mode}");
            if (scripts.HasSources())
            {
                var compile = scripts.Reload();
                if (compile.Success && scripts.Assembly is { } liveAssembly)
                {
                    scriptAssembly = liveAssembly;
                    root.LoadGamepacksFromAssembly(liveAssembly);
                    GD.Print($"[NovaV2.Host] Live C# scripts: {liveAssembly.GetName().Name}");
                }
                else
                {
                    GD.PushError("[NovaV2.Host] Live C# compile failed; fix the errors and save to retry.");
                }
            }
            else
            {
                GD.Print("[NovaV2.Host] --hot-reload: no C# sources; using the precompiled assembly.");
                LoadGameAssembly(root, gameDir);
            }
        }
        else
        {
            LoadGameAssembly(root, gameDir);
        }

        var resolver = new V12AssetResolver();
        resolver.Mount("Assets", Path.Combine(gameDir, "Assets"));
        root.Registry.RegisterOrReplace("AssetResolver", resolver);

        string worldFile = Path.Combine(gameDir, worldPath ?? Path.Combine("Scenes", "Main.worldml"));
        if (!File.Exists(worldFile))
        {
            GD.PushError($"[NovaV2.Host] world file not found: {worldFile}");
            scripts?.Dispose();
            return;
        }

        var parser = new WorldMLParser();
        var contents = parser.ParseFile(worldFile);
        var world = new World(contents.Name ?? "Scene");
        world.AddElement(contents);
        world.TickingEnabled = true;
        root.Worlds.Add(world);
        root.SelectWorld(world);

        if (hotReload)
        {
            var hot = new NovaV2HotReloadService(root, world, worldFile, scripts, scriptAssembly);
            hot.Start();
        }

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
    /// True when the game's manifest declares <c>"Xr": true</c>, meaning the
    /// game wants XR started at launch. Works for a project folder, a file pak
    /// and an embedded <c>res://</c> pak (the manifest is streamed, so the pak
    /// is never extracted just to answer this).
    /// </summary>
    private static bool GameRequestsXr(string? gameDir, PakLocation pak)
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

            if (pak.Resource is { } resource)
            {
                return V12.Pak.V12PakReader
                    .ReadManifestOnly(() => GodotPakStream.Open(resource))?.Xr == true;
            }

            if (pak.Path is { } path)
                return V12.Pak.V12PakReader.ReadManifestOnly(path)?.Xr == true;
        }
        catch (Exception ex)
        {
            GD.Print($"[NovaV2.Host] Could not read the game manifest: {ex.Message}");
        }

        return false;
    }

    /// <summary>A game pak is either a real file or a Godot resource.</summary>
    private readonly record struct PakLocation(string? Path, string? Resource)
    {
        public bool IsEmpty => Path is null && Resource is null;
    }

    /// <summary>
    /// Finds the game pak, in order: an explicit <c>--pak</c> (file or Godot
    /// path), a file named after the executable beside it, a
    /// <c>res://&lt;exe-name&gt;.v12pak</c>, then the first <c>*.v12pak</c> at
    /// the package root. The last two are what an exported Android build uses:
    /// the APK has no writable folder beside its executable, and the export
    /// cannot rename that executable per game.
    /// </summary>
    private static PakLocation ResolvePak(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (requested.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
                requested.StartsWith("user://", StringComparison.OrdinalIgnoreCase))
            {
                if (Godot.FileAccess.FileExists(requested))
                    return new PakLocation(null, requested);

                GD.PushError($"[NovaV2.Host] Pak resource not found: {requested}");
                return new PakLocation(null, null);
            }

            if (File.Exists(requested))
                return new PakLocation(System.IO.Path.GetFullPath(requested), null);

            // A bare name may still live inside the package.
            if (Godot.FileAccess.FileExists(requested))
                return new PakLocation(null, requested);

            GD.PushError($"[NovaV2.Host] Pak not found: {requested}");
            return new PakLocation(null, null);
        }

        // Pak policy belongs to the engine-side host (dev inbox, game.json,
        // executable-name fallback). The shell only handles Godot resource
        // paths below, because System.IO cannot see inside the package.
        try
        {
            var source = V12PakResolver.Resolve(new V12HostOptions
            {
                ContentDirectory = AppContext.BaseDirectory,
                UserDirectory = ProjectSettings.GlobalizePath("user://"),
                UseDevInbox = true,
                UseExecutableNameFallback = true,
            });

            if (source is not null)
            {
                GD.Print($"[NovaV2.Host] Pak from {source.Origin}: {source.Path}");
                return new PakLocation(source.Path, null);
            }
        }
        catch (Exception ex)
        {
            GD.PushError($"[NovaV2.Host] Pak resolution failed: {ex.Message}");
        }

        // A descriptor shipped inside the package names its pak by data, so the
        // executable name does not have to select the game. The dev inbox above
        // still wins, which is what makes a pushed build replace the shipped one.
        if (ReadEmbeddedDescriptor() is { } embeddedDescriptor)
        {
            if (embeddedDescriptor.Parts is { Count: > 0 })
            {
                // A split pak needs several streams joined; the runtime's pak
                // path takes one. Ship a single pak inside the package.
                GD.PushError(
                    $"[NovaV2.Host] {V12GameDescriptor.FileName} lists split parts; " +
                    "split paks inside the app package are not supported. Ship one pak or use the dev inbox.");
            }
            else if (embeddedDescriptor.Pak is { Length: > 0 } pakName)
            {
                string resource = pakName.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
                    ? pakName
                    : "res://" + pakName;

                if (Godot.FileAccess.FileExists(resource))
                {
                    GD.Print($"[NovaV2.Host] Using descriptor pak: {resource}");
                    return new PakLocation(null, resource);
                }

                GD.PushWarning(
                    $"[NovaV2.Host] {V12GameDescriptor.FileName} names '{pakName}', " +
                    $"but '{resource}' is not in the package.");
            }
        }

        string executable = OS.GetExecutablePath();
        if (!string.IsNullOrWhiteSpace(executable))
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(executable);
            if (!string.IsNullOrWhiteSpace(name))
            {
                foreach (string? directory in new[]
                         {
                             System.IO.Path.GetDirectoryName(executable),
                             Directory.GetCurrentDirectory(),
                             ProjectSettings.GlobalizePath("res://"),
                         })
                {
                    if (string.IsNullOrWhiteSpace(directory))
                        continue;

                    string candidate = System.IO.Path.Combine(directory, name + ".v12pak");
                    if (File.Exists(candidate))
                    {
                        GD.Print($"[NovaV2.Host] Using pak named after the executable: {candidate}");
                        return new PakLocation(candidate, null);
                    }
                }

                string resource = "res://" + name + ".v12pak";
                if (Godot.FileAccess.FileExists(resource))
                {
                    GD.Print($"[NovaV2.Host] Using embedded pak: {resource}");
                    return new PakLocation(null, resource);
                }
            }
        }

        string? embedded = FindEmbeddedPak();
        if (embedded is not null)
            GD.Print($"[NovaV2.Host] Using embedded pak: {embedded}");

        return new PakLocation(null, embedded);
    }

    /// <summary>
    /// <c>res://game.json</c> when the export shipped one. Godot's
    /// <see cref="Godot.FileAccess"/> is the only way to read it inside an APK.
    /// </summary>
    private static V12GameDescriptor? ReadEmbeddedDescriptor()
    {
        try
        {
            string path = "res://" + V12GameDescriptor.FileName;
            if (!Godot.FileAccess.FileExists(path))
                return null;

            return V12GameDescriptor.Parse(Godot.FileAccess.GetFileAsString(path));
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[NovaV2.Host] Could not read {V12GameDescriptor.FileName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// First <c>*.v12pak</c> at the package root, alphabetically. Android
    /// exports put content inside the APK, where this is the normal discovery
    /// path; a desktop source run can use it too.
    /// </summary>
    private static string? FindEmbeddedPak()
    {
        try
        {
            foreach (string file in DirAccess.GetFilesAt("res://")
                         .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                if (!file.EndsWith(".v12pak", StringComparison.OrdinalIgnoreCase))
                    continue;

                // A split part is not a whole archive; without its descriptor it
                // cannot be loaded on its own.
                if (V12PakParts.IsPartFile(file))
                    continue;

                return "res://" + file;
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[NovaV2.Host] Could not scan res:// for a pak: {ex.Message}");
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

    /// <summary>
    /// Two explicit views of the same world, each with its own camera and UI
    /// surface. Run with <c>--views-demo 1</c>.
    /// </summary>
    private static void BuildViewsDemo(GameRoot root)
    {
        var world = root.SelectedWorld;
        if (world is null) return;

        var secondCamera = new Element("ViewsDemoCamera");
        secondCamera.SetPosition(-3f, 2f, 6f);
        secondCamera.AddComponent(new CameraComponent
        {
            IsCurrent = false,
            Fov = 55f,
            NearClip = 0.05f,
            FarClip = 200f,
        });
        world.AddElement(secondCamera);

        var canvasElement = new Element("ViewsDemoCanvas");
        canvasElement.AddComponent(new CanvasComponent
        {
            ScreenSpace = true,
            Width = 1280,
            Height = 720,
        });

        var row = new Element("ViewsDemoRow");
        row.AddComponent(new HLayoutComponent { Spacing = 4f, Padding = 4f });
        canvasElement.AddChild(row);

        AddView(row, world, "ViewsDemoLeft", secondCamera.Id, "#202830");
        AddView(row, world, "ViewsDemoRight", FindCameraId(world), "#302028");

        world.AddElement(canvasElement);
    }

    private static void AddView(
        Element parent,
        World world,
        string name,
        long cameraId,
        string background)
    {
        var view = new Element(name);
        view.AddComponent(new VLayoutComponent { Expand = true });
        view.AddComponent(new ViewportComponent { BackgroundColor = background });
        view.AddComponent(new RenderViewComponent
        {
            CameraId = cameraId,
            WorldId = world.Id,
            Clear = V12.Rendering.ClearMode.Color,
            ClearColor = background,
            LayerMask = V12.Rendering.RenderLayers.Default,
            IsActive = true,
        });
        parent.AddChild(view);
    }

    private static long FindCameraId(World world)
    {
        foreach (var root in world.Root)
        {
            long id = FindCameraId(root);
            if (id != 0) return id;
        }
        return 0;
    }

    private static long FindCameraId(IWorldElement element)
    {
        if (element.GetComponent<CameraComponent>() is { IsCurrent: true })
            return element.Id;

        foreach (var child in element.Children)
        {
            long id = FindCameraId(child);
            if (id != 0) return id;
        }
        return 0;
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

    /// <summary>
    /// True when the default gamepak folder contains at least one assembly.
    /// Used to decide whether to build the demo world, so a real gamepak gets a
    /// clean scene without the host's demo UI.
    /// </summary>
    private static bool HasDefaultGamepaks()
    {
        const string directory = "gamepaks";
        return Directory.Exists(directory)
            && Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Any();
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
