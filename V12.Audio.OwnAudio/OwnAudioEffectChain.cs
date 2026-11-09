using System.Collections.Concurrent;
using System.Reflection;
using Ownaudio.Audio.Effects;
using Ownaudio.Audio.Tracks;

namespace V12.Audio.OwnAudio;

internal sealed class OwnAudioEffectChain : IAudioEffectChain
{
    private readonly NativeEffectChain _chain;
    private readonly float _sampleRate;
    private readonly List<IAudioEffect> _effects = new();
    private uint _nextId;

    internal OwnAudioEffectChain(NativeEffectChain chain, int sampleRate)
    {
        _chain = chain;
        _sampleRate = sampleRate;
    }

    public IReadOnlyList<IAudioEffect> Effects => _effects;

    public IAudioEffect Add(AudioEffectKind kind)
    {
        var effectType = Map(kind);
        var wrapper = _chain.Add(effectType, _sampleRate);
        var effect = new OwnAudioEffect(
            this,
            new AudioEffectHandle(++_nextId, 1),
            kind,
            wrapper);
        _effects.Add(effect);
        return effect;
    }

    public bool Remove(IAudioEffect effect)
    {
        if (effect is not OwnAudioEffect own)
            return false;

        if (!_effects.Remove(own))
            return false;

        _chain.Remove(own.Wrapper);
        own.MarkRemoved();
        return true;
    }

    public void Clear()
    {
        _chain.Clear();
        foreach (var effect in _effects)
        {
            if (effect is OwnAudioEffect own)
                own.MarkRemoved();
        }

        _effects.Clear();
    }

    private static EffectType Map(AudioEffectKind kind) => kind switch
    {
        AudioEffectKind.Reverb => EffectType.Reverb,
        AudioEffectKind.Equalizer => EffectType.Equalizer,
        AudioEffectKind.Compressor => EffectType.Compressor,
        AudioEffectKind.Limiter => EffectType.Limiter,
        AudioEffectKind.Delay => EffectType.Delay,
        AudioEffectKind.Chorus => EffectType.Chorus,
        AudioEffectKind.Distortion => EffectType.Distortion,
        AudioEffectKind.Overdrive => EffectType.Overdrive,
        AudioEffectKind.Flanger => EffectType.Flanger,
        AudioEffectKind.Phaser => EffectType.Phaser,
        AudioEffectKind.Rotary => EffectType.Rotary,
        AudioEffectKind.AutoGain => EffectType.AutoGain,
        AudioEffectKind.Enhancer => EffectType.Enhancer,
        AudioEffectKind.Gate => EffectType.Gate,
        AudioEffectKind.PitchShift => EffectType.PitchShift,
        AudioEffectKind.DynamicAmp => EffectType.DynamicAmp,
        AudioEffectKind.Equalizer30 => EffectType.Equalizer30,
        AudioEffectKind.OwnReverb => EffectType.OwnReverb,
        AudioEffectKind.OwnCompressor => EffectType.OwnCompressor,
        AudioEffectKind.OwnDelay => EffectType.OwnDelay,
        AudioEffectKind.OwnDynamicAmp => EffectType.OwnDynamicAmp,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown audio effect kind."),
    };
}

internal sealed class OwnAudioEffect : IAudioEffect
{
    private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfo?> PropertyCache = new();

    private readonly OwnAudioEffectChain _chain;
    private readonly object _wrapper;
    private bool _enabled = true;
    private float _mix = 1f;
    private bool _removed;

    internal OwnAudioEffect(
        OwnAudioEffectChain chain,
        AudioEffectHandle handle,
        AudioEffectKind kind,
        object wrapper)
    {
        _chain = chain;
        Handle = handle;
        Kind = kind;
        _wrapper = wrapper;
        ApplyMix();
    }

    public AudioEffectHandle Handle { get; }
    public AudioEffectKind Kind { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            ApplyMix();
        }
    }

    public float Mix
    {
        get => _mix;
        set
        {
            _mix = Math.Clamp(value, 0f, 1f);
            ApplyMix();
        }
    }

    internal object Wrapper => _wrapper;

    public void SetParameter(string name, float value)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A parameter name is required.", nameof(name));

        var property = FindProperty(_wrapper.GetType(), name);
        if (property is null || !property.CanWrite)
            throw new AudioEngineException(
                $"Effect '{Kind}' has no writable parameter named '{name}'.");

        property.SetValue(_wrapper, ConvertTo(value, property.PropertyType));
    }

    public float GetParameter(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A parameter name is required.", nameof(name));

        var property = FindProperty(_wrapper.GetType(), name);
        if (property is null || !property.CanRead)
            throw new AudioEngineException(
                $"Effect '{Kind}' has no readable parameter named '{name}'.");

        return ToFloat(property.GetValue(_wrapper));
    }

    internal void MarkRemoved()
    {
        _removed = true;
    }

    public void Dispose()
    {
        if (_removed)
            return;
        _chain.Remove(this);
    }

    private void ApplyMix()
    {
        var property = FindProperty(_wrapper.GetType(), "Mix");
        if (property is null || !property.CanWrite)
            return;

        property.SetValue(_wrapper, ConvertTo(_enabled ? _mix : 0f, property.PropertyType));
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        return PropertyCache.GetOrAdd((type, name), static key =>
            key.Type.GetProperty(
                key.Name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));
    }

    private static float ToFloat(object? value) => value switch
    {
        float f => f,
        double d => (float)d,
        int i => i,
        uint u => u,
        long l => l,
        bool b => b ? 1f : 0f,
        _ => 0f,
    };

    private static object ConvertTo(float value, Type targetType)
    {
        if (targetType == typeof(float))
            return value;
        if (targetType == typeof(double))
            return (double)value;
        if (targetType == typeof(int))
            return (int)MathF.Round(value);
        if (targetType == typeof(uint))
            return (uint)MathF.Max(0, MathF.Round(value));
        if (targetType == typeof(long))
            return (long)MathF.Round(value);
        if (targetType == typeof(bool))
            return value != 0f;
        if (targetType.IsEnum)
            return Enum.ToObject(targetType, (int)MathF.Round(value));
        return Convert.ChangeType(value, targetType);
    }
}
