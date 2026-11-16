namespace V12.Skeletal;

/// <summary>
/// Creates runtime animators from a neutral <see cref="SkeletonAsset"/>.
/// Backends are registered by name; the host picks one per manager.
/// </summary>
public interface ISkeletalBackend
{
    string Name { get; }

    ISkeletalAnimator Create(SkeletonAsset asset);
}
