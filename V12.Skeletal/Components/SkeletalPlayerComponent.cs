using System;
using System.Numerics;
using V12.Components;
using V12.Core;
using V12.Core.Core.Interfaces;

namespace V12.Skeletal.Components;

/// <summary>
/// Playback state for one character. It forwards to the shared
/// <see cref="SkeletalManagerComponent"/>, but the clip, time, speed, loop and
/// pause state belong here so a scene can drive several characters
/// independently while they share an asset.
/// </summary>
[ComponentEntry("GAMEPLAY", "Skeletal Player", SingleInstance = true, SortOrder = 7, Icon = "play")]
public class SkeletalPlayerComponent : ComponentBase
{
    private SkeletalManagerComponent? _manager;
    private bool _applied;

    public override string Name => "Skeletal Player";
    public override string Description => "Clip, time, speed and pause state for a skeletal manager";

    /// <summary>Explicit manager. When null it resolves on the same element, parent, child or world.</summary>
    public SkeletalManagerComponent? Manager
    {
        get => _manager;
        set
        {
            _manager = value;
            _applied = false;
        }
    }

    /// <summary>Clip to start with. Empty leaves the manager's own auto-play alone.</summary>
    public string? PlayOnLoad { get; set; }

    public bool LoopOnLoad { get; set; } = true;

    public float Speed { get; set; } = 1f;

    public bool IsReady => _manager?.Animator is not null;

    public IReadOnlyList<Matrix4x4> LocalPose =>
        _manager?.LocalPose ?? Array.Empty<Matrix4x4>();

    public IReadOnlyList<string> Clips => _manager?.Clips ?? Array.Empty<string>();

    public string? CurrentClip => _manager?.CurrentClip;

    public float Duration => _manager?.Duration ?? 0f;

    public bool IsPlaying => _manager?.IsPlaying ?? false;

    /// <summary>Playback position in seconds. Assigning seeks immediately.</summary>
    public float Time
    {
        get => _manager?.Time ?? 0f;
        set => _manager?.Seek(value);
    }

    public bool Loop
    {
        get => _manager?.Loop ?? LoopOnLoad;
        set
        {
            LoopOnLoad = value;
            if (_manager is not null)
                _manager.Loop = value;
        }
    }

    public override void OnAttach(IWorldElement worldElement)
    {
        base.OnAttach(worldElement);
        Resolve();
        Apply();
    }

    public override void Update(float deltaTime)
    {
        if (_applied)
            return;

        Resolve();
        Apply();
    }

    public bool Play(string clip, bool loop = true, float blendTime = 0f) =>
        _manager?.Play(clip, loop, blendTime) == true;

    public bool CrossFade(string clip, float duration, bool loop = true) =>
        _manager?.CrossFade(clip, duration, loop) == true;

    public void Seek(float seconds) => _manager?.Seek(seconds);

    public void Pause() => _manager?.Pause();

    public void Resume() => _manager?.Resume();

    public void Restart()
    {
        if (_manager?.CurrentClip is { } clip)
            _manager.Play(clip, LoopOnLoad);
    }

    private void Resolve()
    {
        if (_manager is { Animator: not null })
            return;
        if (Manager is { } explicitManager)
        {
            _manager = explicitManager;
            return;
        }

        var owner = Owner;
        if (owner is null)
            return;

        _manager = owner.GetComponent<SkeletalManagerComponent>()
                   ?? owner.Parent?.GetComponent<SkeletalManagerComponent>();

        if (_manager is null)
        {
            foreach (IWorldElement child in owner.Children)
            {
                if (child.GetComponent<SkeletalManagerComponent>() is { } found)
                {
                    _manager = found;
                    break;
                }
            }
        }

        _manager ??= GameRoot.Instance?.SelectedWorld
            ?.FindElementWithComponentRecursive<SkeletalManagerComponent>()
            ?.GetComponent<SkeletalManagerComponent>();
    }

    private void Apply()
    {
        if (_manager is null || _manager.Animator is null)
            return;

        _manager.Player = this;
        _manager.Speed = Speed;
        _manager.Loop = LoopOnLoad;

        if (!string.IsNullOrWhiteSpace(PlayOnLoad))
            _manager.Play(PlayOnLoad!, LoopOnLoad);

        _applied = true;
    }

    public override IWorldElement BuildUI() => new Element();
}

