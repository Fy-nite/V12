using System;
using System.Numerics;
using V12.Basic.Components;
using V12.Components;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;

namespace SpaceFlight.Components;

public enum CameraViewMode
{
    Orbit,
    Follow,
}

/// <summary>
/// Slow orbit around the planet or a chase camera that can be panned and
/// tilted around the ship with the arrow keys. The camera element also owns
/// the <see cref="CameraComponent"/>; this component only writes its pose.
/// </summary>
public sealed class OrbitCameraComponent : ScriptableComponent
{
    public float Radius { get; set; } = 1250f;
    public float Height { get; set; } = 520f;
    public float AngularSpeed { get; set; } = 0.018f;
    public Vector3 Target { get; set; } = Vector3.Zero;

    /// <summary>Surface radius the scene's Radius/Height values were tuned for.
    /// Larger or smaller planets scale the cinematic orbit proportionally.</summary>
    public float ReferenceRadius { get; set; } = 400f;

    /// <summary>When set and <see cref="Mode"/> is <see cref="CameraViewMode.Follow"/>,
    /// the camera chases this element.</summary>
    public IWorldElement? FollowTarget { get; set; }
    public GravityBodySet? Bodies { get; set; }
    public CameraViewMode Mode { get; set; } = CameraViewMode.Orbit;

    /// <summary>Backwards-compatible alias for the old orbit/chase toggle.</summary>
    public bool Follow
    {
        get => Mode == CameraViewMode.Follow;
        set
        {
            Mode = value ? CameraViewMode.Follow : CameraViewMode.Orbit;
            if (value)
                CaptureFollowBase();
            else
                _lastOrbitRealTime = -1;
        }
    }

    public float FollowDistance { get; set; } = 420f;
    public float FollowHeight { get; set; } = 120f;

    /// <summary>Yaw around the ship's orbital up axis, in radians.</summary>
    public float FollowYaw { get; private set; }
    /// <summary>Pitch above the base chase offset, in radians.</summary>
    public float FollowPitch { get; private set; }

    public float FollowOrbitSpeed { get; set; } = 1.25f;
    public float FollowPitchSpeed { get; set; } = 1.0f;
    public float FollowZoomSpeed { get; set; } = 320f;
    public float MinFollowDistance { get; set; } = 60f;
    public float MaxFollowDistance { get; set; } = 900f;

    private float _angle = MathF.PI / 2f;
    private double _lastOrbitRealTime = -1;
    private bool _hasFollowBase;

    public CameraViewMode CycleMode()
    {
        Mode = Mode == CameraViewMode.Orbit
            ? CameraViewMode.Follow
            : CameraViewMode.Orbit;

        if (Mode == CameraViewMode.Follow)
            CaptureFollowBase();
        else
            _lastOrbitRealTime = -1;

        return Mode;
    }

    /// <summary>
    /// Freeze the chase camera's starting direction relative to the ship. After
    /// this, only the user's yaw/pitch moves the camera; the orbital velocity
    /// and gravity field no longer drag it around.
    /// </summary>
    private void CaptureFollowBase()
    {
        _hasFollowBase = false;
        FollowYaw = 0f;
        FollowPitch = 0f;

        if (FollowTarget == null)
            return;

        var body = FollowTarget.GetComponent<PhysicsBodyComponent>()?.Body;
        var target = body?.Position ?? FollowTarget.LocalTransform.Position;
        var localBody = Bodies?.FindDominant(target);
        var velocity = (body?.LinearVelocity ?? Vector3.Zero)
                     - (Bodies?.VelocityOf(localBody) ?? Vector3.Zero);

        var center = localBody?.WorldPosition ?? Vector3.Zero;
        var outward = target - center;
        outward = outward.LengthSquared() > 1e-4f
            ? Vector3.Normalize(outward)
            : Vector3.UnitY;

        var backward = velocity.LengthSquared() > 1e-4f
            ? -Vector3.Normalize(velocity)
            : Vector3.Zero;

        var direction = backward * 0.75f
                      + outward * 0.25f
                      + Vector3.UnitY * (FollowHeight / MathF.Max(1f, FollowDistance));

        direction = direction.LengthSquared() > 1e-6f
            ? Vector3.Normalize(direction)
            : -Vector3.UnitZ;

        FollowYaw = MathF.Atan2(direction.X, direction.Z);
        FollowPitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
        _hasFollowBase = true;
    }

    /// <summary>
    /// Pan and tilt the chase camera around the ship. X is yaw, Y is pitch,
    /// and zoom moves the camera in or out along its orbit radius.
    /// </summary>
    public void OrbitFollow(Vector2 orbitInput, float zoomInput, float deltaTime)
    {
        if (Mode != CameraViewMode.Follow || deltaTime <= 0f)
            return;

        if (orbitInput.LengthSquared() > 1e-8f)
        {
            FollowYaw += orbitInput.X * FollowOrbitSpeed * deltaTime;
            // Up should look up: lower the camera, not raise it over the ship.
            FollowPitch = Math.Clamp(
                FollowPitch - orbitInput.Y * FollowPitchSpeed * deltaTime,
                -1.35f,
                1.35f);
        }

        if (MathF.Abs(zoomInput) > 1e-4f)
        {
            FollowDistance = Math.Clamp(
                FollowDistance - zoomInput * FollowZoomSpeed * deltaTime,
                MinFollowDistance,
                MaxFollowDistance);
        }
    }

    public override void Update(float deltaTime)
    {
        if (Mode == CameraViewMode.Follow && FollowTarget != null)
        {
            if (!_hasFollowBase)
                CaptureFollowBase();

            var body = FollowTarget.GetComponent<PhysicsBodyComponent>()?.Body;
            var target = body?.Position ?? FollowTarget.LocalTransform.Position;

            // A plain spherical orbit around the ship. Yaw is around world up
            // and pitch is elevation; there is no frame that chases velocity or
            // gravity, so user input fully owns the camera angle.
            float cosPitch = MathF.Cos(FollowPitch);
            var direction = new Vector3(
                cosPitch * MathF.Sin(FollowYaw),
                MathF.Sin(FollowPitch),
                cosPitch * MathF.Cos(FollowYaw));
            var position = target + direction * FollowDistance;

            if (Owner != null)
                Owner.LocalTransform = LookAt(position, target, Vector3.UnitY);
            return;
        }

        // Cinematic motion is visual, not simulation: keep it at real time.
        float realDelta = RealTimeInput.Delta(ref _lastOrbitRealTime);
        _angle += AngularSpeed * realDelta;

        // The cinematic orbit follows the planet the ship is actually
        // orbiting. Radius and height are scaled from the main world's
        // reference size so Mars and Luna get the same framing.
        var orbitTarget = Target;
        float radius = Radius;
        float height = Height;

        if (Bodies != null && FollowTarget != null)
        {
            var shipBody = FollowTarget.GetComponent<PhysicsBodyComponent>()?.Body;
            var shipPosition = shipBody?.Position ?? FollowTarget.LocalTransform.Position;
            var localBody = Bodies.FindDominant(shipPosition);
            if (localBody != null)
            {
                orbitTarget = localBody.WorldPosition;
                float scale = MathF.Max(1f, localBody.SurfaceRadius)
                            / MathF.Max(1f, ReferenceRadius);
                radius *= scale;
                height *= scale;
            }
        }

        var orbitPosition = new Vector3(
            MathF.Cos(_angle) * radius,
            height,
            MathF.Sin(_angle) * radius) + orbitTarget;

        if (Owner != null)
            Owner.LocalTransform = LookAt(orbitPosition, orbitTarget);
    }

    /// <summary>Build a camera TRS whose -Z axis points at <paramref name="target"/>.</summary>
    public static TRS LookAt(Vector3 eye, Vector3 target)
        => LookAt(eye, target, Vector3.UnitY);

    public static TRS LookAt(Vector3 eye, Vector3 target, Vector3 up)
    {
        if (up.LengthSquared() < 1e-6f)
            up = Vector3.UnitY;

        // A camera directly above or below the ship has no stable world-up
        // basis. Pick another axis instead of letting the view matrix flip.
        var viewDirection = target - eye;
        if (viewDirection.LengthSquared() > 1e-6f)
        {
            var direction = Vector3.Normalize(viewDirection);
            var normalizedUp = Vector3.Normalize(up);
            if (MathF.Abs(Vector3.Dot(direction, normalizedUp)) > 0.999f)
            {
                up = MathF.Abs(Vector3.Dot(direction, Vector3.UnitX)) < 0.9f
                    ? Vector3.UnitX
                    : Vector3.UnitZ;
            }
        }

        var view = Matrix4x4.CreateLookAt(eye, target, up);
        if (!Matrix4x4.Invert(view, out var world))
            world = Matrix4x4.CreateTranslation(eye);
        if (!Matrix4x4.Decompose(world, out _, out var rotation, out _))
            rotation = Quaternion.Identity;

        return new TRS
        {
            Position = eye,
            Rotation = rotation,
            Scale = Vector3.One,
        };
    }
}
