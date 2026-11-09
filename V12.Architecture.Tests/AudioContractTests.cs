using V12.Audio;
using V12.Components;
using V12.Core;
using V12.Core.Audio;
using Xunit;

namespace V12.Architecture.Tests;

public sealed class AudioContractTests
{
    [Fact]
    public async Task NullEngineRunsAndCarriesChannelState()
    {
        await using var engine = new NullAudioEngine(new AudioEngineOptions
        {
            SampleRate = 48_000,
            Channels = 2,
            MaxChannels = 128,
        });

        await engine.StartAsync();
        Assert.True(engine.IsRunning);

        var bus = engine.CreateBus(new AudioBusDesc { Name = "Music", Gain = 0.5f });
        var clip = engine.CreateClip(new AudioClipDesc { Path = "test.wav" });
        var channel = engine.CreateChannel(new AudioChannelDesc
        {
            Name = "Tone",
            Kind = AudioChannelKind.File,
            Clip = clip.Handle,
            Bus = bus.Handle,
            Loop = true,
        });

        channel.Gain = 0.75f;
        channel.Pan = -0.25f;
        channel.Play();

        Assert.Equal(AudioPlaybackState.Playing, channel.State);
        Assert.False(channel.IsFinished);
        Assert.Same(bus, channel.Bus);
        Assert.Equal(0.75f, channel.Gain);
        Assert.Equal(-0.25f, channel.Pan);
        Assert.Equal(1, engine.Info.ActiveChannels);

        channel.Pause();
        Assert.Equal(AudioPlaybackState.Paused, channel.State);

        channel.Stop();
        Assert.Equal(AudioPlaybackState.Stopped, channel.State);

        engine.DestroyChannel(channel);
        engine.DestroyBus(bus);
        engine.DestroyClip(clip);
        Assert.Equal(0, engine.Info.ActiveChannels);
    }

    [Fact]
    public async Task NullEngineTransportReflectsState()
    {
        await using var engine = new NullAudioEngine();
        await engine.StartAsync();

        engine.Transport.Play();
        Assert.Equal(AudioPlaybackState.Playing, engine.Transport.State);

        engine.Transport.Seek(4.5);
        Assert.Equal(4.5, engine.Transport.Position);

        engine.Transport.Pause();
        Assert.Equal(AudioPlaybackState.Paused, engine.Transport.State);

        engine.Transport.Stop();
        Assert.Equal(AudioPlaybackState.Stopped, engine.Transport.State);
        Assert.Equal(0, engine.Transport.Position);
    }

    [Fact]
    public void NullEngineDoesNotAdvertiseUnsupportedFeatures()
    {
        using var engine = new NullAudioEngine();
        Assert.Equal(AudioEngineCapabilities.None, engine.Capabilities);
    }

    [Fact]
    public async Task AudioSystemRoutesSourceThroughMixerChannelAndBus()
    {
        var root = new GameRoot();

        var engine = new NullAudioEngine();
        root.Registry.Register("IAudioEngine", engine);
        await engine.StartAsync();

        var world = root.CreateWorld("AudioRoutingTest");
        world.TickingEnabled = true;

        var listener = new Element("Listener");
        listener.AddComponent(new AudioListenerComponent());
        world.AddElement(listener);

        var busElement = new Element("Bus");
        busElement.AddComponent(new AudioBusComponent
        {
            BusName = "TestBus",
            ParentBusName = "Master",
            Gain = 0.5f,
        });
        world.AddElement(busElement);

        var mixerElement = new Element("Mixer");
        mixerElement.AddComponent(new AudioChannelComponent
        {
            MixerName = "TestChannel",
            BusName = "TestBus",
            Gain = 0.5f,
            Pan = 0.25f,
        });
        world.AddElement(mixerElement);

        var emitter = new Element("Emitter");
        emitter.AddComponent(new AudioSourceComponent("test.wav", volume: 0.5f, autoplay: true)
        {
            MixerChannel = "TestChannel",
        });
        emitter.AddComponent(new AudioEffectComponent
        {
            Target = AudioEffectTarget.MixerChannel,
            TargetName = "TestChannel",
            Kind = AudioEffectKind.Reverb,
            Mix = 0.2f,
        });
        world.AddElement(emitter);

        root.SelectWorld(world);
        root.Initialize();

        for (int i = 0; i < 3; i++)
            root.V12Tick(1f / 60f);

        var system = root.Registry.Get<AudioSystem>("AudioSystem");
        Assert.NotNull(system);

        var mixer = system!.GetMixerChannel("TestChannel");
        Assert.NotNull(mixer);
        Assert.Equal(0.5f, mixer!.Gain);
        Assert.Equal(0.25f, mixer.Pan);

        var bus = system.GetBus("TestBus");
        Assert.NotNull(bus);
        Assert.Equal(0.5f, bus!.Gain);

        var channels = new List<IAudioChannel>();
        system.GetChannelsForMixerChannel("TestChannel", channels);
        var channel = Assert.Single(channels);
        Assert.Single(channel.Effects.Effects);
        Assert.Equal(0.25f, channel.Gain, 3);
        Assert.Equal(0.25f, channel.Pan, 3);

        engine.Dispose();
    }
}
