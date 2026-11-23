using System.Numerics;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;

namespace MultiplayerDemo;

/// <summary>
/// Host-owned avatar body. The host applies input here and the element's
/// transform is replicated through the normal WorldElementUpdate path.
/// </summary>
public sealed class MultiplayerAvatarComponent : ComponentBase
{
    private const float MoveSpeed = 3.2f;
    private const float HalfExtent = 9f;

    private float _inputX;
    private float _inputZ;
    private float _yaw;
    private IWorldElement? _head;
    private IWorldElement? _leftHand;
    private IWorldElement? _rightHand;

    public long PlayerId { get; set; }

    public override string Name => "Multiplayer Avatar";
    public override string Description => "Host-owned player body";

    public void SetInput(float moveX, float moveZ, float yaw)
    {
        _inputX = Math.Clamp(moveX, -1f, 1f);
        _inputZ = Math.Clamp(moveZ, -1f, 1f);
        _yaw = yaw;
    }

    /// <summary>
    /// Client-driven body pose for VR. The XR client moves its local rig and
    /// sends the resulting body-centre position, so the avatar body stays under
    /// the headset without a feedback loop against the host transform.
    /// </summary>
    public void SetBodyPose(Vector3 position, float yaw)
    {
        if (Owner is null) return;

        _inputX = 0f;
        _inputZ = 0f;
        _yaw = yaw;
        Owner.LocalTransform = new TRS(
            position,
            Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f));
    }

    /// <summary>
    /// Apply a captured VR rig pose: body root on the floor under the headset
    /// plus head/hands relative to that body root.
    /// </summary>
    public void SetRigPose(MultiplayerRigPose pose)
    {
        if (Owner is null) return;

        _inputX = 0f;
        _inputZ = 0f;
        _yaw = pose.BodyYaw;
        Owner.LocalTransform = new TRS(
            pose.BodyPosition,
            Quaternion.CreateFromYawPitchRoll(pose.BodyYaw, 0f, 0f));

        SetXr(
            pose.HeadPosition, pose.HeadRotation,
            pose.LeftHandPosition, pose.LeftHandRotation,
            pose.RightHandPosition, pose.RightHandRotation);
    }

    /// <summary>Apply remote XR head and hand local poses from the owning client.</summary>
    public void SetXr(
        Vector3 headPosition, Quaternion headRotation,
        Vector3 leftHandPosition, Quaternion leftHandRotation,
        Vector3 rightHandPosition, Quaternion rightHandRotation)
    {
        EnsureXrParts();
        if (_head != null)
            _head.LocalTransform = new TRS(headPosition, headRotation);
        if (_leftHand != null)
            _leftHand.LocalTransform = new TRS(leftHandPosition, leftHandRotation);
        if (_rightHand != null)
            _rightHand.LocalTransform = new TRS(rightHandPosition, rightHandRotation);
    }

    /// <summary>Host-local XR: copy the host's own tracked rig onto its avatar.</summary>
    public void SyncLocalXr(IWorldElement localPlayer)
    {
        EnsureXrParts();
        var head = localPlayer.FindChildByNameRecursive("Head");
        var left = localPlayer.FindChildByNameRecursive("LeftHand");
        var right = localPlayer.FindChildByNameRecursive("RightHand");

        if (_head != null && head != null)
            _head.LocalTransform = head.LocalTransform;
        if (_leftHand != null && left != null)
            _leftHand.LocalTransform = left.LocalTransform;
        if (_rightHand != null && right != null)
            _rightHand.LocalTransform = right.LocalTransform;
    }

    private void EnsureXrParts()
    {
        if (Owner == null) return;

        _head ??= Owner.FindChildByNameRecursive("Head");
        _leftHand ??= Owner.FindChildByNameRecursive("LeftHand");
        _rightHand ??= Owner.FindChildByNameRecursive("RightHand");
    }

    public override void Update(float deltaTime)
    {
        if (Owner is null) return;

        var rotation = Quaternion.CreateFromYawPitchRoll(_yaw, 0f, 0f);
        var forward = Vector3.Transform(-Vector3.UnitZ, rotation);
        var right = Vector3.Transform(Vector3.UnitX, rotation);

        var move = forward * (-_inputZ) + right * _inputX;
        if (move.LengthSquared() > 1f)
            move = Vector3.Normalize(move);

        var position = Owner.LocalTransform.Position;
        position += move * MoveSpeed * deltaTime;
        position.X = Math.Clamp(position.X, -HalfExtent, HalfExtent);
        position.Z = Math.Clamp(position.Z, -HalfExtent, HalfExtent);

        Owner.LocalTransform = new TRS(position, rotation);
    }
}
