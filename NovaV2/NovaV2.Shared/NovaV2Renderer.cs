using Godot;
using V12.Rendering;
using GodotEnvironment = Godot.Environment;

namespace NovaV2.Rendering;

/// <summary>
/// Godot 4 implementation of the backend-free <see cref="IRenderer"/> contract.
/// All members must be called on the Godot main thread: resource creation,
/// node mutation and presentation are Godot-side operations.
///
/// v1 scope: flat world transforms, one surface per mesh, 2D textures, built-in
/// StandardMaterial3D materials, cameras/lights/environment and SubViewport
/// render targets. UI, text, skinning, instancing and custom shaders are not
/// implemented yet.
/// </summary>
public sealed class NovaV2Renderer : IRenderer, IRendererLifecycle
{
    private sealed class MeshRecord
    {
        public ResourceHandle Handle;
        public ArrayMesh Mesh = null!;
    }

    private sealed class TextureRecord
    {
        public ResourceHandle Handle;
        public Texture2D Texture = null!;
        public TextureColorSpace ColorSpace;
        public RenderTargetHandle? Owner;
    }

    private sealed class ShaderRecord
    {
        public ResourceHandle Handle;
        public ShaderProgram Program;
        public Shader? GodotShader;
        public bool IsCustom;
    }

    private sealed class MaterialRecord
    {
        public ResourceHandle Handle;
        public ShaderHandle Shader;
        public MaterialDesc Desc;
        public Material Material = null!;
        public StandardMaterial3D? Standard;
        public ShaderMaterial? Custom;
        public MaterialValue[] Parameters = Array.Empty<MaterialValue>();
        public MaterialTextureBinding[] Textures = Array.Empty<MaterialTextureBinding>();
    }

    private sealed class RenderTargetRecord
    {
        public ResourceHandle Handle;
        public RenderTargetDesc Desc;
        public SubViewport Viewport = null!;
        public TextureHandle Texture;
    }

    private sealed class ItemNode
    {
        public MeshInstance3D Node = null!;
        public MeshHandle Mesh;
        public MaterialHandle Material;
    }

    private readonly object _lock = new();
    private readonly Dictionary<uint, MeshRecord> _meshes = new();
    private readonly Dictionary<uint, TextureRecord> _textures = new();
    private readonly Dictionary<uint, ShaderRecord> _shaders = new();
    private readonly Dictionary<uint, MaterialRecord> _materials = new();
    private readonly Dictionary<uint, RenderTargetRecord> _targets = new();
    private readonly Dictionary<(long Id, int Index), ItemNode> _items = new();
    private readonly Dictionary<(long Id, StereoEye Eye), Camera3D> _cameras = new();
    private readonly Dictionary<long, Light3D> _lights = new();
    private EnvironmentData _currentEnvironment = EnvironmentData.Default;

    /// <summary>When true the renderer leaves the view to the XR headset camera
    /// and does not create snapshot cameras.</summary>
    public bool XREnabled { get; set; }
    private readonly Dictionary<(long Id, int Index), Material> _overrideMaterials = new();
    private readonly CanvasLayer _presentLayer;
    private readonly Dictionary<uint, TextureRect> _presentedTargets = new();
    private readonly List<string> _diagnostics = new();

    private readonly Node3D _root;
    private readonly WorldEnvironment _worldEnvironment;
    private readonly GodotEnvironment _environment;
    private PanoramaSkyMaterial? _panoramaSky;
    private Sky? _sky;

    private DeviceEpoch _epoch = DeviceEpoch.Initial;
    private bool _lost;
    private bool _disposed;
    private uint _nextId;

    public NovaV2Renderer(Node parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        _root = new Node3D { Name = "NovaV2Renderer" };
        parent.AddChild(_root);

        _presentLayer = new CanvasLayer { Name = "NovaV2Present", Layer = 100 };
        _root.AddChild(_presentLayer);

        _environment = new GodotEnvironment();
        _worldEnvironment = new WorldEnvironment
        {
            Name = "NovaV2Environment",
            Environment = _environment,
        };
        _root.AddChild(_worldEnvironment);
    }

    public RendererCapabilities Capabilities { get; } = new(
        RendererFeature.Shadows |
        RendererFeature.Fog |
        RendererFeature.VolumetricFog |
        RendererFeature.Sky |
        RendererFeature.RenderTargets |
        RendererFeature.Readback |
        RendererFeature.Tonemap |
        RendererFeature.Bloom |
        RendererFeature.Ssao |
        RendererFeature.Ssil |
        RendererFeature.Ssr);

    public DeviceEpoch CurrentEpoch
    {
        get { lock (_lock) return _epoch; }
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

        lock (_lock)
        {
            RequireAvailableLocked();
            if (frame.Epoch != _epoch)
                RejectLocked($"Frame {frame.Sequence} belongs to {frame.Epoch}, current epoch is {_epoch}.");

            var renderFrame = frame.Frame;
            ValidateFrameLocked(renderFrame);
            ApplyFrameLocked(renderFrame);
        }

        frame.Release();
    }

    // ── Meshes ──────────────────────────────────────────────────────────────

    public MeshHandle CreateMesh(in MeshData data)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            ArrayMesh mesh = GodotInterop.ToArrayMesh(data);
            var handle = NewHandleLocked();
            _meshes[handle.Id] = new MeshRecord { Handle = handle, Mesh = mesh };
            return new MeshHandle(handle);
        }
    }

    public void UpdateMesh(MeshHandle mesh, in MeshData data)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            RequireLocked(mesh.Value, ResourceKind.Mesh);
            _meshes[mesh.Value.Id].Mesh = GodotInterop.ToArrayMesh(data);
        }
    }

    public void DestroyMesh(MeshHandle mesh)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            RequireLocked(mesh.Value, ResourceKind.Mesh);

            foreach (var item in _items.Values)
            {
                if (item.Mesh.Value.Id == mesh.Value.Id)
                    item.Node.Mesh = null;
            }

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
            _textures[handle.Id] = new TextureRecord
            {
                Handle = handle,
                Texture = BuildTextureLocked(data),
                ColorSpace = data.ColorSpace,
            };
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

            record.Texture = BuildTextureLocked(data);
            record.ColorSpace = data.ColorSpace;
            RefreshMaterialsUsingTextureLocked(texture.Value.Id);
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

            var record = new ShaderRecord { Handle = default, Program = program };
            bool custom = program.SourceLanguage is ShaderSourceLanguage.GodotSpatial or ShaderSourceLanguage.GodotCanvasItem;
            if (custom)
            {
                if (string.IsNullOrWhiteSpace(program.Source))
                    RejectLocked($"Shader '{program.AssetUri ?? "<custom>"}' has no source for {program.SourceLanguage}.");

                record.IsCustom = true;
                record.GodotShader = new Shader { Code = program.Source };
            }
            else if (program.BuiltIn == BuiltInShader.None)
            {
                RejectLocked("Custom shader is missing a supported source language.");
            }

            var handle = NewHandleLocked();
            record.Handle = handle;
            _shaders[handle.Id] = record;
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
            var shaderRecord = _shaders[shader.Value.Id];
            StandardMaterial3D? standard = null;
            ShaderMaterial? custom = null;
            Material material;

            if (shaderRecord.IsCustom)
            {
                custom = new ShaderMaterial { Shader = shaderRecord.GodotShader };
                material = custom;
            }
            else
            {
                standard = new StandardMaterial3D();
                material = standard;
            }

            ApplyMaterialLocked(material, shader, desc, parameters, textures);

            _materials[handle.Id] = new MaterialRecord
            {
                Handle = handle,
                Shader = shader,
                Desc = desc with { Parameters = parameters, Textures = textures },
                Material = material,
                Standard = standard,
                Custom = custom,
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
            ApplyMaterialLocked(record.Material, record.Shader, record.Desc, parameters, textures);
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

            int width = Math.Max(1, desc.Width);
            int height = Math.Max(1, desc.Height);

            var targetHandle = NewHandleLocked();
            var textureHandle = NewHandleLocked();
            var target = new RenderTargetHandle(targetHandle);

            var viewport = new SubViewport
            {
                Name = $"NovaV2Target_{targetHandle.Id}",
                Size = new Vector2I(width, height),
                TransparentBg = desc.Transparent,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                OwnWorld3D = desc.OwnWorld3D,
                HandleInputLocally = false,
            };
            _root.AddChild(viewport);

            _textures[textureHandle.Id] = new TextureRecord
            {
                Handle = textureHandle,
                Texture = viewport.GetTexture(),
                ColorSpace = TextureColorSpace.Linear,
                Owner = target,
            };

            _targets[targetHandle.Id] = new RenderTargetRecord
            {
                Handle = targetHandle,
                Desc = desc with { Width = width, Height = height },
                Viewport = viewport,
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
            int w = Math.Max(1, width);
            int h = Math.Max(1, height);
            record.Viewport.Size = new Vector2I(w, h);
            record.Desc = record.Desc with { Width = w, Height = h };
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

            foreach (var key in _cameras.Keys.ToArray())
            {
                var camera = _cameras[key];
                if (camera.GetParent() == record.Viewport)
                {
                    camera.QueueFree();
                    _cameras.Remove(key);
                }
            }

            foreach (var key in _items.Keys.ToArray())
            {
                var item = _items[key];
                if (item.Node.GetParent() == record.Viewport)
                {
                    item.Node.QueueFree();
                    _items.Remove(key);
                    _overrideMaterials.Remove(key);
                }
            }

            if (_presentedTargets.TryGetValue(target.Value.Id, out var presented))
            {
                if (GodotObject.IsInstanceValid(presented))
                    presented.QueueFree();
                _presentedTargets.Remove(target.Value.Id);
            }

            record.Viewport.QueueFree();
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

    /// <summary>
    /// Presents a render target as a full-screen overlay. This is Nova-specific
    /// presentation plumbing used for the screen-space UI viewport.
    /// </summary>
    public void PresentRenderTarget(RenderTargetHandle target)
    {
        lock (_lock)
        {
            RequireAvailableLocked();
            RequireLocked(target.Value, ResourceKind.RenderTarget);

            var record = _targets[target.Value.Id];
            if (!_presentedTargets.TryGetValue(target.Value.Id, out var rect) ||
                !GodotObject.IsInstanceValid(rect))
            {
                rect = new TextureRect
                {
                    Name = $"NovaV2Present_{target.Value.Id}",
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.Scale,
                };
                _presentLayer.AddChild(rect);
                _presentedTargets[target.Value.Id] = rect;
            }

            rect.Texture = record.Viewport.GetTexture();
            rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            rect.Visible = true;
        }
    }

    public ValueTask<ReadbackResult> RequestReadbackAsync(
        RenderTargetHandle? target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            RequireAvailableLocked();

            if (target is null)
            {
                return new ValueTask<ReadbackResult>(
                    new ReadbackResult(false, 0, 0, TextureFormat.Rgba8Unorm, ReadOnlyMemory<byte>.Empty,
                        "NovaV2 readback requires an explicit render target."));
            }

            RequireLocked(target.Value.Value, ResourceKind.RenderTarget);
            var record = _targets[target.Value.Value.Id];

            Image? image = record.Viewport.GetTexture()?.GetImage();
            if (image is null)
            {
                return new ValueTask<ReadbackResult>(
                    new ReadbackResult(false, 0, 0, TextureFormat.Rgba8Unorm, ReadOnlyMemory<byte>.Empty,
                        "The render target has no readable image."));
            }

            if (image.IsCompressed())
                image.Decompress();

            if (image.GetFormat() != Image.Format.Rgba8)
                image.Convert(Image.Format.Rgba8);

            return new ValueTask<ReadbackResult>(
                new ReadbackResult(
                    true,
                    image.GetWidth(),
                    image.GetHeight(),
                    TextureFormat.Rgba8Unorm,
                    image.GetData()));
        }
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
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            ClearResourcesLocked();
            _root.QueueFree();
        }
    }

    // ── Frame application ───────────────────────────────────────────────────

    private void ApplyFrameLocked(in RenderFrame frame)
    {
        var viewportTargets = new Dictionary<long, SubViewport>();
        _currentEnvironment = frame.Environment ?? EnvironmentData.Default;
        ApplyViewsLocked(frame, viewportTargets);
        ApplyItemsLocked(frame, viewportTargets);
        ApplyLightsLocked(frame);
        ApplyEnvironmentLocked(_currentEnvironment);
    }

    private void ApplyViewsLocked(in RenderFrame frame, Dictionary<long, SubViewport> viewportTargets)
    {
        // In XR the headset camera owns the view; snapshot cameras would fight
        // it for Current and break stereo.
        if (XREnabled)
        {
            foreach (var camera in _cameras.Values)
                if (GodotObject.IsInstanceValid(camera))
                    camera.QueueFree();
            _cameras.Clear();
            return;
        }

        var active = new HashSet<(long, StereoEye)>();

        foreach (var view in frame.Views.Span)
        {
            var key = (view.Id, view.Eye);
            active.Add(key);

            if (!_cameras.TryGetValue(key, out var camera) || !GodotObject.IsInstanceValid(camera))
            {
                camera = new Camera3D { Name = $"NovaV2View_{view.Id}_{view.Eye}" };
                _root.AddChild(camera);
                _cameras[key] = camera;
            }

            Node parent = _root;
            if (view.Target is { } targetHandle)
            {
                var target = _targets[targetHandle.Value.Id];
                parent = target.Viewport;
                viewportTargets[view.Id] = target.Viewport;
            }

            if (camera.GetParent() != parent)
                camera.Reparent(parent);

            camera.GlobalTransform = GodotInterop.ViewToCameraTransform(view.View);
            camera.Near = Math.Max(0.001f, view.ProjectionMetadata.Near);
            camera.Far = Math.Max(camera.Near, view.ProjectionMetadata.Far);
            camera.Fov = Math.Clamp(view.ProjectionMetadata.FovY, 1f, 179f);
            camera.Projection = view.ProjectionMetadata.Kind == ProjectionKind.Perspective
                ? Camera3D.ProjectionType.Perspective
                : Camera3D.ProjectionType.Orthogonal;
            if (view.ProjectionMetadata.Kind == ProjectionKind.Orthographic)
            {
                camera.KeepAspect = Camera3D.KeepAspectEnum.Height;
                camera.Size = view.ProjectionMetadata.OrthoSize;
            }
            camera.CullMask = view.LayerMask == 0 ? uint.MaxValue : view.LayerMask;
            camera.Current = view.Target is null ? view.IsCurrentCamera : true;
        }

        foreach (var key in _cameras.Keys.ToArray())
        {
            if (active.Contains(key)) continue;
            var camera = _cameras[key];
            if (GodotObject.IsInstanceValid(camera))
                camera.QueueFree();
            _cameras.Remove(key);
        }
    }

    private void ApplyItemsLocked(in RenderFrame frame, IReadOnlyDictionary<long, SubViewport> viewportTargets)
    {
        var active = new HashSet<(long, int)>();

        for (int i = 0; i < frame.Items.Length; i++)
        {
            var item = frame.Items.Span[i];
            if (!item.Active)
                continue;

            var key = (item.ElementId, i);
            active.Add(key);

            if (!_items.TryGetValue(key, out var node) || !GodotObject.IsInstanceValid(node.Node))
            {
                var meshInstance = new MeshInstance3D { Name = $"NovaV2Item_{item.ElementId}_{i}" };
                _root.AddChild(meshInstance);
                node = new ItemNode { Node = meshInstance };
                _items[key] = node;
            }

            Node parent = _root;
            if (item.ViewportId != 0 && viewportTargets.TryGetValue(item.ViewportId, out var viewport))
                parent = viewport;

            if (node.Node.GetParent() != parent)
                node.Node.Reparent(parent);

            var mesh = _meshes[item.Mesh.Value.Id];
            var material = _materials[item.Material.Value.Id];

            node.Mesh = item.Mesh;
            node.Material = item.Material;
            node.Node.Mesh = mesh.Mesh;
            node.Node.GlobalTransform = GodotInterop.ToTransform(item.World);
            node.Node.Layers = item.LayerMask == 0 ? uint.MaxValue : item.LayerMask;
            node.Node.Visible = true;
            node.Node.CastShadow = material.Desc.CastShadows
                ? GeometryInstance3D.ShadowCastingSetting.On
                : GeometryInstance3D.ShadowCastingSetting.Off;

            if (item.OverridesIndex >= 0)
            {
                var overrides = frame.DrawOverrides.Span[item.OverridesIndex];
                var overrideMaterial = BuildOverrideMaterialLocked(material);
                ApplyOverridesLocked(
                    overrideMaterial,
                    _shaders[material.Shader.Value.Id],
                    overrides.Values.Span);
                _overrideMaterials[key] = overrideMaterial;
                node.Node.MaterialOverride = overrideMaterial;
            }
            else
            {
                _overrideMaterials.Remove(key);
                node.Node.MaterialOverride = material.Material;
            }
        }

        foreach (var key in _items.Keys.ToArray())
        {
            if (active.Contains(key)) continue;
            var item = _items[key];
            if (GodotObject.IsInstanceValid(item.Node))
                item.Node.QueueFree();
            _items.Remove(key);
            _overrideMaterials.Remove(key);
        }
    }

    private void ApplyLightsLocked(in RenderFrame frame)
    {
        var active = new HashSet<long>();

        foreach (var light in frame.Lights.Span)
        {
            if (!light.Active)
                continue;

            active.Add(light.ElementId);

            _lights.TryGetValue(light.ElementId, out var node);
            if (node is not null && (!GodotObject.IsInstanceValid(node) || !MatchesLightKind(node, light.Kind)))
            {
                if (GodotObject.IsInstanceValid(node))
                    node.QueueFree();
                _lights.Remove(light.ElementId);
                node = null;
            }

            if (node is null)
            {
                node = light.Kind switch
                {
                    LightKind.Directional => new DirectionalLight3D(),
                    LightKind.Spot => new SpotLight3D(),
                    _ => new OmniLight3D(),
                };
                node.Name = $"NovaV2Light_{light.ElementId}";
                _root.AddChild(node);
                _lights[light.ElementId] = node;
            }

            node.GlobalTransform = GodotInterop.ToTransform(light.World);
            node.LightColor = GodotInterop.ToGodotColor(light.Color);
            node.LightEnergy = light.Intensity;

            var shadows = _currentEnvironment.Shadows;
            node.ShadowEnabled = light.CastShadows && shadows.Enabled;
            node.ShadowBias = shadows.Bias;
            node.ShadowNormalBias = shadows.NormalBias;
            node.ShadowOpacity = shadows.Opacity;
            node.LightCullMask = light.LayerMask == 0 ? uint.MaxValue : light.LayerMask;

            switch (node)
            {
                case OmniLight3D omni:
                    omni.OmniRange = light.Range;
                    break;
                case SpotLight3D spot:
                    spot.SpotRange = light.Range;
                    spot.SpotAngle = light.OuterCone;
                    spot.SpotAngleAttenuation = light.OuterCone > 0f
                        ? Math.Clamp(1f - (light.InnerCone / light.OuterCone), 0f, 1f)
                        : 0.5f;
                    break;
                case DirectionalLight3D directional:
                    // A single orthographic split keeps the depth comparison stable
                    // on scene-scale receivers; the default PSSM splits degenerate
                    // when the camera's far plane is large, which shows up as the
                    // whole floor reading shadowed. Distance comes from the
                    // environment so a game can trade range for sharpness.
                    directional.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal;
                    directional.DirectionalShadowMaxDistance = shadows.Distance;
                    break;
            }
        }

        foreach (var id in _lights.Keys.ToArray())
        {
            if (active.Contains(id)) continue;
            var node = _lights[id];
            if (GodotObject.IsInstanceValid(node))
                node.QueueFree();
            _lights.Remove(id);
        }
    }

    private void ApplyEnvironmentLocked(EnvironmentData data)
    {
        var sky = data.Sky;

        if (sky.SkyboxEnabled && sky.Skybox is { } skyboxHandle)
        {
            var texture = _textures[skyboxHandle.Value.Id].Texture;
            _panoramaSky ??= new PanoramaSkyMaterial();
            _panoramaSky.Panorama = texture;
            _panoramaSky.EnergyMultiplier = Math.Max(0f, sky.Energy);

            _sky ??= new Sky();
            _sky.SkyMaterial = _panoramaSky;
            _environment.BackgroundMode = GodotEnvironment.BGMode.Sky;
            _environment.Sky = _sky;
        }
        else
        {
            _environment.BackgroundMode = GodotEnvironment.BGMode.Color;
            _environment.BackgroundColor = GodotInterop.ToGodotColor(sky.Color);
            _environment.BackgroundEnergyMultiplier = Math.Max(0f, sky.Energy);
        }

        _environment.SkyRotation = new Basis(new Godot.Quaternion(
            sky.SkyboxRotation.X,
            sky.SkyboxRotation.Y,
            sky.SkyboxRotation.Z,
            sky.SkyboxRotation.W)).GetEuler();

        _environment.AmbientLightSource = GodotEnvironment.AmbientSource.Color;
        _environment.AmbientLightColor = GodotInterop.ToGodotColor(data.Ambient.Color);
        _environment.AmbientLightEnergy = Math.Max(0f, data.Ambient.Intensity);

        ApplyFogLocked(data.Fog);

        _environment.TonemapMode = data.Tonemap.Mode switch
        {
            TonemapMode.Linear => GodotEnvironment.ToneMapper.Linear,
            TonemapMode.Reinhard => GodotEnvironment.ToneMapper.Reinhardt,
            TonemapMode.Filmic => GodotEnvironment.ToneMapper.Filmic,
            TonemapMode.Aces => GodotEnvironment.ToneMapper.Aces,
            TonemapMode.Agx => GodotEnvironment.ToneMapper.Agx,
            _ => GodotEnvironment.ToneMapper.Filmic,
        };
        _environment.TonemapWhite = Math.Max(0.01f, data.Tonemap.White);
        _environment.TonemapExposure = data.Tonemap.Exposure;
        _environment.AdjustmentEnabled = true;
        _environment.AdjustmentBrightness = data.Tonemap.Brightness;
        _environment.AdjustmentContrast = data.Tonemap.Contrast;
        _environment.AdjustmentSaturation = data.Tonemap.Saturation;

        _environment.GlowEnabled = data.Bloom.Mode != BloomMode.Disabled;
        _environment.GlowIntensity = Math.Max(0f, data.Bloom.Intensity);
        _environment.GlowHdrThreshold = Math.Max(0f, data.Bloom.Threshold);
        _environment.GlowHdrScale = Math.Max(0f, data.Bloom.SoftThreshold);
        _environment.GlowBlendMode = data.Bloom.Mode switch
        {
            BloomMode.Additive => GodotEnvironment.GlowBlendModeEnum.Additive,
            BloomMode.Screen => GodotEnvironment.GlowBlendModeEnum.Screen,
            _ => GodotEnvironment.GlowBlendModeEnum.Mix,
        };

        _environment.SsaoEnabled = data.Ssao.Enabled;
        _environment.SsaoIntensity = data.Ssao.Intensity;
        _environment.SsaoRadius = data.Ssao.Radius;
        _environment.SsaoPower = data.Ssao.Power;

        _environment.SsilEnabled = data.Ssil.Enabled;
        _environment.SsilIntensity = data.Ssil.GiIntensity;
        _environment.SsilRadius = data.Ssil.Radius;
        _environment.SsrEnabled = data.Ssr.Enabled;
    }

    private void ApplyFogLocked(FogSettings fog)
    {
        _environment.FogEnabled = fog.Mode != FogMode.None;
        _environment.FogLightColor = GodotInterop.ToGodotColor(fog.Color);
        _environment.FogSkyAffect = Math.Clamp(fog.SkyAffect, 0f, 1f);

        switch (fog.Mode)
        {
            case FogMode.Linear:
                _environment.FogMode = GodotEnvironment.FogModeEnum.Depth;
                _environment.FogDepthBegin = Math.Max(0f, fog.Start);
                _environment.FogDepthEnd = Math.Max(_environment.FogDepthBegin, fog.End);
                _environment.FogDensity = Math.Max(0f, fog.Density);
                break;
            case FogMode.Exponential:
            case FogMode.ExponentialSquared:
                _environment.FogMode = GodotEnvironment.FogModeEnum.Exponential;
                _environment.FogDensity = Math.Max(0f, fog.Mode == FogMode.ExponentialSquared
                    ? fog.Density * fog.Density
                    : fog.Density);
                break;
            default:
                _environment.FogDensity = 0f;
                break;
        }

        _environment.VolumetricFogEnabled = fog.VolumetricEnabled;
        _environment.VolumetricFogDensity = Math.Max(0f, fog.VolumetricDensity);
        _environment.VolumetricFogAnisotropy = Math.Clamp(fog.VolumetricAnisotropy, -1f, 1f);
        _environment.VolumetricFogLength = Math.Max(0f, fog.VolumetricLength);
        _environment.VolumetricFogSkyAffect = Math.Clamp(fog.VolumetricSkyAffect, 0f, 1f);
    }

    // ── Materials ───────────────────────────────────────────────────────────

    private void ApplyMaterialLocked(
        Material material,
        ShaderHandle shader,
        in MaterialDesc desc,
        ReadOnlySpan<MaterialValue> parameters,
        ReadOnlySpan<MaterialTextureBinding> textures)
    {
        var shaderRecord = _shaders[shader.Value.Id];
        if (shaderRecord.IsCustom)
        {
            ApplyCustomMaterialLocked((ShaderMaterial)material, shaderRecord, desc, parameters, textures);
            return;
        }

        ApplyStandardMaterialLocked((StandardMaterial3D)material, shaderRecord, desc, parameters, textures);
    }

    private void ApplyStandardMaterialLocked(
        StandardMaterial3D material,
        ShaderRecord shaderRecord,
        in MaterialDesc desc,
        ReadOnlySpan<MaterialValue> parameters,
        ReadOnlySpan<MaterialTextureBinding> textures)
    {
        bool lit = desc.Lit && shaderRecord.Program.BuiltIn != BuiltInShader.Unlit;

        material.AlbedoColor = Colors.White;
        material.Metallic = 0f;
        material.Roughness = 1f;
        material.EmissionEnabled = false;
        material.Emission = Colors.Black;
        material.EmissionEnergyMultiplier = 1f;
        material.AOEnabled = false;
        material.MetallicSpecular = 0.5f;
        material.NormalScale = 1f;
        material.AlphaScissorThreshold = 0.5f;
        material.Uv1Offset = Godot.Vector3.Zero;
        material.Uv1Scale = Godot.Vector3.One;
        material.Uv1Triplanar = false;
        material.Uv1WorldTriplanar = false;
        material.Uv1TriplanarSharpness = 1f;
        material.AlbedoTexture = null;
        material.AlbedoTextureForceSrgb = false;
        material.NormalTexture = null;
        material.MetallicTexture = null;
        material.RoughnessTexture = null;
        // glTF packs metallic/roughness into blue/green; reset with the textures
        // so a reused material record can never keep a stale channel.
        material.MetallicTextureChannel = BaseMaterial3D.TextureChannel.Blue;
        material.RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Green;
        material.AOTexture = null;
        material.EmissionTexture = null;

        material.ShadingMode = lit
            ? BaseMaterial3D.ShadingModeEnum.PerPixel
            : BaseMaterial3D.ShadingModeEnum.Unshaded;

        material.CullMode = desc.Cull switch
        {
            CullMode.Front => BaseMaterial3D.CullModeEnum.Front,
            CullMode.None => BaseMaterial3D.CullModeEnum.Disabled,
            _ => BaseMaterial3D.CullModeEnum.Back,
        };

        material.Transparency = desc.Blend == BlendMode.Opaque
            ? BaseMaterial3D.TransparencyEnum.Disabled
            : BaseMaterial3D.TransparencyEnum.Alpha;
        material.BlendMode = desc.Blend switch
        {
            BlendMode.Additive => BaseMaterial3D.BlendModeEnum.Add,
            BlendMode.Multiply => BaseMaterial3D.BlendModeEnum.Mul,
            _ => BaseMaterial3D.BlendModeEnum.Mix,
        };

        material.NoDepthTest = !desc.DepthTest;
        material.DepthDrawMode = desc.DepthWrite
            ? BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly
            : BaseMaterial3D.DepthDrawModeEnum.Disabled;
        material.DisableReceiveShadows = !desc.ReceiveShadows;

        for (int index = 0; index < parameters.Length; index++)
            ApplyMaterialValueLocked(material, index, parameters[index]);

        foreach (var binding in textures)
            ApplyTextureSlotLocked(material, binding.Slot, binding.Texture);
    }

    private void ApplyCustomMaterialLocked(
        ShaderMaterial material,
        ShaderRecord shaderRecord,
        in MaterialDesc desc,
        ReadOnlySpan<MaterialValue> parameters,
        ReadOnlySpan<MaterialTextureBinding> textures)
    {
        var reflection = shaderRecord.Program.Reflection;
        if (reflection is null)
            return;

        for (int index = 0; index < parameters.Length; index++)
        {
            var value = parameters[index];
            if (value.Kind == MaterialValueKind.None)
                continue;

            var parameter = FindParameter(reflection, index);
            if (parameter is null)
                continue;

            if (value.Kind == MaterialValueKind.Texture)
            {
                ApplyCustomTextureLocked(material, reflection, index, value.TextureValue);
                continue;
            }

            if (TryToVariant(value, out var variant))
                material.SetShaderParameter(parameter.Value.Name, variant);
        }

        foreach (var binding in textures)
            ApplyCustomTextureSlotLocked(material, reflection, binding.Slot, binding.Texture);
    }

    private void ApplyCustomOverridesLocked(
        ShaderMaterial material,
        ShaderRecord shaderRecord,
        ReadOnlySpan<DrawOverride> overrides)
    {
        var reflection = shaderRecord.Program.Reflection;
        if (reflection is null)
            return;

        foreach (var item in overrides)
        {
            var parameter = FindParameter(reflection, item.ParameterIndex);
            if (parameter is null)
                continue;

            if (item.Value.Kind == MaterialValueKind.Texture)
            {
                ApplyCustomTextureLocked(material, reflection, item.ParameterIndex, item.Value.TextureValue);
                continue;
            }

            if (TryToVariant(item.Value, out var variant))
                material.SetShaderParameter(parameter.Value.Name, variant);
        }
    }

    private void ApplyCustomTextureSlotLocked(
        ShaderMaterial material,
        ShaderReflection reflection,
        int slot,
        TextureHandle texture)
    {
        if (!texture.IsValid)
            return;

        var binding = FindTextureSlot(reflection, slot);
        if (binding is null)
            return;

        material.SetShaderParameter(binding.Value.Name, _textures[texture.Value.Id].Texture);
    }

    private void ApplyCustomTextureLocked(
        ShaderMaterial material,
        ShaderReflection reflection,
        int index,
        TextureHandle texture)
    {
        if (!texture.IsValid)
            return;

        var parameter = FindParameter(reflection, index);
        if (parameter is null)
            return;

        material.SetShaderParameter(parameter.Value.Name, _textures[texture.Value.Id].Texture);
    }

    private static ShaderParameter? FindParameter(ShaderReflection reflection, int index)
    {
        foreach (var parameter in reflection.Parameters)
        {
            if (parameter.Index == index)
                return parameter;
        }
        return null;
    }

    private static ShaderTextureSlot? FindTextureSlot(ShaderReflection reflection, int slot)
    {
        foreach (var texture in reflection.Textures)
        {
            if (texture.Slot == slot)
                return texture;
        }
        return null;
    }

    private static bool TryToVariant(in MaterialValue value, out Variant variant)
    {
        switch (value.Kind)
        {
            case MaterialValueKind.Float:
                variant = value.FloatValue;
                return true;
            case MaterialValueKind.Int:
                variant = value.IntValue;
                return true;
            case MaterialValueKind.Bool:
                variant = value.BoolValue;
                return true;
            case MaterialValueKind.Vector2:
                variant = new Godot.Vector2(value.Vector2Value.X, value.Vector2Value.Y);
                return true;
            case MaterialValueKind.Vector3:
                variant = new Godot.Vector3(value.Vector3Value.X, value.Vector3Value.Y, value.Vector3Value.Z);
                return true;
            case MaterialValueKind.Vector4:
                variant = new Godot.Vector4(
                    value.Vector4Value.X,
                    value.Vector4Value.Y,
                    value.Vector4Value.Z,
                    value.Vector4Value.W);
                return true;
            case MaterialValueKind.Matrix4x4:
                variant = new Projection(
                    value.MatrixValue.M11, value.MatrixValue.M12, value.MatrixValue.M13, value.MatrixValue.M14,
                    value.MatrixValue.M21, value.MatrixValue.M22, value.MatrixValue.M23, value.MatrixValue.M24,
                    value.MatrixValue.M31, value.MatrixValue.M32, value.MatrixValue.M33, value.MatrixValue.M34,
                    value.MatrixValue.M41, value.MatrixValue.M42, value.MatrixValue.M43, value.MatrixValue.M44);
                return true;
            default:
                variant = default;
                return false;
        }
    }

    private void ApplyMaterialValueLocked(StandardMaterial3D material, int index, in MaterialValue value)
    {
        if (value.Kind == MaterialValueKind.None)
            return;

        if (value.Kind == MaterialValueKind.Texture)
        {
            ApplyTextureSlotLocked(material, index, value.TextureValue);
            return;
        }

        switch (index)
        {
            case BuiltInMaterialParameters.Albedo:
                if (value.Kind == MaterialValueKind.Vector4)
                    material.AlbedoColor = GodotInterop.ToGodotColor(value.Vector4Value);
                else if (value.Kind == MaterialValueKind.Vector3)
                    material.AlbedoColor = GodotInterop.ToGodotColor(value.Vector3Value);
                break;
            case BuiltInMaterialParameters.Metallic:
                if (value.Kind == MaterialValueKind.Float)
                    material.Metallic = Math.Clamp(value.FloatValue, 0f, 1f);
                break;
            case BuiltInMaterialParameters.Roughness:
                if (value.Kind == MaterialValueKind.Float)
                    material.Roughness = Math.Clamp(value.FloatValue, 0f, 1f);
                break;
            case BuiltInMaterialParameters.Emissive:
                if (value.Kind is MaterialValueKind.Vector3 or MaterialValueKind.Vector4)
                {
                    material.EmissionEnabled = true;
                    material.Emission = value.Kind == MaterialValueKind.Vector4
                        ? GodotInterop.ToGodotColor(value.Vector4Value)
                        : GodotInterop.ToGodotColor(value.Vector3Value);
                }
                break;
            case BuiltInMaterialParameters.EmissiveEnergy:
                if (value.Kind == MaterialValueKind.Float)
                    material.EmissionEnergyMultiplier = Math.Max(0f, value.FloatValue);
                break;
            case BuiltInMaterialParameters.AmbientOcclusion:
                if (value.Kind == MaterialValueKind.Float)
                    material.AOEnabled = value.FloatValue > 0f;
                break;
            case BuiltInMaterialParameters.Specular:
                if (value.Kind == MaterialValueKind.Float)
                    material.MetallicSpecular = Math.Clamp(value.FloatValue, 0f, 1f);
                break;
            case BuiltInMaterialParameters.NormalScale:
                if (value.Kind == MaterialValueKind.Float)
                    material.NormalScale = Math.Max(0f, value.FloatValue);
                break;
            case BuiltInMaterialParameters.AlphaCutoff:
                if (value.Kind == MaterialValueKind.Float && value.FloatValue > 0f)
                {
                    material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                    material.AlphaScissorThreshold = Math.Clamp(value.FloatValue, 0f, 1f);
                }
                break;
            case BuiltInMaterialParameters.UvOffset:
                if (value.Kind == MaterialValueKind.Vector2)
                    material.Uv1Offset = new Godot.Vector3(value.Vector2Value.X, value.Vector2Value.Y, 0f);
                break;
            case BuiltInMaterialParameters.UvScale:
                if (value.Kind == MaterialValueKind.Vector2)
                    material.Uv1Scale = new Godot.Vector3(value.Vector2Value.X, value.Vector2Value.Y, 1f);
                break;
            case BuiltInMaterialParameters.Triplanar:
                if (value.Kind == MaterialValueKind.Float)
                    material.Uv1Triplanar = value.FloatValue > 0f;
                break;
            case BuiltInMaterialParameters.TriplanarWorld:
                if (value.Kind == MaterialValueKind.Float)
                    material.Uv1WorldTriplanar = value.FloatValue > 0f;
                break;
            case BuiltInMaterialParameters.TriplanarSharpness:
                if (value.Kind == MaterialValueKind.Float)
                    material.Uv1TriplanarSharpness = Math.Clamp(value.FloatValue, 0f, 150f);
                break;
        }
    }

    private void ApplyTextureSlotLocked(StandardMaterial3D material, int slot, TextureHandle texture)
    {
        if (!texture.IsValid)
            return;

        var record = _textures[texture.Value.Id];
        switch (slot)
        {
            case BuiltInTextureSlots.Albedo:
                material.AlbedoTexture = record.Texture;
                material.AlbedoTextureForceSrgb = record.ColorSpace == TextureColorSpace.Srgb;
                break;
            case BuiltInTextureSlots.Normal:
                material.NormalTexture = record.Texture;
                break;
            case BuiltInTextureSlots.Metallic:
                material.MetallicTexture = record.Texture;
                // glTF packs metallic into the blue channel; Godot defaults to red.
                material.MetallicTextureChannel = BaseMaterial3D.TextureChannel.Blue;
                break;
            case BuiltInTextureSlots.Roughness:
                material.RoughnessTexture = record.Texture;
                // glTF packs roughness into the green channel; Godot defaults to red.
                material.RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Green;
                break;
            case BuiltInTextureSlots.Occlusion:
                material.AOEnabled = true;
                material.AOTexture = record.Texture;
                break;
            case BuiltInTextureSlots.Emission:
                material.EmissionEnabled = true;
                material.EmissionTexture = record.Texture;
                break;
        }
    }

    private Material BuildOverrideMaterialLocked(MaterialRecord source)
    {
        return (Material)source.Material.Duplicate();
    }

    private void ApplyOverridesLocked(
        Material material,
        ShaderRecord shaderRecord,
        ReadOnlySpan<DrawOverride> overrides)
    {
        if (shaderRecord.IsCustom)
        {
            ApplyCustomOverridesLocked((ShaderMaterial)material, shaderRecord, overrides);
            return;
        }

        foreach (var item in overrides)
            ApplyMaterialValueLocked((StandardMaterial3D)material, item.ParameterIndex, item.Value);
    }

    // ── Texture helpers ─────────────────────────────────────────────────────

    private Texture2D BuildTextureLocked(in TextureData data)
    {
        Image? image = GodotInterop.ToImage(data);
        if (image is null)
        {
            RejectLocked(
                $"NovaV2 v1 cannot realize texture {data.Dimension}/{data.Format} " +
                $"({data.Width}x{data.Height}).");
        }

        return ImageTexture.CreateFromImage(image);
    }

    private void RefreshMaterialsUsingTextureLocked(uint textureId)
    {
        foreach (var material in _materials.Values)
        {
            if (!MaterialUsesTexture(material, textureId))
                continue;

            ApplyMaterialLocked(
                material.Material,
                material.Shader,
                material.Desc,
                material.Parameters,
                material.Textures);
        }
    }

    private static bool MaterialUsesTexture(MaterialRecord material, uint textureId)
    {
        foreach (var binding in material.Textures)
        {
            if (binding.Texture.Value.Id == textureId)
                return true;
        }

        foreach (var parameter in material.Parameters)
        {
            if (parameter.Kind == MaterialValueKind.Texture &&
                parameter.TextureValue.Value.Id == textureId)
                return true;
        }

        return false;
    }

    // ── Validation helpers ──────────────────────────────────────────────────

    private static bool MatchesLightKind(Light3D node, LightKind kind) => kind switch
    {
        LightKind.Directional => node is DirectionalLight3D,
        LightKind.Spot => node is SpotLight3D,
        _ => node is OmniLight3D,
    };

    private void ValidateFrameLocked(in RenderFrame frame)
    {
        foreach (var view in frame.Views.Span)
        {
            if (view.Target is { } target)
                RequireLocked(target.Value, ResourceKind.RenderTarget);
        }

        foreach (var item in frame.Items.Span)
        {
            RequireLocked(item.Mesh.Value, ResourceKind.Mesh);
            RequireLocked(item.Material.Value, ResourceKind.Material);

            if (item.OverridesIndex < -1 || item.OverridesIndex >= frame.DrawOverrides.Length)
            {
                RejectLocked(
                    $"Draw item for element {item.ElementId} has overrides index " +
                    $"{item.OverridesIndex} outside the frame's {frame.DrawOverrides.Length} override tables.");
            }

            if (item.OverridesIndex >= 0)
            {
                foreach (var value in frame.DrawOverrides.Span[item.OverridesIndex].Values.Span)
                {
                    if (value.Value.Kind == MaterialValueKind.Texture)
                        RequireLocked(value.Value.TextureValue.Value, ResourceKind.Texture);
                }
            }
        }

        if ((frame.Environment ?? EnvironmentData.Default).Sky.Skybox is { } skybox)
            RequireLocked(skybox.Value, ResourceKind.Texture);
    }

    private ResourceHandle NewHandleLocked() => new(++_nextId, 1, _epoch);

    private void RequireAvailableLocked()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NovaV2Renderer));
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
            if (MaterialUsesTexture(material, textureId))
                return true;
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
        foreach (var item in _items.Values)
        {
            if (GodotObject.IsInstanceValid(item.Node))
                item.Node.QueueFree();
        }
        _items.Clear();
        _overrideMaterials.Clear();

        foreach (var camera in _cameras.Values)
        {
            if (GodotObject.IsInstanceValid(camera))
                camera.QueueFree();
        }
        _cameras.Clear();

        foreach (var light in _lights.Values)
        {
            if (GodotObject.IsInstanceValid(light))
                light.QueueFree();
        }
        _lights.Clear();

        foreach (var target in _targets.Values)
        {
            if (GodotObject.IsInstanceValid(target.Viewport))
                target.Viewport.QueueFree();
        }

        foreach (var presented in _presentedTargets.Values)
        {
            if (GodotObject.IsInstanceValid(presented))
                presented.QueueFree();
        }
        _presentedTargets.Clear();

        _environment.Sky = null;
        _panoramaSky = null;
        _sky = null;

        _meshes.Clear();
        _textures.Clear();
        _shaders.Clear();
        _materials.Clear();
        _targets.Clear();
    }
}
