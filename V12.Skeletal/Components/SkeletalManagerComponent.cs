using System;
using System.Collections.Generic;
using System.Numerics;
using V12.Components;
using V12.Components.Renderables;
using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Core.UI;

namespace V12.Skeletal.Components;

/// <summary>
/// Owns one skeletal asset: the skeleton, clips, runtime animator and the
/// generated part elements. Playback state lives on
/// <see cref="SkeletalPlayerComponent"/>; this component is the shared rig.
/// </summary>
[ComponentEntry("RENDER", "Skeletal Manager", SingleInstance = true, SortOrder = 6, Icon = "asset")]
public class SkeletalManagerComponent : ComponentBase, IDisposable
{
    private readonly List<IWorldElement> _generated = new();

    private string? _assetPath;
    private SkeletonAsset? _asset;
    private ISkeletalAnimator? _animator;
    private Matrix4x4[] _boneLocals = Array.Empty<Matrix4x4>();
    private Matrix4x4[] _inverseBindPoses = Array.Empty<Matrix4x4>();
    private int[] _boneParents = Array.Empty<int>();
    private bool _rebuilding;

    public override string Name => "Skeletal Manager";
    public override string Description => "Skeletal rig, clips, parts and animator";

    /// <summary>Path to a model: <c>v12://</c>, <c>res://</c>, or a plain file path.</summary>
    public string? AssetPath
    {
        get => _assetPath;
        set
        {
            if (_assetPath == value)
                return;
            _assetPath = value;
            MarkDirty();
            Rebuild();
        }
    }

    /// <summary>Importer name. Null selects the default (Clay).</summary>
    public string? Importer { get; set; }

    /// <summary>Runtime backend name. Null selects the default (Prowl.Motion).</summary>
    public string? Backend { get; set; }

    public float SampleRate { get; set; } = 30f;

    public string? PlayOnLoad { get; set; }

    public bool LoopOnLoad { get; set; } = true;

    public bool AutoPlay { get; set; } = true;

    public SkeletonAsset? Asset => _asset;
    public ISkeletalAnimator? Animator => _animator;
    public IReadOnlyList<IWorldElement> GeneratedElements => _generated;

    public SkeletalPlayerComponent? Player { get; set; }

    public IReadOnlyList<Matrix4x4> LocalPose => _animator?.LocalPose ?? Array.Empty<Matrix4x4>();
    public Matrix4x4[] BoneLocals => _boneLocals;
    public Matrix4x4[] InverseBindPoses => _inverseBindPoses;
    public int[] BoneParents => _boneParents;

    public Vector3 BoundsMin => _asset?.BoundsMin ?? Vector3.Zero;
    public Vector3 BoundsMax => _asset?.BoundsMax ?? Vector3.Zero;
    public float Height => _asset?.Height ?? 0f;
    public float GroundOffset => _asset?.GroundOffset ?? 0f;

    public IReadOnlyList<string> Clips => _animator?.Clips ?? Array.Empty<string>();
    public string? CurrentClip => _animator?.CurrentClip;
    public float Duration => _animator?.Duration ?? 0f;
    public bool IsPlaying => _animator?.IsPlaying ?? false;

    /// <summary>Playback position in seconds. Assigning seeks immediately.</summary>
    public float Time
    {
        get => _animator?.Time ?? 0f;
        set => Seek(value);
    }

    public float Speed
    {
        get => _animator?.Speed ?? 1f;
        set
        {
            if (_animator is not null)
                _animator.Speed = value;
        }
    }

    public bool Loop
    {
        get => _animator?.Loop ?? true;
        set
        {
            if (_animator is not null)
                _animator.Loop = value;
        }
    }

    public override void OnAttach(IWorldElement worldElement)
    {
        base.OnAttach(worldElement);
        Rebuild();
    }

    public override void Update(float deltaTime)
    {
        if (_animator is null)
            return;

        _animator.Update(deltaTime);
        CopyPose();
        MarkDirty();
    }

    public bool Play(string clip, bool loop = true, float blendTime = 0f)
    {
        if (_animator is null ||
            !_animator.Clips.Any(c => string.Equals(c, clip, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _animator.Play(clip, loop, blendTime);
        CopyPose();
        return true;
    }

    public bool CrossFade(string clip, float duration, bool loop = true) =>
        Play(clip, loop, duration);

    public void Seek(float seconds)
    {
        _animator?.Seek(seconds);
        CopyPose();
    }

    public void Pause() => _animator?.Pause();

    public void Resume() => _animator?.Resume();

    /// <summary>Bone index for a name, or -1.</summary>
    public int IndexOfBone(string? name) => _asset?.IndexOfBone(name) ?? -1;

    /// <summary>
    /// Model-space transform of a bone in the current pose. Walks the parent
    /// chain on demand; use it for attachments, aiming and gameplay probes.
    /// </summary>
    public Matrix4x4 GetBoneModelMatrix(int boneIndex)
    {
        if (_animator is null || boneIndex < 0 || boneIndex >= _boneLocals.Length)
            return Matrix4x4.Identity;

        Matrix4x4 model = _boneLocals[boneIndex];
        int parent = _boneParents[boneIndex];
        while (parent >= 0 && parent < _boneLocals.Length)
        {
            model *= _boneLocals[parent];
            parent = _boneParents[parent];
        }
        return model;
    }

    public void Rebuild()
    {
        if (_rebuilding)
            return;

        var owner = Owner;
        if (owner is null)
            return;

        _rebuilding = true;
        try
        {
            ClearGenerated(owner);
            _animator?.Dispose();
            _animator = null;
            _asset = null;
            _boneLocals = Array.Empty<Matrix4x4>();
            _inverseBindPoses = Array.Empty<Matrix4x4>();
            _boneParents = Array.Empty<int>();

            if (string.IsNullOrWhiteSpace(_assetPath))
                return;

            string resolved = ModelLoader.ResolvePath(_assetPath!);
            SkeletonAsset? asset;
            try
            {
                asset = SkeletalImporters.Resolve(Importer).Import(resolved, SampleRate);
            }
            catch (Exception ex)
            {
                GameRoot.Log?.Error(ex, "[SkeletalManager] Could not import '{Asset}'", _assetPath);
                return;
            }

            if (asset is null)
            {
                GameRoot.Log?.Warning(
                    "[SkeletalManager] '{Asset}' has no skin, so nothing can be skinned.",
                    _assetPath);
                return;
            }

            foreach (string warning in asset.Warnings)
                GameRoot.Log?.Warning("[SkeletalManager] {Warning}", warning);

            _asset = asset;
            _boneParents = asset.BoneParents.ToArray();
            _inverseBindPoses = asset.InverseBindPoses.ToArray();
            _boneLocals = new Matrix4x4[asset.BoneCount];
            for (int i = 0; i < _boneLocals.Length; i++)
                _boneLocals[i] = asset.ReferenceLocals[i];

            _animator = SkeletalBackends.Resolve(Backend).Create(asset);
            CopyPose();

            GameRoot.Log?.Information(
                "[SkeletalManager] '{Asset}': {Bones} bones, {Clips} clip(s), height {Height:F2}, feet offset {Offset:F2}, backend {Backend}",
                _assetPath,
                asset.BoneCount,
                asset.Clips.Count,
                asset.Height,
                asset.GroundOffset,
                _animator.GetType().Name);

            string? initial = !string.IsNullOrWhiteSpace(PlayOnLoad)
                ? PlayOnLoad
                : AutoPlay && asset.Clips.Count > 0
                    ? asset.Clips[0].Name
                    : null;

            if (initial is not null)
                Play(initial, LoopOnLoad);

            foreach (SkeletalMeshPart part in asset.Parts)
                CreatePart(owner, part);
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void CreatePart(IWorldElement owner, SkeletalMeshPart part)
    {
        var element = new Element(part.Name);
        element.AddComponent(new NonPersistentComponent());
        element.AddComponent(new SkeletalMeshPartComponent
        {
            Manager = this,
            Part = part,
        });
        element.AddComponent(MaterialComponent.FromModelMaterial(part.Material, needsTriplanar: false));

        if (owner is Element parent)
            parent.AddChild(element);
        else
            owner.AddChild(element);

        _generated.Add(element);
    }

    private void ClearGenerated(IWorldElement owner)
    {
        foreach (IWorldElement element in _generated)
        {
            if (owner is Element parent)
                parent.RemoveChild(element);
        }
        _generated.Clear();
    }

    private void CopyPose()
    {
        if (_animator is null)
            return;

        IReadOnlyList<Matrix4x4> pose = _animator.LocalPose;
        if (_boneLocals.Length != pose.Count)
            _boneLocals = new Matrix4x4[pose.Count];

        for (int i = 0; i < pose.Count; i++)
            _boneLocals[i] = pose[i];
    }

    public override void OnDetach(IWorldElement worldElement)
    {
        Dispose();
        base.OnDetach(worldElement);
    }

    public void Dispose()
    {
        if (Owner is { } owner)
            ClearGenerated(owner);

        _animator?.Dispose();
        _animator = null;
        _asset = null;
        _boneLocals = Array.Empty<Matrix4x4>();
        _inverseBindPoses = Array.Empty<Matrix4x4>();
        _boneParents = Array.Empty<int>();
    }

    public override IWorldElement BuildUI() => new Element();
}
