using System.Collections.Concurrent;
using Content.Shared.CCVar;
using Content.Shared.TTS;
using Robust.Client.Audio;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;

namespace Content.Client.TTS;

/// <summary>
/// Plays TTS audio in world
/// </summary>
// ReSharper disable once InconsistentNaming
public sealed class TTSSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly IAudioManager _audioManager = default!;

    private ISawmill _sawmill = default!;
    private float _volume;
    private float _radioVolume;
    private bool _radioQueueEnabled;

    private readonly ConcurrentQueue<QueuedTTS> _radioQueue = new();
    private readonly List<(EntityUid Entity, AudioComponent Component)> _currentRadioPlaying = new();

    public override void Initialize()
    {
        _sawmill = Logger.GetSawmill("tts");
        _cfg.OnValueChanged(CCVars.TTSVolume, OnTtsVolumeChanged, true);
        _cfg.OnValueChanged(CCVars.TTSRadioVolume, OnTtsRadioVolumeChanged, true);
        _cfg.OnValueChanged(CCVars.TTSRadioQueue, OnTtsRadioQueueChanged, true);
        SubscribeNetworkEvent<PlayTTSEvent>(OnPlayTTS);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _cfg.UnsubValueChanged(CCVars.TTSVolume, OnTtsVolumeChanged);
        _cfg.UnsubValueChanged(CCVars.TTSRadioVolume, OnTtsRadioVolumeChanged);
        _cfg.UnsubValueChanged(CCVars.TTSRadioQueue, OnTtsRadioQueueChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Clean up finished radio TTS
        _currentRadioPlaying.RemoveAll(x => Deleted(x.Entity));

        // If there's still radio TTS playing globally (queued), don't start another
        if (_currentRadioPlaying.Count > 0)
            return;

        if (_radioQueue.TryDequeue(out var queued))
        {
            var result = queued.SourceUid.HasValue
                ? PlayTTSBytes(queued.Data, queued.SourceUid.Value, queued.Params)
                : PlayTTSGlobal(queued.Data, queued.Params);

            if (result.HasValue)
                _currentRadioPlaying.Add(result.Value);
        }
    }

    public void RequestPreviewTTS(string voiceId)
    {
        RaiseNetworkEvent(new RequestPreviewTTSEvent(voiceId));
    }

    private void OnTtsVolumeChanged(float volume)
    {
        _volume = volume;
    }

    private void OnTtsRadioVolumeChanged(float volume)
    {
        _radioVolume = volume;
    }

    private void OnTtsRadioQueueChanged(bool enabled)
    {
        _radioQueueEnabled = enabled;
    }

    private void OnPlayTTS(PlayTTSEvent ev)
    {
        _sawmill.Verbose($"Play TTS audio {ev.Data.Length} bytes from {ev.SourceUid} entity");

        if (ev.Data.Length == 0)
        {
            _sawmill.Error("Received empty TTS audio data");
            return;
        }

        var sourceUid = ev.SourceUid.HasValue ? GetEntity(ev.SourceUid.Value) : (EntityUid?) null;
        var volume = ev.IsRadio ? _radioVolume : _volume;

        var isRadioFromDevice = ev.IsRadio && sourceUid.HasValue;
        var audioParams = AudioParams.Default
            .WithVolume(GetVolume(volume, ev.IsWhisper))
            .WithMaxDistance(GetDistance(ev.IsWhisper, ev.IsRadio && !isRadioFromDevice));

        if (ev.IsRadio)
        {
            if (_radioQueueEnabled)
            {
                _radioQueue.Enqueue(new QueuedTTS(ev.Data, audioParams, sourceUid));
            }
            else
            {
                // Play immediately without queuing
                var result = sourceUid.HasValue
                    ? PlayTTSBytes(ev.Data, sourceUid.Value, audioParams)
                    : PlayTTSGlobal(ev.Data, audioParams);

                if (result.HasValue)
                    _currentRadioPlaying.Add(result.Value);
            }
            return;
        }

        PlayTTSBytes(ev.Data, sourceUid, audioParams);
    }

    private (EntityUid Entity, AudioComponent Component)? PlayTTSBytes(byte[] data, EntityUid? sourceUid, AudioParams audioParams)
    {
        var shortArray = new short[data.Length / 2];
        for (var i = 0; i < shortArray.Length; i++)
            shortArray[i] = (short) ((data[i * 2 + 1] << 8) | (data[i * 2] & 0xFF));

        var audioStream = _audioManager.LoadAudioRaw(shortArray, 1, 22050);

        if (sourceUid != null)
            return _audio.PlayEntity(audioStream, sourceUid.Value, null, audioParams);

        return _audio.PlayGlobal(audioStream, null, audioParams);
    }

    private (EntityUid Entity, AudioComponent Component)? PlayTTSGlobal(byte[] data, AudioParams audioParams)
    {
        var shortArray = new short[data.Length / 2];
        for (var i = 0; i < shortArray.Length; i++)
            shortArray[i] = (short) ((data[i * 2 + 1] << 8) | (data[i * 2] & 0xFF));

        var audioStream = _audioManager.LoadAudioRaw(shortArray, 1, 22050);
        return _audio.PlayGlobal(audioStream, null, audioParams);
    }

    private float GetVolume(float baseVolume, bool isWhisper)
    {
        var volume = baseVolume;

        if (isWhisper)
            volume = 0.05f + (volume - 0.05f) * 0.25f;

        volume *= baseVolume / 3f;

        return SharedAudioSystem.GainToVolume(volume);
    }

    private float GetDistance(bool isWhisper, bool isGlobalRadio)
    {
        if (isGlobalRadio)
            return 0f;
        return isWhisper ? 5f : 10f;
    }

    private sealed record QueuedTTS(byte[] Data, AudioParams Params, EntityUid? SourceUid);
}
