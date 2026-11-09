namespace V12.Audio;

/// <summary>Session-wide transport.</summary>
public interface IAudioTransport
{
    AudioPlaybackState State { get; }

    /// <summary>Project position in seconds.</summary>
    double Position { get; }

    void Play();
    void Pause();
    void Stop();
    void Seek(double seconds);

    /// <summary>Raised when every playing channel has reached its end.</summary>
    event EventHandler? PlaybackEnded;
}

/// <summary>The final bus: gain, pan, effects and metering on the summed mix.</summary>
public interface IAudioMasterBus
{
    float Gain { get; set; }
    float Pan { get; set; }
    IAudioEffectChain Effects { get; }
    (float Left, float Right) Peaks { get; }
}
