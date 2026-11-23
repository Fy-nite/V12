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
public sealed class SpaceFlightInputComponent : ScriptableComponent
{
    private static readonly float[] SpeedLevels = { 0.25f, 0.5f, 1f, 2f, 4f, 8f, 16f, 32f,64f,128f };
    private const int MaxFixedStepsPerFrame = 16;

    public Vector3 SpawnPosition { get; set; }
    public Vector3 SpawnVelocity { get; set; }
    public IWorldElement? ResetTarget { get; set; }
    public float ResetMu { get; set; }
    public float ResetRadius { get; set; } = 800f;
    public float BoostDeltaV { get; set; } = 1f;
    public float BrakeDeltaV { get; set; } = 1f;
    public float ThrustAcceleration { get; set; } = 50f;
    public OrbitCameraComponent? Camera { get; set; }
    public ProgradeAttitudeComponent? Attitude { get; set; }
    public ThrustPlumeComponent? ThrustPlume { get; set; }

    private InputActionMap? _actions;
    private InputService? _input;
    private int _speedLevel = 2; // 1x
    private double _lastRealTime = -1;
    private bool _wasManual;
    private bool _wasThrusting;

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
        Console.WriteLine(
            "[SpaceFlight] controls active: [ ] time, B/N burn, C camera, " +
            "T attitude, WASD rotate, Space thrust, arrows orbit camera, Q/E zoom, R reset.");
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

        // WASD is the ship's attitude stick. The arrow keys pan and tilt the
        // chase camera around the ship; Q/E zoom it in and out.
        actions.BindVector2("ShipRotate", InputKey.D, InputKey.A, InputKey.W, InputKey.S);
        actions.BindVector2("CameraOrbit", InputKey.Right, InputKey.Left, InputKey.Up, InputKey.Down);
        actions.BindAxis("CameraZoom", InputKey.E, InputKey.Q);
        actions.BindButton("Thrust", InputKey.Space);
    }

    public override void OnDetach(V12.Core.Core.Interfaces.IWorldElement worldElement)
    {
        if (_input != null && _actions != null)
            _actions.Detach(_input);

        _actions = null;
        _input = null;
        base.OnDetach(worldElement);
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
        bool manual = MathF.Abs(yaw) > 1e-4f || MathF.Abs(pitch) > 1e-4f;
        if (manual)
        {
            if (!_wasManual)
                Console.WriteLine("[SpaceFlight] manual attitude");
            if (realDelta > 0f)
                Attitude?.ManualRotate(yaw, pitch, realDelta);
        }
        _wasManual = manual;

        bool thrusting = _actions.GetButton("Thrust");
        if (thrusting != _wasThrusting)
            Console.WriteLine($"[SpaceFlight] thrust {(thrusting ? "on" : "off")}");
        _wasThrusting = thrusting;

        if (thrusting)
            ApplyThrust(deltaTime);

        if (ThrustPlume != null)
            ThrustPlume.Thrusting = thrusting;

        if (Camera?.Mode == CameraViewMode.Follow && realDelta > 0f)
        {
            Camera.OrbitFollow(
                _actions.GetVector2("CameraOrbit"),
                _actions.GetAxis("CameraZoom"),
                realDelta);
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

        var velocity = body.LinearVelocity;
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
