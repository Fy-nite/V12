using System.Numerics;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;

namespace MultiplayerDemo;

/// <summary>
/// Host-owned avatar body. The host's own body applies local input; client
/// bodies apply sequenced commands from their owner. The element's transform is
/// replicated through the normal WorldElementUpdate path.
/// </summary>
public sealed class MultiplayerAvatarComponent : ComponentBase
{
    private const float MoveSpeed = 3.2f;
    private const float HalfExtent = 9f;

    private float _inputX;
    private float _inputZ;
    private float _yaw;
    private float _envelope = 1f;
    private IWorldElement? _head;
    private IWorldElement? _leftHand;
    private IWorldElement? _rightHand;

    public long PlayerId { get; set; }

    /// <summary>
    /// Host-side flag: this body is driven by the owning client's sequenced
    /// input commands, so <see cref="Update"/> must not integrate local input.
    /// The client runs the same deterministic steps for prediction.
    /// </summary>
    public bool ClientDriven { get; set; }

    private uint _lastAppliedSequence;

    /// <summary>Last client input sequence the host applied to this body.</summary>
    public uint LastAppliedSequence => _lastAppliedSequence;

    public override string Name => "Multiplayer Avatar";
    public override string Description => "Host-owned player body";

    public void SetInput(float moveX, float moveZ, float yaw, float envelope = 1f)
    {
        _inputX = Math.Clamp(moveX, -1f, 1f);
        _inputZ = Math.Clamp(moveZ, -1f, 1f);
        _yaw = yaw;
        _envelope = Math.Clamp(envelope, 0f, 1f);
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

    /// <summary>
    /// Advance a client-driven body by exactly one of that client's input
    /// commands. The owning client runs <see cref="ComputeStep"/> with the same
    /// arguments, so its predicted position matches the host result.
    /// </summary>
    public void ApplyInputStep(
        float moveX, float moveZ, float yaw, float deltaTime, float envelope, uint sequence)
    {
        if (Owner is null) return;

        // Reliable ordering normally prevents stale commands, but reconnects or
        // protocol changes can still deliver an old one. Ignore it instead of
        // teleporting the body backwards.
        if (sequence != 0 && sequence <= _lastAppliedSequence) return;

        var (position, rotation) = ComputeStep(
            Owner.LocalTransform.Position, moveX, moveZ, yaw, deltaTime, envelope);
        Owner.LocalTransform = new TRS(position, rotation);

        if (sequence != 0)
            _lastAppliedSequence = sequence;
    }

    /// <summary>
    /// One deterministic movement step shared by the host body and the owning
    /// client's prediction. Keep this the only place the movement math lives.
    /// </summary>
    public static (Vector3 Position, Quaternion Rotation) ComputeStep(
        Vector3 position, float moveX, float moveZ, float yaw, float deltaTime, float envelope = 1f)
    {
        var rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f);
        var forward = Vector3.Transform(-Vector3.UnitZ, rotation);
        var right = Vector3.Transform(Vector3.UnitX, rotation);

        var move = forward * (-moveZ) + right * moveX;
        if (move.LengthSquared() > 1f)
            move = Vector3.Normalize(move);

        float speed = MoveSpeed * Math.Clamp(envelope, 0f, 1f);
        position += move * speed * deltaTime;
        position.X = Math.Clamp(position.X, -HalfExtent, HalfExtent);
        position.Z = Math.Clamp(position.Z, -HalfExtent, HalfExtent);

        return (position, rotation);
    }

    public override void Update(float deltaTime)
    {
        // Client-driven bodies advance from sequenced commands in
        // ApplyInputStep, not from the host's local input.
        if (Owner is null || ClientDriven) return;

        var (position, rotation) = ComputeStep(
            Owner.LocalTransform.Position, _inputX, _inputZ, _yaw, deltaTime, _envelope);
        Owner.LocalTransform = new TRS(position, rotation);
    }
}
