using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using R3D_cs;
using Raylib_cs;
using V12.Core;
using V12.Core.Interfaces;
using V12.Core.Interfaces.Renderer;
using V12.Core.Rendering;
using V12.Core.UI.Paper;
using V12.Components;
using R3DCamera = R3D_cs.Camera;
using R3DLight = R3D_cs.Light;
using R3DLightType = R3D_cs.LightType;
using R3DMaterial = R3D_cs.Material;
using R3DMesh = R3D_cs.Mesh;
using RlCamera3D = Raylib_cs.Camera3D;
using RlColor = Raylib_cs.Color;
using RlProjection = Raylib_cs.CameraProjection;
using DrawingColor = System.Drawing.Color;
using V12CameraProjection = V12.Core.Interfaces.Renderer.CameraProjection;
using V12LightType = V12.Core.Interfaces.Renderer.LightType;

namespace Rockstar
{
    public class R3DRenderer : IRenderer, IDisposable
    {
        private sealed class MeshEntry
        {
            public R3DMesh Mesh;
            public long Signature;
        }

        private sealed class MaterialEntry
        {
            public R3DMaterial Material;
            public long Signature;
        }

        private sealed class LightEntry
        {
            public R3DLight Handle;
            public R3DLightType Type;
        }

        private readonly Dictionary<IMeshRenderable, MeshEntry> _meshes = new();
        private readonly Dictionary<long, MaterialEntry> _materials = new();
        private readonly Dictionary<long, LightEntry> _lights = new();

        private RenderPacket? _packet;
        private R3DLight? _defaultLight;
        private bool _defaultLightEnabled;
        private string? _skyboxPath;
        private Cubemap _skybox;
        private AmbientMap _ambientMap;
        private bool _hasAmbientMap;

        private int _width;
        private int _height;
        private bool _disposed;
        private readonly Dictionary<long, RenderTexture2D> _viewportTargets = new();

        public bool LockMouse { get; set; }

        public void ApplySnapshot(FrameSnapshot snapshot)
        {
        }

        public RendererInfo GetAllInfo()
        {
            return new RendererInfo(
                FPS: GetFPS(),
                CurrentObjectsCount: _meshes.Count,
                CurrentTextures: 0,
                CurrentAudioSources: 0,
                CurrentMaterials: _materials.Count,
                CurrentLights: _lights.Count + (_defaultLight != null ? 1 : 0),
                TotalMemoryUsage: 0);
        }

        public int GetFPS() => Raylib.GetFPS();

        public int GetScreenHeight() => Raylib.GetScreenHeight();

        public int GetScreenWidth() => Raylib.GetScreenWidth();

        [Obsolete("use QueueItems instead of QueueItem for better performance")]
        public void QueueItem(IRenderable item)
        {
            if (item is not IMeshRenderable mesh) return;
            _packet ??= new RenderPacket();
            long id = (mesh as ComponentBase)?.Owner?.Id ?? 0;
            _packet.Meshes.Add(new MeshDraw
            {
                ElementId = id,
                ViewportId = 0,
                Active = true,
                Mesh = mesh,
                Albedo = DrawingColor.White,
                UvScale = Vector2.One,
            });
        }

        public void QueueItems(RenderPacket packet)
        {
            _packet = packet;
            ReconcileMeshes(packet);
            ReconcileMaterials(packet);
            SyncLights(packet);
            ApplyEnvironment(packet.Environment);
        }

        [Obsolete("use RemoveItems instead of RemoveItem for better performance")]
        public void RemoveItem(IRenderable item)
        {
            if (item is not IMeshRenderable mesh) return;
            UnloadMesh(mesh);
            if ((mesh as ComponentBase)?.Owner is { } owner)
                UnloadMaterial(owner.Id);
        }

        public void RemoveItems(RenderPacket packet)
        {
            if (packet == null) return;
            foreach (var draw in packet.Meshes)
            {
                if (draw.Mesh != null)
                    UnloadMesh(draw.Mesh);
                UnloadMaterial(draw.ElementId);
            }
        }

        public void step()
        {
            int width = Raylib.GetScreenWidth();
            int height = Raylib.GetScreenHeight();
            if (width > 0 && height > 0 && (width != _width || height != _height))
            {
                _width = width;
                _height = height;
                R3D.SetResolution(width, height);
            }

            if (Raylib.WindowShouldClose())
                System.Environment.Exit(0);
        }

        public void DrawFrame(PaperFrame? uiFrame = null, Action? overlay = null)
        {
            if (_disposed) return;

            var camera = ResolveCamera();
            var environment = _packet?.Environment ?? EnvironmentData.Default;
            var viewportById = uiFrame?.Viewports
                .GroupBy(v => v.Id)
                .ToDictionary(g => g.Key, g => g.First())
                ?? new Dictionary<long, PaperViewportInfo>();

            var mainDraws = new List<MeshDraw>();

            // Pass 1: render each UI viewport into its own render texture. The
            // Paper UI pass later draws those textures inside the dock panel.
            if (_packet != null)
            {
                foreach (var group in _packet.Meshes.Where(d => d.Active).GroupBy(d => d.ViewportId))
                {
                    if (group.Key == 0 ||
                        !viewportById.TryGetValue(group.Key, out var viewport) ||
                        viewport.Rect.Z <= 0f || viewport.Rect.W <= 0f)
                    {
                        mainDraws.AddRange(group);
                        continue;
                    }

                    int width = Math.Max(1, (int)MathF.Round(viewport.Rect.Z));
                    int height = Math.Max(1, (int)MathF.Round(viewport.Rect.W));
                    var target = GetOrCreateViewportTarget(group.Key, width, height);
                    viewport.Texture = target.Texture;

                    // R3D's View descriptor is the render-to-texture path: it
                    // binds the target and derives the camera aspect from the
                    // viewport rect. Raylib.BeginTextureMode is not enough
                    // because R3D renders through its own framebuffer path.
                    R3D.BeginPro(new R3D_cs.View
                    {
                        Camera = camera,
                        Target = target,
                        Viewport = new Rectangle(0, 0, width, height),
                    });
                    DrawPacketMeshes(group);
                    R3D.End();
                }
            }

            // Pass 2: main screen (ViewportId 0 plus any viewport that has no UI
            // panel this frame), then the Paper/UI overlay.
            R3D.SetResolution(Math.Max(1, _width), Math.Max(1, _height));
            Raylib.BeginDrawing();
            Raylib.ClearBackground(ToRl(environment.SkyColor));

            R3D.BeginEx(camera);
            DrawPacketMeshes(mainDraws);
            R3D.End();

            overlay?.Invoke();

            Raylib.EndDrawing();
        }

        private void DrawPacketMeshes(IEnumerable<MeshDraw> draws)
        {
            foreach (var draw in draws)
            {
                if (!draw.Active) continue;

                var mesh = GetOrBuildMesh(draw);
                if (mesh == null) continue;

                var drawMesh = mesh.Value;
                drawMesh.ShadowCastMode = draw.CastShadows
                    ? R3D_cs.ShadowCastMode.OnAuto
                    : R3D_cs.ShadowCastMode.Disabled;

                var material = GetOrBuildMaterial(draw);
                R3D.DrawMeshPro(drawMesh, material, Matrix4x4.Transpose(draw.World));
            }
        }

        private RenderTexture2D GetOrCreateViewportTarget(long id, int width, int height)
        {
            if (_viewportTargets.TryGetValue(id, out var existing) &&
                existing.Texture.Width == width && existing.Texture.Height == height)
                return existing;

            if (existing.Texture.Id != 0)
                Raylib.UnloadRenderTexture(existing);

            var target = Raylib.LoadRenderTexture(width, height);
            _viewportTargets[id] = target;
            return target;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var target in _viewportTargets.Values)
                if (target.Texture.Id != 0) Raylib.UnloadRenderTexture(target);
            _viewportTargets.Clear();

            foreach (var entry in _meshes.Values)
                R3D.UnloadMesh(entry.Mesh);
            _meshes.Clear();

            foreach (var entry in _materials.Values)
                R3D.UnloadMaterial(entry.Material);
            _materials.Clear();

            foreach (var entry in _lights.Values)
            {
                R3D.DisableLight(entry.Handle);
                R3D.DestroyLight(entry.Handle);
            }
            _lights.Clear();

            if (_defaultLight is { } fallback)
            {
                R3D.DisableLight(fallback);
                R3D.DestroyLight(fallback);
                _defaultLight = null;
            }

            if (_hasAmbientMap)
            {
                R3D.UnloadAmbientMap(_ambientMap);
                _hasAmbientMap = false;
            }

            if (_skybox.Texture != 0)
            {
                R3D.UnloadCubemap(_skybox);
                _skybox = default;
            }
        }

        private void ReconcileMeshes(RenderPacket packet)
        {
            var alive = new HashSet<IMeshRenderable>();
            foreach (var draw in packet.Meshes)
                if (draw.Mesh != null)
                    alive.Add(draw.Mesh);

            RemoveInactive(_meshes, alive, UnloadMesh);
        }

        private void ReconcileMaterials(RenderPacket packet)
        {
            var alive = new HashSet<long>();
            foreach (var draw in packet.Meshes)
                if (draw.Mesh != null)
                    alive.Add(draw.ElementId);

            RemoveInactive(_materials, alive, UnloadMaterial);
        }

        private void RemoveInactive<TKey, TValue>(
            Dictionary<TKey, TValue> cache,
            HashSet<TKey> alive,
            Action<TKey> unload) where TKey : notnull
        {
            if (cache.Count == 0) return;

            List<TKey>? dead = null;
            foreach (var key in cache.Keys)
            {
                if (!alive.Contains(key))
                    (dead ??= new List<TKey>()).Add(key);
            }
            if (dead == null) return;

            foreach (var key in dead)
            {
                unload(key);
                cache.Remove(key);
            }
        }

        private void SyncLights(RenderPacket packet)
        {
            var alive = new HashSet<long>();

            foreach (var data in packet.Lights)
            {
                if (!data.Active) continue;

                var type = ToR3DType(data.Type);
                if (type == null) continue;

                alive.Add(data.ElementId);

                if (!_lights.TryGetValue(data.ElementId, out var entry) || entry.Type != type.Value)
                {
                    if (entry != null)
                    {
                        R3D.DisableLight(entry.Handle);
                        R3D.DestroyLight(entry.Handle);
                    }
                    entry = new LightEntry
                    {
                        Handle = R3D.CreateLight(type.Value),
                        Type = type.Value,
                    };
                    _lights[data.ElementId] = entry;
                }

                var light = entry.Handle;
                if (type.Value == R3DLightType.Dir)
                {
                    R3D.SetLightDirection(light, data.Direction);
                }
                else
                {
                    R3D.SetLightPosition(light, data.Position);
                    if (type.Value == R3DLightType.Spot)
                        R3D.SetLightDirection(light, data.Direction);
                }
                R3D.SetLightColor(light, ToRl(data.Color));
                R3D.SetLightEnergy(light, MathF.Max(0f, data.Intensity));
                R3D.SetLightRange(light, MathF.Max(0.01f, data.Range));

                if (type.Value == R3DLightType.Spot)
                {
                    float outer = MathF.Max(0.01f, data.Angle <= 0f ? 45f : data.Angle);
                    float inner = outer * Math.Clamp(1f - data.SpotSoftness, 0f, 0.99f);
                    R3D.SetLightAngle(light, inner, outer);
                }

                R3D.EnableLight(light);
            }

            List<long>? dead = null;
            foreach (var id in _lights.Keys)
                if (!alive.Contains(id))
                    (dead ??= new List<long>()).Add(id);

            if (dead != null)
            {
                foreach (var id in dead)
                {
                    var entry = _lights[id];
                    R3D.DisableLight(entry.Handle);
                    R3D.DestroyLight(entry.Handle);
                    _lights.Remove(id);
                }
            }

            if (alive.Count == 0)
            {
                if (_defaultLight == null)
                {
                    var sun = R3D.CreateLight(R3DLightType.Dir);
                    R3D.SetLightDirection(sun, Vector3.Normalize(new Vector3(0.45f, -1f, 0.35f)));
                    R3D.SetLightColor(sun, new RlColor(255, 242, 217, 255));
                    R3D.SetLightEnergy(sun, 1f);
                    R3D.SetLightRange(sun, 100f);
                    R3D.EnableLight(sun);
                    _defaultLight = sun;
                    _defaultLightEnabled = true;
                }
            }
            else if (_defaultLight is { } fallback && _defaultLightEnabled)
            {
                R3D.DisableLight(fallback);
                R3D.DestroyLight(fallback);
                _defaultLight = null;
                _defaultLightEnabled = false;
            }
        }

        private static R3DLightType? ToR3DType(V12LightType type) => type switch
        {
            V12LightType.Point => R3DLightType.Omni,
            V12LightType.Directional => R3DLightType.Dir,
            V12LightType.Spot => R3DLightType.Spot,
            V12LightType.AreaLight => R3DLightType.Omni,
            _ => null,
        };

        private void ApplyEnvironment(EnvironmentData data)
        {
            var env = R3D.GetEnvironmentEx();

            env.Background.Color = ToRl(data.SkyColor);
            env.Background.Energy = data.HasValue ? MathF.Max(0f, data.BackgroundEnergy) : 1f;
            env.Ambient.Color = ToRl(data.AmbientColor);
            env.Ambient.Energy = MathF.Max(0f, data.AmbientIntensity);

            UpdateSkybox(data);
            env.Background.Sky = _skybox.Texture != 0 ? _skybox : default;
            env.Ambient.Map = _hasAmbientMap ? _ambientMap : default;

            if (data.HasValue)
            {
                env.Background.SkyBlur = Math.Clamp(data.SkyBlur, 0f, 1f);
                env.Background.Rotation = data.SkyboxRotation;

                env.Fog.Mode = MapFog(data.FogMode);
                env.Fog.Color = ToRl(data.FogColor);
                env.Fog.Start = MathF.Max(0f, data.FogStart);
                env.Fog.End = MathF.Max(env.Fog.Start, data.FogEnd);
                env.Fog.Density = MathF.Max(0f, data.FogDensity);
                env.Fog.SkyAffect = Math.Clamp(data.FogSkyAffect, 0f, 1f);

                env.VolumetricFog.Enabled = data.VolumetricFogEnabled;
                env.VolumetricFog.ScatteringDensity = MathF.Max(0f, data.VolumetricFogDensity);
                env.VolumetricFog.Anisotropy = Math.Clamp(data.VolumetricFogAnisotropy, -1f, 1f);
                env.VolumetricFog.Length = MathF.Max(0f, data.VolumetricFogLength);
                env.VolumetricFog.SkyAffect = Math.Clamp(data.VolumetricFogSkyAffect, 0f, 1f);

                env.Tonemap.Mode = MapTonemap(data.TonemapMode);
                env.Tonemap.Exposure = MathF.Max(0f, data.Exposure);
                env.Tonemap.White = MathF.Max(0.0001f, data.TonemapWhite);

                env.Color.Brightness = MathF.Max(0f, data.Brightness);
                env.Color.Contrast = MathF.Max(0f, data.Contrast);
                env.Color.Saturation = MathF.Max(0f, data.Saturation);

                env.Bloom.Mode = MapBloom(data.BloomMode);
                env.Bloom.Intensity = MathF.Max(0f, data.BloomIntensity);
                env.Bloom.Threshold = MathF.Max(0f, data.BloomThreshold);
                env.Bloom.SoftThreshold = MathF.Max(0f, data.BloomSoftThreshold);

                env.AutoExposure.Enabled = data.AutoExposureEnabled;
                env.AutoExposure.MinEV = data.AutoExposureMinEV;
                env.AutoExposure.MaxEV = data.AutoExposureMaxEV;
                env.AutoExposure.ExposureCompensation = data.AutoExposureCompensation;

                env.Ssao.Enabled = data.SsaoEnabled;
                env.Ssao.Intensity = MathF.Max(0f, data.SsaoIntensity);
                env.Ssao.Radius = MathF.Max(0f, data.SsaoRadius);
                env.Ssao.Power = MathF.Max(0f, data.SsaoPower);

                env.Ssil.Enabled = data.SsilEnabled;
                env.Ssil.GiIntensity = MathF.Max(0f, data.SsilGiIntensity);
                env.Ssil.AoIntensity = MathF.Max(0f, data.SsilAoIntensity);
                env.Ssil.Radius = MathF.Max(0f, data.SsilRadius);

                env.Ssgi.Enabled = data.SsgiEnabled;
                env.Ssgi.Intensity = MathF.Max(0f, data.SsgiIntensity);

                env.Ssr.Enabled = data.SsrEnabled;

                env.Dof.Mode = data.DofEnabled ? R3D_cs.DoF.Enabled : R3D_cs.DoF.Disabled;
                env.Dof.FocusPoint = MathF.Max(0f, data.DofFocusDistance);
                env.Dof.MaxBlurSize = MathF.Max(0f, data.DofMaxBlurSize);
            }

            R3D.SetEnvironmentEx(env);
        }

        private void UpdateSkybox(EnvironmentData data)
        {
            string? path = data.Skybox ? ResolveTexturePath(data.SkyboxPath) : null;
            if (path == _skyboxPath && (path == null || _skybox.Texture != 0))
                return;

            if (_hasAmbientMap)
            {
                R3D.UnloadAmbientMap(_ambientMap);
                _ambientMap = default;
                _hasAmbientMap = false;
            }

            if (_skybox.Texture != 0)
            {
                R3D.UnloadCubemap(_skybox);
                _skybox = default;
            }

            _skyboxPath = path;
            if (path == null)
                return;

            _skybox = R3D.LoadCubemap(path, R3D_cs.CubemapLayout.AutoDetect);
            if (_skybox.Texture == 0)
                return;

            _ambientMap = R3D.GenAmbientMap(_skybox, AmbientFlags.Illumination | AmbientFlags.Reflection);
            _hasAmbientMap = _ambientMap.Irradiance != 0 || _ambientMap.Prefilter != 0;
        }

        private static R3D_cs.Fog MapFog(EnvironmentFogMode mode) => mode switch
        {
            EnvironmentFogMode.Linear => R3D_cs.Fog.Linear,
            EnvironmentFogMode.ExponentialSquared => R3D_cs.Fog.EXP2,
            EnvironmentFogMode.Exponential => R3D_cs.Fog.Exp,
            _ => R3D_cs.Fog.Disabled,
        };

        private static R3D_cs.Tonemap MapTonemap(EnvironmentTonemapMode mode) => mode switch
        {
            EnvironmentTonemapMode.Linear => R3D_cs.Tonemap.Linear,
            EnvironmentTonemapMode.Reinhard => R3D_cs.Tonemap.Reinhard,
            EnvironmentTonemapMode.Filmic => R3D_cs.Tonemap.Filmic,
            EnvironmentTonemapMode.Aces => R3D_cs.Tonemap.Aces,
            EnvironmentTonemapMode.Agx => R3D_cs.Tonemap.Agx,
            _ => R3D_cs.Tonemap.Filmic,
        };

        private static R3D_cs.Bloom MapBloom(EnvironmentBloomMode mode) => mode switch
        {
            EnvironmentBloomMode.Mix => R3D_cs.Bloom.Mix,
            EnvironmentBloomMode.Additive => R3D_cs.Bloom.Additive,
            EnvironmentBloomMode.Screen => R3D_cs.Bloom.Screen,
            _ => R3D_cs.Bloom.Disabled,
        };

        private R3DCamera ResolveCamera()
        {
            CameraData? chosen = null;
            if (_packet != null)
            {
                foreach (var camera in _packet.Cameras)
                {
                    if (!camera.Active) continue;
                    if (camera.IsCurrent)
                    {
                        chosen = camera;
                        break;
                    }
                    chosen ??= camera;
                }
            }

            return chosen is { } data
                ? BuildCamera(data)
                : BuildDefaultCamera();
        }

        private static R3DCamera BuildCamera(CameraData data)
        {
            var world = data.World;
            var position = new Vector3(world.M41, world.M42, world.M43);

            var forward = new Vector3(-world.M31, -world.M32, -world.M33);
            if (forward.LengthSquared() < 1e-8f) forward = -Vector3.UnitZ;
            forward = Vector3.Normalize(forward);

            var up = new Vector3(world.M21, world.M22, world.M23);
            if (up.LengthSquared() < 1e-8f || MathF.Abs(Vector3.Dot(up, forward)) > 0.99f)
                up = MathF.Abs(forward.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
            up = Vector3.Normalize(up - forward * Vector3.Dot(up, forward));

            bool orthographic = data.Projection == V12CameraProjection.Orthographic;
            float fov = orthographic
                ? (data.OrthoSize > 0f ? data.OrthoSize * 2f : 10f)
                : (data.FieldOfView > 0f ? data.FieldOfView : 75f);

            var rl = new RlCamera3D
            {
                Position = position,
                Target = position + forward,
                Up = up,
                FovY = fov,
                Projection = orthographic ? RlProjection.Orthographic : RlProjection.Perspective,
            };

            var camera = R3D.CameraFromRL(rl);
            camera.NearPlane = data.NearClip > 0f ? data.NearClip : 0.05f;
            camera.FarPlane = data.FarClip > camera.NearPlane ? data.FarClip : 1000f;
            return camera;
        }

        private static R3DCamera BuildDefaultCamera()
        {
            var rl = new RlCamera3D
            {
                Position = new Vector3(3f, 3f, 6f),
                Target = new Vector3(0f, 1f, 0f),
                Up = Vector3.UnitY,
                FovY = 75f,
                Projection = RlProjection.Perspective,
            };

            var camera = R3D.CameraFromRL(rl);
            camera.NearPlane = 0.05f;
            camera.FarPlane = 1000f;
            return camera;
        }

        private R3DMesh? GetOrBuildMesh(MeshDraw draw)
        {
            var source = draw.Mesh;
            if (source == null) return null;

            long signature = ComputeMeshSignature(source);
            if (_meshes.TryGetValue(source, out var existing))
            {
                if (existing.Signature == signature && R3D.IsMeshValid(existing.Mesh))
                    return existing.Mesh;

                R3D.UnloadMesh(existing.Mesh);
                _meshes.Remove(source);
            }

            var built = BuildMesh(source);
            if (built == null) return null;

            _meshes[source] = new MeshEntry { Mesh = built.Value, Signature = signature };
            return built;
        }

        private static long ComputeMeshSignature(IMeshRenderable mesh)
        {
            var hash = new HashCode();
            hash.Add(RuntimeHelpers.GetHashCode(mesh.MeshPoints));
            hash.Add(RuntimeHelpers.GetHashCode(mesh.Indices));
            return hash.ToHashCode();
        }

        private static R3DMesh? BuildMesh(IMeshRenderable source)
        {
            var points = source.MeshPoints;
            var indices = source.Indices;
            if (points == null || indices == null || points.Length < 9 || indices.Length < 3)
                return null;

            int vertexCount = points.Length / 3;
            for (int i = 0; i < indices.Length; i++)
            {
                if (indices[i] >= vertexCount)
                    return null;
            }

            var positions = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                int p = i * 3;
                positions[i] = new Vector3(
                    (float)points[p],
                    (float)points[p + 1],
                    (float)points[p + 2]);
            }

            for (int t = 0; t < indices.Length; t += 3)
            {
                uint i0 = indices[t];
                uint i1 = indices[t + 1];
                uint i2 = indices[t + 2];

                var p0 = positions[i0];
                var p1 = positions[i1];
                var p2 = positions[i2];
                var normal = Vector3.Cross(p1 - p0, p2 - p0);

                normals[i0] += normal;
                normals[i1] += normal;
                normals[i2] += normal;
            }

            for (int i = 0; i < vertexCount; i++)
            {
                var n = normals[i];
                normals[i] = n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitY;
            }

            var uvs = BakePlanarUvs(positions, normals);

            var data = R3D.CreateMeshData(vertexCount, indices.Length);
            var vertices = data.Vertices;
            var indexSpan = data.Indices;

            for (int i = 0; i < vertexCount; i++)
            {
                vertices[i] = R3D.MakeVertex(
                    positions[i],
                    uvs[i],
                    normals[i],
                    new Vector4(1f, 0f, 0f, 1f),
                    RlColor.White);
            }

            for (int i = 0; i < indices.Length; i++)
                indexSpan[i] = indices[i];

            var mesh = R3D.LoadMesh(R3D_cs.PrimitiveType.Triangles, data);
            R3D.UnloadMeshData(data);
            return mesh;
        }

        private static Vector2[] BakePlanarUvs(Vector3[] positions, Vector3[] normals)
        {
            var uvs = new Vector2[positions.Length];
            if (positions.Length == 0) return uvs;

            var min = positions[0];
            var max = positions[0];
            for (int i = 1; i < positions.Length; i++)
            {
                min = Vector3.Min(min, positions[i]);
                max = Vector3.Max(max, positions[i]);
            }

            var size = max - min;
            float sx = size.X > 1e-6f ? 1f / size.X : 0f;
            float sy = size.Y > 1e-6f ? 1f / size.Y : 0f;
            float sz = size.Z > 1e-6f ? 1f / size.Z : 0f;

            for (int i = 0; i < positions.Length; i++)
            {
                var p = positions[i];
                var n = normals[i];
                float ax = MathF.Abs(n.X);
                float ay = MathF.Abs(n.Y);
                float az = MathF.Abs(n.Z);

                if (ax >= ay && ax >= az)
                    uvs[i] = new Vector2((p.Z - min.Z) * sz, (max.Y - p.Y) * sy);
                else if (ay >= az)
                    uvs[i] = new Vector2((p.X - min.X) * sx, (p.Z - min.Z) * sz);
                else
                    uvs[i] = new Vector2((p.X - min.X) * sx, (max.Y - p.Y) * sy);
            }

            return uvs;
        }

        private R3DMaterial GetOrBuildMaterial(MeshDraw draw)
        {
            long signature = ComputeMaterialSignature(draw);
            if (_materials.TryGetValue(draw.ElementId, out var existing))
            {
                if (existing.Signature == signature)
                    return existing.Material;

                R3D.UnloadMaterial(existing.Material);
                _materials.Remove(draw.ElementId);
            }

            var material = BuildMaterial(draw);
            _materials[draw.ElementId] = new MaterialEntry
            {
                Material = material,
                Signature = signature,
            };
            return material;
        }

        private static long ComputeMaterialSignature(in MeshDraw draw)
        {
            var hash = new HashCode();
            hash.Add(draw.Albedo.ToArgb());
            hash.Add(draw.EmissiveColor.ToArgb());
            hash.Add(draw.EmissiveIntensity);
            hash.Add(draw.Metallic);
            hash.Add(draw.Roughness);
            hash.Add(draw.AmbientOcclusion);
            hash.Add(draw.Specular);
            hash.Add(draw.NormalScale);
            hash.Add(draw.AlphaCutoff);
            hash.Add(draw.BlendMode);
            hash.Add(draw.RenderPriority);
            hash.Add(draw.Unlit);
            hash.Add(draw.NoDepthTest);
            hash.Add(draw.DoubleSided);
            hash.Add(draw.CastShadows);
            hash.Add(draw.ReceiveShadows);
            hash.Add(draw.UvOffset);
            hash.Add(draw.UvScale);
            hash.Add(draw.TexturePath);
            hash.Add(draw.NormalTexturePath);
            hash.Add(draw.MetallicTexturePath);
            hash.Add(draw.RoughnessTexturePath);
            hash.Add(draw.OcclusionTexturePath);
            hash.Add(draw.EmissionTexturePath);
            return hash.ToHashCode();
        }

        private static R3DMaterial BuildMaterial(in MeshDraw draw)
        {
            var material = R3D.GetDefaultMaterial();
            var tint = ToRl(draw.Albedo);

            material.Albedo.Color = tint;
            material.Orm.Occlusion = Math.Clamp(draw.AmbientOcclusion, 0f, 1f);
            material.Orm.Roughness = Math.Clamp(draw.Roughness, 0f, 1f);
            material.Orm.Metalness = Math.Clamp(draw.Metallic, 0f, 1f);
            material.Orm.Specular = Math.Clamp(draw.Specular, 0f, 1f);
            material.Normal.Scale = MathF.Max(0f, draw.NormalScale);
            material.Emission.Color = ToRl(draw.EmissiveColor);
            material.Emission.Energy = MathF.Max(0f, draw.EmissiveIntensity);
            material.AlphaCutoff = Math.Clamp(draw.AlphaCutoff, 0f, 1f);
            material.Priority = draw.RenderPriority;
            material.Unlit = draw.Unlit;
            material.UvOffset = draw.UvOffset;
            material.UvScale = draw.UvScale.X == 0f && draw.UvScale.Y == 0f
                ? Vector2.One
                : draw.UvScale;
            material.CullMode = draw.DoubleSided ? R3D_cs.CullMode.None : R3D_cs.CullMode.Back;
            material.BlendMode = draw.BlendMode switch
            {
                MaterialBlendMode.Additive => R3D_cs.BlendMode.Additive,
                MaterialBlendMode.Multiplicative => R3D_cs.BlendMode.Multiply,
                _ => R3D_cs.BlendMode.Mix,
            };
            material.TransparencyMode = draw.BlendMode == MaterialBlendMode.Opaque && draw.Albedo.A >= 255
                ? R3D_cs.TransparencyMode.Disabled
                : R3D_cs.TransparencyMode.Alpha;

            var albedoPath = ResolveTexturePath(draw.TexturePath);
            if (albedoPath != null)
                material.Albedo = R3D.LoadAlbedoMap(albedoPath, tint);

            var normalPath = ResolveTexturePath(draw.NormalTexturePath);
            if (normalPath != null)
                material.Normal = R3D.LoadNormalMap(normalPath, draw.NormalScale);

            var ormPath = ResolveTexturePath(draw.MetallicTexturePath)
                ?? ResolveTexturePath(draw.RoughnessTexturePath)
                ?? ResolveTexturePath(draw.OcclusionTexturePath);
            if (ormPath != null)
                material.Orm = R3D.LoadOrmMap(ormPath, draw.AmbientOcclusion, draw.Roughness, draw.Metallic, draw.Specular);

            var emissionPath = ResolveTexturePath(draw.EmissionTexturePath);
            if (emissionPath != null)
                material.Emission = R3D.LoadEmissionMap(emissionPath, ToRl(draw.EmissiveColor), draw.EmissiveIntensity);

            return material;
        }

        private static string? ResolveTexturePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var resolver = GameRoot.Instance?.Registry?.Get<IAssetResolver>();

            string candidate = path;
            if (path.StartsWith("v12://", StringComparison.OrdinalIgnoreCase))
                candidate = resolver?.Resolve(path) ?? path;
            else if (path.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
                candidate = resolver?.Resolve("v12://" + path.Substring(6).TrimStart('/')) ?? path;
            else if (!Path.IsPathRooted(path) && resolver != null)
                candidate = resolver.Resolve("v12://" + path.TrimStart('/'));

            if (candidate.StartsWith("v12://", StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
                return null;

            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);

            var raw = V12AssetResolver.ResolveGlobal(path);
            return File.Exists(raw) ? Path.GetFullPath(raw) : null;
        }

        private void UnloadMesh(IMeshRenderable mesh)
        {
            if (_meshes.TryGetValue(mesh, out var entry))
            {
                R3D.UnloadMesh(entry.Mesh);
                _meshes.Remove(mesh);
            }
        }

        private void UnloadMaterial(long elementId)
        {
            if (_materials.TryGetValue(elementId, out var entry))
            {
                R3D.UnloadMaterial(entry.Material);
                _materials.Remove(elementId);
            }
        }

        private static RlColor ToRl(DrawingColor color)
        {
            return new RlColor(color.R, color.G, color.B, color.A);
        }
    }
}
