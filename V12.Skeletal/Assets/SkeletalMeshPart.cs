using V12.Components.Renderables;

namespace V12.Skeletal;

/// <summary>
/// One drawable piece of a skeletal model: a submesh in the model's vertex
/// space, four skin weights per vertex, and the material authored for it.
/// </summary>
public sealed class SkeletalMeshPart
{
    public required string Name { get; init; }

    /// <summary>Flat positions [x, y, z, ...].</summary>
    public required double[] Points { get; init; }

    public double[]? Uvs { get; init; }

    public double[]? Normals { get; init; }

    /// <summary>Flat tangents [x, y, z, w, ...].</summary>
    public double[]? Tangents { get; init; }

    public required uint[] Indices { get; init; }

    /// <summary>Four normalized weights per vertex.</summary>
    public required float[] BoneWeights { get; init; }

    /// <summary>Four bone indices per vertex, in the skeleton's bone order.</summary>
    public required int[] BoneIndices { get; init; }

    public required ModelMaterial Material { get; init; }
}
