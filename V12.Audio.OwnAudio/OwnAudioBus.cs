namespace V12.Audio.OwnAudio;

internal sealed class OwnAudioBus : IAudioBus
{
    private readonly OwnAudioEngine _engine;
    private readonly List<OwnAudioChannel> _channels = new();
    private readonly List<OwnAudioBus> _children = new();

    private string _name;
    private OwnAudioBus? _parent;
    private float _gain;
    private bool _muted;
    private bool _solo;
    private bool _disposed;

    internal OwnAudioBus(OwnAudioEngine engine, AudioBusHandle handle, in AudioBusDesc desc)
    {
        _engine = engine;
        Handle = handle;
        _name = string.IsNullOrWhiteSpace(desc.Name) ? "Bus" : desc.Name!;
        _gain = desc.Gain == 0 ? 1f : desc.Gain;
        _muted = desc.Muted;
    }

    public AudioBusHandle Handle { get; }

    public string Name
    {
        get => _name;
        set => _name = value;
    }

    public IAudioBus? Parent
    {
        get => _parent;
        set
        {
            if (ReferenceEquals(_parent, value))
                return;

            if (value is not null and not OwnAudioBus)
                throw new ArgumentException("The parent bus belongs to a different backend.", nameof(value));

            _parent?._children.Remove(this);
            _parent = value as OwnAudioBus;
            _parent?._children.Add(this);
            ApplyGainToChildren();
        }
    }

    public float Gain
    {
        get => _gain;
        set
        {
            _gain = Math.Max(0f, value);
            ApplyGainToChildren();
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value)
                return;
            _muted = value;
            _engine.UpdateSoloState();
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

    internal float EffectiveGain
    {
        get
        {
            if (!IsAudible(_engine.AnySolo))
                return 0f;

            float gain = _gain;
            if (_parent is not null)
                gain *= _parent.EffectiveGain;
            return Math.Max(0f, gain);
        }
    }

    internal bool IsAudible(bool anySolo)
    {
        if (_disposed || _muted)
            return false;

        if (_parent is not null && !_parent.IsAudible(anySolo))
            return false;

        if (!anySolo)
            return true;

        if (_solo)
            return true;

        if (_channels.Any(c => c.Solo))
            return true;

        if (_children.Any(b => b.Solo))
            return true;

        return false;
    }

    internal void AddChannel(OwnAudioChannel channel)
    {
        if (!_channels.Contains(channel))
            _channels.Add(channel);
    }

    internal void RemoveChannel(OwnAudioChannel channel)
    {
        _channels.Remove(channel);
    }

    internal void ApplyGainToChildren()
    {
        foreach (var channel in _channels)
            channel.ApplyGain();

        foreach (var child in _children)
            child.ApplyGainToChildren();
    }

    internal void DetachChildren()
    {
        foreach (var channel in _channels.ToArray())
            channel.Bus = null;

        foreach (var child in _children.ToArray())
            child.Parent = null;

        _channels.Clear();
        _children.Clear();
    }

    internal void DisposeCore()
    {
        if (_disposed)
            return;

        _disposed = true;
        DetachChildren();
        _parent?._children.Remove(this);
        _parent = null;
    }

    public void Dispose() => _engine.DestroyBus(this);
}
