namespace V12.Audio;

/// <summary>
/// Backend-independent audio engine. It owns one output device and one mixer graph:
/// channels feed buses, buses feed the master, the master reaches the device.
///
/// A backend is free to expose only the capabilities it can honour. Callers should
/// check <see cref="Capabilities"/> before relying on bus summing, runtime device
/// switching, offline rendering, and so on.
/// </summary>
public interface IAudioEngine : IDisposable, IAsyncDisposable
{
    AudioEngineStatus Status { get; }
    AudioEngineInfo Info { get; }
    AudioEngineCapabilities Capabilities { get; }
    bool IsRunning { get; }

    IAudioTransport Transport { get; }
    IAudioMasterBus Master { get; }

    /// <summary>
    /// Opens the device and builds the mixer session. Safe to call from any thread;
    /// implementations must not block a UI thread on device open.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the transport and parks the device. The mixer graph stays alive.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();
    IReadOnlyList<AudioDeviceInfo> GetInputDevices();

    /// <summary>
    /// Switches the output device. Backends without
    /// <see cref="AudioEngineCapabilities.RuntimeDeviceSelection"/> should throw
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    Task SetOutputDeviceAsync(string deviceName, CancellationToken cancellationToken = default);

    IAudioBus CreateBus(in AudioBusDesc desc);
    void DestroyBus(IAudioBus bus);

    IAudioChannel CreateChannel(in AudioChannelDesc desc);
    void DestroyChannel(IAudioChannel channel);

    IAudioClip CreateClip(in AudioClipDesc desc);
    void DestroyClip(IAudioClip clip);

    /// <summary>Fired when the output device is lost, recovering, or ready again.</summary>
    event EventHandler<AudioDeviceEvent>? DeviceEvent;
}
