using System.Drawing;
using System.Numerics;
using V12.Rendering;
using Xunit;
using NewEnv = V12.Rendering.EnvironmentData;
using OldEnv = V12.Core.Interfaces.Renderer.EnvironmentData;

namespace V12.Architecture.Tests;

/// <summary>
/// Covers the v1 renderer contract: frame lease/pool lifetime, handle
/// generation/epoch validation, dependency rejection and environment parity.
/// </summary>
public sealed class RendererContractTests
{
    // ── Frame and pool ──────────────────────────────────────────────────────

    [Fact]
    public void Submit_RecordsFrame_AndReleasesPrevious()
    {
        using var renderer = new NullRenderer();
        var pool = NewPool(renderer);
        var first = BuildFrame(pool, renderer);
        renderer.Submit(first);

        Assert.NotNull(renderer.LastFrame);
        Assert.Equal(first.Sequence, renderer.LastFrame!.Sequence);
        Assert.Equal(1, pool.InUse);

        var second = BuildFrame(pool, renderer);
        renderer.Submit(second);

        Assert.Equal(second.Sequence, renderer.LastFrame!.Sequence);
        Assert.Equal(1, pool.InUse);
        Assert.Throws<ObjectDisposedException>(() => _ = first.Frame);
    }

    [Fact]
    public void EmptyFrame_IsDistinctFromNoSubmit()
    {
        using var renderer = new NullRenderer();
        var pool = NewPool(renderer);

        Assert.Null(renderer.LastFrame);

        var lease = BuildFrame(pool, renderer);
        renderer.Submit(lease);

        Assert.NotNull(renderer.LastFrame);
        Assert.Equal(0, renderer.LastFrame!.Frame.Items.Length);
        Assert.Equal(1, renderer.LastFrame.Frame.Views.Length);

        // No further submit: the same completed frame remains cached.
        Assert.Same(lease, renderer.LastFrame);
    }

    [Fact]
    public void Pool_GivesBackPressure_AndReleaseRestoresCapacity()
    {
        using var renderer = new NullRenderer();
        var pool = new RenderFramePool(1) { Epoch = renderer.CurrentEpoch };

        Assert.True(pool.TryRent(out var builder));
        var lease = builder.Seal();
        Assert.False(pool.TryRent(out _));
        Assert.Equal(1, pool.InUse);

        lease.Release();
        Assert.Equal(0, pool.InUse);
        Assert.True(pool.TryRent(out var next));
        next.Seal().Release();
    }

    [Fact]
    public void DoubleRelease_IsADiagnosedBug()
    {
        var pool = new RenderFramePool(1);
        var lease = pool.Rent().Seal();
        lease.Release();

        Assert.Throws<InvalidOperationException>(() => lease.Release());
    }

    [Fact]
    public void SubmittedFrame_CannotBeReadAfterRelease()
    {
        var pool = new RenderFramePool(1);
        var lease = pool.Rent().Seal();
        lease.Release();

        Assert.Throws<ObjectDisposedException>(() => _ = lease.Frame);
    }

    // ── Handles, epochs and dependencies ───────────────────────────────────

    [Fact]
    public void StaleGeneration_IsRejected()
    {
        using var renderer = new NullRenderer();
        var mesh = renderer.CreateMesh(MeshData.Empty);
        var stale = new MeshHandle(new ResourceHandle(
            mesh.Value.Id,
            mesh.Value.Generation + 1,
            mesh.Value.Epoch));

        Assert.Throws<RendererContractException>(() => renderer.UpdateMesh(stale, MeshData.Empty));
        Assert.Contains(renderer.Diagnostics, d => d.Contains("stale or unknown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StaleEpoch_IsRejected_AfterDeviceRecovery()
    {
        using var renderer = new NullRenderer();
        var oldMesh = renderer.CreateMesh(MeshData.Empty);
        var oldEpoch = renderer.CurrentEpoch;

        renderer.LoseDevice();
        Assert.Throws<RendererContractException>(() => renderer.CreateMesh(MeshData.Empty));

        renderer.RecoverDevice();
        Assert.True(renderer.CurrentEpoch.Value > oldEpoch.Value);

        Assert.Throws<RendererContractException>(() => renderer.UpdateMesh(oldMesh, MeshData.Empty));

        var newMesh = renderer.CreateMesh(MeshData.Empty);
        Assert.Equal(renderer.CurrentEpoch, newMesh.Value.Epoch);
        renderer.UpdateMesh(newMesh, MeshData.Empty);
    }

    [Fact]
    public void DeviceEvents_ReportLostRecoveringReady()
    {
        using var renderer = new NullRenderer();
        var events = new List<RendererDeviceEventKind>();
        renderer.DeviceEvent += e => events.Add(e.Kind);

        renderer.LoseDevice();
        renderer.RecoverDevice();

        Assert.Equal(
            new[]
            {
                RendererDeviceEventKind.Lost,
                RendererDeviceEventKind.Recovering,
                RendererDeviceEventKind.Ready,
            },
            events);
    }

    [Fact]
    public void FrameFromOldEpoch_IsRejected()
    {
        using var renderer = new NullRenderer();
        var pool = NewPool(renderer);
        var lease = BuildFrame(pool, renderer);

        renderer.LoseDevice();
        renderer.RecoverDevice();

        try
        {
            Assert.Throws<RendererContractException>(() => renderer.Submit(lease));
            Assert.Null(renderer.LastFrame);
        }
        finally
        {
            lease.Release();
        }
    }

    [Fact]
    public void ShaderWithLiveMaterial_IsRejected()
    {
        using var renderer = new NullRenderer();
        var shader = renderer.CreateShader(new ShaderProgram(BuiltInShader.Unlit));
        var material = renderer.CreateMaterial(shader, new MaterialDesc());

        Assert.Throws<RendererContractException>(() => renderer.DestroyShader(shader));

        renderer.DestroyMaterial(material);
        renderer.DestroyShader(shader);
    }

    [Fact]
    public void TextureWithLiveMaterial_IsRejected()
    {
        using var renderer = new NullRenderer();
        var shader = renderer.CreateShader(new ShaderProgram(BuiltInShader.Unlit));
        var texture = renderer.CreateTexture(NewTexture());
        var material = renderer.CreateMaterial(shader, new MaterialDesc(
            Textures: new[] { new MaterialTextureBinding(0, texture) }));

        Assert.Throws<RendererContractException>(() => renderer.DestroyTexture(texture));

        renderer.DestroyMaterial(material);
        renderer.DestroyTexture(texture);
    }

    [Fact]
    public void RenderTargetTexture_IsOwnedByItsTarget()
    {
        using var renderer = new NullRenderer();
        var shader = renderer.CreateShader(new ShaderProgram(BuiltInShader.Unlit));
        var target = renderer.CreateRenderTarget(new RenderTargetDesc(64, 64));
        var texture = renderer.GetRenderTargetTexture(target);
        var material = renderer.CreateMaterial(shader, new MaterialDesc(
            Textures: new[] { new MaterialTextureBinding(0, texture) }));

        Assert.Throws<RendererContractException>(() => renderer.DestroyTexture(texture));
        Assert.Throws<RendererContractException>(() => renderer.DestroyRenderTarget(target));

        renderer.DestroyMaterial(material);
        renderer.DestroyRenderTarget(target);
        Assert.Throws<RendererContractException>(() => renderer.GetRenderTargetTexture(target));
    }

    [Fact]
    public void DrawOverrides_ArePerItem_AndDoNotMutateMaterial()
    {
        using var renderer = new NullRenderer();
        var pool = NewPool(renderer);
        var mesh = renderer.CreateMesh(MeshData.Empty);
        var shader = renderer.CreateShader(new ShaderProgram(BuiltInShader.Unlit));
        var material = renderer.CreateMaterial(shader, new MaterialDesc());

        var red = new DrawOverrides(new[] { new DrawOverride(0, MaterialValue.From(new Vector4(1, 0, 0, 1))) });
        var green = new DrawOverrides(new[] { new DrawOverride(0, MaterialValue.From(new Vector4(0, 1, 0, 1))) });

        var builder = pool.Rent();
        builder.Views.Add(new RenderView { Id = 1, Projection = Matrix4x4.Identity });
        builder.DrawOverrides.Add(red);
        builder.DrawOverrides.Add(green);
        builder.Items.Add(Item(mesh, material, overridesIndex: 0));
        builder.Items.Add(Item(mesh, material, overridesIndex: 1));
        renderer.Submit(builder.Seal());

        var frame = renderer.LastFrame!.Frame;
        Assert.Equal(2, frame.Items.Length);
        Assert.Equal(2, frame.DrawOverrides.Length);
        Assert.Equal(1f, frame.DrawOverrides.Span[0].Values.Span[0].Value.Vector4Value.X);
        Assert.Equal(1f, frame.DrawOverrides.Span[1].Values.Span[0].Value.Vector4Value.Y);
    }

    [Fact]
    public void InvalidOverridesIndex_IsRejected()
    {
        using var renderer = new NullRenderer();
        var pool = NewPool(renderer);
        var mesh = renderer.CreateMesh(MeshData.Empty);
        var shader = renderer.CreateShader(new ShaderProgram(BuiltInShader.Unlit));
        var material = renderer.CreateMaterial(shader, new MaterialDesc());

        var builder = pool.Rent();
        builder.Views.Add(new RenderView { Id = 1, Projection = Matrix4x4.Identity });
        builder.Items.Add(Item(mesh, material, overridesIndex: 3));
        var lease = builder.Seal();

        try
        {
            Assert.Throws<RendererContractException>(() => renderer.Submit(lease));
        }
        finally
        {
            lease.Release();
        }
    }

    [Fact]
    public void ProjectionMatrix_IsAuthoritative()
    {
        using var renderer = new NullRenderer();
        var pool = NewPool(renderer);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 16f / 9f, 0.1f, 500f);

        var builder = pool.Rent();
        builder.Views.Add(new RenderView
        {
            Id = 1,
            Projection = projection,
            ProjectionMetadata = new ProjectionMetadata(ProjectionKind.Perspective, 0.01f, 100f, 0.5f, 1f),
        });
        renderer.Submit(builder.Seal());

        Assert.Equal(projection, renderer.LastFrame!.Frame.Views.Span[0].Projection);
    }

    // ── Environment parity ─────────────────────────────────────────────────

    [Fact]
    public void EnvironmentDefaults_MatchLegacyDefaults()
    {
        Assert.Equal(NewEnv.Default, Map(OldEnv.Default));
    }

    [Fact]
    public void EnvironmentFields_MapFieldByField()
    {
        var old = OldEnv.Default;
        old.HasValue = true;
        old.SkyColor = Color.FromArgb(10, 20, 30);
        old.BackgroundEnergy = 2f;
        old.AmbientColor = Color.FromArgb(40, 50, 60);
        old.AmbientIntensity = 0.75f;
        old.Skybox = true;
        old.SkyboxPath = "sky.hdr"; // CPU-side source; resolved to Sky.Skybox before the frame
        old.SkyBlur = 0.25f;
        old.SkyboxRotation = Quaternion.CreateFromYawPitchRoll(0.2f, 0.3f, 0.4f);
        old.FogMode = V12.Core.Interfaces.Renderer.EnvironmentFogMode.Exponential;
        old.FogColor = Color.FromArgb(70, 80, 90);
        old.FogStart = 1f;
        old.FogEnd = 200f;
        old.FogDensity = 0.5f;
        old.FogSkyAffect = 0.25f;
        old.VolumetricFogEnabled = true;
        old.VolumetricFogDensity = 0.1f;
        old.VolumetricFogAnisotropy = 0.6f;
        old.VolumetricFogLength = 32f;
        old.VolumetricFogSkyAffect = 0.4f;
        old.TonemapMode = V12.Core.Interfaces.Renderer.EnvironmentTonemapMode.Aces;
        old.TonemapWhite = 2f;
        old.Exposure = 1.5f;
        old.Brightness = 1.1f;
        old.Contrast = 1.2f;
        old.Saturation = 0.9f;
        old.BloomMode = V12.Core.Interfaces.Renderer.EnvironmentBloomMode.Additive;
        old.BloomIntensity = 0.8f;
        old.BloomThreshold = 0.7f;
        old.BloomSoftThreshold = 0.6f;
        old.AutoExposureEnabled = true;
        old.AutoExposureMinEV = -4f;
        old.AutoExposureMaxEV = 4f;
        old.AutoExposureCompensation = 0.3f;
        old.SsaoEnabled = true;
        old.SsaoIntensity = 0.9f;
        old.SsaoRadius = 0.8f;
        old.SsaoPower = 1.1f;
        old.SsilEnabled = true;
        old.SsilGiIntensity = 0.7f;
        old.SsilAoIntensity = 0.6f;
        old.SsilRadius = 2f;
        old.SsgiEnabled = true;
        old.SsgiIntensity = 0.5f;
        old.SsrEnabled = true;
        old.DofEnabled = true;
        old.DofFocusDistance = 5f;
        old.DofMaxBlurSize = 0.2f;

        var mapped = Map(old);

        Assert.True(mapped.HasValue);
        Assert.Equal(V3(old.SkyColor), mapped.Sky.Color);
        Assert.Equal(2f, mapped.Sky.Energy);
        Assert.Equal(V3(old.AmbientColor), mapped.Ambient.Color);
        Assert.Equal(0.75f, mapped.Ambient.Intensity);
        Assert.True(mapped.Sky.SkyboxEnabled);
        Assert.Null(mapped.Sky.Skybox); // path is resolved by the engine
        Assert.Equal(0.25f, mapped.Sky.SkyboxBlur);
        Assert.Equal(old.SkyboxRotation, mapped.Sky.SkyboxRotation);
        Assert.Equal(FogMode.Exponential, mapped.Fog.Mode);
        Assert.Equal(V3(old.FogColor), mapped.Fog.Color);
        Assert.Equal(1f, mapped.Fog.Start);
        Assert.Equal(200f, mapped.Fog.End);
        Assert.Equal(0.5f, mapped.Fog.Density);
        Assert.Equal(0.25f, mapped.Fog.SkyAffect);
        Assert.True(mapped.Fog.VolumetricEnabled);
        Assert.Equal(0.1f, mapped.Fog.VolumetricDensity);
        Assert.Equal(0.6f, mapped.Fog.VolumetricAnisotropy);
        Assert.Equal(32f, mapped.Fog.VolumetricLength);
        Assert.Equal(0.4f, mapped.Fog.VolumetricSkyAffect);
        Assert.Equal(TonemapMode.Aces, mapped.Tonemap.Mode);
        Assert.Equal(2f, mapped.Tonemap.White);
        Assert.Equal(1.5f, mapped.Tonemap.Exposure);
        Assert.Equal(1.1f, mapped.Tonemap.Brightness);
        Assert.Equal(1.2f, mapped.Tonemap.Contrast);
        Assert.Equal(0.9f, mapped.Tonemap.Saturation);
        Assert.Equal(BloomMode.Additive, mapped.Bloom.Mode);
        Assert.Equal(0.8f, mapped.Bloom.Intensity);
        Assert.Equal(0.7f, mapped.Bloom.Threshold);
        Assert.Equal(0.6f, mapped.Bloom.SoftThreshold);
        Assert.True(mapped.AutoExposure.Enabled);
        Assert.Equal(-4f, mapped.AutoExposure.MinEV);
        Assert.Equal(4f, mapped.AutoExposure.MaxEV);
        Assert.Equal(0.3f, mapped.AutoExposure.Compensation);
        Assert.True(mapped.Ssao.Enabled);
        Assert.Equal(0.9f, mapped.Ssao.Intensity);
        Assert.Equal(0.8f, mapped.Ssao.Radius);
        Assert.Equal(1.1f, mapped.Ssao.Power);
        Assert.True(mapped.Ssil.Enabled);
        Assert.Equal(0.7f, mapped.Ssil.GiIntensity);
        Assert.Equal(0.6f, mapped.Ssil.AoIntensity);
        Assert.Equal(2f, mapped.Ssil.Radius);
        Assert.True(mapped.Ssgi.Enabled);
        Assert.Equal(0.5f, mapped.Ssgi.Intensity);
        Assert.True(mapped.Ssr.Enabled);
        Assert.True(mapped.Dof.Enabled);
        Assert.Equal(5f, mapped.Dof.FocusDistance);
        Assert.Equal(0.2f, mapped.Dof.MaxBlurSize);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static RenderFramePool NewPool(NullRenderer renderer, int capacity = 4)
        => new(capacity) { Epoch = renderer.CurrentEpoch };

    private static RenderFrameLease BuildFrame(RenderFramePool pool, NullRenderer renderer)
    {
        var builder = pool.Rent();
        builder.Views.Add(new RenderView
        {
            Id = 1,
            Projection = Matrix4x4.Identity,
        });
        return builder.Seal();
    }

    private static DrawItem Item(MeshHandle mesh, MaterialHandle material, int overridesIndex) => new()
    {
        ElementId = 1,
        ViewportId = 1,
        Mesh = mesh,
        Material = material,
        World = Matrix4x4.Identity,
        Active = true,
        OverridesIndex = overridesIndex,
    };

    private static TextureData NewTexture() => new(
        TextureDimension.Texture2D,
        1,
        1,
        1,
        1,
        TextureFormat.Rgba8Unorm,
        TextureColorSpace.Linear,
        TextureUsage.Sampled,
        ReadOnlyMemory<TextureSubresource>.Empty);

    private static Vector3 V3(Color color) => new(color.R / 255f, color.G / 255f, color.B / 255f);

    private static NewEnv Map(OldEnv old) => new()
    {
        HasValue = old.HasValue,
        Ambient = new AmbientSettings(V3(old.AmbientColor), old.AmbientIntensity),
        Sky = new SkySettings(
            V3(old.SkyColor),
            old.BackgroundEnergy,
            old.Skybox,
            null, // SkyboxPath is resolved to a TextureHandle by the engine
            old.SkyBlur,
            old.SkyboxRotation),
        Fog = new FogSettings(
            MapFog(old.FogMode),
            V3(old.FogColor),
            old.FogStart,
            old.FogEnd,
            old.FogDensity,
            old.FogSkyAffect,
            old.VolumetricFogEnabled,
            old.VolumetricFogDensity,
            old.VolumetricFogAnisotropy,
            old.VolumetricFogLength,
            old.VolumetricFogSkyAffect),
        Tonemap = new TonemapSettings(
            MapTonemap(old.TonemapMode),
            old.TonemapWhite,
            old.Exposure,
            old.Brightness,
            old.Contrast,
            old.Saturation),
        Bloom = new BloomSettings(
            MapBloom(old.BloomMode),
            old.BloomIntensity,
            old.BloomThreshold,
            old.BloomSoftThreshold),
        AutoExposure = new AutoExposureSettings(
            old.AutoExposureEnabled,
            old.AutoExposureMinEV,
            old.AutoExposureMaxEV,
            old.AutoExposureCompensation),
        Ssao = new SsaoSettings(old.SsaoEnabled, old.SsaoIntensity, old.SsaoRadius, old.SsaoPower),
        Ssil = new SsilSettings(old.SsilEnabled, old.SsilGiIntensity, old.SsilAoIntensity, old.SsilRadius),
        Ssgi = new SsgiSettings(old.SsgiEnabled, old.SsgiIntensity),
        Ssr = new SsrSettings(old.SsrEnabled),
        Dof = new DepthOfFieldSettings(old.DofEnabled, old.DofFocusDistance, old.DofMaxBlurSize),
        // Shadows are new to the contract and have no legacy counterpart, so the
        // mapping uses the contract default rather than an old field.
        Shadows = NewEnv.Default.Shadows,
    };

    private static FogMode MapFog(V12.Core.Interfaces.Renderer.EnvironmentFogMode mode) => mode switch
    {
        V12.Core.Interfaces.Renderer.EnvironmentFogMode.Disabled => FogMode.None,
        V12.Core.Interfaces.Renderer.EnvironmentFogMode.Linear => FogMode.Linear,
        V12.Core.Interfaces.Renderer.EnvironmentFogMode.Exponential => FogMode.Exponential,
        V12.Core.Interfaces.Renderer.EnvironmentFogMode.ExponentialSquared => FogMode.ExponentialSquared,
        _ => FogMode.None,
    };

    private static TonemapMode MapTonemap(V12.Core.Interfaces.Renderer.EnvironmentTonemapMode mode) => mode switch
    {
        V12.Core.Interfaces.Renderer.EnvironmentTonemapMode.Linear => TonemapMode.Linear,
        V12.Core.Interfaces.Renderer.EnvironmentTonemapMode.Reinhard => TonemapMode.Reinhard,
        V12.Core.Interfaces.Renderer.EnvironmentTonemapMode.Filmic => TonemapMode.Filmic,
        V12.Core.Interfaces.Renderer.EnvironmentTonemapMode.Aces => TonemapMode.Aces,
        V12.Core.Interfaces.Renderer.EnvironmentTonemapMode.Agx => TonemapMode.Agx,
        _ => TonemapMode.Filmic,
    };

    private static BloomMode MapBloom(V12.Core.Interfaces.Renderer.EnvironmentBloomMode mode) => mode switch
    {
        V12.Core.Interfaces.Renderer.EnvironmentBloomMode.Disabled => BloomMode.Disabled,
        V12.Core.Interfaces.Renderer.EnvironmentBloomMode.Mix => BloomMode.Mix,
        V12.Core.Interfaces.Renderer.EnvironmentBloomMode.Additive => BloomMode.Additive,
        V12.Core.Interfaces.Renderer.EnvironmentBloomMode.Screen => BloomMode.Screen,
        _ => BloomMode.Disabled,
    };
}
