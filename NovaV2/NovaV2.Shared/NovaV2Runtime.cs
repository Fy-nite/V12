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
    private readonly GodotMouseCapture _mouseCapture;
    private readonly OwnAudioEngine _audio;
    private readonly PaperUi _ui;
    private readonly V12.Paper.PaperUiSource _paperSource = new();
    private RenderTargetHandle? _presentedTarget;
    private readonly NovaV2XRService? _xr;
    private readonly int _initialWidth;
    private readonly int _initialHeight;
    private int _viewportWidth;
    private int _viewportHeight;

    private V12PakLoadResult? _loadedPakArchive;
    private float _time;
    private bool _forceRender = true;
    private bool _disposed;
    private RenderEnvironmentData? _lastEnvironment;
    private readonly Dictionary<string, TextureHandle> _skyboxTextures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly bool DebugInput =
        System.Environment.GetEnvironmentVariable("V12_MP_DEBUG") == "1";

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
        EnvironmentProvider = CaptureEnvironment;

        _inputHandler = new NovaV2InputHandler();
        _audio = new OwnAudioEngine(new AudioEngineOptions
        {
            SampleRate = 48_000,
            Channels = 2,
            BufferSize = 256,
            MaxChannels = 128,
        });
        root.Registry.Register("IAudioEngine", _audio);

        // Pointer capture is a host service: gameplay calls Capture/Release
        // through IMouseCapture and never reaches for Godot's Input API.
        _mouseCapture = new GodotMouseCapture();
        _mouseCapture.CaptureChanged += captured => _inputHandler.MouseCaptured = captured;
        root.Registry.Register("IMouseCapture", _mouseCapture);
        _inputHandler.MouseCaptured = _mouseCapture.IsCaptured;

        // NovaV2 owns the Paper layout and input bridge now. Remove the engine's
        // legacy default so no old IUIRenderer path can run.
        root.Registry.Unregister("IUIRenderer");

        var inputService = root.Registry.Get<InputService>()
                           ?? throw new InvalidOperationException("InputService is not registered.");
        _ui = new PaperUi(inputService, _viewportWidth, _viewportHeight);
        // World canvases belong to _paperSource; without this the screen
        // overlay draws a second, screen-space copy of every world canvas.
        _ui.Paper.WorldCanvasesExternal = true;
        _ui.Paper.ViewportTextureProvider =
            viewId => new V12.Core.UI.Paper.PaperTextureRef(viewId);
        root.Registry.RegisterOrReplace("IViewportRegistry", _renderer);

        _producer = new RenderFrameProducer(
            _renderer,
            _initialWidth,
            _initialHeight,
            textureLoader: GodotTextureLoader.Load,
            input: inputService,
            uiSource: _paperSource)
        {
            // NovaV2 composites the UI as a full-screen overlay, so the engine
            // owns the target and names it in RenderFrame.PresentTarget. The
            // runtime never creates or names a UI target itself.
            ScreenUiTargetPolicy = UiTargetPolicy.OwnTarget,
        };

        // XR is always *available* unless the host opts out. Creating the
        // service builds the rig but does not initialise OpenXR; StartXR is the
        // explicit "go into XR now" signal, so the same build runs flat on a
        // phone and can enter VR later from gameplay.
        if (_options.EnableXR)
            _xr = new NovaV2XRService(host, root, inputService, start: _options.StartXR);

        // A --game hot-reload service is registered before this runtime exists.
        // Its world swap recreates the Player/XR elements, so drop the XR cache
        // as soon as the new tree is in place.
        if (_xr is not null &&
            root.Registry.Get<NovaV2HotReloadService>("NovaV2HotReload") is { } hotReload)
        {
            hotReload.WorldReloaded += _xr.InvalidateElements;
        }

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
    public GodotMouseCapture MouseCapture => _mouseCapture;
    public OwnAudioEngine Audio => _audio;
    public PaperUi Ui => _ui;
    public bool IsDisposed => _disposed;
    public bool LockMouseOnStart { get; set; }

    /// <summary>
    /// XR rig and tracking service. Present by default; OpenXR only starts when
    /// requested (<c>StartXR</c>, <c>--xr on</c>, a manifest <c>"Xr": true</c>,
    /// or <see cref="EnableXR"/>).
    /// </summary>
    public NovaV2XRService? XR => _xr;

    /// <summary>True while the viewport is rendering to the headset.</summary>
    public bool IsXRActive => _xr?.IsAvailable == true;

    /// <summary>
    /// Enter XR during gameplay, in this process. Returns false when OpenXR
    /// cannot start (no headset, no runtime, or the host passed
    /// <c>EnableXR = false</c>); the game keeps rendering flat and nothing polls
    /// in the background. Call it again later to retry.
    /// </summary>
    public bool EnableXR()
    {
        bool active = _xr?.Enable() == true;
        SyncXRRenderState();
        return active;
    }

    /// <summary>
    /// Stop rendering to the headset. The OpenXR session stays up, so
    /// <see cref="EnableXR"/> is immediate; the game continues flat.
    /// </summary>
    public void DisableXR()
    {
        _xr?.Disable();
        SyncXRRenderState();
    }

    /// <summary>Flip XR on/off and return the new active state.</summary>
    public bool ToggleXR()
    {
        bool active = _xr?.Toggle() == true;
        SyncXRRenderState();
        return active;
    }

    /// <summary>
    /// Pushes the XR state into the renderer immediately, so a toggle from
    /// gameplay applies to the frame that is already being built instead of
    /// one frame later, then requests a capture.
    /// </summary>
    private void SyncXRRenderState()
    {
        _renderer.XREnabled = _xr?.IsAvailable == true;
        ForceRender();
    }

    /// <summary>Environment source. Replace to supply host-owned skyboxes or effects.</summary>
    public Func<GameRoot, RenderEnvironmentData> EnvironmentProvider { get; set; }

    /// <summary>
    /// Main-thread convenience: update the simulation and render if needed.
    /// Use this when the host does not drive <see cref="GameRoot.V12Tick"/> itself.
    /// </summary>
    public void Tick(float deltaTime)
    {
        ThrowIfDisposed();

        // Input uses the state from the previous frame (tracking is not updated
        // yet); the renderer is told after XR has had its turn this frame.
        bool xrAvailable = _xr?.IsAvailable == true;

        var inputService = _root.Registry.Get<InputService>();
        if (inputService is not null)
        {
            bool escape = _inputHandler.ProcessFrame(inputService, xrAvailable, _root);
            if (escape)
                _mouseCapture.SetCaptured(!_mouseCapture.IsCaptured);
        }

        // XR poses and controller events must land before the world ticks, so
        // gameplay reads this frame's tracking rather than last frame's.
        _xr?.Update();

        // Update can activate XR (a retry succeeded) or drop back to flat (the
        // runtime stopped); the renderer needs that before this frame builds.
        _renderer.XREnabled = _xr?.IsAvailable == true;

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

        // Capture once: the model drives world-canvas layout, the laid-out
        // PaperFrame drives the screen overlay.
        var capturedUi = _root.CaptureUI();
        _ui.Apply(capturedUi);
        var uiContext = new UiFrameContext(
            capturedUi,
            _ui.LastFrame,
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

        // The frame names what to present; the engine owns the target's life.
        var presentTarget = lease.Frame.PresentTarget;
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

        if (presentTarget is { } present && present != _presentedTarget)
        {
            _renderer.PresentRenderTarget(present);
            _presentedTarget = present;
        }
    }

    public void ForceRender() => _forceRender = true;

    public void HandleInputEvent(Godot.InputEvent inputEvent)
    {
        var inputService = _root.Registry.Get<InputService>();
        if (inputService is null)
            return;

        if (DebugInput)
        {
            if (inputEvent is Godot.InputEventKey { Echo: false } key)
                GD.Print($"[NovaV2.Input] key physical={key.PhysicalKeycode} " +
                         $"keycode={key.Keycode} pressed={key.Pressed}");
            else if (inputEvent is Godot.InputEventMouseButton button)
                GD.Print($"[NovaV2.Input] mouse {button.ButtonIndex} pressed={button.Pressed}");
        }

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
        _mouseCapture.SetCaptured(captured);
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
        _mouseCapture.Shutdown();
        _ui.Dispose();
        if (_root.Registry.Get<NovaV2HotReloadService>("NovaV2HotReload") is { } hotReload)
        {
            hotReload.Dispose();
            _root.Registry.Unregister("NovaV2HotReload");
        }
        _xr?.Dispose();
        _producer.Dispose();
        _paperSource.Dispose();

        foreach (var skybox in _skyboxTextures.Values)
        {
            if (skybox.IsValid)
                _renderer.DestroyTexture(skybox);
        }
        _skyboxTextures.Clear();

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
            // The producer resizes the UI target from the next UiFrameContext;
            // the host no longer owns that target.
            _forceRender = true;
        }
    }

    private void LoadContent()
    {
        SetupAssemblyLoadContext();

        // Shader manifests must be registered before the first frame so a
        // MaterialComponent.ShaderUri resolves during the very first capture.
        if (!string.IsNullOrWhiteSpace(_options.ShaderDirectory))
        {
            int shaders = ShaderAssetLoader.LoadDirectory(_options.ShaderDirectory);
            if (shaders > 0)
                GD.Print($"[NovaV2] Registered {shaders} shader(s) from '{_options.ShaderDirectory}'.");
        }

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

                LoadPakShaders(_loadedPakArchive);

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

    /// <summary>
    /// Registers every <c>.v12shader</c> the pak's manifest lists. The pak
    /// resolver materializes archive entries to disk, so the shader loader can
    /// read them with the normal file path.
    /// </summary>
    private static void LoadPakShaders(V12PakLoadResult pak)
    {
        var files = new List<string>();
        foreach (string key in pak.Manifest.Assets.Keys)
        {
            if (!key.EndsWith(ShaderAssetLoader.Extension, StringComparison.OrdinalIgnoreCase))
                continue;

            string resolved = pak.Resolver.Resolve("v12://" + key.TrimStart('/'));
            if (File.Exists(resolved))
                files.Add(resolved);
        }

        if (files.Count == 0)
            return;

        int loaded = ShaderAssetLoader.LoadFiles(files);
        if (loaded > 0)
        {
            GD.Print($"[NovaV2] Registered {loaded} shader(s) from pak " +
                     $"'{Path.GetFileName(pak.PakPath)}'.");
        }
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

    private RenderEnvironmentData CaptureEnvironment(GameRoot root)
    {
        var element = root.SelectedWorld?.FindElementWithComponentRecursive<EnvironmentComponent>()
                      ?? root.PersistentWorld.FindElementWithComponentRecursive<EnvironmentComponent>();
        var component = element?.GetComponent<EnvironmentComponent>();
        if (component is null)
            return RenderEnvironmentData.Default;

        TextureHandle? skybox = null;
        if (component.Mode == BackgroundMode.Skybox &&
            !string.IsNullOrWhiteSpace(component.SkyboxPath))
        {
            skybox = GetSkyboxTexture(component.SkyboxPath);
        }

        bool hasSkybox = skybox.HasValue && skybox.Value.IsValid;
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
                Energy = MathF.Max(0f, component.SkyEnergy),
                SkyboxEnabled = hasSkybox,
                Skybox = hasSkybox ? skybox : null,
                // The contract carries a quaternion; the component exposes the
                // one axis a panorama actually needs.
                SkyboxRotation = System.Numerics.Quaternion.CreateFromAxisAngle(
                    System.Numerics.Vector3.UnitY,
                    component.SkyboxRotation * (MathF.PI / 180f)),
            },
        };
    }

    /// <summary>
    /// Resolves an environment skybox path to a GPU texture once. The handle is
    /// cached by path, so switching worlds back and forth does not thrash it;
    /// all cached handles are released with the runtime.
    /// </summary>
    private TextureHandle? GetSkyboxTexture(string path)
    {
        if (_skyboxTextures.TryGetValue(path, out var existing))
            return existing;

        var data = GodotTextureLoader.Load(path);
        if (data is null)
            return null;

        var handle = _renderer.CreateTexture(data);
        _skyboxTextures[path] = handle;
        return handle;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NovaV2Runtime));
    }
}
