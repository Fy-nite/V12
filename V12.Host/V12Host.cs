using V12.Core;
using V12.Pak;

namespace V12.Hosting;

/// <summary>
/// Renderer-agnostic game host. It owns one <see cref="GameRoot"/>, decides which
/// pak runs (via <see cref="V12PakResolver"/>), loads it, starts its gamepaks and
/// initializes the root.
///
/// A shell (a Godot app, a MonoGame app, a libgodot embedder, a console) owns the
/// process and the frame pump, registers an <see cref="V12.Rendering.IRenderer"/>
/// if it has one, and calls <see cref="Start"/> once. With no renderer the root
/// runs headless, which the engine already supports.
///
/// Only one host is expected per process, because <see cref="GameRoot.Instance"/>
/// is a singleton and gamepaks reach for it.
/// </summary>
public sealed class V12Host : IDisposable
{
    private readonly V12HostOptions _options;
    private bool _started;
    private bool _disposed;

    public V12Host(V12HostOptions? options = null)
    {
        _options = options ?? new V12HostOptions();

        if (_options.ConfigureLogging && !GameRoot.LoggingConfigured)
            GameRoot.ConfigureLogging();

        Root = new GameRoot();

        if (_options.Renderer is not null)
            Root.Registry.RegisterOrReplace("IRenderer", _options.Renderer);
    }

    /// <summary>The root this host owns. Valid immediately, before <see cref="Start"/>.</summary>
    public GameRoot Root { get; }

    /// <summary>What was loaded, or null when no pak was found.</summary>
    public V12PakLoadResult? Pak { get; private set; }

    /// <summary>Which pak was chosen and why.</summary>
    public V12PakSource? Source { get; private set; }

    /// <summary>The descriptor that named the pak, when one did.</summary>
    public V12GameDescriptor? Descriptor => Source?.Descriptor;

    /// <summary>True when no renderer backend was supplied.</summary>
    public bool IsHeadless => _options.Renderer is null;

    public bool IsRunning => _started && !_disposed;

    /// <summary>
    /// Resolve, load and start. Safe to call once. A missing pak is not an
    /// error: the root initializes empty so a shell can show its own UI.
    /// </summary>
    public V12Host Start()
    {
        ThrowIfDisposed();
        if (_started)
            throw new InvalidOperationException("V12Host already started.");

        _started = true;

        Source = V12PakResolver.Resolve(_options);
        if (Source is not null)
        {
            if (Source.IsSplit && _options.VerifyParts)
                VerifyParts(Source);

            LogInfo("Running pak '{Pak}' (source: {Origin})", Source.Path, Source.Origin);
            Pak = Source.IsSplit
                ? V12PakLoader.LoadParts(Source.Files, _options.PakOptions, Root)
                : V12PakLoader.Load(Source.Path, _options.PakOptions, Root);

            SelectWorld();

            if (Root.Gamepaks.Gamepaks.Count > 0)
            {
                Root.Gamepaks.InitializeAll();
                Root.Gamepaks.StartAll();
            }
        }
        else
        {
            LogInfo("No pak found; starting an empty root.");
        }

        if (_options.InitializeRoot)
            Root.Initialize();

        return this;
    }

    private static void VerifyParts(V12PakSource source)
    {
        if (source.Descriptor?.PartInfo is not { Count: > 0 } partInfo)
        {
            LogWarning(
                "VerifyParts is enabled, but '{Pak}' has no recorded part hashes; skipping verification.",
                Path.GetFileName(source.Path));
            return;
        }

        V12PakParts.Verify(source.Files, partInfo);
    }

    private void SelectWorld()    {
        if (Pak is null || Pak.LoadedWorlds.Count == 0)
            return;

        string? requested = Descriptor?.World;
        if (!string.IsNullOrWhiteSpace(requested) &&
            Pak.LoadedWorlds.FirstOrDefault(w =>
                string.Equals(w.WorldName, requested, StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            Root.SelectWorld(named);
            return;
        }

        Root.SelectWorld(Pak.LoadedWorlds[0]);
    }

    /// <summary>
    /// Unload the pak and stop the root. Called by <see cref="Dispose"/>; a shell
    /// restarting the game calls it first.
    /// </summary>
    public void Shutdown()
    {
        if (_disposed) return;
        _disposed = true;

        try { Pak?.Unload(); }
        catch (Exception ex) { LogWarning("Pak unload failed: {Message}", ex.Message); }

        Pak = null;

        try { Root.Shutdown(); }
        catch (Exception ex) { LogWarning("Root shutdown failed: {Message}", ex.Message); }
    }

    public void Dispose() => Shutdown();

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(V12Host));
    }

    private static void LogInfo(string message, params object?[] args)
    {
        if (GameRoot.LoggingConfigured) GameRoot.Log.Information(message, args);
    }

    private static void LogWarning(string message, params object?[] args)
    {
        if (GameRoot.LoggingConfigured) GameRoot.Log.Warning(message, args);
    }
}
