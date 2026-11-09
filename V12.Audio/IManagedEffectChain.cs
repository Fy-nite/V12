namespace V12.Audio;

/// <summary>
/// An ordered chain of managed effects. Unlike <see cref="IAudioEffectChain"/>, this
/// runs caller-authored .NET DSP (typically on the producer thread before
/// <see cref="IAudioChannel.Write"/>), not as a native real-time insert.
/// </summary>
public interface IManagedEffectChain
{
    IReadOnlyList<IManagedEffect> Effects { get; }

    /// <summary>Current format the chain is configured for.</summary>
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>
    /// Sets the format used to initialize effects added after this call and
    /// reinitializes effects already in the chain.
    /// </summary>
    void Configure(int sampleRate, int channels);

    /// <summary>Appends an effect to the chain and initializes it with the current format.</summary>
    void Add(IManagedEffect effect);

    /// <summary>Removes an effect from the chain. The effect is not disposed.</summary>
    bool Remove(IManagedEffect effect);

    /// <summary>Removes and disposes all effects in the chain.</summary>
    void Clear();

    /// <summary>Processes interleaved samples through every effect in order, in place.</summary>
    /// <param name="interleaved">Interleaved float PCM buffer.</param>
    /// <param name="frames">Number of frames to process.</param>
    void Process(Span<float> interleaved, int frames);

    /// <summary>Resets every effect in the chain.</summary>
    void Reset();
}
