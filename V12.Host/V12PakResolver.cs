using System.Diagnostics;
using V12.Pak;

namespace V12.Hosting;

/// <summary>A pak the host should run, and why it was chosen.</summary>
/// <param name="Path">Absolute path to the <c>.v12pak</c>, or to its first part when split.</param>
/// <param name="Origin">Short tag for logs: <c>argument</c>, <c>dev-inbox</c>, <c>descriptor</c>, <c>executable-name</c>.</param>
/// <param name="Descriptor">The descriptor that named the pak, when one did.</param>
/// <param name="Parts">Every part file, in order, when the pak is split. Null for a whole archive.</param>
public sealed record V12PakSource(
    string Path,
    string Origin,
    V12GameDescriptor? Descriptor = null,
    IReadOnlyList<string>? Parts = null)
{
    /// <summary>The files to load: the parts in order, or the single archive.</summary>
    public IReadOnlyList<string> Files => Parts is { Count: > 0 } ? Parts : new[] { Path };

    /// <summary>True when the archive is split across several files.</summary>
    public bool IsSplit => Parts is { Count: > 1 };
}

/// <summary>
/// Decides which pak runs. Kept separate from <see cref="V12Host"/> so a shell
/// can log or display the choice before loading, and so the policy has one home
/// instead of living in every renderer's entry point.
///
/// Order: explicit argument, dev inbox, descriptor, executable-name fallback.
/// The dev inbox outranks the shipped build because that is what makes
/// <c>vcsdk serve --reload</c> replace a build without a reinstall.
/// </summary>
public static class V12PakResolver
{
    public const string DescriptorFileName = "game.json";
    public const string InboxFileName = "dev.v12pak";
    public const string InboxFolder = "v12inbox";

    /// <summary>
    /// Resolves the pak to run, or null when nothing is installed. Throws when
    /// an explicit path or a descriptor names something that does not exist,
    /// because silently falling back would hide a broken build.
    /// </summary>
    public static V12PakSource? Resolve(V12HostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.PakPath))
        {
            string path = Path.GetFullPath(options.PakPath);
            var parts = V12PakParts.DiscoverParts(path);

            if (parts.Count > 1)
                return new V12PakSource(parts[0], "argument", Parts: parts);

            if (V12PakParts.IsPartFile(parts[0]))
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(parts[0])}' is one part of a split pak, but its siblings are missing.");
            }

            if (!File.Exists(parts[0]))
                throw new FileNotFoundException($"The pak '{options.PakPath}' does not exist.", path);

            return new V12PakSource(parts[0], "argument");
        }

        foreach (string directory in InboxDirectories(options))
        {
            string candidate = Path.Combine(directory, InboxFileName);
            if (File.Exists(candidate))
                return new V12PakSource(Path.GetFullPath(candidate), "dev-inbox");
        }

        var (descriptor, descriptorPath) = ResolveDescriptor(options);
        if (descriptor is not null)
        {
            string baseDirectory = descriptorPath is not null
                ? Path.GetDirectoryName(Path.GetFullPath(descriptorPath)) ?? options.ContentDirectory ?? "."
                : options.ContentDirectory ?? ".";

            if (descriptor.Parts is { Count: > 0 } declared)
            {
                var resolved = new List<string>(declared.Count);
                foreach (string part in declared)
                {
                    string path = Path.GetFullPath(Path.Combine(baseDirectory, part));
                    if (!File.Exists(path))
                    {
                        throw new FileNotFoundException(
                            $"The descriptor names part '{part}', but '{path}' does not exist.", path);
                    }
                    resolved.Add(path);
                }

                return resolved.Count > 1
                    ? new V12PakSource(resolved[0], "descriptor", descriptor, resolved)
                    : new V12PakSource(resolved[0], "descriptor", descriptor);
            }

            if (descriptor.Pak is { Length: > 0 } pak)
            {
                string path = Path.GetFullPath(Path.Combine(baseDirectory, pak));

                // A split build may omit `parts`; the part files are enough.
                var parts = V12PakParts.DiscoverParts(path);
                if (parts.Count > 1)
                    return new V12PakSource(parts[0], "descriptor", descriptor, parts);

                if (!File.Exists(path))
                {
                    throw new FileNotFoundException(
                        $"The descriptor names '{pak}', but '{path}' does not exist.", path);
                }

                return new V12PakSource(path, "descriptor", descriptor);
            }
        }

        if (options.UseExecutableNameFallback && ProcessName() is { Length: > 0 } name)
        {
            foreach (string directory in ContentDirectories(options))
            {
                string candidate = Path.Combine(directory, name + ".v12pak");
                if (File.Exists(candidate))
                    return new V12PakSource(Path.GetFullPath(candidate), "executable-name");
            }
        }

        return null;
    }

    /// <summary>The descriptor this host would read, if any.</summary>
    public static (V12GameDescriptor? Descriptor, string? Path) ResolveDescriptor(V12HostOptions options)
    {
        if (options.Descriptor is not null)
            return (options.Descriptor, options.DescriptorPath);

        string path = options.DescriptorPath
            ?? Path.Combine(options.ContentDirectory ?? AppContext.BaseDirectory, DescriptorFileName);

        return (V12GameDescriptor.Load(path), File.Exists(path) ? path : null);
    }

    /// <summary>Default user directory, when a shell does not supply one.</summary>
    public static string DefaultUserDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(Path.GetTempPath(), "V12")
            : Path.Combine(appData, "V12");
    }

    /// <summary>
    /// Infers an Android package id from a Godot-style user directory
    /// (<c>.../&lt;package&gt;/files</c>), which is how the host finds the
    /// external inbox without a JNI call.
    /// </summary>
    public static string? PackageFromUserDirectory(string? userDirectory)
    {
        if (string.IsNullOrWhiteSpace(userDirectory)) return null;

        string trimmed = userDirectory.TrimEnd('/', '\\');
        string? parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrWhiteSpace(parent)) return null;

        string name = Path.GetFileName(parent);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static IEnumerable<string> ContentDirectories(V12HostOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ContentDirectory))
            yield return options.ContentDirectory!;

        string baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDirectory) &&
            !string.Equals(baseDirectory, options.ContentDirectory, StringComparison.OrdinalIgnoreCase))
            yield return baseDirectory;
    }

    private static IEnumerable<string> InboxDirectories(V12HostOptions options)
    {
        if (!options.UseDevInbox) yield break;

        if (!string.IsNullOrWhiteSpace(options.DevInbox))
            yield return options.DevInbox!;

        string? environment = Environment.GetEnvironmentVariable("V12_PAK_INBOX");
        if (!string.IsNullOrWhiteSpace(environment))
            yield return environment!;

        string user = string.IsNullOrWhiteSpace(options.UserDirectory)
            ? DefaultUserDirectory()
            : options.UserDirectory!;

        yield return Path.Combine(user, InboxFolder);

        if (!OperatingSystem.IsAndroid()) yield break;

        string? package = PackageFromUserDirectory(user);
        if (string.IsNullOrWhiteSpace(package)) yield break;

        yield return $"/storage/emulated/0/Android/data/{package}/files/{InboxFolder}";
        yield return $"/sdcard/Android/data/{package}/files/{InboxFolder}";
    }

    private static string? ProcessName()
    {
        string? executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
            return Path.GetFileNameWithoutExtension(executable);

        try { return Process.GetCurrentProcess().ProcessName; }
        catch { return null; }
    }
}
