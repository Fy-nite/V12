using Ownaudio.Audio.Tracks;
using Ownaudio.Decoders;
using OwnaudioNET.Sources;

namespace V12.Audio.OwnAudio;

internal sealed class OwnAudioClip : IAudioClip
{
    private readonly OwnAudioEngine _engine;
    private readonly string _path;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly double _duration;
    private float[]? _samples;
    private GroupClip? _groupClip;
    private bool _disposed;

    internal OwnAudioClip(OwnAudioEngine engine, AudioClipHandle handle, in AudioClipDesc desc)
    {
        _engine = engine;
        Handle = handle;
        _path = desc.Path ?? throw new AudioEngineException("A clip needs a path.");
        Kind = desc.Kind;
        _sampleRate = desc.SampleRate > 0 ? desc.SampleRate : engine.Options.SampleRate;
        _channels = desc.Channels > 0 ? desc.Channels : engine.Options.Channels;
        _duration = ProbeDuration();

        if (desc.Preload || desc.Kind == AudioClipKind.Memory)
        {
            _samples = TryLoadSamples();
            if (_samples is null)
            {
                // Preloading failed (unsupported container, decoder unavailable). A
                // Memory channel can still fall back to a streaming file track.
                Kind = AudioClipKind.File;
            }
        }
    }

    public AudioClipHandle Handle { get; }
    public string Path => _path;
    public AudioClipKind Kind { get; private set; }
    public double Duration => _duration;
    public int SampleRate => _sampleRate;
    public int Channels => _channels;

    internal float[]? Samples => _samples;

    internal GroupClip GetGroupClip()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _groupClip ??= GroupClip.Open(
            _path,
            _sampleRate,
            _channels,
            Math.Max(0, _engine.Options.MemoryMaxSeconds));
        return _groupClip;
    }

    private double ProbeDuration()
    {
        try
        {
            using var clip = GroupClip.Open(_path, _sampleRate, _channels, 0);
            return clip.Duration.TotalSeconds;
        }
        catch
        {
            // GroupClip probing is the cheap path; the decoder is the fallback for
            // containers the group clip refuses to probe.
        }

        try
        {
            using var decoder = AudioDecoderFactory.Create(_path, _sampleRate, _channels);
            return decoder.StreamInfo.Duration.TotalSeconds;
        }
        catch
        {
            return 0;
        }
    }

    private float[]? TryLoadSamples()
    {
        try
        {
            using var source = new FileSource(
                _path,
                targetSampleRate: _sampleRate,
                targetChannels: _channels);
            return source.GetFloatAudioData(TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    internal void DisposeCore()
    {
        if (_disposed)
            return;

        _disposed = true;
        try { _groupClip?.Dispose(); } catch { }
        _groupClip = null;
        _samples = null;
    }

    public void Dispose() => _engine.DestroyClip(this);
}
