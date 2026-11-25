using System.Numerics;
using System.Text.Json;

namespace MultiplayerDemo;

/// <summary>Client-to-host player intent and XR poses.</summary>
public sealed class PlayerInputCommand
{
    /// <summary>Monotonic client input sequence. The host echoes the last applied value.</summary>
    public uint Sequence { get; set; }

    /// <summary>Fixed-step delta this command advances. Older senders leave this at zero.</summary>
    public float Delta { get; set; }

    public float MoveX { get; set; }
    public float MoveZ { get; set; }
    public float Yaw { get; set; }

    /// <summary>ADSR speed multiplier at the time this input was sampled. The
    /// client and host must apply the same value so prediction stays exact.</summary>
    public float Envelope { get; set; } = 1f;

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

/// <summary>
/// Host-to-client authoritative player state. The sequence identifies the last
/// input command the host applied, so the owning client can replay its pending
/// inputs from the authoritative transform.
/// </summary>
public sealed class PlayerStateAck
{
    public uint Sequence { get; set; }
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
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
