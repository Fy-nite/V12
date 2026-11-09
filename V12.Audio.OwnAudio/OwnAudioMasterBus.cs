namespace V12.Audio.OwnAudio;

internal sealed class OwnAudioMasterBus : IAudioMasterBus
{
    private readonly OwnAudioEngine _engine;
    private OwnAudioEffectChain? _effects;

    internal OwnAudioMasterBus(OwnAudioEngine engine)
    {
        _engine = engine;
    }

    public float Gain
    {
        get => _engine.MasterGain;
        set => _engine.MasterGain = value;
    }

    public float Pan
    {
        get => _engine.MasterPan;
        set => _engine.MasterPan = value;
    }

    public IAudioEffectChain Effects => _effects ??= _engine.CreateMasterEffectChain();

    public (float Left, float Right) Peaks => _engine.GetMasterPeaks();
}
