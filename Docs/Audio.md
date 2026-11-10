# V12 Audio

`V12.Audio` is the backend-independent audio contract. `V12.Audio.OwnAudio` is the
OwnAudioSharp 4.x backend. The game-side adapter lives in `V12-Engine/Core/Audio`
and is intentionally separate, so the audio library can be used by a console app,
a DAW, or any other .NET 10 program without pulling in V12 or Godot.

## Projects

| Project | Responsibility |
| --- | --- |
| `V12.Audio` | Engine, channel, bus, clip, effect and transport contracts. `NullAudioEngine` for headless tests. |
| `V12.Audio.OwnAudio` | OwnAudioSharp backend built on `MultiTrackSession` and `Ownaudio.Safe`. |
| `V12-Engine/Core/Audio` | `AudioSystem`, the game adapter that maps `AudioSourceComponent` to channels and computes 3D pan/attenuation. |

The backend does not use `AudioMixer`. That convenience wrapper has a managed
`AudioConstants.MaxAudioSources = 30` guard. `MultiTrackSession` does not, and it
exposes per-track gain, pan, tempo, pitch, mute, effects and output routing. That is
the mixer a game or DAW actually wants.

## Standalone use

```csharp
using V12.Audio;
using V12.Audio.OwnAudio;

await using var engine = new OwnAudioEngine(new AudioEngineOptions
{
    SampleRate = 48_000,
    Channels = 2,
    BufferSize = 256,
    MaxChannels = 128,
});

await engine.StartAsync();

var music = engine.CreateBus(new AudioBusDesc { Name = "Music", Gain = 0.8f });
var clip = engine.CreateClip(new AudioClipDesc
{
    Path = "song.flac",
    Kind = AudioClipKind.File,
});

var channel = engine.CreateChannel(new AudioChannelDesc
{
    Name = "Drums",
    Kind = AudioChannelKind.File,
    Clip = clip.Handle,
    Bus = music.Handle,
});

channel.Play();
engine.Transport.Play();
```

## Channel kinds

| Kind | Backend source | Use |
| --- | --- | --- |
| `File` | `FileTrack` | Music, ambience, streamed dialogue. |
| `ClipLane` | `GroupTrack` + `GroupClip` | A DAW lane with one or more clips on a timeline. |
| `Memory` | `MemoryTrack` | Short one-shots decoded into native memory. |
| `Input` | `InputTrack` | Microphone or line input. |
| `Stream` | `AudioTrack.Write` | A caller-generated stream. |

## Buses and the master

A channel has gain, pan, mute, solo, tempo (0.25 to 4.0) and pitch (-24 to +24
semitones). A bus has gain, mute and solo. The master bus has gain, pan, the
native master effect chain and peak metering.

Buses are V12-level groups today. Gain, mute and solo are real. True summed-bus
effects are not exposed by OwnAudioSharp's managed API, so
`AudioEngineCapabilities.TrueBusSumming` is not set by the first backend. Per-channel
effects and master effects are native and are available now.

## Effects

```csharp
var reverb = channel.Effects.Add(AudioEffectKind.Reverb);
reverb.Mix = 0.25f;
reverb.SetParameter("RoomSize", 0.6f);
```

Effect parameters are addressed by name and applied through the managed wrapper
OwnAudioSharp creates for the native effect. Parameter access is control-rate only.

## Game adapter

`AudioSystem` is registered by `GameRoot` as `"AudioSystem"` and runs as an
`IGameService`. It resolves `"IAudioEngine"` from the registry during
`GameRoot.Initialize()`. A host registers its backend before that call:

```csharp
var audio = new OwnAudioEngine(new AudioEngineOptions
{
    SampleRate = 48_000,
    Channels = 2,
    BufferSize = 256,
    MaxChannels = 128,
});

root.Registry.Register("IAudioEngine", audio);
root.Initialize();
```

If no backend is registered, `AudioSystem` creates a `NullAudioEngine` so headless
runs and tests still work.

Each `AudioSourceComponent` gets one channel. The adapter computes distance
attenuation and stereo pan from the listener and writes `Gain`, `Pan`, `Tempo` and
transport state. If no `AudioListenerComponent` exists, the active camera is used as
the listener.

The component fields are:

- `AudioClipPath`
- `Volume`, `Pitch`
- `Loop`, `Autoplay`
- `MinDistance`, `MaxDistance`
- `Pan`, `SpatialBlend`
- `Attenuation` (`None`, `Linear`, `InverseDistance`, `Exponential`)
- `Bus` (`Master`, `Music`, `Sfx`, `Ui`, `Voice`)
- `MixerChannel` (optional named mixer strip; overrides `Bus`)
- `Priority`

## Audio components

The game adapter ships with a small component set:

| Component | Purpose |
| --- | --- |
| `AudioSourceComponent` | One clip emitter with volume, pitch, loop, autoplay, 3D position and bus. |
| `AudioListenerComponent` | Marks the listener element. Falls back to the active camera when absent. |
| `AudioMixerComponent` | Controls Master, Music, SFX, UI and Voice bus volumes and mutes. |
| `AudioBusComponent` | Creates a named bus with a parent, gain, mute and solo. |
| `AudioChannelComponent` | Creates a named mixer strip with a bus, gain, pan, mute and solo. |
| `AudioEffectComponent` | Adds one native effect to a source or to a named mixer channel. |
| `AudioPlaylistComponent` | Plays a list of clips in order, with shuffle and playlist loop. |

### Low-level routing

The low-level layer is scene-authored:

- `AudioBusComponent` creates a named bus. `ParentBusName` lets a bus feed another bus.
- `AudioChannelComponent` creates a named mixer strip. `BusName` is where the strip routes.
- `AudioSourceComponent` and `AudioPlaylistComponent` have a `Mixer Channel` field. Set it to the strip name and the source routes through that strip.
- `AudioEffectComponent.Target = MixerChannel` and `TargetName = <strip name>` applies the effect to every source on that strip.

```xml
<Element name="Bus">
  <Component type="AudioBusComponent" BusName="MusicBus" ParentBusName="Master" Gain="0.8" />
</Element>

<Element name="Mixer">
  <Component type="AudioChannelComponent"
             MixerName="MusicChannel"
             BusName="MusicBus"
             Gain="0.5" Pan="0.2" />
</Element>

<Element name="Music">
  <LocalTransform>
    <Position>0,0,0</Position>
  </LocalTransform>
  <Component type="AudioSourceComponent"
             AudioClipPath="v12://Assets/music.ogg"
             Volume="0.6" Loop="True" Autoplay="True"
             MixerChannel="MusicChannel" />
  <Component type="AudioEffectComponent"
             Target="MixerChannel"
             TargetName="MusicChannel"
             Kind="Reverb" Mix="0.3" />
</Element>
```

A mixer channel is a logical strip, not a native submix. OwnAudioSharp exposes one
native track per source, so gain, pan, mute and solo are applied to each source on the
strip, and channel effects are applied per source. True summed-bus effects remain a
future native extension.

`AudioEffectComponent` resolves the source on the same element and attaches its effect
to that source's live channel. If the source changes clip and the channel rebuilds,
the effect is reattached automatically. Add a second `AudioEffectComponent` for a
second effect.

`AudioPlaylistComponent` is an `IAudioSource`, so the normal `AudioSystem` channel
handling applies. Each clip plays once, then the playlist advances. `Playlist Loop`
repeats the list; `Shuffle` picks a different random clip each step. Edit its clip
list in the inspector.

A typical scene:

```xml
<Element name="Settings">
  <Component type="AudioMixerComponent" MusicVolume="0.2" SfxVolume="0.7" />
</Element>

<Element name="Music">
  <LocalTransform>
    <Position>0,0,0</Position>
  </LocalTransform>
  <Component type="AudioSourceComponent"
             AudioClipPath="v12://Assets/music.ogg"
             Volume="0.6" Loop="True" Autoplay="True" Bus="Music"
             SpatialBlend="0" />
  <Component type="AudioEffectComponent" Kind="Reverb" Mix="0.25" />
</Element>
```

## Limits and notes

- 128 channels is the default policy cap, not a native limit. Raise
  `AudioEngineOptions.MaxChannels` for a large DAW session and benchmark the CPU.
- The first validation run created 128 file channels and played all 128 at once on
  a 48 kHz stereo session: about 4% CPU, 32 ms reported latency and zero underruns.
  The old `AudioMixer` 30-source guard is not in this path.
- Tempo/pitch stretching is the expensive part. Game SFX that do not need it should
  leave tempo at 1.0 and pitch at 0.
- OwnAudioSharp has no HRTF or built-in 3D listener. The game adapter provides
  distance attenuation and equal-power stereo pan. Doppler and HRTF are future work.
- Runtime output-device switching is not implemented in the first backend. The
  device is selected when `StartAsync` opens the session.
- `IAudioEngine.DeviceEvent` is raised from the backend's poll thread. Handlers
  must be thread-safe and should not block.
- `IAudioPlayer` and the `FrameSnapshot` audio fields were removed. Legacy Nova no
  longer has an audio backend; migrate it to `IAudioEngine` when it comes back into
  scope.
- Godot must use the Dummy audio driver in `NovaV2.Host/project.godot` so Godot and
  OwnAudioSharp do not both open the output device.

## Validation

- `V12.Architecture.Tests/AudioContractTests.cs` covers the null engine contract.
- The standalone spike in the repo history validates device open, file playback,
  transport, pause, loop, end-of-stream and replay.
- The game spike validates `GameRoot` + `AudioSystem` + `AudioSourceComponent` with
  a listener and a positional emitter.
- For Godot exports, confirm `runtimes/<rid>/native/ownaudio_ffi.dll` (or the
  platform equivalent) is present in the export.
