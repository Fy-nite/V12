namespace V12.Audio;

/// <summary>
/// A reusable audio source description. File clips stream from disk, memory clips
/// are decoded into a native buffer, and clip-lane clips are placed on a group
/// timeline.
/// </summary>
public interface IAudioClip : IDisposable
{
    AudioClipHandle Handle { get; }
    string Path { get; }
    AudioClipKind Kind { get; }
    double Duration { get; }
    int SampleRate { get; }
    int Channels { get; }
}
