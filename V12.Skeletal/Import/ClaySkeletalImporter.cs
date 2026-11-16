using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using Prowl.Clay;
using Prowl.Clay.Importer;
using Prowl.Vector;
using ModelMaterial = V12.Components.Renderables.ModelMaterial;
using ModelNode = Prowl.Clay.ModelNode;
using NQuaternion = System.Numerics.Quaternion;

namespace V12.Skeletal.Import;

/// <summary>
/// Default importer: Prowl.Clay reads glTF/GLB/VRM/OBJ/FBX into a model graph,
/// and this class flattens it into <see cref="SkeletonAsset"/>.
///
/// Non-joint ancestors (a glTF Armature, pivot groups) are folded into each
/// bone's local transform. Dropping them leaves the mesh in raw mesh space and
/// the character renders sideways.
/// </summary>
public sealed class ClaySkeletalImporter : ISkeletalAssetImporter
{
    public string Name => "clay";

    public SkeletonAsset? Import(string path, float sampleRate = 30f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Model model = ModelImporter.Load(path, ModelImporterSettings.GameQuality);
        return FromModel(model, sampleRate, path);
    }

    public SkeletonAsset? FromModel(Model model, float sampleRate = 30f, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (sampleRate <= 0f)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");

        Skin? skin = SelectSkin(model);
        if (skin is null || skin.BoneNodeIndices.Length == 0)
            return null;

        int boneCount = skin.BoneNodeIndices.Length;
        var boneToJoint = new Dictionary<int, int>(boneCount);
        for (int i = 0; i < boneCount; i++)
            boneToJoint[skin.BoneNodeIndices[i]] = i;

        var boneNames = new string[boneCount];
        var parents = new int[boneCount];
        var referenceLocals = new Matrix4x4[boneCount];
        var inverseBinds = new Matrix4x4[boneCount];
        var boneAncestors = new Float4x4[boneCount];
        var nodeDefaults = new TransformTRS[boneCount];
        var usedNames = new HashSet<string>(StringComparer.Ordinal);

        for (int bone = 0; bone < boneCount; bone++)
        {
            ModelNode node = model.Nodes[skin.BoneNodeIndices[bone]];
            boneNames[bone] = UniqueName(
                string.IsNullOrWhiteSpace(node.Name) ? $"bone_{bone}" : node.Name,
                usedNames);

            Float4x4 chain = ComposeToParentJoint(
                node,
                boneToJoint,
                out int parentBone,
                out Float4x4 ancestorChain);

            parents[bone] = parentBone;
            referenceLocals[bone] = AnimationMath.ToMatrix(chain);
            boneAncestors[bone] = ancestorChain;
            nodeDefaults[bone] = new TransformTRS(
                new Vector3(node.LocalPosition.X, node.LocalPosition.Y, node.LocalPosition.Z),
                new NQuaternion(
                    node.LocalRotation.X,
                    node.LocalRotation.Y,
                    node.LocalRotation.Z,
                    node.LocalRotation.W),
                new Vector3(node.LocalScale.X, node.LocalScale.Y, node.LocalScale.Z));

            inverseBinds[bone] = bone < skin.InverseBindPoses.Length
                ? AnimationMath.ToMatrix(skin.InverseBindPoses[bone])
                : Matrix4x4.Identity;
        }

        var warnings = new List<string>();
        if (boneCount > 255)
        {
            warnings.Add(
                $"Skeleton has {boneCount} bones; the skin index format is 8-bit, so " +
                "bone indices above 255 cannot be represented.");
        }

        IReadOnlyList<SkeletalMeshPart> parts = BuildParts(model, skin, warnings);

        var clips = new List<SkeletalClipData>();
        var usedClipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int clipIndex = 0;
        foreach (Prowl.Clay.AnimationClip clayClip in model.AnimationClips)
        {
            SkeletalClipData? clip = ConvertClip(
                clayClip,
                boneCount,
                boneToJoint,
                boneAncestors,
                nodeDefaults,
                sampleRate,
                warnings);

            if (clip is null)
            {
                clipIndex++;
                continue;
            }

            string name = string.IsNullOrWhiteSpace(clayClip.Name)
                ? $"clip_{clipIndex}"
                : UniqueName(clayClip.Name, usedClipNames);

            clips.Add(new SkeletalClipData
            {
                Name = name,
                Duration = clip.Duration,
                SampleRate = clip.SampleRate,
                BoneCount = clip.BoneCount,
                Frames = clip.Frames,
                Loop = clip.Loop,
            });
            clipIndex++;
        }

        ComputeSkinnedBounds(
            parts,
            boneCount,
            parents,
            referenceLocals,
            inverseBinds,
            out Vector3 boundsMin,
            out Vector3 boundsMax);

        foreach (ImportLogEntry entry in model.Log.Entries)
        {
            if (entry.Severity is ImportLogSeverity.Warning or ImportLogSeverity.Error)
                warnings.Add(entry.ToString());
        }

        return new SkeletonAsset
        {
            BoneNames = boneNames,
            BoneParents = parents,
            ReferenceLocals = referenceLocals,
            InverseBindPoses = inverseBinds,
            Clips = clips,
            Parts = parts,
            BoundsMin = boundsMin,
            BoundsMax = boundsMax,
            Warnings = warnings,
            SourcePath = sourcePath ?? model.SourcePath,
        };
    }

    private readonly record struct TransformTRS(Vector3 Position, NQuaternion Rotation, Vector3 Scale);

    /// <summary>
    /// The skeleton host is the skin with the most joints. Multi-skin models
    /// (character + weapon) import one skeleton at a time.
    /// </summary>
    private static Skin? SelectSkin(Model model)
    {
        Skin? best = null;
        foreach (Skin skin in model.Skins)
        {
            if (best is null || skin.BoneNodeIndices.Length > best.BoneNodeIndices.Length)
                best = skin;
        }
        return best;
    }

    private static IReadOnlyList<SkeletalMeshPart> BuildParts(
        Model model,
        Skin skin,
        List<string> warnings)
    {
        int skinIndex = -1;
        for (int i = 0; i < model.Skins.Count; i++)
        {
            if (ReferenceEquals(model.Skins[i], skin))
            {
                skinIndex = i;
                break;
            }
        }

        if (skinIndex < 0)
            return Array.Empty<SkeletalMeshPart>();

        var parts = new List<SkeletalMeshPart>();

        foreach (ModelNode node in model.Nodes)
        {
            if (node.SkinIndex != skinIndex || node.MeshIndex < 0)
                continue;
            if (node.MeshIndex >= model.Meshes.Count)
                continue;

            Mesh mesh = model.Meshes[node.MeshIndex];
            if (mesh.BoneWeights is null || mesh.BoneWeights.Length != mesh.VertexCount)
            {
                warnings.Add($"Skinned mesh '{mesh.Name}' has no usable bone weights.");
                continue;
            }

            double[] points = Flatten(mesh.Vertices);
            double[]? normals = mesh.Normals is { } normalArray ? Flatten(normalArray) : null;
            double[]? uvs = mesh.UVs.Length > 0 && mesh.UVs[0] is { } uvArray
                ? Flatten(uvArray)
                : null;
            double[]? tangents = mesh.Tangents is { } tangentArray ? Flatten(tangentArray) : null;

            var weights = new float[mesh.VertexCount * 4];
            var boneIndices = new int[mesh.VertexCount * 4];
            for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                BoneWeight bone = mesh.BoneWeights[vertex];
                int o = vertex * 4;

                weights[o + 0] = bone.Weight0;
                weights[o + 1] = bone.Weight1;
                weights[o + 2] = bone.Weight2;
                weights[o + 3] = bone.Weight3;

                boneIndices[o + 0] = bone.Index0;
                boneIndices[o + 1] = bone.Index1;
                boneIndices[o + 2] = bone.Index2;
                boneIndices[o + 3] = bone.Index3;

                float sum = weights[o] + weights[o + 1] + weights[o + 2] + weights[o + 3];
                if (sum > 1e-6f)
                {
                    weights[o + 0] /= sum;
                    weights[o + 1] /= sum;
                    weights[o + 2] /= sum;
                    weights[o + 3] /= sum;
                }
                else
                {
                    weights[o + 0] = 1f;
                }
            }

            string baseName = string.IsNullOrWhiteSpace(node.Name) ? mesh.Name : node.Name;

            for (int subMesh = 0; subMesh < mesh.SubMeshes.Length; subMesh++)
            {
                SubMesh sub = mesh.SubMeshes[subMesh];
                if (sub.Topology != PrimitiveTopology.Triangles || sub.IndexCount < 3)
                    continue;

                uint[] indices = mesh.GetIndices32(subMesh);
                if (sub.BaseVertex != 0)
                {
                    for (int i = 0; i < indices.Length; i++)
                        indices[i] = (uint)(indices[i] + sub.BaseVertex);
                }

                parts.Add(new SkeletalMeshPart
                {
                    Name = mesh.SubMeshes.Length == 1 ? baseName : $"{baseName}_{subMesh}",
                    Points = points,
                    Uvs = uvs,
                    Normals = normals,
                    Tangents = tangents,
                    Indices = indices,
                    BoneWeights = weights,
                    BoneIndices = boneIndices,
                    Material = MapMaterial(model, sub.MaterialIndex),
                });
            }
        }

        return parts;
    }

    private static ModelMaterial MapMaterial(Model model, int materialIndex)
    {
        if (materialIndex < 0 || materialIndex >= model.Materials.Count)
            return new ModelMaterial();

        Material material = model.Materials[materialIndex];

        return new ModelMaterial
        {
            Name = material.Name,
            Albedo = new Vector4(
                material.BaseColor.R,
                material.BaseColor.G,
                material.BaseColor.B,
                material.BaseColor.A),
            Metallic = material.Metallic,
            Roughness = material.Roughness,
            Emission = new Vector4(
                material.EmissiveFactor.R,
                material.EmissiveFactor.G,
                material.EmissiveFactor.B,
                1f),
            EmissionStrength = material.EmissiveStrength,
            NormalScale = material.NormalScale,
            AlphaCutoff = material.AlphaCutoff,
            AlphaBlend = material.AlphaMode == MaterialAlphaMode.Blend,
            DoubleSided = material.DoubleSided,
            Unlit = material.Unlit,
            AlbedoTexture = ResolveTexture(model, material.BaseColorTexture),
            NormalTexture = ResolveTexture(model, material.NormalTexture),
            MetallicTexture = ResolveTexture(model, material.MetallicRoughnessTexture),
            RoughnessTexture = ResolveTexture(model, material.MetallicRoughnessTexture),
            EmissionTexture = ResolveTexture(model, material.EmissiveTexture),
            OcclusionTexture = ResolveTexture(model, material.OcclusionTexture),
        };
    }

    private static string? ResolveTexture(Model model, MaterialTextureSlot? slot)
    {
        if (slot is null)
            return null;
        if (slot.TextureIndex < 0 || slot.TextureIndex >= model.Textures.Count)
            return null;

        Texture texture = model.Textures[slot.TextureIndex];
        if (!string.IsNullOrWhiteSpace(texture.SourcePath))
            return texture.SourcePath;

        if (texture.EncodedBytes is { Length: > 0 } bytes)
            return CacheTexture(bytes, texture.MimeType);

        return null;
    }

    private static string CacheTexture(byte[] bytes, string? mimeType)
    {
        string extension = mimeType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/ktx2" => ".ktx2",
            _ => ".bin",
        };

        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string directory = Path.Combine(Path.GetTempPath(), "v12-textures");
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, hash + extension);
        if (!File.Exists(path))
            File.WriteAllBytes(path, bytes);
        return path;
    }

    private static SkeletalClipData? ConvertClip(
        Prowl.Clay.AnimationClip clayClip,
        int boneCount,
        IReadOnlyDictionary<int, int> boneToJoint,
        Float4x4[] boneAncestors,
        TransformTRS[] nodeDefaults,
        float sampleRate,
        List<string> warnings)
    {
        var bindings = new List<(AnimatedProperty Property, AnimationCurve Curve)>?[boneCount];

        foreach (AnimationBinding binding in clayClip.Bindings)
        {
            if (binding.Property is not (
                    AnimatedProperty.Position or
                    AnimatedProperty.Rotation or
                    AnimatedProperty.Scale))
            {
                continue;
            }

            if (!boneToJoint.TryGetValue(binding.NodeIndex, out int bone))
                continue;

            (bindings[bone] ??= new List<(AnimatedProperty, AnimationCurve)>())
                .Add((binding.Property, binding.Curve));
        }

        bool hasBindings = false;
        foreach (var list in bindings)
        {
            if (list is { Count: > 0 })
            {
                hasBindings = true;
                break;
            }
        }

        if (!hasBindings || !(clayClip.Duration > 0f))
            return null;

        float duration = clayClip.Duration;
        int frameCount = Math.Max(2, (int)MathF.Ceiling(duration * sampleRate) + 1);
        var frames = new BoneLocal[frameCount * boneCount];

        for (int frame = 0; frame < frameCount; frame++)
        {
            float time = Math.Min(clayClip.StartTime + frame / sampleRate, clayClip.EndTime);

            for (int bone = 0; bone < boneCount; bone++)
            {
                TransformTRS nodeLocal = nodeDefaults[bone];

                if (bindings[bone] is { } boneBindings)
                {
                    foreach (var (property, curve) in boneBindings)
                    {
                        switch (property)
                        {
                            case AnimatedProperty.Position:
                                nodeLocal = nodeLocal with
                                {
                                    Position = ToVector3(curve.EvaluateFloat3(time)),
                                };
                                break;
                            case AnimatedProperty.Rotation:
                                Prowl.Vector.Quaternion raw =
                                    curve.EvaluateQuaternion(time);
                                var rotation = new NQuaternion(
                                    raw.X, raw.Y, raw.Z, raw.W);
                                if (rotation.Length() > 1e-4f)
                                    nodeLocal = nodeLocal with { Rotation = NQuaternion.Normalize(rotation) };
                                break;
                            case AnimatedProperty.Scale:
                                nodeLocal = nodeLocal with
                                {
                                    Scale = ToVector3(curve.EvaluateFloat3(time)),
                                };
                                break;
                        }
                    }
                }

                Float4x4 nodeMatrix = Float4x4.CreateTRS(
                    new Float3(nodeLocal.Position.X, nodeLocal.Position.Y, nodeLocal.Position.Z),
                    new Prowl.Vector.Quaternion(
                        nodeLocal.Rotation.X,
                        nodeLocal.Rotation.Y,
                        nodeLocal.Rotation.Z,
                        nodeLocal.Rotation.W),
                    new Float3(nodeLocal.Scale.X, nodeLocal.Scale.Y, nodeLocal.Scale.Z));

                Matrix4x4 local = AnimationMath.ToMatrix(boneAncestors[bone] * nodeMatrix);
                frames[frame * boneCount + bone] = BoneLocal.FromMatrix(local);
            }
        }

        if (frameCount > 4096)
        {
            warnings.Add(
                $"Clip '{clayClip.Name}' sampled to {frameCount} frames at {sampleRate} Hz; " +
                "consider a lower sample rate.");
        }

        return new SkeletalClipData
        {
            Name = clayClip.Name,
            Duration = duration,
            SampleRate = sampleRate,
            BoneCount = boneCount,
            Frames = frames,
            Loop = true,
        };
    }

    private static Vector3 ToVector3(Float3 value) => new(value.X, value.Y, value.Z);

    private static Float4x4 ComposeToParentJoint(
        ModelNode node,
        IReadOnlyDictionary<int, int> boneToJoint,
        out int parentBone,
        out Float4x4 ancestorChain)
    {
        parentBone = -1;

        ModelNode? parentJoint = null;
        for (ModelNode? ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (boneToJoint.TryGetValue(ancestor.Index, out int bone))
            {
                parentBone = bone;
                parentJoint = ancestor;
                break;
            }
        }

        ancestorChain = Float4x4.Identity;
        for (ModelNode? ancestor = node.Parent;
             ancestor is not null && !ReferenceEquals(ancestor, parentJoint);
             ancestor = ancestor.Parent)
        {
            ancestorChain = ancestor.LocalMatrix * ancestorChain;
        }

        return ancestorChain * node.LocalMatrix;
    }

    private static void ComputeSkinnedBounds(
        IReadOnlyList<SkeletalMeshPart> parts,
        int boneCount,
        int[] parents,
        Matrix4x4[] referenceLocals,
        Matrix4x4[] inverseBinds,
        out Vector3 min,
        out Vector3 max)
    {
        var model = new Matrix4x4[boneCount];
        var skin = new Matrix4x4[boneCount];
        for (int bone = 0; bone < boneCount; bone++)
        {
            int parent = parents[bone];
            model[bone] = parent >= 0 ? referenceLocals[bone] * model[parent] : referenceLocals[bone];
            skin[bone] = model[bone] * inverseBinds[bone];
        }

        min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        foreach (SkeletalMeshPart part in parts)
        {
            int vertexCount = part.Points.Length / 3;
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                var point = new Vector3(
                    (float)part.Points[vertex * 3],
                    (float)part.Points[vertex * 3 + 1],
                    (float)part.Points[vertex * 3 + 2]);

                var skinned = Vector3.Zero;
                float total = 0f;
                for (int influence = 0; influence < 4; influence++)
                {
                    float weight = part.BoneWeights[vertex * 4 + influence];
                    if (weight <= 0f)
                        continue;

                    int bone = part.BoneIndices[vertex * 4 + influence];
                    if (bone < 0 || bone >= boneCount)
                        continue;

                    skinned += Vector3.Transform(point, skin[bone]) * weight;
                    total += weight;
                }

                skinned = total > 0f ? skinned / total : point;
                min = Vector3.Min(min, skinned);
                max = Vector3.Max(max, skinned);
            }
        }

        if (min.X > max.X)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
        }
    }

    private static double[] Flatten(Float3[] values)
    {
        var result = new double[values.Length * 3];
        for (int i = 0; i < values.Length; i++)
        {
            result[i * 3 + 0] = values[i].X;
            result[i * 3 + 1] = values[i].Y;
            result[i * 3 + 2] = values[i].Z;
        }
        return result;
    }

    private static double[] Flatten(Float2[] values)
    {
        var result = new double[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            result[i * 2 + 0] = values[i].X;
            result[i * 2 + 1] = values[i].Y;
        }
        return result;
    }

    private static double[] Flatten(Float4[] values)
    {
        var result = new double[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            result[i * 4 + 0] = values[i].X;
            result[i * 4 + 1] = values[i].Y;
            result[i * 4 + 2] = values[i].Z;
            result[i * 4 + 3] = values[i].W;
        }
        return result;
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        if (used.Add(name))
            return name;

        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{name}_{suffix}";
            if (used.Add(candidate))
                return candidate;
        }
    }
}
