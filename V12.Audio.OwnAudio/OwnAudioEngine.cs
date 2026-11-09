using Ownaudio.Audio.Tracks;
using Ownaudio.Safe;
using SafeAudioEngine = Ownaudio.Safe.AudioEngine;
using SafeAudioDevice = Ownaudio.Safe.AudioDevice;
using SafeAudioOutputStream = Ownaudio.Safe.AudioOutputStream;

namespace V12.Audio.OwnAudio;

/// <summary>
/// OwnAudioSharp 4.x backend. It talks to <see cref="MultiTrackSession"/> directly
/// instead of <c>AudioMixer</c>, so the convenience mixer's 30-source guard does not
/// apply and every channel is a real native track with its own gain, pan, tempo,
/// pitch, mute, effects and output routing.
/// </summary>
public sealed class OwnAudioEngine : IAudioEngine
{
    private readonly AudioEngineOptions _options;
    private readonly object _sync = new();
    private readonly Dictionary<uint, OwnAudioChannel> _channels = new();
    private readonly Dictionary<uint, OwnAudioBus> _buses = new();
    private readonly Dictionary<uint, OwnAudioClip> _clips = new();
    private readonly OwnAudioTransport _transport;
    private readonly OwnAudioMasterBus _master;

    private SafeAudioEngine? _nativeEngine;
    private MultiTrackSession? _session;
    private SafeAudioOutputStream? _output;
    private SafeAudioDevice? _outputDevice;
    private SafeAudioDevice? _inputDevice;
    private Timer? _pollTimer;
    private AudioEngineStatus _status = AudioEngineStatus.Stopped;
    private uint _nextId;
    private bool _disposed;
    private bool _anySolo;
    private long _lastErrorCount;

    public OwnAudioEngine(AudioEngineOptions? options = null)
    {
        _options = options ?? new AudioEngineOptions();
        _transport = new OwnAudioTransport(this);
        _master = new OwnAudioMasterBus(this);
    }

    internal AudioEngineOptions Options => _options;
    internal int SampleRate => _options.SampleRate;
    internal int OutputChannels => _options.Channels;
    internal MultiTrackSession Session => _session ?? throw new AudioEngineException("The audio engine has not started.");
    internal bool AnySolo => _anySolo;

    public AudioEngineStatus Status
    {
        get { lock (_sync) return _status; }
    }

    public bool IsRunning => Status == AudioEngineStatus.Running;

    public AudioEngineCapabilities Capabilities =>
        AudioEngineCapabilities.FilePlayback |
        AudioEngineCapabilities.MemoryPlayback |
        AudioEngineCapabilities.ClipLanes |
        AudioEngineCapabilities.InputRecording |
        AudioEngineCapabilities.PerChannelEffects |
        AudioEngineCapabilities.MasterEffects |
        AudioEngineCapabilities.BusGain |
        AudioEngineCapabilities.OutputRouting |
        AudioEngineCapabilities.DeviceEnumeration |
        AudioEngineCapabilities.Metering |
        AudioEngineCapabilities.ManagedEffects;

    public IAudioTransport Transport => _transport;
    public IAudioMasterBus Master => _master;

    public AudioEngineInfo Info
    {
        get
        {
            lock (_sync)
            {
                var info = new AudioEngineInfo
                {
                    Status = _status,
                    SampleRate = _options.SampleRate,
                    Channels = _options.Channels,
                    ActiveChannels = _channels.Count,
                    MaxChannels = _options.MaxChannels,
                    DeviceName = _outputDevice?.Name,
                    BackendName = "OwnAudioSharp 4.0.12",
                };

                if (_output is not null)
                {
                    try
                    {
                        info.LatencyFrames = (int)_output.LatencyFrames;
                        info.LatencyMs = info.LatencyFrames * 1000f / Math.Max(1, _options.SampleRate);
                        info.CpuLoad = _output.GetLoad().AverageLoad;
                        info.UnderrunFrames = (long)_output.UnderrunFrames;
                    }
                    catch
                    {
                        // A device that vanished can refuse diagnostics; keep the last
                        // good shape rather than failing the Info read.
                    }
                }

                return info;
            }
        }
    }

    public event EventHandler<AudioDeviceEvent>? DeviceEvent;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_status is AudioEngineStatus.Running or AudioEngineStatus.Starting)
                return;

            // StopAsync parks the device but keeps the graph alive. Resume that
            // session instead of building a second engine and leaking the first.
            if (_session is not null && _output is not null)
            {
                _output.Play();
                _status = AudioEngineStatus.Running;
                return;
            }

            _status = AudioEngineStatus.Starting;
        }

        try
        {
            await Task.Run(() => StartCore(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _status = AudioEngineStatus.Failed;
            }

            DisposeCore();
            throw new AudioEngineException("Failed to start the OwnAudioSharp backend.", ex);
        }
    }

    private void StartCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Touching the high-level assembly runs its module initializer, which registers
        // the native decoder factory used by FileSource and clip preloading. We still
        // drive the low-level Safe engine ourselves.
        _ = typeof(OwnaudioNET.OwnaudioNet);

        SafeAudioEngine? engine = null;
        MultiTrackSession? session = null;
        SafeAudioOutputStream? output = null;

        try
        {
            if (_disposed)
                return;

            engine = SafeAudioEngine.Create();
            if (_disposed)
                return;

            var outputs = engine.EnumerateOutputDevices();
            var selectedOutput = SelectDevice(outputs, _options.OutputDeviceName, preferOutput: true);

            SafeAudioDevice? selectedInput = null;
            if (_options.EnableInput)
            {
                var inputs = engine.EnumerateInputDevices();
                selectedInput = SelectDevice(inputs, _options.InputDeviceName, preferOutput: false);
            }

            session = new MultiTrackSession(
                _options.SampleRate,
                (ushort)Math.Clamp(_options.Channels, 1, 256));
            session.MasterGain = Math.Clamp(_options.MasterVolume, 0f, 1f);

            if (_disposed)
                return;

            output = session.OpenOutput(engine, selectedOutput, _options.BufferSize);

            lock (_sync)
            {
                if (_disposed)
                    return;

                _nativeEngine = engine;
                _session = session;
                _output = output;
                _outputDevice = selectedOutput;
                _inputDevice = selectedInput;
                _status = AudioEngineStatus.Running;
                _pollTimer = new Timer(Poll, null, 100, 100);
            }

            engine = null;
            session = null;
            output = null;
        }
        finally
        {
            // Anything still local was not handed to the engine (start was cancelled
            // or disposed mid-flight), so release it here.
            try { output?.Dispose(); } catch { }
            try { session?.Dispose(); } catch { }
            try { engine?.Dispose(); } catch { }
        }
    }

    private static SafeAudioDevice? SelectDevice(
        IReadOnlyList<SafeAudioDevice> devices,
        string? name,
        bool preferOutput)
    {
        if (devices.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(name))
        {
            var exact = devices.FirstOrDefault(d =>
                string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;

            var contains = devices.FirstOrDefault(d =>
                d.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (contains is not null)
                return contains;
        }

        return devices.FirstOrDefault(d => preferOutput ? d.IsDefaultOutput : d.IsDefaultInput)
            ?? devices[0];
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            try
            {
                _session?.StopAll();
                _output?.Pause();
            }
            catch
            {
                // A lost device refuses transport calls; stopping is still the right
                // state to report.
            }

            _status = AudioEngineStatus.Stopped;
            _transport.StopInternal();
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        lock (_sync)
        {
            if (_nativeEngine is null)
                return Array.Empty<AudioDeviceInfo>();
            return MapDevices(_nativeEngine.EnumerateOutputDevices());
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        lock (_sync)
        {
            if (_nativeEngine is null)
                return Array.Empty<AudioDeviceInfo>();
            return MapDevices(_nativeEngine.EnumerateInputDevices());
        }
    }

    private static AudioDeviceInfo[] MapDevices(IReadOnlyList<SafeAudioDevice> devices)
    {
        var result = new AudioDeviceInfo[devices.Count];
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            result[i] = new AudioDeviceInfo(
                d.Name,
                d.IsDefaultOutput,
                d.IsDefaultInput,
                d.MaxInputChannels,
                d.MaxOutputChannels,
                d.DefaultSampleRate);
        }

        return result;
    }

    public Task SetOutputDeviceAsync(string deviceName, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "The OwnAudioSharp backend selects the output device when StartAsync opens the session. " +
            "Runtime device switching is not implemented yet.");

    public IAudioBus CreateBus(in AudioBusDesc desc)
    {
        lock (_sync)
        {
            ThrowIfNotRunning();
            var handle = new AudioBusHandle(++_nextId, 1);
            var bus = new OwnAudioBus(this, handle, desc);
            _buses[handle.Id] = bus;

            if (desc.Parent.IsValid && _buses.TryGetValue(desc.Parent.Id, out var parent))
                bus.Parent = parent;

            UpdateSoloStateLocked();
            return bus;
        }
    }

    public void DestroyBus(IAudioBus bus)
    {
        if (bus is not OwnAudioBus own)
            return;

        lock (_sync)
        {
            if (!_buses.Remove(own.Handle.Id))
                return;

            own.DetachChildren();
            own.DisposeCore();
            UpdateSoloStateLocked();
        }
    }

    public IAudioChannel CreateChannel(in AudioChannelDesc desc)
    {
        lock (_sync)
        {
            ThrowIfNotRunning();

            if (_channels.Count >= _options.MaxChannels)
                throw new AudioEngineException(
                    $"Channel limit reached ({_options.MaxChannels}). Raise AudioEngineOptions.MaxChannels for a larger session.");

            var clip = ResolveClip(desc.Clip);
            var (track, source) = CreateTrack(desc, clip);
            var handle = new AudioChannelHandle(++_nextId, 1);
            var channel = new OwnAudioChannel(this, handle, desc, track, source, clip);
            _channels[handle.Id] = channel;

            if (desc.Bus.IsValid && _buses.TryGetValue(desc.Bus.Id, out var bus))
                channel.Bus = bus;

            UpdateSoloStateLocked();
            return channel;
        }
    }

    public void DestroyChannel(IAudioChannel channel)
    {
        if (channel is not OwnAudioChannel own)
            return;

        lock (_sync)
        {
            if (!_channels.Remove(own.Handle.Id))
                return;

            own.DisposeCore();
            UpdateSoloStateLocked();
        }
    }

    public IAudioClip CreateClip(in AudioClipDesc desc)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var handle = new AudioClipHandle(++_nextId, 1);
            var clip = new OwnAudioClip(this, handle, desc);
            _clips[handle.Id] = clip;
            return clip;
        }
    }

    public void DestroyClip(IAudioClip clip)
    {
        if (clip is not OwnAudioClip own)
            return;

        lock (_sync)
        {
            if (!_clips.Remove(own.Handle.Id))
                return;
            own.DisposeCore();
        }
    }

    internal OwnAudioClip? ResolveClip(AudioClipHandle handle)
    {
        if (!handle.IsValid)
            return null;
        if (_clips.TryGetValue(handle.Id, out var clip))
            return clip;
        throw new AudioEngineException($"Unknown clip {handle}.");
    }

    internal (AudioTrack Track, object? Source) CreateTrack(in AudioChannelDesc desc, OwnAudioClip? clip)
    {
        var session = Session;
        int channels = desc.OutputChannels > 0
            ? desc.OutputChannels
            : _options.Channels;

        switch (desc.Kind)
        {
            case AudioChannelKind.File:
            {
                if (clip is null)
                    throw new AudioEngineException("A File channel needs a clip.");
                var file = session.AddFileTrack(clip.Path, (ushort)Math.Clamp(channels, 1, 256));
                file.Loop = desc.Loop;
                return (file.Track, file);
            }

            case AudioChannelKind.ClipLane:
            {
                if (clip is null)
                    throw new AudioEngineException("A ClipLane channel needs a clip.");
                var group = session.AddGroupTrack((ushort)Math.Clamp(channels, 1, 256));
                try
                {
                    group.AddClip(clip.GetGroupClip(), TimeSpan.Zero);
                }
                catch
                {
                    try { session.RemoveTrack(group.Track); } catch { }
                    throw;
                }
                return (group.Track, group);
            }

            case AudioChannelKind.Memory:
            {
                if (clip?.Samples is null)
                {
                    // Preload failed (unsupported container, decoder unavailable).
                    // Fall back to a streaming file track rather than dropping the channel.
                    if (clip is null)
                        throw new AudioEngineException("A Memory channel needs a clip.");
                    var fallback = session.AddFileTrack(clip.Path, (ushort)Math.Clamp(channels, 1, 256));
                    fallback.Loop = desc.Loop;
                    return (fallback.Track, fallback);
                }

                var memory = session.AddMemoryTrack(clip.Samples, desc.Loop);
                return (memory.Track, memory);
            }

            case AudioChannelKind.Input:
            {
                if (_nativeEngine is null)
                    throw new AudioEngineException("The native engine is not open.");
                var input = session.AddInputTrack(
                    _nativeEngine,
                    _inputDevice,
                    (uint)Math.Max(16, _options.BufferSize));
                return (input.Track, input);
            }

            case AudioChannelKind.Stream:
                return (session.AddTrack(), null);

            default:
                throw new AudioEngineException($"Unsupported channel kind '{desc.Kind}'.");
        }
    }

    internal void RemoveTrack(AudioTrack track, object? source)
    {
        try { track.Stop(); } catch { }
        if (source is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch { }
        }

        try { _session?.RemoveTrack(track); } catch { }
    }

    internal void ReplaceChannelSource(OwnAudioChannel channel, IAudioClip? clip)
    {
        lock (_sync)
        {
            if (!_channels.ContainsKey(channel.Handle.Id))
                return;

            var newClip = clip as OwnAudioClip;
            var desc = channel.Desc;
            var wasPlaying = channel.State == AudioPlaybackState.Playing;

            var oldTrack = channel.Track;
            var oldSource = channel.Source;

            // Build the replacement first. If it fails, the existing channel keeps
            // playing instead of being left with a dead track.
            var (newTrack, newSource) = CreateTrack(desc, newClip);

            channel.ClearEffects();
            RemoveTrack(oldTrack, oldSource);
            channel.SetTrack(newTrack, newSource, newClip);

            if (wasPlaying)
                channel.PlayCore();

            UpdateSoloStateLocked();
        }
    }

    internal void UpdateSoloState()
    {
        lock (_sync)
        {
            UpdateSoloStateLocked();
        }
    }

    private void UpdateSoloStateLocked()
    {
        _anySolo = _channels.Values.Any(c => c.Solo) || _buses.Values.Any(b => b.Solo);

        foreach (var channel in _channels.Values)
            channel.ApplyMute(_anySolo);

        foreach (var bus in _buses.Values)
            bus.ApplyGainToChildren();
    }

    internal void SeekAll(double seconds)
    {
        lock (_sync)
        {
            foreach (var channel in _channels.Values)
                channel.SeekCore(seconds);
        }
    }

    internal double GetTransportPosition()
    {
        lock (_sync)
        {
            double position = 0;
            foreach (var channel in _channels.Values)
                position = Math.Max(position, channel.Position);
            return position;
        }
    }

    internal void TransportPlay()
    {
        lock (_sync)
        {
            ThrowIfNotRunning();
            _session!.PlayAll();
        }
    }

    internal void TransportPause()
    {
        lock (_sync)
        {
            ThrowIfNotRunning();
            _session!.PauseAll();
        }
    }

    internal void TransportStop()
    {
        lock (_sync)
        {
            if (_session is null)
                return;
            _session.StopAll();
        }
    }

    internal float MasterGain
    {
        get { lock (_sync) return _session?.MasterGain ?? _options.MasterVolume; }
        set
        {
            lock (_sync)
            {
                if (_session is not null)
                    _session.MasterGain = Math.Clamp(value, 0f, 1f);
            }
        }
    }

    internal float MasterPan
    {
        get { lock (_sync) return _session?.MasterPan ?? 0f; }
        set
        {
            lock (_sync)
            {
                if (_session is not null)
                    _session.MasterPan = Math.Clamp(value, -1f, 1f);
            }
        }
    }

    internal OwnAudioEffectChain CreateMasterEffectChain()
    {
        lock (_sync)
        {
            if (_session is null)
                throw new AudioEngineException("The audio engine has not started.");
            return new OwnAudioEffectChain(_session.MasterEffects, _options.SampleRate);
        }
    }

    internal (float Left, float Right) GetMasterPeaks()
    {
        lock (_sync)
        {
            return _session?.GetMasterPeaks() ?? (0f, 0f);
        }
    }

    internal OwnAudioEffectChain CreateTrackEffectChain(TrackEffectChain chain)
        => new OwnAudioEffectChain(chain, _options.SampleRate);

    internal bool AllChannelsFinished()
    {
        lock (_sync)
        {
            if (_channels.Count == 0)
                return false;

            foreach (var channel in _channels.Values)
            {
                if (!channel.IsFinished && channel.State != AudioPlaybackState.Stopped)
                    return false;
            }

            return true;
        }
    }

    private void Poll(object? state)
    {
        SafeAudioOutputStream? output;
        lock (_sync)
        {
            if (_disposed || _status == AudioEngineStatus.Disposed)
                return;
            output = _output;
        }

        if (output is null)
            return;

        try
        {
            var kind = output.PollErrorState(out ulong count);
            if (kind != AudioStreamErrorKind.None &&
                count != (ulong)Interlocked.Read(ref _lastErrorCount))
            {
                Interlocked.Exchange(ref _lastErrorCount, (long)count);
                var eventKind = kind == AudioStreamErrorKind.DeviceNotAvailable
                    ? AudioDeviceEventKind.Lost
                    : AudioDeviceEventKind.PermanentFailure;

                DeviceEvent?.Invoke(this, new AudioDeviceEvent(
                    eventKind,
                    _outputDevice?.Name,
                    kind.ToString()));
            }
        }
        catch
        {
            // Diagnostics must never take the engine down.
        }

        if (_transport.State == AudioPlaybackState.Playing && AllChannelsFinished())
            _transport.RaisePlaybackEnded();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _status = AudioEngineStatus.Disposed;
        }

        DisposeCore();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_disposed)
                await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Dispose still has to release the device.
        }

        Dispose();
    }

    private void DisposeCore()
    {
        Timer? timer;
        List<OwnAudioChannel> channels;
        List<OwnAudioBus> buses;
        List<OwnAudioClip> clips;
        SafeAudioOutputStream? output;
        MultiTrackSession? session;
        SafeAudioEngine? engine;

        lock (_sync)
        {
            timer = _pollTimer;
            _pollTimer = null;

            channels = _channels.Values.ToList();
            buses = _buses.Values.ToList();
            clips = _clips.Values.ToList();
            _channels.Clear();
            _buses.Clear();
            _clips.Clear();

            output = _output;
            session = _session;
            engine = _nativeEngine;
            _output = null;
            _session = null;
            _nativeEngine = null;
        }

        timer?.Dispose();

        foreach (var channel in channels)
        {
            try { channel.DisposeCore(); } catch { }
        }

        foreach (var bus in buses)
        {
            try { bus.DisposeCore(); } catch { }
        }

        foreach (var clip in clips)
        {
            try { clip.DisposeCore(); } catch { }
        }

        try { output?.Dispose(); } catch { }
        try { session?.Dispose(); } catch { }
        try { engine?.Dispose(); } catch { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(OwnAudioEngine));
    }

    private void ThrowIfNotRunning()
    {
        if (_status != AudioEngineStatus.Running)
            throw new AudioEngineException($"The audio engine is not running (status: {_status}).");
    }
}
