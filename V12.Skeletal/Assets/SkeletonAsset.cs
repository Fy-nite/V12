using System.Numerics;

namespace V12.Skeletal;

/// <summary>
/// Imported, backend-neutral skeletal asset. Bones are identified by name in
/// authoring and by index at runtime; vertex bone indices and inverse binds use
/// the same order.
/// </summary>
public sealed class SkeletonAsset
{
    public required IReadOnlyList<string> BoneNames { get; init; }

    /// <summary>Parent bone index per bone, or -1 for a root.</summary>
    public required IReadOnlyList<int> BoneParents { get; init; }

    /// <summary>Reference (bind) parent-space transform per bone.</summary>
    public required IReadOnlyList<Matrix4x4> ReferenceLocals { get; init; }

    /// <summary>Inverse bind pose per bone, in skeleton space.</summary>
    public required IReadOnlyList<Matrix4x4> InverseBindPoses { get; init; }

    public required IReadOnlyList<SkeletalClipData> Clips { get; init; }

    public required IReadOnlyList<SkeletalMeshPart> Parts { get; init; }

    /// <summary>Bounds of the reference pose after skinning.</summary>
    public required Vector3 BoundsMin { get; init; }

    public required Vector3 BoundsMax { get; init; }

    /// <summary>Non-fatal import messages.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public string? SourcePath { get; init; }

    public int BoneCount => BoneNames.Count;

    public float Height => MathF.Max(0f, BoundsMax.Y - BoundsMin.Y);

    /// <summary>Offset that puts the model's lowest point on y = 0.</summary>
    public float GroundOffset => -BoundsMin.Y;

    /// <summary>Bone index for a name, or -1.</summary>
    public int IndexOfBone(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return -1;

        for (int i = 0; i < BoneNames.Count; i++)
        {
            if (string.Equals(BoneNames[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    public SkeletalClipData? FindClip(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        foreach (SkeletalClipData clip in Clips)
        {
            if (string.Equals(clip.Name, name, StringComparison.OrdinalIgnoreCase))
                return clip;
        }
        return null;
    }
}
