namespace V12.Audio;

/// <summary>
/// A managed, in-place DSP effect intended for producer-side processing (for example,
/// before writing samples into a <see cref="AudioChannelKind.Stream"/> channel).
/// Implementations must not allocate from <see cref="Process"/>.
/// </summary>
public interface IManagedEffect : IDisposable
{
    /// <summary>Prepares the effect for a given format.</summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="channels">Interleaved channel count.</param>
    void Initialize(int sampleRate, int channels);

    /// <summary>Processes interleaved samples in place.</summary>
    /// <param name="interleaved">Interleaved float PCM buffer.</param>
    /// <param name="frames">Number of frames to process; <c>frames * channels</c> samples are valid.</param>
    void Process(Span<float> interleaved, int frames);

    /// <summary>Clears internal state such as delay lines or filter history.</summary>
    void Reset();
}
