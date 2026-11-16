using System.Numerics;

namespace V12.Skeletal;

/// <summary>
/// One playing skeleton. The manager owns an instance, players drive it, and
/// renderable parts read <see cref="LocalPose"/>.
///
/// This is the swap point: everything above it (components, render contract)
/// works with this interface, so a different runtime (a native library, a
/// custom evaluator, a networked proxy) can replace the default backend.
/// </summary>
public interface ISkeletalAnimator : IDisposable
{
    /// <summary>Parent-space bone transforms for the current pose.</summary>
    IReadOnlyList<Matrix4x4> LocalPose { get; }

    int BoneCount { get; }

    /// <summary>Clip names this animator can play.</summary>
    IReadOnlyList<string> Clips { get; }

    string? CurrentClip { get; }

    /// <summary>Playback position in seconds. Assigning seeks immediately.</summary>
    float Time { get; set; }

    float Duration { get; }

    float Speed { get; set; }

    bool Loop { get; set; }

    bool IsPlaying { get; }

    /// <summary>Starts a clip. A non-zero blend time cross-fades from the current pose.</summary>
    void Play(string clip, bool loop = true, float blendTime = 0f);

    void CrossFade(string clip, float duration, bool loop = true);

    /// <summary>Jumps to a time in the current clip and samples immediately.</summary>
    void Seek(float seconds);

    void Pause();

    void Resume();

    /// <summary>Advances playback and refreshes <see cref="LocalPose"/>.</summary>
    void Update(float deltaTime);
}
