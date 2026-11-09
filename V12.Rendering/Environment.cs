using System.Numerics;

namespace V12.Rendering;

public enum FogMode
{
    None,
    Linear,
    Exponential,
    ExponentialSquared,
}

public enum TonemapMode
{
    Linear,
    Reinhard,
    Filmic,
    Aces,
    Agx,
}

public enum BloomMode
{
    Disabled,
    Mix,
    Additive,
    Screen,
}

public readonly record struct AmbientSettings(Vector3 Color, float Intensity);

public readonly record struct SkySettings(
    Vector3 Color,
    float Energy,
    bool SkyboxEnabled,
    TextureHandle? Skybox,
    float SkyboxBlur,
    Quaternion SkyboxRotation);

public readonly record struct FogSettings(
    FogMode Mode,
    Vector3 Color,
    float Start,
    float End,
    float Density,
    float SkyAffect,
    bool VolumetricEnabled,
    float VolumetricDensity,
    float VolumetricAnisotropy,
    float VolumetricLength,
    float VolumetricSkyAffect);

public readonly record struct TonemapSettings(
    TonemapMode Mode,
    float White,
    float Exposure,
    float Brightness,
    float Contrast,
    float Saturation);

public readonly record struct BloomSettings(
    BloomMode Mode,
    float Intensity,
    float Threshold,
    float SoftThreshold);

public readonly record struct AutoExposureSettings(
    bool Enabled,
    float MinEV,
    float MaxEV,
    float Compensation);

public readonly record struct SsaoSettings(bool Enabled, float Intensity, float Radius, float Power);

public readonly record struct SsilSettings(bool Enabled, float GiIntensity, float AoIntensity, float Radius);

public readonly record struct SsgiSettings(bool Enabled, float Intensity);

public readonly record struct SsrSettings(bool Enabled);

public readonly record struct DepthOfFieldSettings(bool Enabled, float FocusDistance, float MaxBlurSize);

/// <summary>Realtime shadow settings shared by every light in the scene.</summary>
public readonly record struct ShadowSettings(
    bool Enabled,
    float Distance,
    float Bias,
    float NormalBias,
    float Opacity);

/// <summary>
/// Typed environment description. Mirrors every field of the current
/// <c>RenderPacket.Environment</c>; the engine resolves source paths to textures
/// before filling this. <see cref="Default"/> matches the legacy defaults.
/// </summary>
public sealed record EnvironmentData
{
    public bool HasValue { get; init; }

    public AmbientSettings Ambient { get; init; }
    public SkySettings Sky { get; init; }
    public FogSettings Fog { get; init; }
    public TonemapSettings Tonemap { get; init; }
    public BloomSettings Bloom { get; init; }
    public AutoExposureSettings AutoExposure { get; init; }
    public SsaoSettings Ssao { get; init; }
    public SsilSettings Ssil { get; init; }
    public SsgiSettings Ssgi { get; init; }
    public SsrSettings Ssr { get; init; }
    public DepthOfFieldSettings Dof { get; init; }
    public ShadowSettings Shadows { get; init; }

    public static EnvironmentData Default { get; } = new()
    {
        HasValue = false,
        Ambient = new AmbientSettings(new Vector3(96f / 255f, 102f / 255f, 122f / 255f), 0.9f),
        Sky = new SkySettings(
            new Vector3(77f / 255f, 140f / 255f, 230f / 255f),
            1f,
            false,
            null,
            0f,
            Quaternion.Identity),
        Fog = new FogSettings(
            FogMode.None,
            new Vector3(128f / 255f, 128f / 255f, 128f / 255f),
            0f,
            100f,
            0f,
            0f,
            false,
            0f,
            0f,
            64f,
            0f),
        Tonemap = new TonemapSettings(TonemapMode.Filmic, 1f, 1f, 1f, 1f, 1f),
        Bloom = new BloomSettings(BloomMode.Disabled, 0f, 1f, 0f),
        AutoExposure = new AutoExposureSettings(false, -8f, 8f, 0f),
        Ssao = new SsaoSettings(false, 1f, 0.5f, 1f),
        Ssil = new SsilSettings(false, 1f, 1f, 1f),
        Ssgi = new SsgiSettings(false, 1f),
        Ssr = new SsrSettings(false),
        Dof = new DepthOfFieldSettings(false, 10f, 0.05f),
        Shadows = new ShadowSettings(true, 100f, 2f, 1f, 1f),
    };
}
