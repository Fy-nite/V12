using System.Buffers;

namespace V12.Audio;

/// <summary>
/// Reference engine with no device and no sound. It validates the contract shape,
/// records handles and is the headless/test implementation.
/// </summary>
public sealed class NullAudioEngine : IAudioEngine
{
    private readonly object _lock = new();
    private readonly AudioEngineOptions _options;
    private readonly NullTransport _transport = new();
    private readonly NullMasterBus _master;
    private readonly Dictionary<uint, NullChannel> _channels = new();
    private readonly Dictionary<uint, NullBus> _buses = new();
    private readonly Dictionary<uint, NullClip> _clips = new();
    private AudioEngineStatus _status = AudioEngineStatus.Stopped;
    private uint _nextId;
    private bool _disposed;

    public NullAudioEngine(AudioEngineOptions? options = null)
    {
        _options = options ?? new AudioEngineOptions();
        _master = new NullMasterBus(this);
    }

    public AudioEngineStatus Status => _status;
    public bool IsRunning => _status == AudioEngineStatus.Running;
    public AudioEngineCapabilities Capabilities => AudioEngineCapabilities.None;
    public IAudioTransport Transport => _transport;
    public IAudioMasterBus Master => _master;

    public AudioEngineInfo Info => new()
    {
        Status = _status,
        SampleRate = _options.SampleRate,
        Channels = _options.Channels,
        ActiveChannels = _channels.Count,
        MaxChannels = _options.MaxChannels,
        BackendName = "NullAudioEngine",
    };

    public event EventHandler<AudioDeviceEvent>? DeviceEvent;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _status = AudioEngineStatus.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _status = AudioEngineStatus.Stopped;
        _transport.Stop();
        return Task.CompletedTask;
    }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => Array.Empty<AudioDeviceInfo>();
    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => Array.Empty<AudioDeviceInfo>();

    public Task SetOutputDeviceAsync(string deviceName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public IAudioBus CreateBus(in AudioBusDesc desc)
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            uint id = ++_nextId;
            var bus = new NullBus(new AudioBusHandle(id, 1), desc);
            if (desc.Parent.IsValid && _buses.TryGetValue(desc.Parent.Id, out var parent))
                bus.Parent = parent;
            _buses[id] = bus;
            return bus;
        }
    }

    public void DestroyBus(IAudioBus bus)
    {
        if (bus is null) return;
        lock (_lock)
        {
            if (_buses.Remove(bus.Handle.Id))
                bus.Dispose();
        }
    }

    public IAudioChannel CreateChannel(in AudioChannelDesc desc)
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            uint id = ++_nextId;
            var channel = new NullChannel(
                new AudioChannelHandle(id, 1),
                desc,
                () => ++_nextId,
                _options.SampleRate,
                _options.Channels);
            if (desc.Bus.IsValid && _buses.TryGetValue(desc.Bus.Id, out var bus))
                channel.Bus = bus;
            _channels[id] = channel;
            return channel;
        }
    }

    public void DestroyChannel(IAudioChannel channel)
    {
        if (channel is null) return;
        lock (_lock)
        {
            if (_channels.Remove(channel.Handle.Id))
                channel.Dispose();
        }
    }

    public IAudioClip CreateClip(in AudioClipDesc desc)
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            uint id = ++_nextId;
            var clip = new NullClip(new AudioClipHandle(id, 1), desc);
            _clips[id] = clip;
            return clip;
        }
    }

    public void DestroyClip(IAudioClip clip)
    {
        if (clip is null) return;
        lock (_lock)
        {
            if (_clips.Remove(clip.Handle.Id))
                clip.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _status = AudioEngineStatus.Disposed;
        _transport.Stop();
        _channels.Clear();
        _buses.Clear();
        _clips.Clear();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NullAudioEngine));
    }

    private sealed class NullTransport : IAudioTransport
    {
        private AudioPlaybackState _state;
        private double _position;

        public AudioPlaybackState State => _state;
        public double Position => _position;

        public void Play() => _state = AudioPlaybackState.Playing;
        public void Pause() => _state = _state == AudioPlaybackState.Playing ? AudioPlaybackState.Paused : _state;
        public void Stop() { _state = AudioPlaybackState.Stopped; _position = 0; }
        public void Seek(double seconds) => _position = Math.Max(0, seconds);

        public event EventHandler? PlaybackEnded;
    }

    private sealed class NullMasterBus : IAudioMasterBus
    {
        private readonly NullAudioEngine _owner;
        private readonly NullEffectChain _effects;

        public NullMasterBus(NullAudioEngine owner)
        {
            _owner = owner;
            _effects = new NullEffectChain(() => ++_owner._nextId);
        }

        public float Gain { get; set; } = 1f;
        public float Pan { get; set; }
        public IAudioEffectChain Effects => _effects;
        public (float Left, float Right) Peaks => (0f, 0f);
    }

    private sealed class NullChannel : IAudioChannel
    {
        private readonly NullEffectChain _effects;
        private readonly ManagedEffectChain? _managedEffects;
        private AudioPlaybackState _state;
        private double _position;

        public NullChannel(
            AudioChannelHandle handle,
            in AudioChannelDesc desc,
            Func<uint> nextEffectId,
            int sampleRate,
            int defaultChannels)
        {
            Handle = handle;
            Name = desc.Name ?? "Channel";
            Kind = desc.Kind;
            Gain = desc.Gain == 0 ? 1f : desc.Gain;
            Pan = desc.Pan;
            Loop = desc.Loop;
            _effects = new NullEffectChain(nextEffectId);

            if (desc.Kind == AudioChannelKind.Stream)
            {
                int channels = desc.OutputChannels > 0 ? desc.OutputChannels : defaultChannels;
                _managedEffects = new ManagedEffectChain(sampleRate, channels);
            }
        }

        public AudioChannelHandle Handle { get; }
        public string Name { get; set; }
        public AudioChannelKind Kind { get; }
        public IAudioBus? Bus { get; set; }
        public IAudioClip? Clip { get; set; }
        public bool Loop { get; set; }
        public float Gain { get; set; }
        public float Pan { get; set; }
        public bool Muted { get; set; }
        public bool Solo { get; set; }
        public float Tempo { get; set; } = 1f;
        public float PitchSemitones { get; set; }
        public AudioPlaybackState State => _state;
        public double Position => _position;
        public double Duration => Clip?.Duration ?? 0;
        public bool IsFinished => false;
        public IAudioEffectChain Effects => _effects;

        public IManagedEffectChain ManagedEffects =>
            _managedEffects ?? throw new AudioEngineException("Managed effects are only available on stream channels.");

        public (float Left, float Right) Peaks => (0f, 0f);

        public void Play() => _state = AudioPlaybackState.Playing;
        public void Pause() => _state = _state == AudioPlaybackState.Playing ? AudioPlaybackState.Paused : _state;

        public void Stop()
        {
            _state = AudioPlaybackState.Stopped;
            _position = 0;
            _managedEffects?.Reset();
        }

        public void Seek(double seconds)
        {
            _position = Math.Max(0, seconds);
            _managedEffects?.Reset();
        }

        public int Write(ReadOnlySpan<float> samples)
        {
            if (_managedEffects is null || _managedEffects.Effects.Count == 0)
                return samples.Length;

            int channels = _managedEffects.Channels;
            int frames = samples.Length / channels;
            if (frames == 0)
                return 0;

            int sampleCount = frames * channels;
            float[] buffer = ArrayPool<float>.Shared.Rent(sampleCount);
            try
            {
                samples.Slice(0, sampleCount).CopyTo(buffer.AsSpan(0, sampleCount));
                _managedEffects.Process(buffer.AsSpan(0, sampleCount), frames);
                return sampleCount;
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        }
        public void SetOutputChannelMap(ReadOnlySpan<int> mapping) { }
        public void ClearOutputChannelMap() { }

        public void Dispose()
        {
            _effects.Clear();
            _managedEffects?.Dispose();
            _state = AudioPlaybackState.Stopped;
        }
    }

    private sealed class NullBus : IAudioBus
    {
        public NullBus(AudioBusHandle handle, in AudioBusDesc desc)
        {
            Handle = handle;
            Name = desc.Name ?? "Bus";
            Gain = desc.Gain == 0 ? 1f : desc.Gain;
            Muted = desc.Muted;
        }

        public AudioBusHandle Handle { get; }
        public string Name { get; set; }
        public IAudioBus? Parent { get; set; }
        public float Gain { get; set; }
        public bool Muted { get; set; }
        public bool Solo { get; set; }
        public void Dispose() { }
    }

    private sealed class NullClip : IAudioClip
    {
        public NullClip(AudioClipHandle handle, in AudioClipDesc desc)
        {
            Handle = handle;
            Path = desc.Path ?? string.Empty;
            Kind = desc.Kind;
        }

        public AudioClipHandle Handle { get; }
        public string Path { get; }
        public AudioClipKind Kind { get; }
        public double Duration => 0;
        public int SampleRate => 0;
        public int Channels => 0;
        public void Dispose() { }
    }

    private sealed class NullEffectChain : IAudioEffectChain
    {
        private readonly Func<uint> _nextId;
        private readonly List<IAudioEffect> _effects;

        public NullEffectChain(Func<uint> nextId)
        {
            _nextId = nextId;
            _effects = new List<IAudioEffect>();
        }

        public IReadOnlyList<IAudioEffect> Effects => _effects;

        public IAudioEffect Add(AudioEffectKind kind)
        {
            var effect = new NullEffect(new AudioEffectHandle(_nextId(), 1), kind);
            _effects.Add(effect);
            return effect;
        }

        public bool Remove(IAudioEffect effect)
        {
            if (!_effects.Remove(effect)) return false;
            effect.Dispose();
            return true;
        }

        public void Clear()
        {
            foreach (var effect in _effects)
                effect.Dispose();
            _effects.Clear();
        }
    }

    private sealed class NullEffect : IAudioEffect
    {
        public NullEffect(AudioEffectHandle handle, AudioEffectKind kind)
        {
            Handle = handle;
            Kind = kind;
        }

        public AudioEffectHandle Handle { get; }
        public AudioEffectKind Kind { get; }
        public bool Enabled { get; set; } = true;
        public float Mix { get; set; } = 1f;
        public void SetParameter(string name, float value) { }
        public float GetParameter(string name) => 0f;
        public void Dispose() { }
    }
}
