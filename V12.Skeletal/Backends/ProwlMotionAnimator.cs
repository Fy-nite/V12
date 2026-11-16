using Prowl.Motion;
using Prowl.Vector;
using Prowl.Vector.Spatial;
using NMatrix4x4 = System.Numerics.Matrix4x4;

namespace V12.Skeletal.Backends;

/// <summary>
/// Default runtime backend. It converts the neutral clip frames into
/// Prowl.Motion clips once, then samples them itself so playback supports
/// arbitrary seeking, pausing and cross-fades without a hidden cursor.
/// </summary>
public sealed class ProwlMotionAnimator : ISkeletalAnimator
{
    private readonly SkeletonAsset _asset;
    private readonly Skeleton _skeleton;
    private readonly Dictionary<string, AnimationClip> _clips;
    private readonly NMatrix4x4[] _localPose;
    private readonly Pose _currentPose;
    private readonly Pose _fromPose;

    private AnimationClip? _current;
    private AnimationClip? _from;
    private string? _currentName;

    private float _time;
    private float _fromTime;
    private float _fade;
    private float _fadeDuration;
    private float _speed = 1f;
    private bool _loop = true;
    private bool _playing;
    private bool _disposed;

    public ProwlMotionAnimator(SkeletonAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        _asset = asset;

        var boneIds = new StringID[asset.BoneCount];
        var referencePose = new Transform3D[asset.BoneCount];

        for (int bone = 0; bone < asset.BoneCount; bone++)
        {
            boneIds[bone] = new StringID(asset.BoneNames[bone]);
            referencePose[bone] = ToTransform(BoneLocal.FromMatrix(asset.ReferenceLocals[bone]));
        }

        _skeleton = new Skeleton(
            boneIds,
            asset.BoneParents.ToArray(),
            referencePose);

        _clips = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
        foreach (SkeletalClipData data in asset.Clips)
            _clips[data.Name] = BuildClip(_skeleton, data);

        _localPose = new NMatrix4x4[asset.BoneCount];
        for (int bone = 0; bone < _localPose.Length; bone++)
            _localPose[bone] = asset.ReferenceLocals[bone];

        _currentPose = new Pose(_skeleton);
        _fromPose = new Pose(_skeleton);
    }

    public IReadOnlyList<NMatrix4x4> LocalPose => _localPose;

    public int BoneCount => _skeleton.BoneCount;

    public IReadOnlyList<string> Clips => _asset.Clips.Select(c => c.Name).ToArray();

    public string? CurrentClip => _currentName;

    public float Time
    {
        get => _time;
        set => Seek(value);
    }

    public float Duration => _current?.Duration ?? 0f;

    public float Speed
    {
        get => _speed;
        set => _speed = MathF.Max(0f, value);
    }

    public bool Loop
    {
        get => _loop;
        set => _loop = value;
    }

    public bool IsPlaying => _playing;

    public void Play(string clip, bool loop = true, float blendTime = 0f)
    {
        ThrowIfDisposed();
        AnimationClip target = Resolve(clip);

        if (blendTime > 0f && _current is not null && !ReferenceEquals(_current, target))
        {
            _from = _current;
            _fromTime = _time;
            _fade = 0f;
            _fadeDuration = blendTime;
        }
        else
        {
            _from = null;
            _fadeDuration = 0f;
        }

        _current = target;
        _currentName = clip;
        _time = 0f;
        _loop = loop;
        _playing = true;
        Evaluate();
    }

    public void CrossFade(string clip, float duration, bool loop = true) =>
        Play(clip, loop, duration);

    public void Seek(float seconds)
    {
        ThrowIfDisposed();
        if (_current is null)
            return;

        _time = Wrap(seconds, _current.Duration);
        // Seeking is an explicit jump: cancel any in-flight blend so the pose
        // lands exactly where the caller asked.
        _from = null;
        _fadeDuration = 0f;
        Evaluate();
    }

    public void Pause()
    {
        _playing = false;
        Evaluate();
    }

    public void Resume()
    {
        if (_current is not null)
            _playing = true;
    }

    public void Update(float deltaTime)
    {
        ThrowIfDisposed();
        if (_current is null)
            return;

        if (_playing && deltaTime > 0f)
        {
            float step = deltaTime * _speed;
            _time = Advance(_time, step, _current.Duration, _loop);

            if (_from is not null && _fadeDuration > 0f)
            {
                _fromTime = Advance(_fromTime, step, _from.Duration, _loop);
                _fade += deltaTime;
                if (_fade >= _fadeDuration)
                {
                    _from = null;
                    _fadeDuration = 0f;
                }
            }
        }

        Evaluate();
    }

    private void Evaluate()
    {
        if (_current is null)
        {
            for (int bone = 0; bone < _localPose.Length; bone++)
                _localPose[bone] = _asset.ReferenceLocals[bone];
            return;
        }

        _current.GetPose(_time, _currentPose);

        if (_from is not null && _fadeDuration > 0f)
        {
            _from.GetPose(_fromTime, _fromPose);
            float weight = Math.Clamp(_fade / _fadeDuration, 0f, 1f);

            for (int bone = 0; bone < _localPose.Length; bone++)
            {
                Transform3D blended = Transform3D.Lerp(
                    _fromPose.GetTransform(bone),
                    _currentPose.GetTransform(bone),
                    weight);
                _localPose[bone] = AnimationMath.ToMatrix(blended);
            }
            return;
        }

        for (int bone = 0; bone < _localPose.Length; bone++)
            _localPose[bone] = AnimationMath.ToMatrix(_currentPose.GetTransform(bone));
    }

    private AnimationClip Resolve(string clip)
    {
        if (_clips.TryGetValue(clip, out AnimationClip? found))
            return found;

        throw new ArgumentException(
            $"Clip '{clip}' is not in the skeleton. Known clips: {string.Join(", ", _clips.Keys)}.",
            nameof(clip));
    }

    private static AnimationClip BuildClip(Skeleton skeleton, SkeletalClipData data)
    {
        var frames = new Pose[data.FrameCount];
        for (int frame = 0; frame < frames.Length; frame++)
        {
            var pose = new Pose(skeleton);
            for (int bone = 0; bone < skeleton.BoneCount; bone++)
                pose.SetTransform(bone, ToTransform(data.Get(frame, bone)));
            frames[frame] = pose;
        }

        return new AnimationClip(skeleton, frames, data.Duration);
    }

    private static Transform3D ToTransform(BoneLocal local) => new(
        new Float3(local.Position.X, local.Position.Y, local.Position.Z),
        new Prowl.Vector.Quaternion(
            local.Rotation.X,
            local.Rotation.Y,
            local.Rotation.Z,
            local.Rotation.W),
        new Float3(local.Scale.X, local.Scale.Y, local.Scale.Z));

    private static float Wrap(float time, float duration)
    {
        if (!(duration > 0f))
            return 0f;

        time %= duration;
        return time < 0f ? time + duration : time;
    }

    private static float Advance(float time, float step, float duration, bool loop)
    {
        if (!(duration > 0f))
            return 0f;

        time += step;
        if (loop)
            return Wrap(time, duration);
        return Math.Clamp(time, 0f, duration);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ProwlMotionAnimator));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _clips.Clear();
        _current = null;
        _from = null;
    }
}
