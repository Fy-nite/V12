using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Godot;
using V12.Audio;
using V12.Audio.OwnAudio;
using V12.Components;
using V12.Core;
using V12.Core.GamePak;
using V12.Core.Input;
using V12.Core.Rendering;
using V12.Pak;
using V12.Rendering;
using NovaV2.Rendering;
using PaperUi = V12.Paper.PaperUiSystem;
using V12InputEvent = V12.Core.Input.InputEvent;
using RenderEnvironmentData = V12.Rendering.EnvironmentData;
using RenderWindowEvent = V12.Rendering.WindowEvent;

namespace NovaV2.Hosting;

/// <summary>
/// Minimal main-thread host for the v1 renderer contract. It drives the
/// simulation through <see cref="GameRoot.V12Tick"/>, captures a
/// <see cref="FrameSnapshot"/>, maps the environment, produces a
/// <see cref="RenderFrameLease"/> and submits it to <see cref="NovaV2Renderer"/>.
///
/// It owns input forwarding, audio, Paper UI layout and the screen-space UI
/// viewport. XR and gamepak discovery remain host concerns for now.
/// </summary>
public sealed class NovaV2Runtime : IDisposable
{
    private readonly GameRoot _root;
    private readonly NovaV2RuntimeOptions _options;
    private readonly NovaV2Renderer _renderer;
    private readonly RenderFrameProducer _producer;
    private readonly GodotPresentationHost _presentation;
    private readonly NovaV2InputHandler _inputHandler;
    private readonly OwnAudioEngine _audio;
    private readonly PaperUi _ui;
    private readonly RenderTargetHandle _uiTarget;
    private readonly NovaV2XRService? _xr;
    private readonly int _initialWidth;
    private readonly int _initialHeight;
    private int _viewportWidth;
    private int _viewportHeight;

    private const long UiViewId = -1000;

    private V12PakLoadResult? _loadedPakArchive;
    private float _time;
    private bool _forceRender = true;
    private bool _disposed;
    private RenderEnvironmentData? _lastEnvironment;

    public NovaV2Runtime(Node3D host, GameRoot root, NovaV2RuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(root);

        _root = root;
        _options = options ?? new NovaV2RuntimeOptions();

        var windowSize = host.GetWindow().Size;
        var viewportSize = host.GetViewport().GetVisibleRect().Size;
        int configuredWidth = (int)ProjectSettings.GetSetting("display/window/size/viewport_width");
        int configuredHeight = (int)ProjectSettings.GetSetting("display/window/size/viewport_height");

        _initialWidth = windowSize.X > 128 ? windowSize.X :
            configuredWidth > 0 ? configuredWidth :
            viewportSize.X >= 1f ? (int)viewportSize.X : 1920;
        _initialHeight = windowSize.Y > 128 ? windowSize.Y :
            configuredHeight > 0 ? configuredHeight :
            viewportSize.Y >= 1f ? (int)viewportSize.Y : 1080;
        _viewportWidth = _initialWidth;
        _viewportHeight = _initialHeight;

        _renderer = new NovaV2Renderer(host);
        _presentation = new GodotPresentationHost(host.GetWindow());
        _presentation.WindowEvent += OnWindowEvent;

        _inputHandler = new NovaV2InputHandler();
        _audio = new OwnAudioEngine(new AudioEngineOptions
        {
            SampleRate = 48_000,
            Channels = 2,
            BufferSize = 256,
            MaxChannels = 128,
        });
        root.Registry.Register("IAudioEngine", _audio);

        // NovaV2 owns the Paper layout and input bridge now. Remove the engine's
        // legacy default so no old IUIRenderer path can run.
        root.Registry.Unregister("IUIRenderer");

        var inputService = root.Registry.Get<InputService>()
                           ?? throw new InvalidOperationException("InputService is not registered.");
        _ui = new PaperUi(inputService, _viewportWidth, _viewportHeight);
        _uiTarget = _renderer.CreateRenderTarget(new RenderTargetDesc(
            _viewportWidth,
            _viewportHeight,
            Transparent: true,
            OwnWorld3D: true));
        _renderer.PresentRenderTarget(_uiTarget);

        _producer = new RenderFrameProducer(
            _renderer,
            _initialWidth,
            _initialHeight,
            textureLoader: GodotTextureLoader.Load);

        if (_options.EnableXR)
            _xr = new NovaV2XRService(host, root, inputService);

        if (_options.LockMouseOnStart)
            SetMouseCaptured(true);

        // Content (paks, gamepaks, worlds) must load before the engine
        // initializes services: a gamepak registers its gameplay systems from
        // its own Initialize(), and GameRoot.Initialize is what calls
        // IGameService.Initialize on everything registered. Doing it the other
        // way round leaves pak-loaded games with uninitialised physics.
        LoadContent();
        _root.Initialize();
    }

    public GameRoot Root => _root;
    public NovaV2Renderer Renderer => _renderer;
    public RenderFrameProducer Producer => _producer;
    public GodotPresentationHost Presentation => _presentation;
    public NovaV2InputHandler InputHandler => _inputHandler;
    public OwnAudioEngine Audio => _audio;
    public PaperUi Ui => _ui;
    public bool IsDisposed => _disposed;
    public bool LockMouseOnStart { get; set; }

    /// <summary>Environment source. Replace to supply host-owned skyboxes or effects.</summary>
    public Func<GameRoot, RenderEnvironmentData> EnvironmentProvider { get; set; } = CaptureEnvironment;

    /// <summary>
    /// Main-thread convenience: update the simulation and render if needed.
    /// Use this when the host does not drive <see cref="GameRoot.V12Tick"/> itself.
    /// </summary>
    public void Tick(float deltaTime)
    {
        ThrowIfDisposed();

        bool xrAvailable = _xr?.IsAvailable == true;
        _renderer.XREnabled = xrAvailable;

        var inputService = _root.Registry.Get<InputService>();
        if (inputService is not null)
        {
            bool escape = _inputHandler.ProcessFrame(inputService, xrAvailable, _root);
            if (escape)
                SetMouseCaptured(!_inputHandler.MouseCaptured);
        }

        // XR poses and controller events must land before the world ticks, so
        // gameplay reads this frame's tracking rather than last frame's.
        _xr?.Update();

        _root.V12Tick(deltaTime);
        Render(deltaTime);
    }

    /// <summary>
    /// Captures and submits a frame when the engine is dirty or a force-render
    /// was requested (startup, resize, device recovery).
    /// </summary>
    public void Render(float deltaTime)
    {
        ThrowIfDisposed();

        var environment = EnvironmentProvider(_root);
        bool environmentChanged = !Equals(environment, _lastEnvironment);
        bool dirty = _root.ConsumeRenderDirty();
        if (!environmentChanged && !dirty && !_forceRender)
            return;

        _forceRender = false;
        _time += Math.Max(0f, deltaTime);
        _lastEnvironment = environment;

        var snapshot = _root.CaptureFrame();

        _ui.Apply(_root.CaptureUI());
        var uiContext = new UiFrameContext(
            _ui.LastFrame,
            _uiTarget,
            UiViewId,
            _viewportWidth,
            _viewportHeight);

        if (!_producer.TryBuild(snapshot, environment, _time, deltaTime, uiContext, out var lease) ||
            lease is null)
        {
            // Pool back-pressure: retry on the next tick instead of losing the
            // dirty state.
            _forceRender = true;
            return;
        }

        try
        {
            _renderer.Submit(lease);
        }
        catch
        {
            // Submit only transfers ownership on success; release on failure.
            lease.Release();
            throw;
        }
    }

    public void ForceRender() => _forceRender = true;

    public void HandleInputEvent(Godot.InputEvent inputEvent)
    {
        var inputService = _root.Registry.Get<InputService>();
        if (inputService is null)
            return;

        switch (inputEvent)
        {
            case Godot.InputEventMouseMotion motion:
                _inputHandler.AccumulateMouseLook(motion.Relative);
                SendPointerMove(inputService, motion.Position);
                _forceRender = true;
                break;

            case Godot.InputEventMouseButton button:
                if (button.ButtonIndex is Godot.MouseButton.WheelUp or Godot.MouseButton.WheelDown
                    or Godot.MouseButton.WheelLeft or Godot.MouseButton.WheelRight)
                {
                    float wheel = button.ButtonIndex switch
                    {
                        Godot.MouseButton.WheelUp => 1f,
                        Godot.MouseButton.WheelDown => -1f,
                        Godot.MouseButton.WheelLeft => -1f,
                        _ => 1f,
                    };
                    inputService.SendEvent(new V12InputEvent
                    {
                        Type = InputEventType.Axis,
                        Name = "ui_wheel",
                        Value = wheel,
                        Pointer = new System.Numerics.Vector2(button.Position.X, button.Position.Y),
                    });
                }
                else
                {
                    var pointerButton = button.ButtonIndex switch
                    {
                        Godot.MouseButton.Left => PointerButton.Left,
                        Godot.MouseButton.Right => PointerButton.Right,
                        Godot.MouseButton.Middle => PointerButton.Middle,
                        Godot.MouseButton.Xbutton1 => PointerButton.Button4,
                        Godot.MouseButton.Xbutton2 => PointerButton.Button5,
                        _ => PointerButton.None,
                    };

                    inputService.SendEvent(new V12InputEvent
                    {
                        Type = button.Pressed ? InputEventType.ButtonDown : InputEventType.ButtonUp,
                        Name = "mouse_" + pointerButton.ToString().ToLowerInvariant(),
                        Button = pointerButton,
                        IsPressed = button.Pressed,
                        Pointer = new System.Numerics.Vector2(button.Position.X, button.Position.Y),
                    });
                }
                _forceRender = true;
                break;

            case Godot.InputEventKey key:
                // Every physical key edge is forwarded as a raw engine key so
                // games can bind their own actions to InputKey values instead of
                // relying on host-registered named actions.
                _inputHandler.ForwardKeyEdge(inputService, key);

                if (key.Pressed && !key.Echo && key.Unicode != 0)
                {
                    inputService.SendEvent(new V12InputEvent
                    {
                        Type = InputEventType.Char,
                        Character = char.ConvertFromUtf32((int)key.Unicode)[0],
                    });
                }
                _forceRender = true;
                break;
        }
    }

    private static void SendPointerMove(InputService inputService, Godot.Vector2 position)
    {
        inputService.SendEvent(new V12InputEvent
        {
            Type = InputEventType.PointerMove,
            Pointer = new System.Numerics.Vector2(position.X, position.Y),
        });
    }

    public void SetMouseCaptured(bool captured)
    {
        Input.MouseMode = captured ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
        _inputHandler.MouseCaptured = captured;
    }

    public void LoseDevice()
    {
        ThrowIfDisposed();
        _renderer.LoseDevice();
    }

    public void RecoverDevice()
    {
        ThrowIfDisposed();
        _renderer.RecoverDevice();
        _forceRender = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _presentation.WindowEvent -= OnWindowEvent;
        _ui.Dispose();
        _xr?.Dispose();
        _producer.Dispose();
        _renderer.DestroyRenderTarget(_uiTarget);
        _renderer.Dispose();
        _audio.Dispose();
        _presentation.Dispose();
    }

    private void OnWindowEvent(RenderWindowEvent windowEvent)
    {
        if (windowEvent.Kind == WindowEventKind.Resize)
        {
            _viewportWidth = Math.Max(1, windowEvent.Width);
            _viewportHeight = Math.Max(1, windowEvent.Height);
            _producer.Resize(_viewportWidth, _viewportHeight);
            _ui.Resize(_viewportWidth, _viewportHeight);
            _renderer.ResizeRenderTarget(_uiTarget, _viewportWidth, _viewportHeight);
            _forceRender = true;
        }
    }

    private void LoadContent()
    {
        SetupAssemblyLoadContext();

        bool startedAny = false;

        if (!string.IsNullOrWhiteSpace(_options.PakPath) && File.Exists(_options.PakPath))
        {
            try
            {
                _loadedPakArchive = _root.LoadPak(_options.PakPath, new V12PakOptions
                {
                    LoadDlls = true,
                    LoadContractScripts = true,
                    LoadCSharpScripts = true,
                });

                if (_loadedPakArchive.LoadedWorlds.Count > 0)
                    _root.SelectWorld(_loadedPakArchive.LoadedWorlds[0]);

                if (_root.Gamepaks.Gamepaks.Count > 0)
                {
                    _root.Gamepaks.InitializeAll();
                    _root.Gamepaks.StartAll();
                    startedAny = true;
                }

                GD.Print($"[NovaV2] Loaded pak '{Path.GetFileName(_options.PakPath)}' " +
                         $"({_loadedPakArchive.LoadedWorlds.Count} world(s), " +
                         $"{_loadedPakArchive.LoadedGamepaks} gamepak(s)).");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[NovaV2] Failed to load pak '{_options.PakPath}': {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(_options.GamepakDirectory) &&
            Directory.Exists(_options.GamepakDirectory))
        {
            _root.LoadGamepacks(_options.GamepakDirectory);
        }

        if (_options.GamepakAssemblies is { Count: > 0 })
        {
            foreach (var assembly in _options.GamepakAssemblies)
            {
                string assemblyName = assembly.GetName().Name ?? string.Empty;
                bool alreadyLoaded = _root.Gamepaks.Gamepaks.Any(p =>
                    string.Equals(
                        p.GetType().Assembly.GetName().Name,
                        assemblyName,
                        StringComparison.OrdinalIgnoreCase));
                if (alreadyLoaded)
                    continue;

                try
                {
                    if (_root.LoadGamepacksFromAssembly(assembly) > 0)
                        startedAny = true;
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[NovaV2] Failed to load gamepaks from '{assemblyName}': {ex.Message}");
                }
            }
        }

        if (!startedAny && !string.IsNullOrWhiteSpace(_options.ForceGamepak))
        {
            var pak = _root.Gamepaks.FindByName(_options.ForceGamepak);
            if (pak is not null)
            {
                pak.OnStart();
                startedAny = true;
            }
            else
            {
                GD.PrintErr($"[NovaV2] Gamepak '{_options.ForceGamepak}' was not found.");
            }
        }

        if (!startedAny && _options.AutoStartFirstGamepak)
        {
            var pak = _root.Gamepaks.Gamepaks.FirstOrDefault();
            if (pak is not null)
            {
                pak.OnStart();
                startedAny = true;
            }
        }

        if (!startedAny &&
            _options.CreateEmptyWorldIfNoPak &&
            _root.Worlds.Count == 0 &&
            _root.SelectedWorld is null)
        {
            _root.CreateWorld("Empty");
        }

        if (_root.SelectedWorld is null && _root.Worlds.Count > 0)
            _root.SelectWorld(_root.Worlds[0]);
    }

    private static void SetupAssemblyLoadContext()
    {
        var context = AssemblyLoadContext.GetLoadContext(typeof(NovaV2Runtime).Assembly);
        if (context is null)
            return;

        GamepackLoader.AssemblyLoader = path => context.LoadFromAssemblyPath(path);
        GamepackLoader.AssemblyFinder = name => context.Assemblies
            .FirstOrDefault(a => string.Equals(
                a.GetName().Name,
                name,
                StringComparison.OrdinalIgnoreCase));

        V12PakLoader.AssemblyLoader = path => context.LoadFromAssemblyPath(path);
        V12PakLoader.AssemblyFinder = name => context.Assemblies
            .FirstOrDefault(a => string.Equals(
                a.GetName().Name,
                name,
                StringComparison.OrdinalIgnoreCase));
    }

    private static RenderEnvironmentData CaptureEnvironment(GameRoot root)
    {
        var element = root.SelectedWorld?.FindElementWithComponentRecursive<EnvironmentComponent>()
                      ?? root.PersistentWorld.FindElementWithComponentRecursive<EnvironmentComponent>();
        var component = element?.GetComponent<EnvironmentComponent>();
        if (component is null)
            return RenderEnvironmentData.Default;

        var defaults = RenderEnvironmentData.Default;
        return defaults with
        {
            HasValue = true,
            Ambient = new AmbientSettings(
                new System.Numerics.Vector3(
                    component.AmbientR,
                    component.AmbientG,
                    component.AmbientB),
                component.AmbientEnergy),
            Shadows = new ShadowSettings(
                component.ShadowsEnabled,
                component.ShadowDistance,
                component.ShadowBias,
                component.ShadowNormalBias,
                component.ShadowOpacity),
            Sky = defaults.Sky with
            {
                Color = new System.Numerics.Vector3(
                    component.SkyR,
                    component.SkyG,
                    component.SkyB),
                Energy = 1f,
                // Path resolution is a host/asset concern; fall back to the
                // solid sky colour until the engine publishes a TextureHandle.
                SkyboxEnabled = false,
                Skybox = null,
            },
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NovaV2Runtime));
    }
}
