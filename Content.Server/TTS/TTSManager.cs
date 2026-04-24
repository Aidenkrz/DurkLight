using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared.CCVar;
using Prometheus;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Server.TTS;

// ReSharper disable once InconsistentNaming
public sealed class TTSManager
{
    private static readonly Histogram RequestTimings = Metrics.CreateHistogram(
        "tts_req_timings",
        "Timings of TTS API requests",
        new HistogramConfiguration
        {
            LabelNames = ["type"],
            Buckets = Histogram.ExponentialBuckets(.1, 1.5, 10),
        });

    private static readonly Counter WantedCount = Metrics.CreateCounter(
        "tts_wanted_count",
        "Amount of wanted TTS audio.");

    private static readonly Counter ReusedCount = Metrics.CreateCounter(
        "tts_reused_count",
        "Amount of reused TTS audio from cache.");

    private static readonly Counter DeduplicatedCount = Metrics.CreateCounter(
        "tts_deduplicated_count",
        "Amount of TTS requests deduplicated from in-flight requests.");

    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IResourceManager _resource = default!;

    private ISawmill _sawmill = default!;
    private HttpClient _httpClient = default!;
    private readonly Dictionary<int, byte[]> _memoryCache = [];
    private readonly Dictionary<int, Task<byte[]?>> _inFlightRequests = [];
    private readonly object _inFlightLock = new();

    private ResPath _cachePath;
    private string _apiUrl = "http://localhost:5000";
    private string _apiKey = "";
    private int _requestTimeout = 15;
    private int _maxRetries = 3;
    private int _maxQueuedGenerations = 20;
    private int _queuedGenerations;
    private SemaphoreSlim _generationSemaphore = new(1);

    private bool _connectionVerified;
    private CancellationTokenSource? _connectionRetryCts;
    private const int ConnectionRetryIntervalMs = 15000;

    public TTSManager()
    {
        Initialize();
    }

    private void Initialize()
    {
        IoCManager.InjectDependencies(this);
        _sawmill = Logger.GetSawmill("tts");

        _cachePath = MakeDataPath(_cfg.GetCVar(CCVars.TTSCachePath));
        _cfg.OnValueChanged(CCVars.TTSCachePath, OnCachePathChanged);

        _apiUrl = _cfg.GetCVar(CCVars.TTSApiUrl).TrimEnd('/');
        _cfg.OnValueChanged(CCVars.TTSApiUrl, OnApiUrlChanged);

        _apiKey = _cfg.GetCVar(CCVars.TTSApiKey);
        _cfg.OnValueChanged(CCVars.TTSApiKey, OnApiKeyChanged);

        _requestTimeout = _cfg.GetCVar(CCVars.TTSRequestTimeout);
        _cfg.OnValueChanged(CCVars.TTSRequestTimeout, OnTimeoutChanged);

        _maxRetries = _cfg.GetCVar(CCVars.TTSMaxRetries);
        _cfg.OnValueChanged(CCVars.TTSMaxRetries, v => _maxRetries = v);

        _generationSemaphore = new SemaphoreSlim(_cfg.GetCVar(CCVars.TTSSimultaneousGenerations));
        _cfg.OnValueChanged(CCVars.TTSSimultaneousGenerations, OnRateLimitChanged);

        _maxQueuedGenerations = _cfg.GetCVar(CCVars.TTSQueueMax);
        _cfg.OnValueChanged(CCVars.TTSQueueMax, v => _maxQueuedGenerations = v);

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(_requestTimeout) };

        var cacheDir = _cachePath.ToString();
        if (!Directory.Exists(cacheDir))
            Directory.CreateDirectory(cacheDir);
    }

    private void OnCachePathChanged(string path)
    {
        _cachePath = MakeDataPath(path);
        var cacheDir = _cachePath.ToString();
        if (!Directory.Exists(cacheDir))
            Directory.CreateDirectory(cacheDir);
    }

    private void OnApiUrlChanged(string url)
    {
        _apiUrl = url.TrimEnd('/');
        _connectionVerified = false;
        StartConnectionRetry();
    }

    private void OnApiKeyChanged(string key)
    {
        _apiKey = key;
        _connectionVerified = false;
        StartConnectionRetry();
    }

    private void StartConnectionRetry()
    {
        if (_connectionRetryCts != null)
            return;

        _connectionRetryCts = new CancellationTokenSource();
        _ = ConnectionRetryLoopAsync(_connectionRetryCts.Token);
    }

    private void StopConnectionRetry()
    {
        _connectionRetryCts?.Cancel();
        _connectionRetryCts?.Dispose();
        _connectionRetryCts = null;
    }

    private async Task ConnectionRetryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_connectionVerified)
        {
            _sawmill.Info("Attempting to verify TTS API connection at {Url}...", _apiUrl);

            if (await VerifyConnectionAsync())
            {
                _connectionVerified = true;
                _sawmill.Info("TTS API connection verified successfully");
                StopConnectionRetry();
                return;
            }

            _sawmill.Warning("TTS API connection failed, retrying in {Seconds} seconds...", ConnectionRetryIntervalMs / 1000);

            try
            {
                await Task.Delay(ConnectionRetryIntervalMs, ct);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        StopConnectionRetry();
    }

    private async Task<bool> VerifyConnectionAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_apiUrl}/voices");
            AddAuthHeader(request);

            var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e)
        {
            _sawmill.Debug("TTS API connection check failed: {Error}", e.Message);
            return false;
        }
    }

    private void OnTimeoutChanged(int timeout)
    {
        _requestTimeout = timeout;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(timeout) };
    }

    private void OnRateLimitChanged(int limit)
    {
        var currentCount = _generationSemaphore.CurrentCount;
        if (limit > currentCount)
        {
            _generationSemaphore.Release(limit - currentCount);
        }
        else if (limit < currentCount)
        {
            for (var i = 0; i < currentCount - limit; i++)
                _generationSemaphore.Wait();
        }
    }

    private ResPath MakeDataPath(string path)
    {
        return path.StartsWith("data/")
            ? new ResPath(_resource.UserData.RootDir + path[5..])
            : new ResPath(path);
    }

    /// <summary>
    /// Ensures all required voices are downloaded on the Piper TTS server.
    /// </summary>
    public async Task EnsureVoicesDownloadedAsync(IEnumerable<string> requiredVoices)
    {
        var availableVoices = await GetAvailableVoicesAsync();
        if (availableVoices == null)
        {
            StartConnectionRetry();
            return;
        }

        _connectionVerified = true;
        StopConnectionRetry();

        foreach (var voice in requiredVoices)
        {
            if (availableVoices.Contains(voice))
                continue;

            _sawmill.Info("Voice {Voice} not found on server, attempting to download...", voice);
            if (await DownloadVoiceAsync(voice))
                _sawmill.Info("Successfully downloaded voice {Voice}", voice);
        }
    }

    private async Task<HashSet<string>?> GetAvailableVoicesAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_apiUrl}/voices");
        AddAuthHeader(request);

        var response = await SendWithRetryAsync(request);
        if (response?.IsSuccessStatusCode != true)
        {
            _sawmill.Warning("Failed to fetch available voices from Piper API");
            return null;
        }

        _connectionVerified = true;
        var json = await response.Content.ReadAsStringAsync();
        var voices = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        return voices?.Keys.ToHashSet() ?? [];
    }

    private async Task<bool> DownloadVoiceAsync(string voice)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiUrl}/download");
        AddAuthHeader(request);
        request.Content = JsonContent.Create(new DownloadRequest { Voice = voice });

        var response = await SendWithRetryAsync(request);
        if (response?.IsSuccessStatusCode != true)
        {
            _sawmill.Error("Failed to download voice {Voice}", voice);
            return false;
        }

        return true;
    }

    public async Task<byte[]?> ConvertTextToSpeech(string voice, string speaker, string text)
    {
        WantedCount.Inc();

        var key = HashCode.Combine(voice, speaker, text);

        var cachedData = GetFromCache(key);
        if (cachedData != null)
        {
            ReusedCount.Inc();
            return cachedData;
        }

        Task<byte[]?>? existingTask;
        lock (_inFlightLock)
        {
            if (_inFlightRequests.TryGetValue(key, out existingTask))
            {
                DeduplicatedCount.Inc();
                _sawmill.Debug("Deduplicating TTS request for: {Text}", text);
            }
        }

        if (existingTask != null)
            return await existingTask;
        if (Interlocked.Increment(ref _queuedGenerations) > _maxQueuedGenerations)
        {
            Interlocked.Decrement(ref _queuedGenerations);
            _sawmill.Warning("Queue limit exceeded for TTS generation: {Text}", text);
            return null;
        }

        var generationTask = GenerateAudioAsync(key, voice, speaker, text);

        lock (_inFlightLock)
        {
            _inFlightRequests[key] = generationTask;
        }

        var result = await generationTask;

        lock (_inFlightLock)
        {
            _inFlightRequests.Remove(key);
        }

        Interlocked.Decrement(ref _queuedGenerations);
        return result;
    }

    private async Task<byte[]?> GenerateAudioAsync(int key, string voice, string speaker, string text)
    {
        await _generationSemaphore.WaitAsync();

        try
        {
            var cachedData = GetFromCache(key);
            if (cachedData != null)
            {
                ReusedCount.Inc();
                return cachedData;
            }

            var reqTime = DateTime.UtcNow;

            using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl);
            AddAuthHeader(request);

            var requestBody = new TTSRequest { Text = text, Voice = voice };
            if (int.TryParse(speaker, out var speakerId))
                requestBody.SpeakerId = speakerId;
            else
                requestBody.Speaker = speaker;

            request.Content = JsonContent.Create(requestBody);

            var response = await SendWithRetryAsync(request);

            if (response == null)
            {
                RequestTimings.WithLabels("Error").Observe((DateTime.UtcNow - reqTime).TotalSeconds);
                _sawmill.Error("Failed to connect to Piper TTS API at {Url} after {Retries} retries", _apiUrl, _maxRetries);
                _connectionVerified = false;
                StartConnectionRetry();
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                RequestTimings.WithLabels("Error").Observe((DateTime.UtcNow - reqTime).TotalSeconds);
                _sawmill.Error("Piper API request failed with status {Status} for text: {Text}", response.StatusCode, text);
                return null;
            }

            _connectionVerified = true;
            StopConnectionRetry();

            var pcmData = await response.Content.ReadAsByteArrayAsync();
            RequestTimings.WithLabels("Success").Observe((DateTime.UtcNow - reqTime).TotalSeconds);

            byte[] oggData;
            try
            {
                oggData = TTSEncoder.EncodePcmToOggVorbis(pcmData);
            }
            catch (Exception e)
            {
                _sawmill.Error("Failed to encode TTS audio to OGG Vorbis: {Error}", e);
                return null;
            }

            SaveToCache(key, oggData);
            return oggData;
        }
        finally
        {
            _generationSemaphore.Release();
        }
    }

    private async Task<HttpResponseMessage?> SendWithRetryAsync(HttpRequestMessage request)
    {
        var delay = 100;

        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            using var clonedRequest = await CloneRequestAsync(request);

            try
            {
                var response = await _httpClient.SendAsync(clonedRequest);

                if (response.IsSuccessStatusCode
                    || response.StatusCode >= HttpStatusCode.BadRequest
                    && response.StatusCode < HttpStatusCode.InternalServerError
                    && response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    return response;
                }

                if (attempt < _maxRetries)
                {
                    _sawmill.Debug("TTS request failed with {Status}, retrying in {Delay}ms (attempt {Attempt}/{MaxRetries})",
                        response.StatusCode, delay, attempt + 1, _maxRetries);
                    await Task.Delay(delay);
                    delay *= 2;
                }
                else
                {
                    return response;
                }
            }
            catch (TaskCanceledException)
            {
                if (attempt < _maxRetries)
                {
                    _sawmill.Debug("TTS request timed out, retrying in {Delay}ms (attempt {Attempt}/{MaxRetries})",
                        delay, attempt + 1, _maxRetries);
                    await Task.Delay(delay);
                    delay *= 2;
                }
                else
                {
                    _sawmill.Warning("TTS request timed out after {Retries} retries", _maxRetries);
                    return null;
                }
            }
            catch (HttpRequestException e)
            {
                if (attempt < _maxRetries)
                {
                    _sawmill.Debug("TTS request failed: {Error}, retrying in {Delay}ms (attempt {Attempt}/{MaxRetries})",
                        e.Message, delay, attempt + 1, _maxRetries);
                    await Task.Delay(delay);
                    delay *= 2;
                }
                else
                {
                    _sawmill.Warning("TTS request failed after {Retries} retries: {Error}", _maxRetries, e.Message);
                    return null;
                }
            }
        }

        return null;
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content != null)
        {
            var content = await request.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(content);

            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private void AddAuthHeader(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");
    }

    private byte[]? GetFromCache(int key)
    {
        var type = _cfg.GetCVar(CCVars.TTSCacheType);

        if (type == "memory")
            return _memoryCache.GetValueOrDefault(key);

        if (type == "file")
        {
            var path = Path.Combine(_cachePath.ToString(), $"{key}.ogg");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        return null;
    }

    private void SaveToCache(int key, byte[] data)
    {
        var type = _cfg.GetCVar(CCVars.TTSCacheType);

        if (type == "memory")
        {
            while (_memoryCache.Count >= _cfg.GetCVar(CCVars.TTSMaxCached))
                _memoryCache.Remove(_memoryCache.First().Key);

            _memoryCache.TryAdd(key, data);
            return;
        }

        if (type == "file")
        {
            var cacheDir = _cachePath.ToString();
            if (!Directory.Exists(cacheDir))
                return;

            // Clean old files if over limit
            var files = Directory.GetFiles(cacheDir).OrderBy(File.GetLastWriteTimeUtc).ToList();
            var toDelete = files.Count - _cfg.GetCVar(CCVars.TTSMaxCached);
            for (var i = 0; i < toDelete && i < files.Count; i++)
                File.Delete(files[i]);

            File.WriteAllBytes(Path.Combine(cacheDir, $"{key}.ogg"), data);
        }
    }

    public void ClearCache()
    {
        _memoryCache.Clear();

        var cacheDir = _cachePath.ToString();
        if (!Directory.Exists(cacheDir))
            return;

        foreach (var file in Directory.EnumerateFiles(cacheDir, "*.ogg"))
            File.Delete(file);
        foreach (var file in Directory.EnumerateFiles(cacheDir, "*.wav"))
            File.Delete(file);
    }

    private sealed class TTSRequest
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("voice")]
        public string? Voice { get; set; }

        [JsonPropertyName("speaker")]
        public string? Speaker { get; set; }

        [JsonPropertyName("speaker_id")]
        public int? SpeakerId { get; set; }
    }

    private sealed class DownloadRequest
    {
        [JsonPropertyName("voice")]
        public string Voice { get; set; } = "";
    }
}
