using System;
using System.Numerics;
using V12.Basic.Components;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Input;

namespace SpaceFlight.Components;

/// <summary>
/// Demo controls. Time scale changes the engine fixed clock, so it speeds up
/// physics, the camera and the telemetry together. The burns are real impulses
/// on the ship body, so they change the orbit instead of teleporting it.
/// </summary>
public sealed class SpaceFlightInputComponent : ScriptableComponent, IInputHandler, IFloatingOriginAware
{
    // The engine drops catch-up steps above MaxStepsPerFrame, so a level higher
    // than that budget would log a warp it cannot actually deliver. Keep the
    // available levels inside the 8x budget the sample documents.
    private static readonly float[] SpeedLevels = { 0.25f, 0.5f, 1f, 2f, 4f, 8f };
    private const int MaxFixedStepsPerFrame = 8;

    public Vector3 SpawnPosition { get; set; }
    public Vector3 SpawnVelocity { get; set; }
    /// <summary>The same source set the telemetry and orbit path use, so a burn
    /// is measured against the body that dominates the ship's gravity.</summary>
    public GravityBodySet? Bodies { get; set; }
    public IWorldElement? ResetTarget { get; set; }
    public float ResetMu { get; set; }
    public float ResetRadius { get; set; } = 800f;
    public float BoostDeltaV { get; set; } = 1f;
    public float BrakeDeltaV { get; set; } = 1f;
    public float ThrustAcceleration { get; set; } = 50f;
    public OrbitCameraComponent? Camera { get; set; }
    public ProgradeAttitudeComponent? Attitude { get; set; }
    public ThrustPlumeComponent? ThrustPlume { get; set; }
    public AudioSourceComponent? ThrustAudio { get; set; }

    private InputActionMap? _actions;
    private InputService? _input;
    private int _speedLevel = 2; // 1x
    private double _lastRealTime = -1;
    private bool _wasManual;
    private bool _wasThrusting;
    private CameraViewMode? _lastCameraMode;
    private bool _warnedNoMouseCapture;
    private bool _loggedMouseLook;

    // Raw pointer travel in pixels. Mouse look deliberately bypasses
    // InputActionMap: that map clamps every axis to [-1, 1], which turns a real
    // sweep into a single tiny step. The host publishes mouse_dx/mouse_dy only
    // while the pointer is captured, so this stays quiet when the cursor is up.
    private readonly object _lookLock = new();
    private Vector2 _pendingLook;

    public override void OnAttach(V12.Core.Core.Interfaces.IWorldElement worldElement)
    {
        base.OnAttach(worldElement);

        _input = GameRoot.Instance?.Registry.Get<InputService>();
        if (_input == null)
        {
            Console.WriteLine("[SpaceFlight] input service unavailable; keys disabled.");
            return;
        }

        _actions = new InputActionMap { Name = "SpaceFlight" };
        BindDefaults(_actions);
        _actions.Attach(_input);
        // Raw mouse look is read here, not through the action map, so a real
        // sweep keeps its magnitude instead of being clamped to one step.
        _input.RegisterHandler(this);
        Console.WriteLine(
            "[SpaceFlight] controls active: [ ] time, B/N burn, C camera, " +
            "T attitude, WASD rotate, Q/E roll, Space thrust, arrows or " +
            "mouse orbit camera, wheel zoom, R reset.");
    }

    /// <summary>The demo's key bindings, exposed so the sample tests use the same map.</summary>
    public static void BindDefaults(InputActionMap actions)
    {
        actions.BindButton("TimeSlower", InputKey.OemOpenBrackets);
        actions.BindButton("TimeFaster", InputKey.OemCloseBrackets);
        actions.BindButton("OrbitBoost", InputKey.B);
        actions.BindButton("OrbitBrake", InputKey.N);
        actions.BindButton("CameraToggle", InputKey.C);
        actions.BindButton("AttitudeToggle", InputKey.T);
        actions.BindButton("ResetOrbit", InputKey.R);

        // WASD is the ship's attitude stick and Q/E roll it. The arrow keys and
        // a captured mouse drag pan and tilt the chase camera around the ship;
        // the wheel zooms the chase or cinematic camera.
        actions.BindVector2("ShipRotate", InputKey.D, InputKey.A, InputKey.W, InputKey.S);
        actions.BindAxis("ShipRoll", InputKey.E, InputKey.Q);
        actions.BindVector2("CameraOrbit", InputKey.Right, InputKey.Left, InputKey.Up, InputKey.Down);
        actions.BindAxis("CameraZoom", "ui_wheel", "");
        // Either held mouse button enters/realises the chase camera. Left is
        // the natural drag for many players; right matches the other samples.
        actions.BindButton("CameraLook", "mouse_right");
        actions.AddButton("CameraLook", "mouse_left");
        actions.BindButton("Thrust", InputKey.Space);
    }

    public override void OnDetach(V12.Core.Core.Interfaces.IWorldElement worldElement)
    {
        if (_input != null)
        {
            _input.UnregisterHandler(this);
            if (_actions != null)
                _actions.Detach(_input);
        }

        _actions = null;
        _input = null;
        base.OnDetach(worldElement);
    }

    /// <summary>Buffers the host's raw relative mouse travel. Input events may
    /// arrive from another thread, so the buffer is locked.</summary>
    public void OnInputEvent(InputEvent evt)
    {
        if (evt is null || evt.Type != InputEventType.Axis
            || string.IsNullOrEmpty(evt.Name))
            return;

        lock (_lookLock)
        {
            if (string.Equals(evt.Name, "mouse_dx", StringComparison.OrdinalIgnoreCase))
                _pendingLook.X += evt.Value;
            else if (string.Equals(evt.Name, "mouse_dy", StringComparison.OrdinalIgnoreCase))
                _pendingLook.Y += evt.Value;
        }
    }

    private Vector2 TakeMouseLook()
    {
        lock (_lookLock)
        {
            var look = _pendingLook;
            _pendingLook = Vector2.Zero;
            return look;
        }
    }

    public override void Update(float deltaTime)
    {
        if (_actions == null) return;

        float realDelta = RealTimeInput.Delta(ref _lastRealTime);
        _actions.Update(deltaTime);

        if (_actions.GetButtonDown("TimeSlower")) ChangeSpeed(-1);
        if (_actions.GetButtonDown("TimeFaster")) ChangeSpeed(+1);
        if (_actions.GetButtonDown("OrbitBoost")) Burn(+1);
        if (_actions.GetButtonDown("OrbitBrake")) Burn(-1);
        if (_actions.GetButtonDown("CameraToggle")) ToggleCamera();
        if (_actions.GetButtonDown("AttitudeToggle")) ToggleAttitude();
        if (_actions.GetButtonDown("ResetOrbit")) ResetOrbit();

        float yaw = _actions.GetAxis("ShipRotate_X");
        float pitch = _actions.GetAxis("ShipRotate_Y");
        float roll = _actions.GetAxis("ShipRoll");
        bool manual = MathF.Abs(yaw) > 1e-4f
                   || MathF.Abs(pitch) > 1e-4f
                   || MathF.Abs(roll) > 1e-4f;
        if (manual)
        {
            if (!_wasManual)
                Console.WriteLine("[SpaceFlight] manual attitude");
            if (realDelta > 0f)
                Attitude?.ManualRotate(yaw, pitch, roll, realDelta);
        }
        _wasManual = manual;

        bool thrusting = _actions.GetButton("Thrust");
        if (thrusting != _wasThrusting)
        {
            Console.WriteLine($"[SpaceFlight] thrust {(thrusting ? "on" : "off")}");
            if (thrusting)
                ThrustAudio?.Play();
            else
                ThrustAudio?.Stop();
        }
        _wasThrusting = thrusting;

        if (thrusting)
            ApplyThrust(deltaTime);

        if (ThrustPlume != null)
            ThrustPlume.Thrusting = thrusting;

        // Drain the raw pointer travel every update, even while the cursor is
        // free or the cinematic camera is active, so stale motion cannot build
        // up and jump the chase camera when the pointer is captured again.
        var mouseLook = TakeMouseLook();

        var mouse = GameRoot.Instance?.MouseCapture;
        if (mouse == null)
        {
            if (!_warnedNoMouseCapture)
            {
                _warnedNoMouseCapture = true;
                Console.WriteLine(
                    "[SpaceFlight] mouse capture service unavailable; " +
                    "use the arrow keys to orbit the camera.");
            }
        }
        else if (Camera != null)
        {
            // Entering the chase camera captures the pointer, leaving it hands
            // the cursor back. C is enough to start mouse look; the cinematic
            // orbit view never steals the cursor.
            var mode = Camera.Mode;
            if (_lastCameraMode != mode)
            {
                bool capture = mode == CameraViewMode.Follow;
                mouse.SetCaptured(capture);
                Console.WriteLine(
                    $"[SpaceFlight] mouse {(capture ? "captured" : "released")}");
                _lastCameraMode = mode;
            }

            // A mouse button is the shortcut from the orbit view, and
            // re-captures after the host's Escape fallback released the pointer
            // while the chase camera stayed active.
            if (_actions.GetButtonDown("CameraLook"))
            {
                if (Camera.Mode == CameraViewMode.Orbit)
                    ToggleCamera();
                mouse.SetCaptured(true);
            }

            if (mouse.IsCaptured)
            {
                if (!_loggedMouseLook && mouseLook.LengthSquared() > 1e-8f)
                {
                    _loggedMouseLook = true;
                    Console.WriteLine(
                        "[SpaceFlight] mouse look active " +
                        $"({mouseLook.X:F1}, {mouseLook.Y:F1} px)");
                }
                Camera.LookFollow(mouseLook);
            }
        }

        // Wheel zoom is discrete: one notch is one step, so it does not get
        // stretched by the simulation time scale. Arrow-key orbit still runs
        // on real frame time.
        float wheel = _actions.GetAxis("CameraZoom");
        if (Camera != null)
        {
            if (Camera.Mode == CameraViewMode.Follow)
            {
                if (realDelta > 0f)
                {
                    Camera.OrbitFollow(
                        _actions.GetVector2("CameraOrbit"), 0f, realDelta);
                }
                Camera.ZoomFollow(wheel);
            }
            else
            {
                Camera.ZoomOrbit(wheel);
            }
        }
    }

    /// <summary>
    /// Continuous nose thrust. Impulse-per-frame keeps the acceleration
    /// independent of the render rate and follows the ship's current aim.
    /// </summary>
    public void ApplyThrust(float deltaTime)
    {
        var body = Owner?.GetComponent<PhysicsBodyComponent>()?.Body;
        if (body == null || deltaTime <= 0f)
            return;

        var forward = Vector3.Transform(-Vector3.UnitZ, body.Rotation);
        body.AddImpulse(forward * (ThrustAcceleration * body.Mass * deltaTime));
    }

    /// <summary>The spawn fallback is a world-space point; keep it in the bubble frame.</summary>
    public void OnOriginShift(Vector3 delta)
    {
        SpawnPosition -= delta;
    }

    /// <summary>Step the simulation speed up or down and return the new scale.</summary>
    public float ChangeSpeed(int direction)
    {
        _speedLevel = Math.Clamp(_speedLevel + direction, 0, SpeedLevels.Length - 1);
        float scale = SpeedLevels[_speedLevel];

        if (GameRoot.Instance != null)
        {
            GameRoot.Instance.TimeScale = scale;
            // A hard fixed-step budget protects frame time. Scaling the
            // catch-up budget with warp made high time scales run hundreds of
            // physics steps in one frame and tank the perceived FPS.
            GameRoot.Instance.Simulation.MaxStepsPerFrame = MaxFixedStepsPerFrame;
        }

        Console.WriteLine($"[SpaceFlight] time scale x{scale:0.##}");
        return scale;
    }

    /// <summary>Toggle the planet orbit view and the ship chase camera.</summary>
    public CameraViewMode ToggleCamera()
    {
        if (Camera == null) return CameraViewMode.Orbit;

        var mode = Camera.CycleMode();
        Console.WriteLine($"[SpaceFlight] camera {mode}");
        return mode;
    }

    /// <summary>Cycle prograde, retrograde, tidal lock and free attitude.</summary>
    public SpacecraftAttitudeMode ToggleAttitude()
    {
        if (Attitude == null) return SpacecraftAttitudeMode.Free;

        var mode = Attitude.CycleMode();
        Console.WriteLine($"[SpaceFlight] attitude {mode}");
        return mode;
    }

    /// <summary>Apply a prograde or retrograde impulse to the ship body.</summary>
    public bool Burn(int direction)
    {
        var body = Owner?.GetComponent<PhysicsBodyComponent>()?.Body;
        if (body == null) return false;

        // A world velocity includes the planet's own track velocity. Burning
        // along it is only accurate while the local orbit happens to line up
        // with that track; subtracting the dominant body's velocity keeps the
        // impulse tangent to the orbit the telemetry is reporting.
        var localBody = Bodies?.FindDominant(body.Position);
        var frameVelocity = Bodies?.VelocityOf(localBody)
            ?? ResetTarget?.GetComponent<PlanetOrbitComponent>()?.LinearVelocity
            ?? Vector3.Zero;
        var velocity = body.LinearVelocity - frameVelocity;
        if (velocity.LengthSquared() < 1e-4f) return false;

        float deltaV = direction > 0 ? BoostDeltaV : -BrakeDeltaV;
        var prograde = Vector3.Normalize(velocity);
        body.AddImpulse(prograde * deltaV * body.Mass);

        Console.WriteLine(
            $"[SpaceFlight] {(direction > 0 ? "prograde" : "retrograde")} burn " +
            $"{deltaV:+0.0;-0.0} m/s");
        return true;
    }

    /// <summary>Put the ship back on a circular orbit around the reset target.</summary>
    public bool ResetOrbit()
    {
        var body = Owner?.GetComponent<PhysicsBodyComponent>()?.Body;
        if (body == null) return false;

        var targetBody = ResetTarget?.GetComponent<PhysicsBodyComponent>()?.Body;
        var targetPosition = targetBody?.Position
                             ?? ResetTarget?.LocalTransform.Position
                             ?? SpawnPosition;
        var targetVelocity = targetBody?.LinearVelocity ?? Vector3.Zero;

        if (ResetTarget != null && ResetMu > 0f && ResetRadius > 0f)
        {
            // Reset on the target's outward side and move prograde around it.
            // This is the same local orbit the scene starts in, but follows
            // the planet after it has moved along its solar orbit.
            var outward = targetPosition.LengthSquared() > 1e-4f
                ? Vector3.Normalize(targetPosition)
                : Vector3.UnitX;
            var tangent = Vector3.Cross(outward, Vector3.UnitY);
            tangent = tangent.LengthSquared() > 1e-6f
                ? Vector3.Normalize(tangent)
                : Vector3.UnitZ;
            float speed = MathF.Sqrt(ResetMu / ResetRadius);

            body.Position = targetPosition + outward * ResetRadius;
            body.LinearVelocity = targetVelocity + tangent * speed;
        }
        else
        {
            body.Position = SpawnPosition;
            body.LinearVelocity = SpawnVelocity;
        }

        body.AngularVelocity = Vector3.Zero;

        Console.WriteLine("[SpaceFlight] orbit reset");
        return true;
    }
}
