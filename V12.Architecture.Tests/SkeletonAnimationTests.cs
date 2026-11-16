using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using V12.Skeletal;
using V12.Skeletal.Import;
using Xunit;

namespace V12.Architecture.Tests;

/// <summary>
/// Covers the skeletal module: playback/seek through the backend abstraction,
/// backend swapping, and the optional Clay import fixture.
/// </summary>
public sealed class SkeletonAnimationTests
{
    [Fact]
    public void Animator_PlaysSeekAndPauses()
    {
        SkeletonAsset asset = BuildTwoBoneAsset();
        using ISkeletalAnimator animator = SkeletalBackends.Default.Create(asset);

        animator.Play("move", loop: false);
        animator.Update(0.5f);

        Assert.Equal("move", animator.CurrentClip);
        Assert.InRange(animator.LocalPose[0].M41, 0.4f, 1.6f);

        animator.Seek(0.25f);

        Assert.Equal(0.25f, animator.Time, 3);
        Assert.InRange(animator.LocalPose[0].M41, 0.4f, 0.6f);

        animator.Pause();
        float frozen = animator.LocalPose[0].M41;
        animator.Update(0.5f);

        Assert.False(animator.IsPlaying);
        Assert.Equal(frozen, animator.LocalPose[0].M41, 4);
    }

    [Fact]
    public void BackendRegistry_AllowsSwappingTheRuntime()
    {
        var stub = new StubBackend();
        SkeletalBackends.Register(stub);

        ISkeletalBackend resolved = SkeletalBackends.Resolve("stub");
        Assert.Same(stub, resolved);

        using ISkeletalAnimator animator = resolved.Create(BuildTwoBoneAsset());
        Assert.IsType<StubAnimator>(animator);
    }

    /// <summary>
    /// Loads a real skinned model when one is provided. The fixture is not
    /// committed, so this is a no-op by default and a manual integration check
    /// when <c>V12_SKINNED_MODEL</c> points at a .glb/.gltf/.fbx.
    /// </summary>
    [Fact]
    public void ClayImport_ReadsSkeletonClipsAndParts_WhenFixtureProvided()
    {
        string? path = Environment.GetEnvironmentVariable("V12_SKINNED_MODEL");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        SkeletonAsset? asset = new ClaySkeletalImporter().Import(path, sampleRate: 30f);

        Assert.NotNull(asset);
        Assert.True(asset!.BoneCount > 1);
        Assert.Equal(asset.BoneCount, asset.BoneParents.Count);
        Assert.Equal(asset.BoneCount, asset.InverseBindPoses.Count);
        Assert.NotEmpty(asset.Parts);
        Assert.NotEmpty(asset.Clips);
        Assert.True(asset.Height > 0f);

        foreach (string name in asset.BoneNames)
            Assert.False(string.IsNullOrWhiteSpace(name));

        foreach (SkeletalMeshPart part in asset.Parts)
        {
            int vertexCount = part.Points.Length / 3;
            Assert.True(vertexCount > 0);
            Assert.Equal(vertexCount * 4, part.BoneWeights.Length);
            Assert.Equal(vertexCount * 4, part.BoneIndices.Length);
            Assert.True(part.Indices.Length >= 3);
        }
    }

    private static SkeletonAsset BuildTwoBoneAsset()
    {
        var frames = new BoneLocal[]
        {
            new BoneLocal(Vector3.Zero, Quaternion.Identity, Vector3.One),
            new BoneLocal(new Vector3(0f, 1f, 0f), Quaternion.Identity, Vector3.One),
            new BoneLocal(new Vector3(2f, 0f, 0f), Quaternion.Identity, Vector3.One),
            new BoneLocal(new Vector3(0f, 1f, 0f), Quaternion.Identity, Vector3.One),
        };

        var clip = new SkeletalClipData
        {
            Name = "move",
            Duration = 1f,
            SampleRate = 2f,
            BoneCount = 2,
            Frames = frames,
            Loop = false,
        };

        return new SkeletonAsset
        {
            BoneNames = new[] { "Root", "Spine" },
            BoneParents = new[] { -1, 0 },
            ReferenceLocals = new[]
            {
                Matrix4x4.Identity,
                Matrix4x4.CreateTranslation(0f, 1f, 0f),
            },
            InverseBindPoses = new[]
            {
                Matrix4x4.Identity,
                Matrix4x4.CreateTranslation(0f, -1f, 0f),
            },
            Clips = new[] { clip },
            Parts = Array.Empty<SkeletalMeshPart>(),
            BoundsMin = Vector3.Zero,
            BoundsMax = new Vector3(2f, 1f, 0f),
            Warnings = Array.Empty<string>(),
        };
    }

    private sealed class StubBackend : ISkeletalBackend
    {
        public string Name => "stub";

        public ISkeletalAnimator Create(SkeletonAsset asset) => new StubAnimator();
    }

    private sealed class StubAnimator : ISkeletalAnimator
    {
        public IReadOnlyList<Matrix4x4> LocalPose { get; } = Array.Empty<Matrix4x4>();
        public int BoneCount => 0;
        public IReadOnlyList<string> Clips { get; } = Array.Empty<string>();
        public string? CurrentClip => null;
        public float Time { get; set; }
        public float Duration => 0f;
        public float Speed { get; set; } = 1f;
        public bool Loop { get; set; }
        public bool IsPlaying => false;
        public void Play(string clip, bool loop = true, float blendTime = 0f) { }
        public void CrossFade(string clip, float duration, bool loop = true) { }
        public void Seek(float seconds) => Time = seconds;
        public void Pause() { }
        public void Resume() { }
        public void Update(float deltaTime) { }
        public void Dispose() { }
    }
}
