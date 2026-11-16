namespace V12.Skeletal.Backends;

/// <summary>Creates <see cref="ProwlMotionAnimator"/> instances. Registered as "prowl".</summary>
public sealed class ProwlMotionBackend : ISkeletalBackend
{
    public string Name => "prowl";

    public ISkeletalAnimator Create(SkeletonAsset asset) => new ProwlMotionAnimator(asset);
}
