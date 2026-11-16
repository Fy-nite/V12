namespace V12.Skeletal;

/// <summary>
/// Converts a file into the backend-neutral <see cref="SkeletonAsset"/>.
/// Swappable like the runtime backend, so a game can use a different importer
/// without touching the components.
/// </summary>
public interface ISkeletalAssetImporter
{
    string Name { get; }

    SkeletonAsset? Import(string path, float sampleRate = 30f);
}
