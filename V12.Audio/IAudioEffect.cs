namespace V12.Audio;

/// <summary>
/// One native effect instance. Parameters are addressed by name so the contract does
/// not have to freeze every backend's parameter set. Parameter access is control-rate
/// only; never call it from an audio callback.
/// </summary>
public interface IAudioEffect : IDisposable
{
    AudioEffectHandle Handle { get; }
    AudioEffectKind Kind { get; }
    bool Enabled { get; set; }
    float Mix { get; set; }

    void SetParameter(string name, float value);
    float GetParameter(string name);
}

/// <summary>An ordered effect chain on a channel or on the master bus.</summary>
public interface IAudioEffectChain
{
    IReadOnlyList<IAudioEffect> Effects { get; }
    IAudioEffect Add(AudioEffectKind kind);
    bool Remove(IAudioEffect effect);
    void Clear();
}
