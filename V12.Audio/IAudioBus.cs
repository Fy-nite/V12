namespace V12.Audio;

/// <summary>
/// A submix group. Buses carry gain, pan, mute and solo now; true summed-bus effects
/// require <see cref="AudioEngineCapabilities.TrueBusSumming"/> from the backend.
/// </summary>
public interface IAudioBus : IDisposable
{
    AudioBusHandle Handle { get; }
    string Name { get; set; }
    IAudioBus? Parent { get; set; }
    float Gain { get; set; }
    bool Muted { get; set; }
    bool Solo { get; set; }
}
