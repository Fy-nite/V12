using System.Buffers;
using Ownaudio.Audio.Tracks;

namespace V12.Audio.OwnAudio;

internal sealed class OwnAudioChannel : IAudioChannel
{
    private readonly OwnAudioEngine _engine;
    private readonly AudioChannelDesc _desc;
    private OwnAudioEffectChain? _effects;
    private ManagedEffectChain? _managedEffects;

    private AudioTrack _track;
    private object? _source;
    private OwnAudioClip? _clip;
    private OwnAudioBus? _bus;
    private FileTrack? _fileTrack;
    private MemoryTrack? _memoryTrack;
    private GroupTrack? _groupTrack;
    private InputTrack? _inputTrack;

    private string _name;
    private float _gain;
    private float _pan;
    private float _tempo = 1f;
    private float _pitchSemitones;
    private bool _userMuted;
    private bool _solo;
    private bool _loop;
    private AudioPlaybackState _state;
    private bool _disposed;

    internal OwnAudioChannel(
        OwnAudioEngine engine,
        AudioChannelHandle handle,
        in AudioChannelDesc desc,
        AudioTrack track,
        object? source,
        OwnAudioClip? clip)
    {
        _engine = engine;
        Handle = handle;
        _desc = desc;
        _name = string.IsNullOrWhiteSpace(desc.Name) ? "Channel" : desc.Name!;
        _gain = desc.Gain == 0 ? 1f : desc.Gain;
        _pan = Math.Clamp(desc.Pan, -1f, 1f);
        _loop = desc.Loop;
        _effects = engine.CreateTrackEffectChain(track.Effects);
        if (desc.Kind == AudioChannelKind.Stream)
        {
            int managedChannels = desc.OutputChannels > 0 ? desc.OutputChannels : engine.OutputChannels;
            _managedEffects = new ManagedEffectChain(engine.SampleRate, managedChannels);
        }

        _track = track;
        BindSource(source, clip);

        _track.Gain = _gain;
        _track.Pan = _pan;
        _track.Tempo = _tempo;
        _track.PitchSemitones = _pitchSemitones;
    }

    public AudioChannelHandle Handle { get; }
    public AudioChannelKind Kind => _desc.Kind;
    public string Name { get => _name; set => _name = value; }

    public IAudioBus? Bus
    {
        get => _bus;
        set
        {
            if (ReferenceEquals(_bus, value))
                return;

            if (value is not null and not OwnAudioBus)
                throw new ArgumentException("The bus belongs to a different backend.", nameof(value));

            _bus?.RemoveChannel(this);
            _bus = value as OwnAudioBus;
            _bus?.AddChannel(this);
            ApplyGain();
        }
    }

    public IAudioClip? Clip
    {
        get => _clip;
        set => _engine.ReplaceChannelSource(this, value);
    }

    public bool Loop
    {
        get => _loop;
        set
        {
            _loop = value;
            if (_fileTrack is not null)
                _fileTrack.Loop = value;
            if (_memoryTrack is not null)
                _memoryTrack.Loop = value;
        }
    }

    public float Gain
    {
        get => _gain;
        set
        {
            _gain = Math.Max(0f, value);
            ApplyGain();
        }
    }

    public float Pan
    {
        get => _pan;
        set
        {
            _pan = Math.Clamp(value, -1f, 1f);
            _track.Pan = _pan;
        }
    }

    public bool Muted
    {
        get => _userMuted;
        set
        {
            _userMuted = value;
            ApplyMute(_engine.AnySolo);
        }
    }

    public bool Solo
    {
        get => _solo;
        set
        {
            if (_solo == value)
                return;
            _solo = value;
            _engine.UpdateSoloState();
        }
    }

    public float Tempo
    {
        get => _tempo;
        set
        {
            _tempo = Math.Clamp(value, 0.25f, 4f);
            _track.Tempo = _tempo;
        }
    }

    public float PitchSemitones
    {
        get => _pitchSemitones;
        set
        {
            _pitchSemitones = Math.Clamp(value, -24f, 24f);
            _track.PitchSemitones = _pitchSemitones;
        }
    }

    public AudioPlaybackState State
    {
        get
        {
            if (_state == AudioPlaybackState.Playing && IsFinished)
                _state = AudioPlaybackState.Stopped;
            return _state;
        }
    }

    public double Position => _track.ContentPosition.TotalSeconds;
    public double Duration => _clip?.Duration ?? 0;

    public bool IsFinished =>
        _fileTrack?.IsFinished == true ||
        _memoryTrack?.IsFinished == true ||
        _groupTrack?.IsFinished == true;

    public IAudioEffectChain Effects =>
        _effects ?? throw new AudioEngineException("This channel has no effect chain.");

    public IManagedEffectChain ManagedEffects =>
        _managedEffects ?? throw new AudioEngineException("Managed effects are only available on stream channels.");

    public (float Left, float Right) Peaks => _track.Peaks;

    internal AudioChannelDesc Desc => _desc;
    internal AudioTrack Track => _track;
    internal object? Source => _source;

    public void Play() => PlayCore();

    internal void PlayCore()
    {
        _track.Play();
        _inputTrack?.Play();
        _state = AudioPlaybackState.Playing;
    }

    public void Pause()
    {
        _track.Pause();
        _inputTrack?.Pause();
        _state = AudioPlaybackState.Paused;
    }

    public void Stop()
    {
        _track.Stop();
        _inputTrack?.Pause();
        _managedEffects?.Reset();
        _state = AudioPlaybackState.Stopped;
    }

    public void Seek(double seconds) => SeekCore(seconds);

    internal void SeekCore(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));

        switch (_source)
        {
            case FileTrack file:
                file.Seek(time);
                break;
            case MemoryTrack memory:
                memory.Seek(time);
                break;
            case GroupTrack group:
                group.Seek(time);
                break;
        }

        _track.Seek(time);
        _managedEffects?.Reset();
    }

    public int Write(ReadOnlySpan<float> samples)
    {
        if (_managedEffects is null || _managedEffects.Effects.Count == 0)
            return _track.Write(samples);

        int channels = _managedEffects.Channels;
        int requestedFrames = samples.Length / channels;
        if (requestedFrames == 0)
            return 0;

        // Process only what the ring can take, so effect state stays aligned with the
        // audio that actually reaches the track.
        int frames = Math.Min(requestedFrames, _track.FreeSampleCount / channels);
        if (frames <= 0)
            return 0;

        int sampleCount = frames * channels;
        float[] buffer = ArrayPool<float>.Shared.Rent(sampleCount);
        try
        {
            samples.Slice(0, sampleCount).CopyTo(buffer.AsSpan(0, sampleCount));
            _managedEffects.Process(buffer.AsSpan(0, sampleCount), frames);
            return _track.Write(buffer.AsSpan(0, sampleCount));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    public void SetOutputChannelMap(ReadOnlySpan<int> mapping) => _track.SetOutputChannelMap(mapping);
    public void ClearOutputChannelMap() => _track.ClearOutputChannelMap();

    internal void SetTrack(AudioTrack track, object? source, OwnAudioClip? clip)
    {
        _track = track;
        BindSource(source, clip);

        _effects = _engine.CreateTrackEffectChain(track.Effects);

        _track.Gain = _gain * (_bus?.EffectiveGain ?? 1f);
        _track.Pan = _pan;
        _track.Tempo = _tempo;
        _track.PitchSemitones = _pitchSemitones;
        ApplyMute(_engine.AnySolo);

        if (_fileTrack is not null)
            _fileTrack.Loop = _loop;
        if (_memoryTrack is not null)
            _memoryTrack.Loop = _loop;
    }

    private void BindSource(object? source, OwnAudioClip? clip)
    {
        _source = source;
        _clip = clip;
        _fileTrack = source as FileTrack;
        _memoryTrack = source as MemoryTrack;
        _groupTrack = source as GroupTrack;
        _inputTrack = source as InputTrack;
    }

    internal void ApplyGain()
    {
        _track.Gain = Math.Max(0f, _gain * (_bus?.EffectiveGain ?? 1f));
    }

    internal void ApplyMute(bool anySolo)
    {
        bool busAudible = _bus?.IsAudible(anySolo) ?? true;
        bool channelAudible = !anySolo || _solo || (_bus?.Solo ?? false);
        _track.Muted = _userMuted || !busAudible || !channelAudible;
    }

    internal void ClearEffects()
    {
        try { _effects?.Clear(); } catch { }
        _effects = null;
    }

    internal void DisposeCore()
    {
        if (_disposed)
            return;

        _disposed = true;
        _state = AudioPlaybackState.Stopped;
        _effects?.Clear();
        _effects = null;
        _managedEffects?.Dispose();
        _managedEffects = null;
        _bus?.RemoveChannel(this);
        _bus = null;
        _engine.RemoveTrack(_track, _source);
    }

    public void Dispose() => _engine.DestroyChannel(this);
}
