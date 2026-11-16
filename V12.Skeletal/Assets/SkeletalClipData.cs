namespace V12.Skeletal;

/// <summary>
/// One uniformly sampled clip in a backend-neutral form. Frames are flattened
/// as <c>frame * BoneCount + bone</c>.
/// </summary>
public sealed class SkeletalClipData
{
    public required string Name { get; init; }

    public required float Duration { get; init; }

    /// <summary>Sampling rate used when the source curves were resampled.</summary>
    public required float SampleRate { get; init; }

    public required int BoneCount { get; init; }

    public required BoneLocal[] Frames { get; init; }

    public bool Loop { get; init; } = true;

    public int FrameCount => BoneCount <= 0 ? 0 : Frames.Length / BoneCount;

    public BoneLocal Get(int frame, int bone) => Frames[frame * BoneCount + bone];
}
