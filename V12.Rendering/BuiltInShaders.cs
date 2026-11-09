namespace V12.Rendering;

/// <summary>
/// Fixed v1 parameter indices for the built-in shader family. These are the
/// indices used by <see cref="MaterialValue"/> and <see cref="DrawOverride"/>
/// until Slang reflection replaces them.
/// </summary>
public static class BuiltInMaterialParameters
{
    public const int Albedo = 0;
    public const int Metallic = 1;
    public const int Roughness = 2;
    public const int Emissive = 3;
    public const int EmissiveEnergy = 4;
    public const int AmbientOcclusion = 5;
    public const int Specular = 6;
    public const int NormalScale = 7;
    public const int AlphaCutoff = 8;
    public const int UvOffset = 9;
    public const int UvScale = 10;
    public const int Triplanar = 11;
    public const int TriplanarWorld = 12;
    public const int TriplanarSharpness = 13;
}

/// <summary>Fixed v1 texture slots for the built-in shader family.</summary>
public static class BuiltInTextureSlots
{
    public const int Albedo = 0;
    public const int Normal = 1;
    public const int Metallic = 2;
    public const int Roughness = 3;
    public const int Occlusion = 4;
    public const int Emission = 5;
}
