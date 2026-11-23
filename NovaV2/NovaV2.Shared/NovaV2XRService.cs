using Godot;
using System;
using System.Collections.Generic;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Input;

namespace NovaV2.Hosting;

/// <summary>
/// Drives OpenXR for the NovaV2 host: creates the origin, headset camera and
/// controller trackers, mirrors the tracked poses onto the V12 XR elements
/// (<see cref="XRRootComponent"/> / <see cref="XRHeadComponent"/> /
/// <see cref="XRHandComponent"/>), and forwards controller buttons and axes
/// into <see cref="InputService"/> as <c>xr_&lt;side&gt;_*</c> events.
///
/// The rig exists from the first frame but stays inert until XR is requested
/// (<c>--xr on</c>, a manifest <c>"Xr": true</c>, or
/// <see cref="Enable"/>). Devices with no OpenXR runtime - an Android phone, a
/// desktop with no headset - keep rendering flat, and a failed start is logged
/// once instead of retried in the background. Nothing here reloads the
/// process, so XR can be entered and left during gameplay.
/// </summary>
public sealed class NovaV2XRService : IDisposable
{
    private const float Deadzone = 0.15f;

    private readonly Node3D _parent;
    private readonly GameRoot _root;
    private readonly InputService _input;
    private readonly string? _actionMapPath;
    private readonly DisplayServer.VSyncMode? _defaultVsync;

    private OpenXRInterface? _interface;
    private bool _requested;
    private bool _initFailureLogged;
    private XROrigin3D? _origin;
    private XRCamera3D? _camera;
    private XRController3D? _leftController;
    private XRController3D? _rightController;
    private XRNode3D? _leftHandTracker;
    private XRNode3D? _rightHandTracker;

    private IWorldElement? _player;
    private IWorldElement? _lastLoggedPlayer;
    private IWorldElement? _rig;
    private IWorldElement? _head;
    private IWorldElement? _leftHand;
    private IWorldElement? _rightHand;
    private int _cacheTimer;

    private readonly Dictionary<string, bool> _prevButtons = new();
    private readonly Dictionary<string, float> _prevFloats = new();
    private static readonly bool DebugInput =
        System.Environment.GetEnvironmentVariable("V12_MP_DEBUG") == "1";
    private Vector2 _lastDebugStick;
    private bool _disposed;

    public NovaV2XRService(
        Node3D parent,
        GameRoot root,
        InputService input,
        string? actionMapPath = "res://openxr_action_map.tres",
        bool start = false)
    {
        _parent = parent;
        _root = root;
        _input = input;
        _actionMapPath = actionMapPath;
        _defaultVsync = DisplayServer.WindowGetVsyncMode();

        CreateNodes();

        // The project enables the OpenXR module at startup because Vulkan needs
        // the instance extensions before the scene loads. That only means the
        // runtime is available; it is not a request to output to the headset.
        // Starting is explicit so a phone with no runtime stays flat, and so a
        // game can enter XR from gameplay without a relaunch.
        if (start)
            Enable();
    }

    /// <summary>True while XR is rendering: OpenXR is running and the viewport
    /// is outputting to the headset.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>True when OpenXR itself is initialised, whether or not the
    /// viewport is currently rendering to the headset.</summary>
    public bool IsRuntimeReady => _interface?.IsInitialized() == true;

    /// <summary>True while the app is asking for XR, including a pending retry.</summary>
    public bool IsRequested => _requested;

    /// <summary>
    /// Starts XR now. Returns false when no OpenXR runtime is available, in
    /// which case the app keeps rendering flat and nothing polls in the
    /// background; call this again (after a headset is connected, from a menu,
    /// or when the game regains focus) to retry.
    /// </summary>
    public bool Enable()
    {
        if (_disposed)
            return false;

        _requested = true;

        if (!IsAvailable)
            Activate("requested");

        return IsAvailable;
    }

    /// <summary>
    /// Stops rendering to the headset. The OpenXR session stays up, so enabling
    /// again is immediate. Safe to call when XR is not active.
    /// </summary>
    public void Disable()
    {
        _requested = false;
        Deactivate();
    }

    /// <summary>Flips XR on/off and returns the new active state.</summary>
    public bool Toggle()
    {
        if (IsAvailable)
            Disable();
        else
            Enable();

        return IsAvailable;
    }

    public XROrigin3D? Origin => _origin;

    /// <summary>Latest trigger/grip values, in case gameplay reads them directly.</summary>
    public float LeftTrigger { get; private set; }
    public float RightTrigger { get; private set; }
    public float LeftGrip { get; private set; }
    public float RightGrip { get; private set; }

    private void CreateNodes()
    {
        _interface = XRServer.FindInterface("OpenXR") as OpenXRInterface;
        if (_interface is null)
        {
            GD.Print("[NovaV2.XR] OpenXR interface not found; running flat.");
            return;
        }

        if (!string.IsNullOrEmpty(_actionMapPath))
            _interface.Set("action_map", _actionMapPath);

        _origin = new XROrigin3D { Name = "NovaV2XROrigin" };
        _parent.AddChild(_origin);

        _camera = new XRCamera3D { Name = "NovaV2XRHead" };
        _origin.AddChild(_camera);

        _leftController = new XRController3D { Name = "NovaV2XRLeft", Tracker = "left_hand" };
        _origin.AddChild(_leftController);

        _rightController = new XRController3D { Name = "NovaV2XRRight", Tracker = "right_hand" };
        _origin.AddChild(_rightController);

        // Optical hand tracking, so bare hands still produce a pose.
        _leftHandTracker = new XRNode3D
        {
            Name = "NovaV2XRLeftHand",
            Tracker = "/user/hand_tracker/left",
            Pose = "default",
            ShowWhenTracked = true,
        };
        _origin.AddChild(_leftHandTracker);

        _rightHandTracker = new XRNode3D
        {
            Name = "NovaV2XRRightHand",
            Tracker = "/user/hand_tracker/right",
            Pose = "default",
            ShowWhenTracked = true,
        };
        _origin.AddChild(_rightHandTracker);

        // Viewport marking is deliberately not done here: `openxr/enabled`
        // initialising the module at startup must not put the game in XR.
        // Activate() flags the viewport when XR is actually requested.
    }

    /// <summary>
    /// Flags the root viewport for XR output. The root window is the only
    /// viewport Godot accepts for this, so it is set explicitly rather than
    /// trusting whatever viewport the node happens to resolve.
    /// </summary>
    private void MarkViewportForXR()
    {
        if (!GodotObject.IsInstanceValid(_parent))
            return;

        var tree = _parent.GetTree();
        var root = tree?.Root;
        if (root is not null && !root.UseXR)
        {
            root.UseXR = true;
            GD.Print($"[NovaV2.XR] Viewport '{root.Name}' marked UseXR.");
        }

        var viewport = _parent.GetViewport();
        if (viewport is not null && viewport != root && !viewport.UseXR)
            viewport.UseXR = true;
    }

    /// <summary>Returns the root viewport to flat rendering.</summary>
    private void UnmarkViewportForXR()
    {
        if (!GodotObject.IsInstanceValid(_parent))
            return;

        var tree = _parent.GetTree();
        var root = tree?.Root;
        if (root is not null && root.UseXR)
            root.UseXR = false;

        var viewport = _parent.GetViewport();
        if (viewport is not null && viewport != root && viewport.UseXR)
            viewport.UseXR = false;
    }

    /// <summary>
    /// Brings OpenXR up if it is not already running. The first interface
    /// initialised becomes the primary interface, so the explicit assignment
    /// the old reload path needed is not required.
    /// </summary>
    private bool EnsureRuntime()
    {
        if (_interface is null)
            return false;

        if (_interface.IsInitialized())
            return true;

        try
        {
            bool started = _interface.Initialize();
            if (started || _interface.IsInitialized())
                return true;

            LogInitFailureOnce("No OpenXR runtime is available; running flat. " +
                               "Start with --xr on, or call EnableXR() once a headset is connected.");
        }
        catch (Exception ex)
        {
            LogInitFailureOnce($"OpenXR init failed: {ex.Message} (running flat).");
        }

        return false;
    }

    private void LogInitFailureOnce(string message)
    {
        if (_initFailureLogged)
            return;

        _initFailureLogged = true;
        GD.Print($"[NovaV2.XR] {message}");
    }

    /// <summary>
    /// Puts the game into XR: starts OpenXR when needed, flags the root
    /// viewport, and lets the headset camera own the view. Idempotent.
    /// </summary>
    private void Activate(string reason)
    {
        if (IsAvailable || _disposed)
            return;

        if (!EnsureRuntime())
            return;

        IsAvailable = true;
        _initFailureLogged = false;

        // XR interfaces sync to the headset; a monitor refresh cap would
        // throttle it to the desktop refresh rate. Restored on Deactivate.
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

        MarkViewportForXR();

        if (_camera is not null && GodotObject.IsInstanceValid(_camera))
            _camera.Current = true;

        _root.Registry.RegisterOrReplace("XRTrackingService", this);
        GD.Print($"[NovaV2.XR] Active ({reason}).");
    }

    /// <summary>
    /// Returns the viewport to flat rendering. The OpenXR session is left
    /// running so a later <see cref="Enable"/> is immediate.
    /// </summary>
    private void Deactivate()
    {
        if (!IsAvailable)
            return;

        IsAvailable = false;
        _prevButtons.Clear();
        _prevFloats.Clear();

        if (_camera is not null && GodotObject.IsInstanceValid(_camera))
            _camera.Current = false;

        UnmarkViewportForXR();

        if (_defaultVsync is { } vsync)
            DisplayServer.WindowSetVsyncMode(vsync);

        _root.Registry.Unregister("XRTrackingService");
        GD.Print("[NovaV2.XR] Inactive.");
    }

    // ── Frame ───────────────────────────────────────────────────────────────

    /// <summary>Call once per frame, before the world ticks.</summary>
    public void Update()
    {
        if (_disposed)
            return;

        // A headset runtime can stop under us (app paused, session lost).
        // Drop back to flat; the request stays recorded, so the next Enable()
        // can start it again.
        if (IsAvailable && _interface is not null && !_interface.IsInitialized())
        {
            GD.Print("[NovaV2.XR] OpenXR runtime stopped; falling back to flat rendering.");
            Deactivate();
        }

        // No background retry on purpose: every Initialize() call spins up the
        // OpenXR loader, and a device with no runtime (an Android phone, a
        // desktop with no headset) would log a failure every second. The host
        // or game calls Enable() again when it has a reason to try.
        if (!IsAvailable)
            return;

        // Re-cache periodically, and immediately when the rig is missing so a
        // snapshot replacement or a late-loaded Player recovers next frame.
        if (++_cacheTimer > 60 ||
            _player is null ||
            (_rig is null && _cacheTimer > 10) ||
            (_head is null && _cacheTimer > 10))
            CacheElements();

        SyncOrigin();
        WritePose(HeadSource, _head);
        WritePose(LeftSource, _leftHand);
        WritePose(RightSource, _rightHand);
        PollControllers();
    }

    private Node3D? HeadSource => Valid(_camera) ? _camera : null;
    private Node3D? LeftSource => Pick(_leftController, _leftHandTracker);
    private Node3D? RightSource => Pick(_rightController, _rightHandTracker);

    /// <summary>Controller when it is tracked, otherwise the optical hand
    /// tracker. Returns null when neither has data, so the element keeps its
    /// last pose instead of snapping to the origin.</summary>
    private static Node3D? Pick(XRController3D? controller, XRNode3D? tracker)
    {
        if (Valid(controller) && (controller!.GetIsActive() || controller.GetHasTrackingData()))
            return controller;
        if (Valid(tracker) && (tracker!.GetIsActive() || tracker.GetHasTrackingData()))
            return tracker;
        return null;
    }

    private static bool Valid(Node? node) => node is not null && GodotObject.IsInstanceValid(node);

    /// <summary>
    /// Drops the cached player/head/hand elements so the next frame resolves
    /// them from the current world tree. Called after a hot reload replaces the
    /// scene.
    /// </summary>
    public void InvalidateElements()
    {
        _player = null;
        _cacheTimer = 0;
    }

    private void CacheElements()
    {
        _cacheTimer = 0;
        // A client world is Remote, so it is not in ActiveWorlds. Search the
        // focused world first, then every hydrated session; otherwise VR
        // tracking stops after the first cache refresh once the client joins a
        // networked world.
        _player = _root.SelectedWorld is { } focused
            ? _root.FindElement(focused, e => e.Name == "Player")
            : null;
        _player ??= _root.FindElementInLoadedWorlds(e => e.Name == "Player");
        _rig = _head = _leftHand = _rightHand = null;

        // Log only transitions: this is the state that used to flip to null
        // after a client world became Remote, silently stopping pose writes.
        if (!ReferenceEquals(_player, _lastLoggedPlayer))
        {
            _lastLoggedPlayer = _player;
            if (_player is null)
                GameRoot.Log?.Warning(
                    "[NovaV2.XR] Local Player not found; head/hand pose mirroring is paused.");
            else
                GameRoot.Log?.Information(
                    "[NovaV2.XR] Local Player resolved (name '{Name}', id {Id}).",
                    _player.Name, _player.Id);
        }

        if (_player is null)
            return;

        foreach (var child in _player.Children)
        {
            if (child.GetComponent<XRRootComponent>() is not null)
            {
                _rig = child;
                break;
            }
        }

        if (_rig is null)
            return;

        foreach (var child in _rig.Children)
        {
            if (child.GetComponent<XRHeadComponent>() is not null)
                _head = child;
            else if (child.GetComponent<XRHandComponent>() is { } hand)
            {
                if (hand.Side == HandSide.Left) _leftHand = child;
                else _rightHand = child;
            }
        }

    }

    /// <summary>The rig follows the player element so room-scale movement plus
    /// player movement compose correctly.</summary>
    private void SyncOrigin()
    {
        if (_player is null || _origin is null || !GodotObject.IsInstanceValid(_origin))
            return;

        var lt = _player.LocalTransform;
        _origin.Position = new Vector3(lt.Position.X, lt.Position.Y, lt.Position.Z);
        _origin.Quaternion = new Quaternion(
            lt.Rotation.X, lt.Rotation.Y, lt.Rotation.Z, lt.Rotation.W);
    }

    private static void WritePose(Node3D? source, IWorldElement? target)
    {
        if (source is null || target is null)
            return;

        var position = source.Position;
        var rotation = source.Transform.Basis.GetRotationQuaternion();

        target.LocalTransform = new V12.Core.Interfaces.Renderer.TRS(
            new System.Numerics.Vector3(position.X, position.Y, position.Z),
            new System.Numerics.Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W));
    }

    // ── Controller input ────────────────────────────────────────────────────

    private void PollControllers()
    {
        PollController(_leftController, "left");
        PollController(_rightController, "right");
    }

    private void PollController(XRController3D? controller, string side)
    {
        if (controller is null || !GodotObject.IsInstanceValid(controller))
            return;

        PollFloat(controller, side, "trigger");
        PollFloat(controller, side, "grip");

        PollButton(controller, side, "trigger_click");
        PollButton(controller, side, "grip_click");
        PollButton(controller, side, "ax_button");
        PollButton(controller, side, "by_button");
        PollButton(controller, side, "menu_button");
        PollButton(controller, side, "primary_click");
        PollStick(controller, side);
    }

    private void PollFloat(XRController3D controller, string side, string name)
    {
        float value = controller.GetFloat(name);
        string key = side + ":" + name;
        if (_prevFloats.TryGetValue(key, out float previous) && Math.Abs(value - previous) < 0.001f)
            return;

        _prevFloats[key] = value;

        if (name == "trigger")
        {
            if (side == "left") LeftTrigger = value;
            else RightTrigger = value;
        }
        else
        {
            if (side == "left") LeftGrip = value;
            else RightGrip = value;
        }

        SendAxis($"xr_{side}_{name}", value);
    }

    private void PollButton(XRController3D controller, string side, string name)
    {
        bool now = controller.IsButtonPressed(name);
        string key = side + ":" + name;
        bool previous = _prevButtons.TryGetValue(key, out bool value) && value;
        _prevButtons[key] = now;

        if (now == previous)
            return;

        string eventName = $"xr_{side}_{name}";
        _input.SendEvent(new V12.Core.Input.InputEvent
        {
            Type = now ? InputEventType.ButtonDown : InputEventType.ButtonUp,
            Name = eventName,
            Value = now ? 1f : 0f,
            IsPressed = now,
        });
    }

    private void PollStick(XRController3D controller, string side)
    {
        var stick = controller.GetVector2("primary");
        float x = Math.Abs(stick.X) < Deadzone ? 0f : stick.X;
        float y = Math.Abs(stick.Y) < Deadzone ? 0f : stick.Y;

        if (DebugInput && side == "left" &&
            (stick - _lastDebugStick).LengthSquared() > 0.0025f)
        {
            _lastDebugStick = stick;
            GD.Print($"[NovaV2.XR] left stick raw=({stick.X:F2}, {stick.Y:F2}) " +
                     $"applied=({x:F2}, {y:F2})");
        }

        SendAxis($"xr_{side}_stick_x", x);
        SendAxis($"xr_{side}_stick_y", y);
    }

    private void SendAxis(string name, float value) =>
        _input.SendEvent(new V12.Core.Input.InputEvent
        {
            Type = InputEventType.Axis,
            Name = name,
            Value = value,
        });

    // ── Lifecycle ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Leave XR output first: uninitialising an interface the viewport is
        // still pointed at is the order that trips Godot's renderer.
        Deactivate();

        if (_interface is not null && _interface.IsInitialized())
        {
            try { _interface.Uninitialize(); } catch { /* shutting down */ }
        }

        if (_origin is not null && GodotObject.IsInstanceValid(_origin))
            _origin.QueueFree();

        _origin = null;
        _camera = null;
        IsAvailable = false;
    }
}
