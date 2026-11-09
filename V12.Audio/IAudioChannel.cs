namespace V12.Audio;

/// <summary>
/// One mixer strip. A channel owns a source (file, clip lane, memory buffer, live
/// input, or a caller-fed stream) and the gain, pan, mute, solo, tempo and pitch
/// that sit on top of it.
/// </summary>
public interface IAudioChannel : IDisposable
{
    AudioChannelHandle Handle { get; }
    string Name { get; set; }
    AudioChannelKind Kind { get; }

    IAudioBus? Bus { get; set; }
    IAudioClip? Clip { get; set; }
    bool Loop { get; set; }

    float Gain { get; set; }
    float Pan { get; set; }
    bool Muted { get; set; }
    bool Solo { get; set; }
    float Tempo { get; set; }
    float PitchSemitones { get; set; }

    AudioPlaybackState State { get; }
    double Position { get; }
    double Duration { get; }

    /// <summary>True when a non-looping source has reached its end.</summary>
    bool IsFinished { get; }

    void Play();
    void Pause();
    void Stop();
    void Seek(double seconds);

    /// <summary>
    /// Pushes interleaved samples into a <see cref="AudioChannelKind.Stream"/>
    /// channel. Returns how many samples the ring accepted. Throws for other kinds.
    /// </summary>
    int Write(ReadOnlySpan<float> samples);

    /// <summary>Source-channel to output-channel mapping. Empty clears it.</summary>
    void SetOutputChannelMap(ReadOnlySpan<int> mapping);
    void ClearOutputChannelMap();

    IAudioEffectChain Effects { get; }

    /// <summary>
    /// Managed, producer-side DSP run in place by <see cref="Write"/> before samples
    /// reach the channel's native feed. Use this for hand-authored .NET effects on
    /// stream channels; native inserts live in <see cref="Effects"/>.
    /// </summary>
    IManagedEffectChain ManagedEffects { get; }

    (float Left, float Right) Peaks { get; }
}
