using System;
using System.Numerics;
using Assimp;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.Interfaces.Renderer;

namespace V12.Skeletal.Components;

/// <summary>
/// One drawable submesh of a <see cref="SkeletalManagerComponent"/>. Geometry
/// comes from the imported part; the pose, inverse binds and parent table come
/// from the manager, so every part shares one skeleton.
/// </summary>
[ComponentEntry("RENDER", "Skeletal Mesh Part", SingleInstance = true, SortOrder = 5, Icon = "mesh")]
public class SkeletalMeshPartComponent : ComponentBase, ISkinnedMeshRenderable
{
    public override string Name => "Skeletal Mesh Part";
    public override string Description => "A skinned submesh bound to its manager's skeleton";

    public SkeletalManagerComponent? Manager { get; set; }

    public SkeletalMeshPart? Part { get; set; }

    public double[] MeshPoints => Part?.Points ?? Array.Empty<double>();
    public double[]? MeshUvs => Part?.Uvs;
    public double[]? MeshNormals => Part?.Normals;
    public double[]? MeshTangents => Part?.Tangents;
    public uint[] Indices => Part?.Indices ?? Array.Empty<uint>();
    public Material Material { get; set; } = new();
    public RenderType RenderType => RenderType.Mesh;
    public bool IsWorldLocked => true;
    public TRS LocalTransform => default;
    public Matrix4x4 WorldTransform => Owner?.WorldTransform ?? Matrix4x4.Identity;

    public long SkeletonId => Manager?.Owner?.Id ?? 0;
    public Matrix4x4 SkeletonWorld => Manager?.Owner?.WorldTransform ?? Matrix4x4.Identity;
    public int[] BoneParents => Manager?.BoneParents ?? Array.Empty<int>();
    public Matrix4x4[] BoneLocals => Manager?.BoneLocals ?? Array.Empty<Matrix4x4>();
    public Matrix4x4[] InverseBindPoses => Manager?.InverseBindPoses ?? Array.Empty<Matrix4x4>();
    public float[] BoneWeights => Part?.BoneWeights ?? Array.Empty<float>();
    public int[] BoneIndices => Part?.BoneIndices ?? Array.Empty<int>();

    public override IWorldElement BuildUI() => new Element();
}

