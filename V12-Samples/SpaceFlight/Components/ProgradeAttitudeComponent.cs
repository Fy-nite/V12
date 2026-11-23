using System;
using System.Numerics;
using V12.Basic.Components;
using V12.Components;

namespace SpaceFlight.Components;

public enum SpacecraftAttitudeMode
{
    Prograde,
    Retrograde,
    TidalLock,
    Free,
}

/// <summary>
/// A flight-assist attitude hold. Central gravity applies no torque, so a body
/// with no control stays fixed in inertial space; this component rotates the
/// hull toward the requested direction without pretending it is a real RCS
/// torque simulation.
/// </summary>
public sealed class ProgradeAttitudeComponent : ScriptableComponent
{
    public SpacecraftAttitudeMode Mode { get; set; } = SpacecraftAttitudeMode.Prograde;
    public float TurnRate { get; set; } = 1.5f;
    public float ManualYawRate { get; set; } = 1.2f;
    public float ManualPitchRate { get; set; } = 1.0f;

    /// <summary>
    /// Rotate the hull directly from stick input. The first manual input drops
    /// the flight-assist hold into Free so the ship keeps the attitude the
    /// player just dialled in instead of snapping back to prograde.
    /// </summary>
    public void ManualRotate(float yawInput, float pitchInput, float deltaTime)
    {
        var body = Owner?.GetComponent<PhysicsBodyComponent>()?.Body;
        if (body == null || deltaTime <= 0f)
            return;

        if (MathF.Abs(yawInput) < 1e-4f && MathF.Abs(pitchInput) < 1e-4f)
            return;

        Mode = SpacecraftAttitudeMode.Free;

        // Local Y is up, local X is right, local -Z is the nose. Positive
        // pitch raises the nose; positive yaw input turns it right.
        var yaw = Quaternion.CreateFromAxisAngle(
            Vector3.UnitY, -yawInput * ManualYawRate * deltaTime);
        var pitch = Quaternion.CreateFromAxisAngle(
            Vector3.UnitX, pitchInput * ManualPitchRate * deltaTime);

        body.Rotation = Quaternion.Normalize(body.Rotation * yaw * pitch);
        body.AngularVelocity = Vector3.Zero;
    }

    public SpacecraftAttitudeMode CycleMode()
    {
        Mode = Mode switch
        {
            SpacecraftAttitudeMode.Prograde => SpacecraftAttitudeMode.Retrograde,
            SpacecraftAttitudeMode.Retrograde => SpacecraftAttitudeMode.TidalLock,
            SpacecraftAttitudeMode.TidalLock => SpacecraftAttitudeMode.Free,
            _ => SpacecraftAttitudeMode.Prograde,
        };
        return Mode;
    }

    public override void Update(float deltaTime)
    {
        if (Mode == SpacecraftAttitudeMode.Free)
            return;

        var body = Owner?.GetComponent<PhysicsBodyComponent>()?.Body;
        if (body == null)
            return;

        var velocity = body.LinearVelocity;
        var position = body.Position;
        if (velocity.LengthSquared() < 1e-4f)
            return;

        var forward = Vector3.Normalize(velocity);
        var radial = position.LengthSquared() > 1e-4f
            ? Vector3.Normalize(position)
            : Vector3.UnitY;

        // Use the orbital plane's normal as "up" so the hull and the chase
        // camera agree even on a polar orbit.
        var orbitUp = Vector3.Cross(velocity, position);
        orbitUp = orbitUp.LengthSquared() > 1e-6f
            ? Vector3.Normalize(orbitUp)
            : radial;

        if (Mode == SpacecraftAttitudeMode.Retrograde)
            forward = -forward;
        else if (Mode == SpacecraftAttitudeMode.TidalLock)
            forward = -radial;

        var target = LookRotation(forward, orbitUp);
        var current = body.Rotation;

        float dot = Math.Clamp(Quaternion.Dot(current, target), -1f, 1f);
        float angle = 2f * MathF.Acos(MathF.Abs(dot));
        float blend = angle > 1e-5f
            ? MathF.Min(1f, TurnRate * deltaTime / angle)
            : 1f;

        body.Rotation = Quaternion.Slerp(current, target, blend);
        body.AngularVelocity = Vector3.Zero;
    }

    private static Quaternion LookRotation(Vector3 forward, Vector3 up)
    {
        if (forward.LengthSquared() < 1e-6f)
            return Quaternion.Identity;

        forward = Vector3.Normalize(forward);
        if (MathF.Abs(Vector3.Dot(forward, up)) > 0.999f)
            up = Vector3.UnitX;

        var view = Matrix4x4.CreateLookAt(Vector3.Zero, forward, up);
        if (!Matrix4x4.Invert(view, out var world))
            return Quaternion.Identity;

        return Matrix4x4.Decompose(world, out _, out var rotation, out _)
            ? rotation
            : Quaternion.Identity;
    }
}
