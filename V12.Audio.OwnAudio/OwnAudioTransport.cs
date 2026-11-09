namespace V12.Audio.OwnAudio;

internal sealed class OwnAudioTransport : IAudioTransport
{
    private readonly OwnAudioEngine _engine;
    private AudioPlaybackState _state;

    internal OwnAudioTransport(OwnAudioEngine engine)
    {
        _engine = engine;
    }

    public AudioPlaybackState State => _state;
    public double Position => _engine.GetTransportPosition();

    public event EventHandler? PlaybackEnded;

    public void Play()
    {
        _engine.TransportPlay();
        _state = AudioPlaybackState.Playing;
    }

    public void Pause()
    {
        _engine.TransportPause();
        _state = AudioPlaybackState.Paused;
    }

    public void Stop()
    {
        _engine.TransportStop();
        _state = AudioPlaybackState.Stopped;
    }

    public void Seek(double seconds) => _engine.SeekAll(seconds);

    internal void StopInternal()
    {
        _state = AudioPlaybackState.Stopped;
    }

    internal void RaisePlaybackEnded()
    {
        if (_state != AudioPlaybackState.Playing)
            return;

        _state = AudioPlaybackState.Stopped;
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }
}
