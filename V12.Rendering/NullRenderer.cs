namespace V12.Rendering;

/// <summary>
/// Reference renderer: records frames, validates handles/epochs/dependencies and
/// discards GPU work. It is the headless/test implementation of the contract.
/// </summary>
public sealed class NullRenderer : IRenderer, IRendererLifecycle
{
    private sealed class MeshRecord
    {
        public ResourceHandle Handle;
        public MeshData Data = MeshData.Empty;
    }

    private sealed class TextureRecord
    {
        public ResourceHandle Handle;
        public TextureData? Data;
        public RenderTargetHandle? Owner;
    }

    private sealed class ShaderRecord
    {
        public ResourceHandle Handle;
        public ShaderProgram Program;
    }

    private sealed class MaterialRecord
    {
        public ResourceHandle Handle;
        public ShaderHandle Shader;
        public MaterialDesc Desc;
        public MaterialValue[] Parameters = Array.Empty<MaterialValue>();
        public MaterialTextureBinding[] Textures = Array.Empty<MaterialTextureBinding>();
    }

    private sealed class RenderTargetRecord
    {
        public ResourceHandle Handle;
        public RenderTargetDesc Desc;
        public TextureHandle Texture;
    }

    private readonly object _lock = new();
    private readonly Dictionary<uint, MeshRecord> _meshes = new();
    private readonly Dictionary<uint, TextureRecord> _textures = new();
    private readonly Dictionary<uint, ShaderRecord> _shaders = new();
    private readonly Dictionary<uint, MaterialRecord> _materials = new();
    private readonly Dictionary<uint, RenderTargetRecord> _targets = new();
    private readonly List<string> _diagnostics = new();

    private DeviceEpoch _epoch = DeviceEpoch.Initial;
    private bool _lost;
    private bool _disposed;
    private uint _nextId;
    private RenderFrameLease? _lastFrame;

    public RendererCapabilities Capabilities { get; } = RendererCapabilities.All;

    public DeviceEpoch CurrentEpoch
    {
        get { lock (_lock) return _epoch; }
    }

    /// <summary>The latest accepted lease. Owned by this renderer until replaced or disposed.</summary>
    public RenderFrameLease? LastFrame
    {
        get { lock (_lock) return _lastFrame; }
    }

    public IReadOnlyList<string> Diagnostics
    {
        get { lock (_lock) return _diagnostics.ToArray(); }
    }

    public event Action<RendererDeviceEvent>? DeviceEvent;

    // ── Frame submission ────────────────────────────────────────────────────

    public void Submit(RenderFrameLease frame)
    {
        if (frame is null) throw new ArgumentNullException(nameof(frame));

        RenderFrameLease? previous;
        lock (_lock)
        {
            RequireAvailableLocked();

            if (frame.Epoch != _epoch)
                RejectLocked($"Frame {frame.Sequence} belongs to {frame.Epoch}, current epoch is {_epoch}.");

            var f = frame.Frame;

            foreach (var view in f.Views.Span)
            {
                if (view.Target is { } target)
                    RequireLocked(target.Value, ResourceKind.RenderTarget);
            }

            foreach (var item in f.Items.Span)
            {
                RequireLocked(item.Mesh.Value, ResourceKind.Mesh);
                RequireLocked(item.Material.Value, ResourceKind.Material);

                if (item.OverridesIndex < -1 || item.OverridesIndex >= f.DrawOverrides.Length)
                    RejectLocked($"Draw item for element {item.ElementId} has overrides index {item.OverridesIndex} outside the frame's {f.DrawOverrides.Length} override tables.");
            }

            if (f.Environment?.Sky.Skybox is { } skybox)
                RequireLocked(skybox.Value, ResourceKind.Texture);

            previous = _lastFrame;
            _lastFrame = frame;
        }

        previous?.Release();
    }

    // ── Meshes ──────────────────────────────────────────────────────────────

    public MeshHandle CreateMesh(in MeshData data)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            var handle = NewHandleLocked();
            _meshes[handle.Id] = new MeshRecord { Handle = handle, Data = data };
            return new MeshHandle(handle);
        }
    }

    public void UpdateMesh(MeshHandle mesh, in MeshData data)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            RequireLocked(mesh.Value, ResourceKind.Mesh);
            _meshes[mesh.Value.Id].Data = data;
        }
    }

    public void DestroyMesh(MeshHandle mesh)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            RequireLocked(mesh.Value, ResourceKind.Mesh);
            _meshes.Remove(mesh.Value.Id);
        }
    }

    // ── Textures ────────────────────────────────────────────────────────────

    public TextureHandle CreateTexture(in TextureData data)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            var handle = NewHandleLocked();
            _textures[handle.Id] = new TextureRecord { Handle = handle, Data = data };
            return new TextureHandle(handle);
        }
    }

    public void UpdateTexture(TextureHandle texture, in TextureData data)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            RequireLocked(texture.Value, ResourceKind.Texture);
            var record = _textures[texture.Value.Id];
            if (record.Owner is not null)
                RejectLocked($"Texture {texture.Value} is owned by a render target; resize the target instead.");
            record.Data = data;
        }
    }

    public void DestroyTexture(TextureHandle texture)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            RequireLocked(texture.Value, ResourceKind.Texture);

            var record = _textures[texture.Value.Id];
            if (record.Owner is not null)
                RejectLocked($"Texture {texture.Value} is owned by render target {record.Owner.Value.Value}; destroy the target instead.");

            if (IsTextureReferencedLocked(texture.Value.Id))
                RejectLocked($"Texture {texture.Value} is still referenced by a material.");

            _textures.Remove(texture.Value.Id);
        }
    }

    // ── Shaders and materials ───────────────────────────────────────────────

    public ShaderHandle CreateShader(in ShaderProgram program)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            var handle = NewHandleLocked();
            _shaders[handle.Id] = new ShaderRecord { Handle = handle, Program = program };
            return new ShaderHandle(handle);
        }
    }

    public void DestroyShader(ShaderHandle shader)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            RequireLocked(shader.Value, ResourceKind.Shader);

            foreach (var material in _materials.Values)
            {
                if (material.Shader.Value.Id == shader.Value.Id)
                    RejectLocked($"Shader {shader.Value} is still referenced by material {material.Handle}.");
            }

            _shaders.Remove(shader.Value.Id);
        }
    }

    public MaterialHandle CreateMaterial(ShaderHandle shader, in MaterialDesc desc)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            RequireLocked(shader.Value, ResourceKind.Shader);

            var parameters = desc.Parameters.ToArray();
            var textures = desc.Textures.ToArray();
            ValidateMaterialBindingsLocked(parameters, textures);

            var handle = NewHandleLocked();
            _materials[handle.Id] = new MaterialRecord
            {
                Handle = handle,
                Shader = shader,
                Desc = desc with { Parameters = parameters, Textures = textures },
                Parameters = parameters,
                Textures = textures,
            };
            return new MaterialHandle(handle);
        }
    }

    public void UpdateMaterial(MaterialHandle material, in MaterialDesc desc)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            RequireLocked(material.Value, ResourceKind.Material);

            var parameters = desc.Parameters.ToArray();
            var textures = desc.Textures.ToArray();
            ValidateMaterialBindingsLocked(parameters, textures);

            var record = _materials[material.Value.Id];
            record.Desc = desc with { Parameters = parameters, Textures = textures };
            record.Parameters = parameters;
            record.Textures = textures;
        }
    }

    public void DestroyMaterial(MaterialHandle material)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            RequireLocked(material.Value, ResourceKind.Material);
            _materials.Remove(material.Value.Id);
        }
    }

    // ── Render targets ──────────────────────────────────────────────────────

    public RenderTargetHandle CreateRenderTarget(in RenderTargetDesc desc)
    {
        lock (_lock)
        {
            RequireAvailableLocked();

            var targetHandle = NewHandleLocked();
            var textureHandle = NewHandleLocked();
            var target = new RenderTargetHandle(targetHandle);

            _textures[textureHandle.Id] = new TextureRecord
            {
                Handle = textureHandle,
                Owner = target,
            };
            _targets[targetHandle.Id] = new RenderTargetRecord
            {
                Handle = targetHandle,
                Desc = desc,
                Texture = new TextureHandle(textureHandle),
            };

            return target;
        }
    }

    public void ResizeRenderTarget(RenderTargetHandle target, int width, int height)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            RequireLocked(target.Value, ResourceKind.RenderTarget);

            var record = _targets[target.Value.Id];
            record.Desc = record.Desc with { Width = width, Height = height };
        }
    }

    public void DestroyRenderTarget(RenderTargetHandle target)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            RequireLocked(target.Value, ResourceKind.RenderTarget);

            var record = _targets[target.Value.Id];
            if (IsTextureReferencedLocked(record.Texture.Value.Id))
                RejectLocked($"Render target {target.Value} texture is still referenced by a material.");

            _targets.Remove(target.Value.Id);
            _textures.Remove(record.Texture.Value.Id);
        }
    }

    public TextureHandle GetRenderTargetTexture(RenderTargetHandle target)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            RequireLocked(target.Value, ResourceKind.RenderTarget);
            return _targets[target.Value.Id].Texture;
        }
    }

    public ValueTask<ReadbackResult> RequestReadbackAsync(
        RenderTargetHandle? target,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            if (target is { } value)
                RequireLocked(value.Value, ResourceKind.RenderTarget);
        }

        return new ValueTask<ReadbackResult>(
            new ReadbackResult(true, 0, 0, TextureFormat.Rgba8Unorm, ReadOnlyMemory<byte>.Empty));
    }

    // ── Device lifecycle ────────────────────────────────────────────────────

    public void LoseDevice()
    {
        DeviceEpoch epoch;
        lock (_lock)
        {
            _lost = true;
            epoch = _epoch;
        }

        DeviceEvent?.Invoke(new RendererDeviceEvent(RendererDeviceEventKind.Lost, epoch));
    }

    public void RecoverDevice()
    {
        DeviceEpoch epoch;
        lock (_lock)
        {
            _epoch = new DeviceEpoch(_epoch.Value + 1);
            _lost = false;
            ClearResourcesLocked();
            epoch = _epoch;
        }

        DeviceEvent?.Invoke(new RendererDeviceEvent(RendererDeviceEventKind.Recovering, epoch));
        DeviceEvent?.Invoke(new RendererDeviceEvent(RendererDeviceEventKind.Ready, epoch));
    }

    public void Dispose()
    {
        RenderFrameLease? last;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            last = _lastFrame;
            _lastFrame = null;
            ClearResourcesLocked();
        }

        last?.Release();
    }

    // ── Validation helpers ──────────────────────────────────────────────────

    private ResourceHandle NewHandleLocked() => new(++_nextId, 1, _epoch);

    private void RequireAvailableLocked()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NullRenderer));
        if (_lost) throw new RendererContractException("The renderer device is lost; no new work is accepted.");
    }

    private void RequireLocked(ResourceHandle handle, ResourceKind kind)
    {
        if (TryValidateLocked(handle, kind, out var error)) return;
        RejectLocked(error);
    }

    private bool TryValidateLocked(ResourceHandle handle, ResourceKind kind, out string error)
    {
        if (handle.Id == 0)
        {
            error = $"{kind} handle is empty.";
            return false;
        }

        if (handle.Epoch != _epoch)
        {
            error = $"{kind} {handle} belongs to {handle.Epoch}, current epoch is {_epoch}.";
            return false;
        }

        bool exists = kind switch
        {
            ResourceKind.Mesh => _meshes.TryGetValue(handle.Id, out var mesh) && mesh.Handle.Generation == handle.Generation,
            ResourceKind.Texture => _textures.TryGetValue(handle.Id, out var texture) && texture.Handle.Generation == handle.Generation,
            ResourceKind.Shader => _shaders.TryGetValue(handle.Id, out var shader) && shader.Handle.Generation == handle.Generation,
            ResourceKind.Material => _materials.TryGetValue(handle.Id, out var material) && material.Handle.Generation == handle.Generation,
            ResourceKind.RenderTarget => _targets.TryGetValue(handle.Id, out var target) && target.Handle.Generation == handle.Generation,
            _ => false,
        };

        if (!exists)
        {
            error = $"{kind} {handle} is stale or unknown.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void ValidateMaterialBindingsLocked(
        MaterialValue[] parameters,
        MaterialTextureBinding[] textures)
    {
        foreach (var binding in textures)
            RequireLocked(binding.Texture.Value, ResourceKind.Texture);

        foreach (var parameter in parameters)
        {
            if (parameter.Kind == MaterialValueKind.Texture)
                RequireLocked(parameter.TextureValue.Value, ResourceKind.Texture);
        }
    }

    private bool IsTextureReferencedLocked(uint textureId)
    {
        foreach (var material in _materials.Values)
        {
            foreach (var binding in material.Textures)
            {
                if (binding.Texture.Value.Id == textureId) return true;
            }

            foreach (var parameter in material.Parameters)
            {
                if (parameter.Kind == MaterialValueKind.Texture && parameter.TextureValue.Value.Id == textureId)
                    return true;
            }
        }

        return false;
    }

    private void RejectLocked(string message)
    {
        _diagnostics.Add(message);
        throw new RendererContractException(message);
    }

    private void ClearResourcesLocked()
    {
        _meshes.Clear();
        _textures.Clear();
        _shaders.Clear();
        _materials.Clear();
        _targets.Clear();
    }
}
