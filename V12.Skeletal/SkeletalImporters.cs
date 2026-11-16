using V12.Skeletal.Import;

namespace V12.Skeletal;

/// <summary>
/// Registry of asset importers. The default is Clay; a game can register a
/// different importer and select it per manager.
/// </summary>
public static class SkeletalImporters
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ISkeletalAssetImporter> Registry =
        new(StringComparer.OrdinalIgnoreCase);

    private static ISkeletalAssetImporter? _default;

    public static ISkeletalAssetImporter Default
    {
        get
        {
            lock (Gate)
                return _default ??= CreateDefaultLocked();
        }
    }

    public static void Register(ISkeletalAssetImporter importer, bool makeDefault = false)
    {
        ArgumentNullException.ThrowIfNull(importer);

        lock (Gate)
        {
            Registry[importer.Name] = importer;
            if (makeDefault || _default is null)
                _default = importer;
        }
    }

    public static ISkeletalAssetImporter Resolve(string? name)
    {
        if (TryResolve(name, out var importer))
            return importer;

        throw new InvalidOperationException($"Skeletal importer '{name}' is not registered.");
    }

    public static bool TryResolve(string? name, out ISkeletalAssetImporter importer)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            importer = Default;
            return true;
        }

        lock (Gate)
            return Registry.TryGetValue(name, out importer!);
    }

    private static ISkeletalAssetImporter CreateDefaultLocked()
    {
        if (Registry.TryGetValue("clay", out var existing))
            return existing;

        var importer = new ClaySkeletalImporter();
        Registry[importer.Name] = importer;
        return importer;
    }
}
