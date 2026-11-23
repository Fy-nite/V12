using System.Numerics;
using System.Text.Json;

namespace MultiplayerDemo;

/// <summary>Client-to-host player intent and XR poses.</summary>
public sealed class PlayerInputCommand
{
    public float MoveX { get; set; }
    public float MoveZ { get; set; }
    public float Yaw { get; set; }

    public bool HasBodyPose { get; set; }
    public Vector3 BodyPosition { get; set; }
    public float BodyYaw { get; set; }

    public bool HasXr { get; set; }
    public Vector3 HeadPosition { get; set; }
    public Quaternion HeadRotation { get; set; }
    public Vector3 LeftHandPosition { get; set; }
    public Quaternion LeftHandRotation { get; set; }
    public Vector3 RightHandPosition { get; set; }
    public Quaternion RightHandRotation { get; set; }
}

/// <summary>Host-to-client assignment of the avatar this client owns.</summary>
public sealed class PlayerWelcome
{
    public long PlayerId { get; set; }
    public long AvatarId { get; set; }
    public Guid AvatarUid { get; set; }
}

/// <summary>JSON settings for game protocol DTOs. Vector3/Quaternion are fields.</summary>
public static class MultiplayerJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true,
    };
}
