using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using R3D_cs;
using Raylib_cs;
using V12.Basic;
using V12.Components;
using V12.Components.Renderables;
using V12.Components.UI;
using V12.Core;
using V12.Core.Interfaces.Renderer;
using V12.Core.Networking;
using V12.Pak;
using UiButton = V12.Components.UI.ButtonComponent;

namespace Rockstar
{
    public class Program
    {
        private static V12PakLoadResult? _loadedPak;

        private sealed class HostOptions
        {
            public string? PakPath;
            public string? Gamepak;
            public string? GamepaksDirectory;
            public bool Demo;
            public int Width = 1280;
            public int Height = 720;
        }

        static void Main(string[] args)
        {
            var options = ParseArgs(args);

            Raylib.InitWindow(options.Width, options.Height, "V12 + R3D");
            Raylib.SetTargetFPS(60);
            R3D.Init(Raylib.GetScreenWidth(), Raylib.GetScreenHeight());
            R3D.SetTextureWrap(TextureWrap.Repeat);

            BsonConfig.Initialize();

            var root = new GameRoot();
            var renderer = new R3DRenderer();
            var ui = new RaylibUIRenderer(Raylib.GetScreenWidth(), Raylib.GetScreenHeight());

            root.Registry.Register("IRenderer", renderer);
            root.Registry.RegisterOrReplace("IUIRenderer", ui);
            BasicRegistry.RegisterAll(root);

            int firstPakIndex = root.Gamepaks.Gamepaks.Count;

            if (!options.Demo)
            {
                string? pakPath = options.PakPath ?? FindSiblingPak();
                if (pakPath != null)
                {
                    LoadPak(root, pakPath);
                }
                else
                {
                    string gamepaksDirectory = ResolveGamepaksDirectory(options.GamepaksDirectory);
                    if (Directory.Exists(gamepaksDirectory))
                    {
                        Console.WriteLine($"[Rockstar] Scanning gamepaks in '{gamepaksDirectory}'...");
                        root.Gamepaks.LoadFromDirectory(gamepaksDirectory);
                    }
                }
            }

            root.Initialize();

            bool gamepakMode = false;
            if (_loadedPak != null)
            {
                root.Gamepaks.InitializeAndStartFrom(firstPakIndex);
                gamepakMode = true;
            }
            else if (!string.IsNullOrWhiteSpace(options.Gamepak))
            {
                gamepakMode = root.Gamepaks.InitializeAndStartByName(options.Gamepak);
                if (!gamepakMode)
                    Console.Error.WriteLine($"[Rockstar] Gamepak '{options.Gamepak}' was not found.");
            }
            else if (root.Gamepaks.Gamepaks.Count > 0)
            {
                ShowGamepakLauncher(root);
                gamepakMode = true;
            }

            if (!gamepakMode)
            {
                Console.WriteLine("[Rockstar] No gamepaks running � starting the built-in demo scene.");
                var world = root.CreateWorld("Demo", "empty");
                BuildDemoScene(world, root);
            }

            while (!Raylib.WindowShouldClose())
            {
                ui.Resize(Raylib.GetScreenWidth(), Raylib.GetScreenHeight());
                ui.FeedInput(Raylib.IsWindowFocused());

                root.V12Tick(Raylib.GetFrameTime());

                renderer.DrawFrame(ui.LastFrame, ui.Draw);
            }

            ui.Dispose();
            renderer.Dispose();
            root.Shutdown();
            _loadedPak?.Dispose();
            R3D.Close();
            Raylib.CloseWindow();
        }

        private static void LoadPak(GameRoot root, string pakPath)
        {
            Console.WriteLine($"[Rockstar] Launching '{Path.GetFileName(pakPath)}'...");
            try
            {
                _loadedPak = root.LoadPak(pakPath, new V12PakOptions
                {
                    LoadDlls = true,
                    LoadContractScripts = true,
                    LoadCSharpScripts = true,
                });

                if (_loadedPak.LoadedWorlds.Count > 0)
                    root.SelectWorld(_loadedPak.LoadedWorlds[0]);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Rockstar] Failed to load '{pakPath}': {ex.Message}");
            }
        }

        private static void ShowGamepakLauncher(GameRoot root)
        {
            var paks = root.Gamepaks.Gamepaks;
            int height = 96 + paks.Count * 44;

            var canvasElement = new Element("Gamepak Launcher");
            canvasElement.AddComponent(new CanvasComponent(0, 0)
            {
                ScreenSpace = true,
                Width = 380,
                Height = height,
                AnchorX = 0.5f,
                AnchorY = 0.5f,
            });
            root.PersistentWorld.AddElement(canvasElement);

            var panel = new Element("Panel");
            panel.AddComponent(new RectComponent
            {
                Width = 360,
                Height = height - 20,
                CornerRadius = 12,
                BackgroundColor = "#1b2330",
            });
            canvasElement.AddChild(panel);

            var content = new Element("Content");
            content.AddComponent(new VLayoutComponent { Spacing = 8f, Padding = 14f });
            panel.AddChild(content);

            var title = new Element("Title");
            title.AddComponent(new LayoutElementComponent { PreferredWidth = 320f, PreferredHeight = 26f });
            title.AddComponent(new LabelComponent("Select a gamepak") { FontSize = 20f });
            content.AddChild(title);

            foreach (var pak in paks)
            {
                string pakName = pak.Name;
                var buttonComponent = new UiButton(pakName);
                buttonComponent.OnClick = () =>
                {
                    root.PersistentWorld.RemoveElement(canvasElement);
                    root.MarkRenderDirty();
                    if (!root.Gamepaks.InitializeAndStartByName(pakName))
                        Console.Error.WriteLine($"[Rockstar] Failed to start gamepak '{pakName}'.");
                };

                var button = new Element(pakName);
                button.AddComponent(new LayoutElementComponent { PreferredWidth = 320f, PreferredHeight = 30f });
                button.AddComponent(buttonComponent);
                content.AddChild(button);
            }

            root.MarkRenderDirty();
        }

        private static void BuildDemoScene(World world, GameRoot root)
        {
            var groundMesh = new MeshComponent(MeshShape.Box, 20f, 0.5f, 20f);
            var ground = new Element("Ground");
            ground.LocalTransform = new TRS(new Vector3(0f, -0.25f, 0f));
            ground.AddComponent(groundMesh);
            ground.AddComponent(new MeshRenderer { Mesh = groundMesh });
            ground.AddComponent(new MaterialComponent(0.24f, 0.27f, 0.31f, 1f, 0.05f, 0.75f));
            world.AddElement(ground);

            world.AddElement(new Element("Environment")
            {
                Components = {
                    new EnvironmentComponent() {
                    }
                }
            });

            var cubeMesh = new MeshComponent(MeshShape.Box, 1f, 1f, 1f);
            var cube = new Element("Cube");
            cube.LocalTransform = new TRS(new Vector3(0f, 1f, 0f));
            cube.AddComponent(cubeMesh);
            cube.AddComponent(new MeshRenderer { Mesh = cubeMesh });
            cube.AddComponent(new MaterialComponent(0.85f, 0.36f, 0.16f, 1f, 0.15f, 0.4f));
            world.AddElement(cube);

            var sphereMesh = new MeshComponent(MeshShape.Sphere, 1.2f, 1.2f, 1.2f);
            var sphere = new Element("Sphere");
            sphere.LocalTransform = new TRS(new Vector3(2.2f, 1.2f, 0f));
            sphere.AddComponent(sphereMesh);
            sphere.AddComponent(new MeshRenderer { Mesh = sphereMesh });
            sphere.AddComponent(new MaterialComponent(0.2f, 0.55f, 0.85f, 1f, 0.6f, 0.25f));
            world.AddElement(sphere);

            var lightElement = new Element("KeyLight");
            lightElement.LocalTransform = new TRS(new Vector3(0f, 1f, 0f));
            lightElement.AddComponent(new PointLightComponent(1f, 0.95f, 0.85f, 15f, 2f));
            world.AddElement(lightElement);

            var canvasElement = new Element("UI");
            canvasElement.AddComponent(new CanvasComponent(0, 0)
            {
                ScreenSpace = true,
                Width = 340,
                Height = 170,
                AnchorX = 0.5f,
                AnchorY = 0.5f,
            });
            world.AddElement(canvasElement);

            var panel = new Element("Panel");
            panel.AddComponent(new RectComponent
            {
                Width = 320,
                Height = 150,
                CornerRadius = 12,
                BackgroundColor = "#1b2330",
            });
            canvasElement.AddChild(panel);

            var title = new Element("Title");
            title.AddComponent(new LabelComponent("V12 + R3D") { FontSize = 22 });
            panel.AddChild(title);

            var buttonComponent = new UiButton("Click me");
            buttonComponent.OnClick = () =>
            {
                buttonComponent.Label = buttonComponent.Label == "Click me" ? "Clicked!" : "Click me";
                root.MarkRenderDirty();
            };

            var button = new Element("Button");
            button.AddComponent(buttonComponent);
            panel.AddChild(button);
        }

        private static HostOptions ParseArgs(string[] args)
        {
            var options = new HostOptions();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--pak":
                    case "-p":
                        if (i + 1 < args.Length) options.PakPath = args[++i];
                        break;
                    case "--gamepak":
                    case "-g":
                        if (i + 1 < args.Length) options.Gamepak = args[++i];
                        break;
                    case "--gamepaks":
                    case "-d":
                        if (i + 1 < args.Length) options.GamepaksDirectory = args[++i];
                        break;
                    case "--demo":
                        options.Demo = true;
                        break;
                    case "--width":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out int width)) options.Width = width;
                        break;
                    case "--height":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out int height)) options.Height = height;
                        break;
                }
            }

            return options;
        }

        private static string ResolveGamepaksDirectory(string? directory)
        {
            string name = string.IsNullOrWhiteSpace(directory) ? "gamepaks" : directory;
            if (Path.IsPathRooted(name))
                return name;

            string besideExecutable = Path.Combine(AppContext.BaseDirectory, name);
            if (Directory.Exists(besideExecutable))
                return besideExecutable;

            return Path.GetFullPath(name);
        }

        private static string? FindSiblingPak()
        {
            var exePath = ResolveExecutablePath();
            var directory = exePath != null
                ? Path.GetDirectoryName(exePath)
                : AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(directory)) return null;

            var exeName = exePath != null ? Path.GetFileNameWithoutExtension(exePath) : null;
            if (string.IsNullOrEmpty(exeName)) return null;

            var exact = Path.Combine(directory, exeName + ".v12pak");
            if (File.Exists(exact)) return exact;

            foreach (var candidate in Directory.EnumerateFiles(directory, "*.v12pak", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(candidate), exeName, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return null;
        }

        private static string? ResolveExecutablePath()
        {
            var processPath = System.Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath) &&
                !string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return processPath;
            }

            return Assembly.GetEntryAssembly()?.Location;
        }
    }
}
