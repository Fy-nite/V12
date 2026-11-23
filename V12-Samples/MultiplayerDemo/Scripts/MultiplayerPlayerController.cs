using System.Numerics;
using V12.Components;
using V12.Components.Renderables;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Input;
using V12.Core.Interfaces.Renderer;

namespace MultiplayerDemo;

/// <summary>
/// One controller for desktop and XR.
///
/// Desktop: WASD moves, right mouse captures the pointer, mouse or right stick
/// looks. XR: the headset owns the view, the left stick moves the rig, and the
/// same component continues to run through a registry pump so a remote world can
/// stay non-simulated while the local player still moves.
/// </summary>
public sealed class MultiplayerPlayerController : ComponentBase
{
    private const float MoveSpeed = 3.2f;
    private const float MouseSensitivity = 0.0025f;
    private const float StickLookSpeed = 1.8f;
    private const float TurnSpeed = 2.2f;
    private const float EyeHeight = 1.55f;

    private static readonly bool DebugInput =
        Environment.GetEnvironmentVariable("V12_MP_DEBUG") == "1";

    private InputActionMap? _actions;
    private IWorldElement? _owner;
    private IWorldElement? _desktopCamera;
    private MultiplayerBootstrap? _bootstrap;
    private IWorldElement? _avatar;
    private IWorldElement? _xrHead;
    private IWorldElement? _xrLeftHand;
    private IWorldElement? _xrRightHand;
    private bool _hasXr;
    private bool _remoteAvatar;
    private bool _localHandsHidden;
    private bool _adoptedAvatarSpawn;
    private bool _positionSeeded;
    private Vector3 _position;
    private float _yaw;
    private float _pitch;
    private float _debugTimer;

    /// <summary>Diagnostics: number of registry pump ticks received.</summary>
    public int TickCount { get; private set; }

    public override string Name => "Multiplayer Player";
    public override string Description => "Desktop and XR player controller";

    public override void OnAttach(IWorldElement worldElement)
    {
        base.OnAttach(worldElement);
        _owner = worldElement;
        _position = worldElement.LocalTransform.Position;
        _desktopCamera = worldElement.FindChildByNameRecursive("DesktopCamera");

        BindInput();
    }

    public override void OnDetach(IWorldElement worldElement)
    {
        if (_actions is not null && Input() is { } input)
            _actions.Detach(input);

        _actions = null;
        base.OnDetach(worldElement);
    }

    /// <summary>
    /// Bind this controller to a replicated avatar. A client avatar is remote:
    /// input goes to the host, and the local camera follows host state.
    /// </summary>
    internal void SetAvatar(IWorldElement avatar, bool remote)
    {
        _avatar = avatar;
        _remoteAvatar = remote;

        // The host's own avatar is visible and the local XR hands would
        // otherwise render on top of it. A client hides its remote avatar
        // instead and keeps its local rig visible.
        if (!remote)
            HideLocalHandVisuals();

        // In VR the local XR origin is authoritative; copying the replicated
        // avatar into it would feed the body offset back into the rig every
        // frame. Take the host-assigned spawn position once so two peers that
        // loaded the same world file do not start inside each other, then let
        // the local rig drive.
        if (_hasXr)
        {
            if (!_adoptedAvatarSpawn && _owner is not null)
            {
                _adoptedAvatarSpawn = true;
                var spawn = avatar.LocalTransform.Position;
                _position = new Vector3(spawn.X, 0f, spawn.Z);
                _owner.LocalTransform = new TRS(
                    _position,
                    Quaternion.CreateFromYawPitchRoll(_yaw, 0f, 0f));
            }
            return;
        }

        FollowAvatar();
    }

    private static InputService? Input() =>
        GameRoot.Instance?.Registry.Get<InputService>();

    private void BindInput()
    {
        if (Input() is not { } input)
        {
            GameRoot.Log?.Warning("[MultiplayerDemo] No InputService; player input disabled.");
            return;
        }

        var actions = new InputActionMap { Name = "MultiplayerPlayer" };

        // XR left stick and desktop WASD drive the same axes. The stick's Y
        // axis is positive when pushed forward, so bind it to the negative
        // side (same sign as W) to make stick-up move forward.
        actions.BindAxis("MoveX", "xr_left_stick_x", "");
        actions.AddAxis("MoveX", InputKey.D, InputKey.A);
        actions.BindAxis("MoveZ", "", "xr_left_stick_y");
        actions.AddAxis("MoveZ", InputKey.S, InputKey.W);
        actions.BindAxis("PadMoveX", GamepadAxis.LeftX);
        actions.BindAxis("PadMoveZ", GamepadAxis.LeftY);

        // Mouse delta and right stick look.
        actions.BindAxis("LookX", "mouse_dx", "");
        actions.BindAxis("LookY", "mouse_dy", "");
        actions.AddAxis("LookX", InputKey.Right, InputKey.Left);
        actions.AddAxis("LookY", InputKey.Down, InputKey.Up);
        actions.BindAxis("StickLookX", GamepadAxis.RightX);
        actions.BindAxis("StickLookY", GamepadAxis.RightY);

        // XR right stick turns the rig. The right stick is a separate axis
        // from desktop gamepad look because the headset owns pitch.
        actions.BindAxis("TurnX", "xr_right_stick_x", "");

        actions.BindButton("Capture", "mouse_right");
        actions.AddButton("Capture", InputKey.Escape);

        actions.Attach(input);
        _actions = actions;
    }

    /// <summary>
    /// Called by a registry pump rather than World.Update. That keeps the local
    /// player alive on a Remote session that intentionally does not simulate.
    /// </summary>
    internal void Tick(float deltaTime)
    {
        if (_actions is null || _owner is null)
            return;

        TickCount++;
        _desktopCamera ??= _owner.FindChildByNameRecursive("DesktopCamera");
        _bootstrap ??= _owner.GetComponent<MultiplayerBootstrap>();

        // Components attach before the world parser finishes writing the
        // element transform, so re-read it on the first tick instead of
        // trusting the value seen in OnAttach.
        if (!_positionSeeded)
        {
            _positionSeeded = true;
            if (_avatar is null)
                _position = _owner.LocalTransform.Position;
        }

        _actions.Update(deltaTime);

        var root = GameRoot.Instance;
        bool xr = root?.Registry.Get("XRTrackingService") != null;
        var capture = root?.MouseCapture;

        if (xr)
        {
            ResolveXrRig();
            _hasXr = _xrHead != null;
            if (_avatar != null && !_remoteAvatar)
                HideLocalHandVisuals();
        }
        else
        {
            _hasXr = false;
        }

        if (_actions.GetButtonDown("Capture") && capture is not null)
        {
            capture.SetCaptured(!capture.IsCaptured);
            if (DebugInput)
                GameRoot.Log?.Information(
                    "[MultiplayerDemo] pointer captured={Captured}", capture.IsCaptured);
        }

        if (!xr)
            UpdateDesktopLook(deltaTime);
        else
        {
            // Smooth turn with the XR right stick. Rotate around the headset
            // rather than the play-space origin so a player standing off-centre
            // does not get swung sideways while turning.
            float turn = _actions.GetAxis("TurnX");
            if (Math.Abs(turn) > 0.1f && _xrHead is not null)
            {
                float newYaw = _yaw - turn * TurnSpeed * deltaTime;
                var headLocal = _xrHead.LocalTransform.Position;
                var oldOffset = Vector3.Transform(
                    headLocal,
                    Quaternion.CreateFromYawPitchRoll(_yaw, 0f, 0f));
                var newOffset = Vector3.Transform(
                    headLocal,
                    Quaternion.CreateFromYawPitchRoll(newYaw, 0f, 0f));

                _position += new Vector3(
                    oldOffset.X - newOffset.X,
                    0f,
                    oldOffset.Z - newOffset.Z);
                _yaw = newYaw;
            }
        }

        Move(deltaTime);
        UpdateDesktopCamera(xr);

        if (DebugInput)
        {
            _debugTimer += deltaTime;
            if (_debugTimer >= 1f)
            {
                _debugTimer = 0f;
                GameRoot.Log?.Information(
                    "[MultiplayerDemo] input tick={Tick} xr={Xr} remote={Remote} avatar={Avatar} " +
                    "captured={Captured} move=({MX:F2},{MZ:F2}) look=({LX:F2},{LY:F2}) " +
                    "turn={Turn:F2} yaw={Yaw:F2} pos=({PX:F2},{PZ:F2})",
                    TickCount, _hasXr, _remoteAvatar, _avatar != null,
                    capture?.IsCaptured ?? false,
                    _actions.GetAxis("MoveX") + _actions.GetAxis("PadMoveX"),
                    _actions.GetAxis("MoveZ") + _actions.GetAxis("PadMoveZ"),
                    _actions.GetAxis("LookX"), _actions.GetAxis("LookY"),
                    _actions.GetAxis("TurnX"), _yaw, _position.X, _position.Z);
            }
        }
    }

    private void UpdateDesktopLook(float deltaTime)
    {
        // Mouse deltas are only published while captured; arrow keys and the
        // right stick stay available as a no-mouse fallback.
        _yaw -= _actions!.GetAxis("LookX") * MouseSensitivity;
        _pitch -= _actions.GetAxis("LookY") * MouseSensitivity;

        _yaw -= _actions.GetAxis("StickLookX") * StickLookSpeed * deltaTime;
        _pitch -= _actions.GetAxis("StickLookY") * StickLookSpeed * deltaTime;
        _pitch = Math.Clamp(_pitch, -1.2f, 1.2f);
    }

    private void Move(float deltaTime)
    {
        if (_owner is null || _actions is null)
            return;

        float x = _actions.GetAxis("MoveX") + _actions.GetAxis("PadMoveX");
        float z = _actions.GetAxis("MoveZ") + _actions.GetAxis("PadMoveZ");
        x = Math.Clamp(x, -1f, 1f);
        z = Math.Clamp(z, -1f, 1f);

        if (_remoteAvatar)
        {
            if (_hasXr)
            {
                // VR is client-moved for responsiveness. Move the local rig
                // locally, then send the floor pose so the host places the
                // replicated body under the headset.
                MoveLocal(deltaTime, useHeadYaw: true);
                _bootstrap?.SendInput(BuildCommand(x, z));
                return;
            }

            // Desktop remains host-authoritative; follow the replicated pose.
            _bootstrap?.SendInput(BuildCommand(x, z));
            FollowAvatar();
            return;
        }

        if (_avatar is not null)
        {
            // Host-local avatar: apply input directly; the avatar component moves
            // and the session DirtyTracker replicates the transform. In VR the
            // tracked rig pose is authoritative, so the body follows the headset
            // instead of the stick.
            var avatarComponent = _avatar.GetComponent<MultiplayerAvatarComponent>();
            if (_hasXr)
            {
                // Host VR: move the local rig with the stick, then let the
                // tracked pose place the replicated body under the headset.
                MoveLocal(deltaTime, useHeadYaw: true);
                if (CaptureRigPose() is { } pose)
                {
                    avatarComponent?.SetRigPose(pose);
                    return;
                }
            }

            avatarComponent?.SetInput(x, z, _yaw);
            FollowAvatar();
            return;
        }

        MoveLocal(deltaTime, useHeadYaw: true);
    }

    private void MoveLocal(float deltaTime, bool useHeadYaw)
    {
        if (_owner is null || _actions is null)
            return;

        float x = _actions.GetAxis("MoveX") + _actions.GetAxis("PadMoveX");
        float z = _actions.GetAxis("MoveZ") + _actions.GetAxis("PadMoveZ");
        x = Math.Clamp(x, -1f, 1f);
        z = Math.Clamp(z, -1f, 1f);
        if (Math.Abs(x) >= 0.01f || Math.Abs(z) >= 0.01f)
        {
            float movementYaw = useHeadYaw && _xrHead is not null
                ? _yaw + MultiplayerRigPose.YawOf(_xrHead.LocalTransform.Rotation)
                : _yaw;

            var rotation = Quaternion.CreateFromYawPitchRoll(movementYaw, 0f, 0f);
            var forward = Vector3.Transform(-Vector3.UnitZ, rotation);
            var right = Vector3.Transform(Vector3.UnitX, rotation);

            // W / stick-up is negative on the MoveZ axis; forward is -Z.
            var move = forward * (-z) + right * x;
            if (move.LengthSquared() > 1f)
                move = Vector3.Normalize(move);

            _position += move * MoveSpeed * deltaTime;
            _position.X = Math.Clamp(_position.X, -7f, 7f);
            _position.Z = Math.Clamp(_position.Z, -7f, 7f);
        }

        // Keep the XR origin unrotated; the headset yaw lives in the tracked
        // head pose. Rotating the origin as well would double the head yaw.
        // Desktop yaw lives here, so publish it even when standing still.
        _owner.LocalTransform = new TRS(
            _position,
            Quaternion.CreateFromYawPitchRoll(_yaw, 0f, 0f));
    }

    private PlayerInputCommand BuildCommand(float moveX, float moveZ)
    {
        var command = new PlayerInputCommand
        {
            MoveX = moveX,
            MoveZ = moveZ,
            Yaw = _yaw,
        };

        if (CaptureRigPose() is not { } pose)
            return command;

        command.HasXr = true;
        command.HasBodyPose = true;
        command.BodyPosition = pose.BodyPosition;
        command.BodyYaw = pose.BodyYaw;
        command.HeadPosition = pose.HeadPosition;
        command.HeadRotation = pose.HeadRotation;
        command.LeftHandPosition = pose.LeftHandPosition;
        command.LeftHandRotation = pose.LeftHandRotation;
        command.RightHandPosition = pose.RightHandPosition;
        command.RightHandRotation = pose.RightHandRotation;

        return command;
    }

    /// <summary>
    /// Capture the tracked rig in the local player's body space, or null when
    /// this peer is not in VR. Also hides the local hand meshes once the host
    /// owns a visible avatar so duplicate hands cannot render on top of it.
    /// </summary>
    private MultiplayerRigPose? CaptureRigPose()
    {
        if (!_hasXr || _owner is null || _xrHead is null)
            return null;

        return MultiplayerRigPose.Capture(_owner, _xrHead, _xrLeftHand, _xrRightHand);
    }

    private void HideLocalHandVisuals()
    {
        if (_localHandsHidden || _owner is null)
            return;

        _localHandsHidden = true;
        foreach (string name in new[] { "LeftHand", "RightHand" })
        {
            var hand = _owner.FindChildByNameRecursive(name);
            if (hand is null) continue;

            foreach (var component in hand.Components.ToArray())
                if (component is MeshRenderer or MeshComponent)
                    hand.RemoveComponent(component);
        }
    }

    private void ResolveXrRig()
    {
        if (_owner == null) return;

        _xrHead ??= _owner.FindChildByNameRecursive("Head");
        _xrLeftHand ??= _owner.FindChildByNameRecursive("LeftHand");
        _xrRightHand ??= _owner.FindChildByNameRecursive("RightHand");
    }

    private void FollowAvatar()
    {
        if (_avatar is null || _owner is null) return;

        // The avatar root is the body centre; the player element is the floor
        // origin that carries the camera/XR rig. Keep the origin on the floor
        // so the desktop eye height and a later XR origin both stay correct.
        var avatarPosition = _avatar.LocalTransform.Position;
        _position = new Vector3(avatarPosition.X, 0f, avatarPosition.Z);
        _owner.LocalTransform = new TRS(
            _position,
            Quaternion.CreateFromYawPitchRoll(_yaw, 0f, 0f));
    }

    private void UpdateDesktopCamera(bool xr)
    {
        if (xr || _desktopCamera is null)
            return;

        _desktopCamera.LocalTransform = new TRS(
            new Vector3(0f, EyeHeight, 0f),
            Quaternion.CreateFromYawPitchRoll(0f, _pitch, 0f));
    }
}
