namespace V12.Audio;

/// <summary>
/// Lifecycle state of an audio engine. A backend moves through these states as the
/// device is opened, started, paused and torn down.
/// </summary>
public enum AudioEngineStatus
{
    Stopped,
    Starting,
    Running,
    Paused,
    DeviceLost,
    Failed,
    Disposed,
}

/// <summary>Transport state of a channel or of the whole session.</summary>
public enum AudioPlaybackState
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>What kind of native source backs a channel.</summary>
public enum AudioChannelKind
{
    /// <summary>One file streamed and decoded by the backend.</summary>
    File,

    /// <summary>A timeline of one or more clips, summed by the backend.</summary>
    ClipLane,

    /// <summary>A fully decoded buffer held in native memory.</summary>
    Memory,

    /// <summary>A live input stream from the capture device.</summary>
    Input,

    /// <summary>A caller-fed ring buffer.</summary>
    Stream,
}

/// <summary>What kind of source an <see cref="IAudioClip"/> describes.</summary>
public enum AudioClipKind
{
    File,
    ClipLane,
    Memory,
}

/// <summary>Built-in native effect types understood by the audio contract.</summary>
public enum AudioEffectKind
{
    Reverb,
    Equalizer,
    Compressor,
    Limiter,
    Delay,
    Chorus,
    Distortion,
    Overdrive,
    Flanger,
    Phaser,
    Rotary,
    AutoGain,
    Enhancer,
    Gate,
    PitchShift,
    DynamicAmp,
    Equalizer30,
    OwnReverb,
    OwnCompressor,
    OwnDelay,
    OwnDynamicAmp,
}

/// <summary>Device events reported by an <see cref="IAudioEngine"/>.</summary>
public enum AudioDeviceEventKind
{
    Lost,
    Recovering,
    Ready,
    PermanentFailure,
}

/// <summary>
/// Backend features. A backend must only advertise what it actually implements;
/// callers use this to decide whether a feature is available without probing.
/// </summary>
[Flags]
public enum AudioEngineCapabilities
{
    None = 0,
    FilePlayback = 1 << 0,
    MemoryPlayback = 1 << 1,
    ClipLanes = 1 << 2,
    InputRecording = 1 << 3,
    PerChannelEffects = 1 << 4,
    MasterEffects = 1 << 5,
    BusGain = 1 << 6,
    TrueBusSumming = 1 << 7,
    OutputRouting = 1 << 8,
    DeviceEnumeration = 1 << 9,
    RuntimeDeviceSelection = 1 << 10,
    Metering = 1 << 11,
    OfflineRender = 1 << 12,
}

/// <summary>Opaque channel identity. The generation catches a destroyed-then-reused slot.</summary>
public readonly record struct AudioChannelHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0;
    public static AudioChannelHandle Invalid => default;
    public override string ToString() => $"channel #{Id}:{Generation}";
}

/// <summary>Opaque bus identity.</summary>
public readonly record struct AudioBusHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0;
    public static AudioBusHandle Invalid => default;
    public override string ToString() => $"bus #{Id}:{Generation}";
}

/// <summary>Opaque clip identity.</summary>
public readonly record struct AudioClipHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0;
    public static AudioClipHandle Invalid => default;
    public override string ToString() => $"clip #{Id}:{Generation}";
}

/// <summary>Opaque effect identity.</summary>
public readonly record struct AudioEffectHandle(uint Id, uint Generation)
{
    public bool IsValid => Id != 0;
    public static AudioEffectHandle Invalid => default;
    public override string ToString() => $"effect #{Id}:{Generation}";
}

/// <summary>One enumerated output or input device.</summary>
public readonly record struct AudioDeviceInfo(
    string Name,
    bool IsDefaultOutput,
    bool IsDefaultInput,
    int MaxInputChannels,
    int MaxOutputChannels,
    int DefaultSampleRate)
{
    public bool IsOutput => MaxOutputChannels > 0;
    public bool IsInput => MaxInputChannels > 0;
}

/// <summary>A device state change reported by the backend.</summary>
public readonly record struct AudioDeviceEvent(
    AudioDeviceEventKind Kind,
    string? DeviceName = null,
    string? Detail = null);

/// <summary>Engine configuration. The backend may clamp values it cannot serve.</summary>
public sealed class AudioEngineOptions
{
    /// <summary>Output sample rate in Hz.</summary>
    public int SampleRate { get; set; } = 48_000;

    /// <summary>Bus width. 2 = stereo.</summary>
    public int Channels { get; set; } = 2;

    /// <summary>Requested device buffer in frames.</summary>
    public int BufferSize { get; set; } = 256;

    /// <summary>
    /// Policy cap on simultaneously registered channels. This is a V12 limit, not a
    /// native one; raise it for a DAW session.
    /// </summary>
    public int MaxChannels { get; set; } = 128;

    /// <summary>Master gain applied when the engine starts.</summary>
    public float MasterVolume { get; set; } = 1f;

    /// <summary>Output device name. Null uses the system default.</summary>
    public string? OutputDeviceName { get; set; }

    /// <summary>Input device name. Null uses the system default.</summary>
    public string? InputDeviceName { get; set; }

    /// <summary>Enable the capture side. Input channels still have to be created.</summary>
    public bool EnableInput { get; set; }

    /// <summary>
    /// Clips up to this length are decoded into memory when a clip-lane clip is opened.
    /// Longer files stream from disk.
    /// </summary>
    public double MemoryMaxSeconds { get; set; } = 30.0;
}

/// <summary>Snapshot of what the backend is doing right now.</summary>
public struct AudioEngineInfo
{
    public AudioEngineStatus Status;
    public int SampleRate;
    public int Channels;
    public int ActiveChannels;
    public int MaxChannels;
    public int LatencyFrames;
    public float LatencyMs;
    public float CpuLoad;
    public long UnderrunFrames;
    public string? DeviceName;
    public string? BackendName;
}

/// <summary>Description of a channel to create.</summary>
public struct AudioChannelDesc
{
    public string? Name;
    public AudioChannelKind Kind;
    public AudioBusHandle Bus;
    public AudioClipHandle Clip;
    public bool Loop;
    public float Gain;
    public float Pan;
    public int Priority;

    /// <summary>
    /// Decode width for file channels. 0 follows the engine bus width.
    /// </summary>
    public int OutputChannels;

    public static AudioChannelDesc Default => new()
    {
        Name = "Channel",
        Kind = AudioChannelKind.File,
        Gain = 1f,
        Pan = 0f,
        Priority = 0,
        OutputChannels = 0,
    };
}

/// <summary>Description of a bus to create.</summary>
public struct AudioBusDesc
{
    public string? Name;
    public AudioBusHandle Parent;
    public float Gain;
    public bool Muted;

    public static AudioBusDesc Default => new()
    {
        Name = "Bus",
        Gain = 1f,
        Muted = false,
    };
}

/// <summary>Description of a clip to create.</summary>
public struct AudioClipDesc
{
    public string? Path;
    public AudioClipKind Kind;
    public bool Preload;
    public int SampleRate;
    public int Channels;
    public double MemoryMaxSeconds;

    public static AudioClipDesc Default => new()
    {
        Kind = AudioClipKind.File,
        Preload = false,
        SampleRate = 0,
        Channels = 0,
        MemoryMaxSeconds = 30.0,
    };
}

/// <summary>Raised when the audio backend cannot fulfil an operation.</summary>
public sealed class AudioEngineException : Exception
{
    public AudioEngineException(string message) : base(message) { }
    public AudioEngineException(string message, Exception inner) : base(message, inner) { }
}
