namespace V12.Skeletal;

/// <summary>
/// Registry of animation backends. The default is Prowl.Motion; a host or game
/// can register another and select it per <see cref="Components.SkeletalManagerComponent"/>.
/// </summary>
public static class SkeletalBackends
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ISkeletalBackend> Registry =
        new(StringComparer.OrdinalIgnoreCase);

    private static ISkeletalBackend? _default;

    /// <summary>The backend used when a manager names none.</summary>
    public static ISkeletalBackend Default
    {
        get
        {
            lock (Gate)
                return _default ??= CreateDefaultLocked();
        }
    }

    /// <summary>Registers a backend. The first one registered becomes the default.</summary>
    public static void Register(ISkeletalBackend backend, bool makeDefault = false)
    {
        ArgumentNullException.ThrowIfNull(backend);

        lock (Gate)
        {
            Registry[backend.Name] = backend;
            if (makeDefault || _default is null)
                _default = backend;
        }
    }

    /// <summary>Resolves a backend by name; null or empty resolves the default.</summary>
    public static ISkeletalBackend Resolve(string? name)
    {
        if (TryResolve(name, out var backend))
            return backend;

        throw new InvalidOperationException(
            $"Skeletal backend '{name}' is not registered. " +
            $"Known backends: {string.Join(", ", KnownNames())}.");
    }

    public static bool TryResolve(string? name, out ISkeletalBackend backend)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            backend = Default;
            return true;
        }

        lock (Gate)
            return Registry.TryGetValue(name, out backend!);
    }

    private static ISkeletalBackend CreateDefaultLocked()
    {
        if (Registry.TryGetValue("prowl", out var existing))
            return existing;

        var backend = new Backends.ProwlMotionBackend();
        Registry[backend.Name] = backend;
        return backend;
    }

    private static IEnumerable<string> KnownNames()
    {
        lock (Gate)
            return Registry.Keys.ToArray();
    }
}
