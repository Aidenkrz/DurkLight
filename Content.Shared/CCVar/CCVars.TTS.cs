using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    #region Client Settings

    public static readonly CVarDef<float> TTSVolume =
        CVarDef.Create("tts.volume", 0.5f * 4, CVar.ARCHIVE | CVar.CLIENTONLY);

    public static readonly CVarDef<float> TTSRadioVolume =
        CVarDef.Create("tts.radio_volume", 0.5f, CVar.ARCHIVE | CVar.CLIENTONLY);

    /// <summary>
    /// When enabled, radio TTS messages are queued and played one at a time.
    /// When disabled, radio TTS messages play immediately and can overlap.
    /// </summary>
    public static readonly CVarDef<bool> TTSRadioQueue =
        CVarDef.Create("tts.radio_queue", true, CVar.ARCHIVE | CVar.CLIENTONLY);

    public static readonly CVarDef<float> TTSUnknownVolume =
        CVarDef.Create("tts.unknown_volume", 0.2f * 4, CVar.ARCHIVE | CVar.CLIENTONLY);

    #endregion
    public static readonly CVarDef<bool> TTSEnabled =
        CVarDef.Create("tts.enabled", true, CVar.SERVERONLY);

    /// <summary>
    /// Number of TTS generations that can be done simultaneously
    /// </summary>
    public static readonly CVarDef<int> TTSSimultaneousGenerations =
        CVarDef.Create("tts.simultaneous_generations", 1, CVar.SERVERONLY);

    /// <summary>
    /// Number of TTS generations that can be queued, anything new TTS generations will be ignored.
    /// </summary>
    public static readonly CVarDef<int> TTSQueueMax =
        CVarDef.Create("tts.queue_max", 20, CVar.SERVERONLY);

    /// Can be "file" to store in the cache_path, or "memory" to store it in memory.
    /// Memory is way faster, but servers are usually more limited by memory than storage, pick your poison.
    public static readonly CVarDef<string> TTSCacheType =
        CVarDef.Create("tts.cache_type", "memory", CVar.SERVERONLY);

    public static readonly CVarDef<string> TTSCachePath =
        CVarDef.Create("tts.cache_path", "data/tts/cache", CVar.SERVERONLY);

    public static readonly CVarDef<int> TTSMaxCached =
        CVarDef.Create("tts.max_cached", 2048, CVar.SERVERONLY);

    /// Cleans up the cache between rounds if false
    public static readonly CVarDef<bool> TTSCacheRoundPersistence =
        CVarDef.Create("tts.cache_round_persistence", true, CVar.SERVERONLY);

    /// <summary>
    /// The URL of the Piper TTS HTTP API server.
    /// </summary>
    public static readonly CVarDef<string> TTSApiUrl =
        CVarDef.Create("tts.api_url", "http://localhost:50000", CVar.SERVERONLY);

    /// <summary>
    /// The API key for authenticating with the Piper TTS server. Leave empty if no authentication is required.
    /// </summary>
    public static readonly CVarDef<string> TTSApiKey =
        CVarDef.Create("tts.api_key", "test", CVar.SERVERONLY | CVar.CONFIDENTIAL);

    /// <summary>
    /// Timeout in seconds for TTS API requests.
    /// </summary>
    public static readonly CVarDef<int> TTSRequestTimeout =
        CVarDef.Create("tts.request_timeout", 15, CVar.SERVERONLY);

    /// <summary>
    /// Maximum number of retry attempts for failed TTS API requests.
    /// </summary>
    public static readonly CVarDef<int> TTSMaxRetries =
        CVarDef.Create("tts.max_retries", 3, CVar.SERVERONLY);
}
