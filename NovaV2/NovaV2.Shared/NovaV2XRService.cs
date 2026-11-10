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
/// The service is inert when OpenXR cannot initialise (desktop without a
/// headset): the scene keeps rendering through its normal V12 camera and the
/// service retries in the background, so plugging a headset in later still
/// brings XR up.
/// </summary>
public sealed class NovaV2XRService : IDisposable
{
    private const float Deadzone = 0.15f;
    private const int RetryIntervalFrames = 60;

    private readonly Node3D _parent;
    private readonly GameRoot _root;
    private readonly InputService _input;
    private readonly string? _actionMapPath;

    private OpenXRInterface? _interface;
    private XROrigin3D? _origin;
    private XRCamera3D? _camera;
    private XRController3D? _leftController;
    private XRController3D? _rightController;
    private XRNode3D? _leftHandTracker;
    private XRNode3D? _rightHandTracker;

    private IWorldElement? _player;
    private IWorldElement? _rig;
    private IWorldElement? _head;
    private IWorldElement? _leftHand;
    private IWorldElement? _rightHand;
    private int _cacheTimer;

    private readonly Dictionary<string, bool> _prevButtons = new();
    private readonly Dictionary<string, float> _prevFloats = new();
    private int _retryCounter;
    private bool _disposed;

    public NovaV2XRService(
        Node3D parent,
        GameRoot root,
        InputService input,
        string? actionMapPath = "res://openxr_action_map.tres")
    {
        _parent = parent;
        _root = root;
        _input = input;
        _actionMapPath = actionMapPath;

        CreateNodes();
        TryInitialize();
    }

    /// <summary>True once OpenXR has initialised and the viewport is in XR.</summary>
    public bool IsAvailable { get; private set; }

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

        // If Godot already booted OpenXR from `openxr/enabled`, mark the
        // viewport now so the first frame has an XR target. Otherwise wait for
        // Initialize() to succeed: flagging a viewport for XR with no XR
        // interface running renders nothing.
        if (_interface.IsInitialized())
            MarkViewportForXR();
    }

    /// <summary>
    /// Flags the root viewport for XR output. The root window is the only
    /// viewport Godot accepts for this, so it is set explicitly rather than
    /// trusting whatever viewport the node happens to resolve.
    /// </summary>
    private void MarkViewportForXR()
    {
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

    private void TryInitialize()
    {
        if (_interface is null || IsAvailable)
            return;

        try
        {
            XRServer.PrimaryInterface = _interface;

            // Initialize() returns false when the interface is already up (for
            // example because `openxr/enabled` booted it), so check both.
            bool started = _interface.Initialize();
            if (!started && !_interface.IsInitialized())
            {
                GD.Print("[NovaV2.XR] OpenXR not ready; retrying while the app runs.");
                return;
            }

            IsAvailable = true;
            _retryCounter = 0;

            // XR interfaces do their own sync; a monitor refresh cap throttles
            // the headset to the desktop refresh rate.
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

            MarkViewportForXR();

            if (_camera is not null && GodotObject.IsInstanceValid(_camera))
                _camera.Current = true;

            _root.Registry.RegisterOrReplace("XRTrackingService", this);
            GD.Print($"[NovaV2.XR] OpenXR initialized (already up: {!started}).");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[NovaV2.XR] OpenXR init failed: {ex.Message}");
        }
    }

    // ── Frame ───────────────────────────────────────────────────────────────

    /// <summary>Call once per frame, before the world ticks.</summary>
    public void Update()
    {
        if (_disposed)
            return;

        if (!IsAvailable)
        {
            if (_interface is not null && ++_retryCounter % RetryIntervalFrames == 0)
                TryInitialize();
            return;
        }

        if (++_cacheTimer > 60 || _player is null)
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

    private void CacheElements()
    {
        _cacheTimer = 0;
        _player = _root.FindElement(e => e.Name == "Player");
        _rig = _head = _leftHand = _rightHand = null;

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

        if (_interface is not null && IsAvailable)
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
