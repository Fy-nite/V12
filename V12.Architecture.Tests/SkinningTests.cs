using System;
using System.Linq;
using System.Numerics;
using V12.Core.Interfaces.Renderer;
using V12.Core.Rendering;
using V12.Rendering;
using Xunit;
using NewEnv = V12.Rendering.EnvironmentData;

namespace V12.Architecture.Tests;

/// <summary>
/// Covers the skinning payload: the skinned vertex layout the factory builds and
/// the skin/bone data the producer emits for a skinned draw item.
/// </summary>
public sealed class SkinningTests
{
    [Fact]
    public void AnimationMath_TransposesProwlColumnMatrix()
    {
        // Column-vector matrix: c0..c2 are basis columns, c3 is translation.
        var source = new Prowl.Vector.Float4x4(
            new Prowl.Vector.Float4(1, 2, 3, 0),
            new Prowl.Vector.Float4(4, 5, 6, 0),
            new Prowl.Vector.Float4(7, 8, 9, 0),
            new Prowl.Vector.Float4(10, 11, 12, 1));

        Matrix4x4 result = V12.Skeletal.AnimationMath.ToMatrix(source);

        // Row 0 is the first basis column, and translation lands in the 4th row.
        Assert.Equal(1f, result.M11);
        Assert.Equal(2f, result.M12);
        Assert.Equal(3f, result.M13);
        Assert.Equal(10f, result.M41);
        Assert.Equal(11f, result.M42);
        Assert.Equal(12f, result.M43);
        Assert.Equal(1f, result.M44);
    }

    [Fact]
    public void SkinnedMeshData_CarriesBoneStreams()
    {
        var positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
        var uvs = new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY };
        var tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) };
        var colors = new[] { Vector4.One, Vector4.One, Vector4.One };
        var weights = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };
        var bones = new int[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        MeshData data = MeshDataFactory.CreateSkinned(
            positions, normals, uvs, tangents, colors, weights, bones, new uint[] { 0, 1, 2 });

        Assert.Equal(84, data.VertexStreams.Span[0].Stride);

        VertexAttribute[] layout = data.Layout.ToArray();
        Assert.Contains(layout, a => a.Semantic == VertexSemantic.Position && a.Offset == 0);
        Assert.Contains(layout, a => a.Semantic == VertexSemantic.SkinWeights && a.Offset == 64);
        Assert.Contains(layout, a => a.Semantic == VertexSemantic.SkinIndices && a.Offset == 80);
    }

    [Fact]
    public void Producer_EmitsSkinAndBonePose_ForSkinnedItem()
    {
        using var renderer = new NullRenderer();
        using var producer = new RenderFrameProducer(renderer);

        var snapshot = new FrameSnapshot();
        snapshot.Renderables.Add(new RenderableSnapshot
        {
            ElementId = 1,
            NodeType = SnapshotNodeType.Camera,
            Transform = Matrix4x4.Identity,
            IsCurrentCamera = true,
            Fov = 60f,
            NearClip = 0.1f,
            FarClip = 100f,
        });

        snapshot.Renderables.Add(new RenderableSnapshot
        {
            ElementId = 2,
            NodeType = SnapshotNodeType.MeshCustom,
            Transform = Matrix4x4.Identity,
            MeshPoints = new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
            MeshIndices = new uint[] { 0, 1, 2 },
            MatA = 1f,
            MatR = 1f,
            MatG = 1f,
            MatB = 1f,
            MeshBoneWeights = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
            MeshBoneIndices = new int[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            SkinId = 10,
            SkinWorld = Matrix4x4.CreateTranslation(0, 1, 0),
            SkinBoneParents = new[] { -1 },
            SkinBoneLocals = new[]
            {
                Matrix4x4.CreateTranslation(0, 1, 0),
            },
            SkinInverseBinds = new[] { Matrix4x4.Identity },
        });

        Assert.True(producer.TryBuild(snapshot, NewEnv.Default, 0f, 0.016f, out var lease));
        var frame = lease!.Frame;

        Assert.Equal(1, frame.Skins.Length);
        Assert.Equal(1, frame.BoneTransforms.Length);
        Assert.Equal(0, frame.Items.Span[0].SkinIndex);

        SkinItem skin = frame.Skins.Span[0];
        Assert.Equal(10, skin.ElementId);
        Assert.Equal(1, skin.BoneCount);
        Assert.Equal(0, skin.BoneStart);
        Assert.Equal(1f, frame.BoneTransforms.Span[0].M42, 3);

        renderer.Submit(lease);
    }
}
