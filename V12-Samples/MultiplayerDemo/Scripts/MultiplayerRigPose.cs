using System.Numerics;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;

namespace MultiplayerDemo;

/// <summary>
/// A body plus tracked head/hand poses in one coordinate convention.
///
/// The local player element is the XR origin: it sits on the floor and stays
/// unrotated so the headset owns the view rotation. A third-person avatar needs
/// its body root on the floor under the headset, and its head/hands expressed
/// relative to that body root. Capturing the relative pose here keeps host-side
/// and client-side VR identical, and stops a body from being left behind when
/// the player walks around their play space.
/// </summary>
public readonly record struct MultiplayerRigPose(
    Vector3 BodyPosition,
    float BodyYaw,
    Vector3 HeadPosition,
    Quaternion HeadRotation,
    Vector3 LeftHandPosition,
    Quaternion LeftHandRotation,
    Vector3 RightHandPosition,
    Quaternion RightHandRotation)
{
    private static readonly Vector3 DefaultHead =
        new(0f, 1.55f, 0f);
    private static readonly Vector3 DefaultLeftHand =
        new(-0.30f, 1.25f, 0.10f);
    private static readonly Vector3 DefaultRightHand =
        new(0.30f, 1.25f, 0.10f);

    /// <summary>
    /// Height of the avatar body centre above the floor. The demo body mesh is
    /// 1.3 m tall and centred on the avatar element, so its root sits at 0.65 m.
    /// </summary>
    public const float BodyCenterHeight = 0.65f;

    /// <summary>
    /// Read the local rig and convert origin-relative tracking into a
    /// body-relative pose. Missing tracked parts fall back to the avatar's
    /// neutral rest pose so the body never snaps to the world origin.
    /// </summary>
    public static MultiplayerRigPose Capture(
        IWorldElement localPlayer,
        IWorldElement? head,
        IWorldElement? leftHand,
        IWorldElement? rightHand)
    {
        var origin = localPlayer.LocalTransform;
        float originYaw = YawOf(origin.Rotation);
        var originRotation = Quaternion.CreateFromYawPitchRoll(originYaw, 0f, 0f);

        var headLocal = head?.LocalTransform
            ?? new TRS(DefaultHead);
        var headWorldPosition =
            origin.Position + Vector3.Transform(headLocal.Position, originRotation);
        var headWorldRotation = originRotation * headLocal.Rotation;

        // Body root: on the floor directly under the head. Headset yaw becomes
        // body yaw so the avatar faces where the player looks.
        float bodyYaw = YawOf(headWorldRotation);
        var bodyPosition = new Vector3(
            headWorldPosition.X,
            origin.Position.Y + BodyCenterHeight,
            headWorldPosition.Z);
        var bodyRotation = Quaternion.CreateFromYawPitchRoll(bodyYaw, 0f, 0f);
        var bodyRotationInverse = Quaternion.Conjugate(bodyRotation);

        var headRelative = ToBodySpace(
            headWorldPosition, headWorldRotation, bodyPosition, bodyRotationInverse);
        var leftRelative = ToBodySpace(
            leftHand, DefaultLeftHand,
            origin.Position, originRotation, bodyPosition, bodyRotationInverse);
        var rightRelative = ToBodySpace(
            rightHand, DefaultRightHand,
            origin.Position, originRotation, bodyPosition, bodyRotationInverse);

        return new MultiplayerRigPose(
            bodyPosition,
            bodyYaw,
            headRelative.Position,
            headRelative.Rotation,
            leftRelative.Position,
            leftRelative.Rotation,
            rightRelative.Position,
            rightRelative.Rotation);
    }

    private static (Vector3 Position, Quaternion Rotation) ToBodySpace(
        IWorldElement? part,
        Vector3 fallback,
        Vector3 originPosition,
        Quaternion originRotation,
        Vector3 bodyPosition,
        Quaternion bodyRotationInverse)
    {
        var local = part?.LocalTransform ?? new TRS(fallback);
        var worldPosition =
            originPosition + Vector3.Transform(local.Position, originRotation);
        var worldRotation = originRotation * local.Rotation;
        return (Vector3.Transform(worldPosition - bodyPosition, bodyRotationInverse),
                Quaternion.Normalize(bodyRotationInverse * worldRotation));
    }

    private static (Vector3 Position, Quaternion Rotation) ToBodySpace(
        Vector3 worldPosition,
        Quaternion worldRotation,
        Vector3 bodyPosition,
        Quaternion bodyRotationInverse)
    {
        return (Vector3.Transform(worldPosition - bodyPosition, bodyRotationInverse),
                Quaternion.Normalize(bodyRotationInverse * worldRotation));
    }

    /// <summary>Yaw around the up axis, valid for q and -q.</summary>
    public static float YawOf(Quaternion q)
    {
        return MathF.Atan2(
            2f * (q.W * q.Y + q.X * q.Z),
            1f - 2f * (q.Y * q.Y + q.X * q.X));
    }
}
