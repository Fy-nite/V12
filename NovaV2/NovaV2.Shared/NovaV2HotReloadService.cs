using System;
using System.IO;
using System.Reflection;
using System.Threading;
using V12.Bindings;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.WorldML;

namespace NovaV2.Hosting;

/// <summary>
/// Development hot reload for a NovaV2 <c>--game</c> project.
///
/// It owns a collectible <see cref="CSharpScriptHost"/> for the project's C#
/// sources, starts it before the first world parse, and on every successful
/// recompile or world-file save it rebuilds the selected world. Component types
/// are resolved through <see cref="ComponentTypeResolver"/> so a reloaded type
/// wins over the previous generation still present in the AppDomain.
///
/// This is a scene reload, not an in-place patch: old components receive
/// <c>OnDetach</c>, the world tree is replaced, and local-only player/XR rigs
/// are recreated from the scene file. That resets a live network session, so
/// this mode is intended for local development.
/// </summary>
public sealed class NovaV2HotReloadService : IGameService, IDisposable
{
    private readonly GameRoot _root;
    private readonly World _world;
    private readonly string _worldPath;
    private readonly CSharpScriptHost? _scripts;
    private readonly int _debounceMs;

    private Assembly? _scriptAssembly;
    private DateTime _lastWorldWriteUtc;
    private DateTime _changeUtc = DateTime.MinValue;
    private bool _worldReloadRequested;
    private bool _disposed;

    public NovaV2HotReloadService(
        GameRoot root,
        World world,
        string worldPath,
        CSharpScriptHost? scripts,
        Assembly? initialScriptAssembly,
        int debounceMs = 200)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _worldPath = Path.GetFullPath(worldPath);
        _scripts = scripts;
        _scriptAssembly = initialScriptAssembly;
        _debounceMs = Math.Max(0, debounceMs);
        _lastWorldWriteUtc = GetWriteTime();
    }

    /// <summary>Raised on the game thread after the world tree has been replaced.</summary>
    public event Action? WorldReloaded;

    /// <summary>Raised on the game thread after a new script assembly is loaded.</summary>
    public event Action<Assembly>? ScriptsReloaded;

    public string WorldPath => _worldPath;
    public string? ScriptDirectory => _scripts?.SourceDirectory;
    public Assembly? ScriptAssembly => _scriptAssembly;

    /// <summary>Call once after the initial world parse and after registering the service.</summary>
    public void Start()
    {
        _root.Registry.RegisterOrReplace("NovaV2HotReload", this);
        _scripts?.Watch();
        GameRoot.Log.Information(
            "[HotReload] Watching '{World}'{Scripts}",
            _worldPath,
            _scripts is null ? "" : $" and C# sources in '{_scripts.SourceDirectory}'");
    }

    void IGameService.Initialize(GameRoot g) { }
    void IGameService.Update(GameRoot gameRoot) { }

    public void Update(float deltaTime)
    {
        if (_disposed) return;

        PumpScripts();
        CheckWorldFile();

        if (_worldReloadRequested &&
            (DateTime.UtcNow - _changeUtc).TotalMilliseconds >= _debounceMs)
        {
            ReloadWorld();
        }
    }

    private void PumpScripts()
    {
        if (_scripts is null) return;

        _scripts.Pump();
        var current = _scripts.Assembly;
        if (current is null || ReferenceEquals(current, _scriptAssembly))
            return;

        // LastResult is the most recent attempt. A failed compile leaves the
        // previous Assembly in place, so a non-null changed assembly means the
        // compile succeeded.
        if (_scripts.LastResult is { Success: false })
            return;

        var previous = _scriptAssembly;
        if (previous is not null)
            _root.Gamepaks.RemoveFromAssembly(previous);

        _scriptAssembly = current;
        _root.LoadGamepacksFromAssembly(current);

        GameRoot.Log.Information(
            "[HotReload] C# scripts reloaded: {Assembly}",
            current.GetName().Name);
        ScriptsReloaded?.Invoke(current);
        RequestWorldReload();
    }

    private void CheckWorldFile()
    {
        var write = GetWriteTime();
        if (write != _lastWorldWriteUtc && write != DateTime.MinValue)
        {
            _lastWorldWriteUtc = write;
            RequestWorldReload();
        }
    }

    private void RequestWorldReload()
    {
        _worldReloadRequested = true;
        _changeUtc = DateTime.UtcNow;
    }

    private void ReloadWorld()
    {
        _worldReloadRequested = false;

        try
        {
            var xml = ReadFileWithRetry(_worldPath);
            if (string.IsNullOrWhiteSpace(xml))
                throw new IOException("the world file is empty or locked");

            var contents = new WorldMLParser().Parse(xml);
            var incoming = new World(contents.Name ?? _world.WorldName);
            incoming.AddElement(contents);

            var session = _root.FindWorldSession(_world);
            session?.Dirty.UntrackWorld(_world);
            WorldContentSwap.Replace(_world, incoming);
            session?.Dirty.TrackWorld(_world);
            GameRoot.Log.Information("[HotReload] World reloaded from '{Path}'", _worldPath);
            WorldReloaded?.Invoke();
        }
        catch (Exception ex)
        {
            GameRoot.Log.Warning(ex, "[HotReload] World reload failed for '{Path}'", _worldPath);
        }
    }

    private DateTime GetWriteTime()
    {
        try { return File.GetLastWriteTimeUtc(_worldPath); }
        catch { return DateTime.MinValue; }
    }

    private static string? ReadFileWithRetry(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) { Thread.Sleep(40); }
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scripts?.Dispose();
    }
}
