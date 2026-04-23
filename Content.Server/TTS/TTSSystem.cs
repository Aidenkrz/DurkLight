using System.Linq;
using System.Threading.Tasks;
using Content.Server.Chat.Systems;
using Content.Server.Radio;
using Content.Server.Radio.Components;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.TTS;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server.TTS;

// ReSharper disable once InconsistentNaming
public sealed partial class TTSSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly TTSManager _ttsManager = default!;
    [Dependency] private readonly SharedTransformSystem _xforms = default!;
    [Dependency] private readonly IRobustRandom _rng = default!;
    [Dependency] private readonly ActorSystem _actor = default!;

    private readonly List<string> _sampleText =
    [
        "Hello station, I teleported the janitor.",
        "Ms. Sarah, will Engineering handle the theater issue?",
        "Since Samuel was detained, should we switch to code green?",
        "He wants an interview. Where are you?",
        "Samuel Rodriguez broke the bridge door with an emag!",
        "Credit where it's due—the newspaper is working great.",
        "Praise and glory from NT.",
        "Can someone build a podium in the theater?",
        "Clown, I'm heading to an interview. I'll be gone about 10 minutes.",
        "Chief, I'm heading to an interview. I'll be gone about 10 minutes.",
        "I think the anomaly broke the barrier between the Singularity and the station."
    ];

    private const int MaxMessageChars = 100 * 2;
    private bool _isEnabled;

    public override void Initialize()
    {
        _cfg.OnValueChanged(CCVars.TTSEnabled, OnTtsEnabledChanged, true);
        _cfg.OnValueChanged(CCVars.TTSApiUrl, OnTtsEndpointChanged);
        _cfg.OnValueChanged(CCVars.TTSApiKey, OnTtsEndpointChanged);

        SubscribeLocalEvent<TransformSpeechEvent>(OnTransformSpeech);
        SubscribeLocalEvent<TTSComponent, EntitySpokeEvent>(OnEntitySpoke);
        SubscribeLocalEvent<RadioSpokeEvent>(OnRadioSpoke);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
        SubscribeNetworkEvent<RequestPreviewTTSEvent>(OnRequestPreviewTTS);
    }

    private async void OnTtsEnabledChanged(bool enabled)
    {
        _isEnabled = enabled;

        if (enabled)
            await EnsureVoicesDownloaded();
    }

    private async void OnTtsEndpointChanged(string _)
    {
        if (!_isEnabled)
            return;

        _ttsManager.ClearCache();
        await EnsureVoicesDownloaded();
    }

    private async Task EnsureVoicesDownloaded()
    {
        var requiredVoices = _prototypeManager
            .EnumeratePrototypes<TTSVoicePrototype>()
            .Select(v => v.Model)
            .Distinct()
            .ToList();

        await _ttsManager.EnsureVoicesDownloadedAsync(requiredVoices);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _ttsManager.ClearCache();
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        if (!_cfg.GetCVar(CCVars.TTSCacheRoundPersistence))
            _ttsManager.ClearCache();
    }

    private async void OnRequestPreviewTTS(RequestPreviewTTSEvent ev, EntitySessionEventArgs args)
    {
        if (!_isEnabled
            || !_prototypeManager.TryIndex<TTSVoicePrototype>(ev.VoiceId, out var protoVoice))
        {
            return;
        }

        var previewText = _rng.Pick(_sampleText);
        var soundData = await GenerateTTS(previewText, protoVoice.Model, protoVoice.Speaker);
        if (soundData is null)
            return;

        RaiseNetworkEvent(new PlayTTSEvent(soundData), Filter.SinglePlayer(args.SenderSession));
    }

    private async void OnEntitySpoke(EntityUid uid, TTSComponent component, EntitySpokeEvent args)
    {
        var voiceId = component.VoicePrototypeId;
        if (!_isEnabled || args.Message.Length > MaxMessageChars || voiceId == null)
            return;

        if (args.Channel != null)
            return;

        var voiceEv = new TransformSpeakerVoiceEvent(uid, voiceId);
        RaiseLocalEvent(uid, voiceEv);
        voiceId = voiceEv.VoiceId;

        if (!_prototypeManager.TryIndex<TTSVoicePrototype>(voiceId, out var protoVoice))
            return;

        if (args.IsWhisper)
            HandleWhisper(uid, args.Message, protoVoice.Model, protoVoice.Speaker);
        else
            HandleSay(uid, args.Message, protoVoice.Model, protoVoice.Speaker);
    }

    private async void OnRadioSpoke(RadioSpokeEvent ev)
    {
        if (!_isEnabled || ev.Message.Length > MaxMessageChars)
            return;

        if (!TryComp<TTSComponent>(ev.MessageSource, out var ttsComp) || ttsComp.VoicePrototypeId == null)
            return;

        var voiceId = ttsComp.VoicePrototypeId;
        var voiceEv = new TransformSpeakerVoiceEvent(ev.MessageSource, voiceId);
        RaiseLocalEvent(ev.MessageSource, voiceEv);
        voiceId = voiceEv.VoiceId;

        if (!_prototypeManager.TryIndex<TTSVoicePrototype>(voiceId, out var protoVoice))
            return;

        var soundData = await GenerateTTS(ev.Message, protoVoice.Model, protoVoice.Speaker);
        if (soundData is null)
            return;

        var xformQuery = GetEntityQuery<TransformComponent>();
        var sourcePos = _xforms.GetWorldPosition(xformQuery.GetComponent(ev.MessageSource), xformQuery);

        _actor.TryGetSession(ev.MessageSource, out var speakerSession);

        var globalFilter = Filter.Empty();

        var radioSpeakers = new List<EntityUid>();

        foreach (var receiver in ev.Receivers)
        {
            if (HasComp<RadioSpeakerComponent>(receiver))
            {
                radioSpeakers.Add(receiver);
                continue;
            }

            var target = Transform(receiver).ParentUid;
            ICommonSession? session = null;

            if (!_actor.TryGetSession(target, out session) || session == null)
                _actor.TryGetSession(receiver, out session);

            if (session == null || session == speakerSession)
                continue;

            if (session.AttachedEntity.HasValue)
            {
                var playerPos = _xforms.GetWorldPosition(xformQuery.GetComponent(session.AttachedEntity.Value), xformQuery);
                var distance = (sourcePos - playerPos).Length();

                if (distance <= ChatSystem.VoiceRange)
                    continue;
            }

            globalFilter.AddPlayer(session);
        }

        if (globalFilter.Recipients.Any())
        {
            var ttsEvent = new PlayTTSEvent(soundData, GetNetEntity(ev.MessageSource), isRadio: true);
            RaiseNetworkEvent(ttsEvent, globalFilter);
        }

        foreach (var speaker in radioSpeakers)
        {
            var ttsEvent = new PlayTTSEvent(soundData, GetNetEntity(speaker), isRadio: true);
            RaiseNetworkEvent(ttsEvent, Filter.Pvs(speaker));
        }
    }

    private async void HandleSay(EntityUid uid, string message, string model, string speaker)
    {
        var soundData = await GenerateTTS(message, model, speaker);
        if (soundData is null)
            return;

        RaiseNetworkEvent(new PlayTTSEvent(soundData, GetNetEntity(uid)), Filter.Pvs(uid));
    }

    private async void HandleWhisper(EntityUid uid, string message, string model, string speaker)
    {
        var soundData = await GenerateTTS(message, model, speaker);
        if (soundData is null)
            return;

        var ttsEvent = new PlayTTSEvent(soundData, GetNetEntity(uid), true);
        var xformQuery = GetEntityQuery<TransformComponent>();
        var sourcePos = _xforms.GetWorldPosition(xformQuery.GetComponent(uid), xformQuery);

        foreach (var session in Filter.Pvs(uid).Recipients)
        {
            if (!session.AttachedEntity.HasValue)
                continue;

            var xform = xformQuery.GetComponent(session.AttachedEntity.Value);
            var distance = (sourcePos - _xforms.GetWorldPosition(xform, xformQuery)).Length();

            if (distance <= 100) // 10 * 10
                RaiseNetworkEvent(ttsEvent, session);
        }
    }

    private async Task<byte[]?> GenerateTTS(string text, string model, string speaker)
    {
        var textSanitized = Sanitize(text);
        if (string.IsNullOrEmpty(textSanitized))
            return null;

        if (char.IsLetter(textSanitized[^1]))
            textSanitized += ".";

        return await _ttsManager.ConvertTextToSpeech(model, speaker, textSanitized);
    }
}

public sealed class TransformSpeakerVoiceEvent(EntityUid sender, string voiceId) : EntityEventArgs
{
    public EntityUid Sender = sender;
    public string VoiceId = voiceId;
}
