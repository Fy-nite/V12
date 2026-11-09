namespace V12.Audio;

/// <summary>
/// Default <see cref="IManagedEffectChain"/> implementation. Effects run in insertion
/// order, in place, on whatever thread calls <see cref="Process"/> — for a stream channel
/// that is the producer thread inside <see cref="IAudioChannel.Write"/>.
/// </summary>
/// <remarks>
/// The chain owns the effects added to it: <see cref="Clear"/> and <see cref="Dispose"/>
/// dispose them, while <see cref="Remove"/> only detaches. Not thread-safe; configure and
/// mutate it from the producer thread.
/// </remarks>
public sealed class ManagedEffectChain : IManagedEffectChain, IDisposable
{
    private readonly List<IManagedEffect> _effects = new();
    private int _sampleRate;
    private int _channels;
    private bool _disposed;

    /// <summary>
    /// Creates an unconfigured chain. Call <see cref="Configure"/> before adding effects.
    /// </summary>
    public ManagedEffectChain()
    {
    }

    /// <summary>Creates a chain already configured for a format.</summary>
    public ManagedEffectChain(int sampleRate, int channels)
    {
        Configure(sampleRate, channels);
    }

    public IReadOnlyList<IManagedEffect> Effects => _effects;

    public int SampleRate => _sampleRate;

    public int Channels => _channels;

    public void Configure(int sampleRate, int channels)
    {
        ThrowIfDisposed();

        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channel count must be positive.");

        _sampleRate = sampleRate;
        _channels = channels;

        foreach (var effect in _effects)
            effect.Initialize(sampleRate, channels);
    }

    public void Add(IManagedEffect effect)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(effect);

        if (_sampleRate <= 0 || _channels <= 0)
            throw new InvalidOperationException("Configure the chain before adding managed effects.");

        effect.Initialize(_sampleRate, _channels);
        _effects.Add(effect);
    }

    public bool Remove(IManagedEffect effect)
    {
        ThrowIfDisposed();
        return effect is not null && _effects.Remove(effect);
    }

    public void Clear()
    {
        ThrowIfDisposed();
        _disposeEffects();
    }

    public void Process(Span<float> interleaved, int frames)
    {
        if (frames <= 0 || interleaved.IsEmpty || _effects.Count == 0)
            return;

        if (_channels <= 0)
            throw new InvalidOperationException("The managed effect chain has not been configured.");

        long required = (long)frames * _channels;
        if (required > interleaved.Length)
            throw new ArgumentOutOfRangeException(
                nameof(frames),
                frames,
                $"{frames} frames of {_channels} channels need {required} samples, but the buffer holds {interleaved.Length}.");

        foreach (var effect in _effects)
            effect.Process(interleaved, frames);
    }

    public void Reset()
    {
        foreach (var effect in _effects)
            effect.Reset();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _disposeEffects();
    }

    private void _disposeEffects()
    {
        foreach (var effect in _effects)
        {
            try
            {
                effect.Dispose();
            }
            catch
            {
                // A misbehaving effect must not keep the rest of the chain alive.
            }
        }

        _effects.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ManagedEffectChain));
    }
}
