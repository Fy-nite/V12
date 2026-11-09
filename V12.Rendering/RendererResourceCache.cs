namespace V12.Rendering;

/// <summary>
/// CPU-owned key to GPU-handle cache for V12-side resource descriptions. It owns
/// only the mapping and versions; the renderer owns the GPU realization.
///
/// Usage: call <see cref="BeginFrame"/> once per frame, request resources by a
/// stable key, and let the cache destroy entries that have been unused for a
/// full frame. Callers must not retain handles across an epoch change: the cache
/// recreates them transparently on the next request.
///
/// This type is not thread-safe. Call it from the same thread that owns renderer
/// resource creation.
/// </summary>
public sealed class RendererResourceCache : IDisposable
{
    private sealed class MeshEntry
    {
        public MeshHandle Handle;
        public long Version;
        public long LastUsedFrame;
    }

    private sealed class TextureEntry
    {
        public TextureHandle Handle;
        public long Version;
        public long LastUsedFrame;
    }

    private sealed class MaterialEntry
    {
        public MaterialHandle Handle;
        public ShaderHandle Shader;
        public long Version;
        public long LastUsedFrame;
    }

    private sealed class CustomShaderEntry
    {
        public ShaderHandle Handle;
        public long Version;
    }

    private readonly IRenderer _renderer;
    private readonly IRendererLifecycle? _lifecycle;
    private readonly Dictionary<string, MeshEntry> _meshes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextureEntry> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MaterialEntry> _materials = new(StringComparer.Ordinal);
    private readonly Dictionary<BuiltInShader, ShaderHandle> _shaders = new();
    private readonly Dictionary<string, CustomShaderEntry> _customShaders = new(StringComparer.Ordinal);

    private DeviceEpoch _epoch;
    private long _frame;
    private bool _disposed;

    public RendererResourceCache(IRenderer renderer)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _lifecycle = renderer as IRendererLifecycle;
        _epoch = _lifecycle?.CurrentEpoch ?? DeviceEpoch.Initial;
    }

    public DeviceEpoch CurrentEpoch => _epoch;
    public long CurrentFrame => _frame;
    public int MeshCount => _meshes.Count;
    public int TextureCount => _textures.Count;
    public int MaterialCount => _materials.Count;
    public int ShaderCount => _shaders.Count + _customShaders.Count;

    /// <summary>
    /// Starts a new cache frame. Resources unused on the previous frame are
    /// eligible for destruction; they are retired in dependency order. If the
    /// renderer device epoch changed, every old handle is dropped without
    /// touching the renderer (the handles are already invalid).
    /// </summary>
    public void BeginFrame()
    {
        ThrowIfDisposed();
        SyncEpoch();

        _frame++;
        if (_frame <= 1)
            return;

        long staleBefore = _frame - 1;
        DestroyUnusedMeshes(staleBefore);
        DestroyUnusedMaterials(staleBefore);
        DestroyUnusedTextures(staleBefore);
    }

    /// <summary>
    /// Returns a mesh handle. If <paramref name="version"/> differs from the
    /// cached version the mesh is updated in place.
    /// </summary>
    public MeshHandle GetOrCreateMesh(string key, in MeshData data, long version = 0)
    {
        ThrowIfDisposed();
        SyncEpoch();
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (_meshes.TryGetValue(key, out var entry))
        {
            entry.LastUsedFrame = _frame;
            if (entry.Version != version)
            {
                _renderer.UpdateMesh(entry.Handle, data);
                entry.Version = version;
            }
            return entry.Handle;
        }

        var handle = _renderer.CreateMesh(data);
        _meshes[key] = new MeshEntry
        {
            Handle = handle,
            Version = version,
            LastUsedFrame = _frame,
        };
        return handle;
    }

    public TextureHandle GetOrCreateTexture(string key, in TextureData data, long version = 0)
    {
        ThrowIfDisposed();
        SyncEpoch();
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (_textures.TryGetValue(key, out var entry))
        {
            entry.LastUsedFrame = _frame;
            if (entry.Version != version)
            {
                _renderer.UpdateTexture(entry.Handle, data);
                entry.Version = version;
            }
            return entry.Handle;
        }

        var handle = _renderer.CreateTexture(data);
        _textures[key] = new TextureEntry
        {
            Handle = handle,
            Version = version,
            LastUsedFrame = _frame,
        };
        return handle;
    }

    public ShaderHandle GetOrCreateShader(BuiltInShader builtIn)
    {
        ThrowIfDisposed();
        SyncEpoch();

        if (_shaders.TryGetValue(builtIn, out var handle))
            return handle;

        handle = _renderer.CreateShader(new ShaderProgram(builtIn));
        _shaders[builtIn] = handle;
        return handle;
    }

    /// <summary>
    /// Returns a custom shader handle keyed by a stable asset URI/hash. The
    /// renderer has no shader update entry point in v1, so a version change
    /// destroys and recreates the shader.
    /// </summary>
    public ShaderHandle GetOrCreateShader(string key, in ShaderProgram program, long version = 0)
    {
        ThrowIfDisposed();
        SyncEpoch();
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (_customShaders.TryGetValue(key, out var entry))
        {
            if (entry.Version == version)
                return entry.Handle;

            TryDestroy(() => _renderer.DestroyShader(entry.Handle));
            _customShaders.Remove(key);
        }

        var handle = _renderer.CreateShader(program);
        _customShaders[key] = new CustomShaderEntry { Handle = handle, Version = version };
        return handle;
    }

    public MaterialHandle GetOrCreateMaterial(
        string key,
        ShaderHandle shader,
        in MaterialDesc desc,
        long version = 0)
    {
        ThrowIfDisposed();
        SyncEpoch();
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (_materials.TryGetValue(key, out var entry))
        {
            entry.LastUsedFrame = _frame;

            if (entry.Shader.Value.Id != shader.Value.Id)
            {
                _renderer.DestroyMaterial(entry.Handle);
                _materials.Remove(key);
            }
            else
            {
                if (entry.Version != version)
                {
                    _renderer.UpdateMaterial(entry.Handle, desc);
                    entry.Version = version;
                }

                MarkMaterialTexturesUsed(desc);
                return entry.Handle;
            }
        }

        var handle = _renderer.CreateMaterial(shader, desc);
        _materials[key] = new MaterialEntry
        {
            Handle = handle,
            Shader = shader,
            Version = version,
            LastUsedFrame = _frame,
        };

        MarkMaterialTexturesUsed(desc);
        return handle;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SyncEpoch();

        foreach (var entry in _materials.Values)
            TryDestroy(() => _renderer.DestroyMaterial(entry.Handle));
        foreach (var entry in _textures.Values)
            TryDestroy(() => _renderer.DestroyTexture(entry.Handle));
        foreach (var entry in _meshes.Values)
            TryDestroy(() => _renderer.DestroyMesh(entry.Handle));
        foreach (var handle in _shaders.Values)
            TryDestroy(() => _renderer.DestroyShader(handle));
        foreach (var entry in _customShaders.Values)
            TryDestroy(() => _renderer.DestroyShader(entry.Handle));

        _materials.Clear();
        _textures.Clear();
        _meshes.Clear();
        _shaders.Clear();
        _customShaders.Clear();
    }

    private void SyncEpoch()
    {
        if (_lifecycle is null)
            return;

        var epoch = _lifecycle.CurrentEpoch;
        if (epoch == _epoch)
            return;

        _epoch = epoch;
        _meshes.Clear();
        _textures.Clear();
        _materials.Clear();
        _shaders.Clear();
        _customShaders.Clear();
    }

    private void DestroyUnusedMeshes(long staleBefore)
    {
        foreach (var key in _meshes.Keys.ToArray())
        {
            var entry = _meshes[key];
            if (entry.LastUsedFrame >= staleBefore)
                continue;

            TryDestroy(() => _renderer.DestroyMesh(entry.Handle));
            _meshes.Remove(key);
        }
    }

    private void DestroyUnusedMaterials(long staleBefore)
    {
        foreach (var key in _materials.Keys.ToArray())
        {
            var entry = _materials[key];
            if (entry.LastUsedFrame >= staleBefore)
                continue;

            TryDestroy(() => _renderer.DestroyMaterial(entry.Handle));
            _materials.Remove(key);
        }
    }

    private void DestroyUnusedTextures(long staleBefore)
    {
        foreach (var key in _textures.Keys.ToArray())
        {
            var entry = _textures[key];
            if (entry.LastUsedFrame >= staleBefore)
                continue;

            TryDestroy(() => _renderer.DestroyTexture(entry.Handle));
            _textures.Remove(key);
        }
    }

    private void MarkMaterialTexturesUsed(in MaterialDesc desc)
    {
        foreach (var binding in desc.Textures.Span)
        {
            if (binding.Texture.Value.Id == 0)
                continue;

            foreach (var entry in _textures.Values)
            {
                if (entry.Handle.Value.Id == binding.Texture.Value.Id)
                {
                    entry.LastUsedFrame = _frame;
                    break;
                }
            }
        }

        foreach (var parameter in desc.Parameters.Span)
        {
            if (parameter.Kind != MaterialValueKind.Texture ||
                parameter.TextureValue.Value.Id == 0)
                continue;

            foreach (var entry in _textures.Values)
            {
                if (entry.Handle.Value.Id == parameter.TextureValue.Value.Id)
                {
                    entry.LastUsedFrame = _frame;
                    break;
                }
            }
        }
    }

    private static void TryDestroy(Action destroy)
    {
        try
        {
            destroy();
        }
        catch (RendererContractException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RendererResourceCache));
    }
}
