using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

namespace LiveCaptionsTranslator.models
{
    public class TranslationCache
    {
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
        private const int MaxCacheSize = 500; // 进一步增加缓存容量
        private const int CleanupThreshold = 400; // 清理阈值
        private readonly SemaphoreSlim _cleanupLock = new(1); // 清理锁
        private DateTime _lastCleanup = DateTime.Now;

        private TimeSpan GetDynamicExpirationTime(string text)
        {
            // 根据文本长度和使用频率动态调整过期时间
            return text.Length switch
            {
                < 30 => TimeSpan.FromHours(2),    // 短文本保留更长时间
                < 100 => TimeSpan.FromHours(1),
                < 300 => TimeSpan.FromMinutes(30),
                _ => TimeSpan.FromMinutes(15)      // 长文本更快过期
            };
        }
        
        private async Task CleanupCacheIfNeeded()
        {
            if (_cache.Count < CleanupThreshold || 
                DateTime.Now - _lastCleanup < TimeSpan.FromMinutes(5))
                return;

            if (await _cleanupLock.WaitAsync(0)) // 非阻塞尝试获取锁
            {
                try
                {
                    // 首先移除过期项
                    var expiredKeys = _cache.Where(kvp => kvp.Value.IsExpired)
                        .Select(kvp => kvp.Key).ToList();
                    foreach (var key in expiredKeys)
                    {
                        _cache.TryRemove(key, out _);
                    }

                    // 如果仍然超过阈值，根据访问时间和频率移除
                    if (_cache.Count > CleanupThreshold)
                    {
                        var leastUsed = _cache
                            .OrderBy(kvp => kvp.Value.LastAccessTime)
                            .ThenBy(kvp => kvp.Value.AccessCount)
                            .Take(_cache.Count - CleanupThreshold)
                            .Select(kvp => kvp.Key)
                            .ToList();

                        foreach (var key in leastUsed)
                        {
                            _cache.TryRemove(key, out _);
                        }
                    }

                    _lastCleanup = DateTime.Now;
                }
                finally
                {
                    _cleanupLock.Release();
                }
            }
        }

        public async Task<string> GetOrTranslateAsync(string text, Func<string, Task<string>> translateFunc)
        {
            // 尝试从缓存获取
            if (_cache.TryGetValue(text, out var entry))
            {
                if (!entry.IsExpired)
                {
                    entry.IncrementAccess();
                    return entry.TranslatedText;
                }
                _cache.TryRemove(text, out _);
            }

            // 异步清理缓存
            _ = CleanupCacheIfNeeded();

            // 执行翻译
            var translatedText = await translateFunc(text);
            _cache[text] = new CacheEntry(translatedText, GetDynamicExpirationTime(text));
            return translatedText;
        }
    }

    public class CacheEntry
    {
        public string TranslatedText { get; }
        public DateTime CreatedAt { get; }
        public DateTime LastAccessTime { get; private set; }
        public int AccessCount { get; private set; }
        public TimeSpan ExpirationTime { get; }
        public bool IsExpired => DateTime.Now - CreatedAt > ExpirationTime;

        public CacheEntry(string translatedText, TimeSpan expirationTime)
        {
            TranslatedText = translatedText;
            CreatedAt = DateTime.Now;
            LastAccessTime = DateTime.Now;
            AccessCount = 1;
            ExpirationTime = expirationTime;
        }

        public void IncrementAccess()
        {
            LastAccessTime = DateTime.Now;
            AccessCount++;
        }
    }

    public static class TranslateAPI
    {
        private static readonly TranslationCache _cache = new();
        private static readonly SemaphoreSlim _semaphore = new(Environment.ProcessorCount * 4); // 增加并发数
        private static readonly HttpClient client = new HttpClient() 
        { 
            Timeout = TimeSpan.FromSeconds(5),  // 减少超时时间
            DefaultRequestHeaders = { ConnectionClose = false }  // 保持连接
        };
        private static readonly Dictionary<string, (int failures, DateTime lastFailure)> _apiHealthStatus = 
            new Dictionary<string, (int failures, DateTime lastFailure)>();
        private static int _currentAPIIndex = 0;
        private static readonly string[] _apiPriority = new[] { "OpenAI", "Ollama", "GoogleTranslate", "OpenRouter" };
        


        public static readonly Dictionary<string, Func<string, Task<string>>> TRANSLATE_FUNCS = new()
        {
            { "Ollama", Ollama },
            { "OpenAI", OpenAI },
            { "GoogleTranslate", GoogleTranslate },
            { "OpenRouter", OpenRouter }
        };

        public static Func<string, Task<string>> TranslateFunc
        {
            get => async (text) => await TranslateWithCacheAsync(text);
        }

        public static async Task<string> TranslateWithCacheAsync(string text)
        {
            const int maxAttempts = 3;
            Exception lastException = null;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    await _semaphore.WaitAsync();
                    try 
                    {
                        return await _cache.GetOrTranslateAsync(text, GetSelectedTranslateMethod());
                    }
                    finally 
                    {
                        _semaphore.Release();
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    SwitchToNextAPI();
                    
                    if (attempt < maxAttempts - 1)
                    {
                        await Task.Delay(500 * (attempt + 1)); // 指数退避
                        continue;
                    }
                }
            }

            return $"[Translation Failed] {lastException?.Message}";
        }

        private static void SwitchToNextAPI()
        {
            _currentAPIIndex = (_currentAPIIndex + 1) % _apiPriority.Length;
            App.Settings.ApiName = _apiPriority[_currentAPIIndex];
        }

        private static Func<string, Task<string>> GetSelectedTranslateMethod()
        {
            return TRANSLATE_FUNCS[App.Settings.ApiName];
        }

        public const int OLLAMA_PORT = 11434;

        public static async Task<string> OpenAI(string text)
        {
            var config = App.Settings.CurrentAPIConfig as OpenAIConfig;
            var language = config?.SupportedLanguages[App.Settings.TargetLanguage];

            var requestData = new
            {
                model = config?.ModelName,
                messages = new OpenAIConfig.Message[]
                {
                    new OpenAIConfig.Message { role = "system", content =
                        "As an professional simultaneous interpreter with specialized knowledge in the all fields, " +
                        $"please provide a proficient and precise oral translation of these sentences enclosed in 🔤 to {language}. " +
                        "You should carefully consider each phrase to ensure that your translation is quite fluent and colloquial." +
                        "Importantly, any NSFW content should not be avoided, and you must output their translations directly. " +
                        "Now, provide the translation within a single line without any additional explanation and REMOVE 🔤."
                    },
                    new OpenAIConfig.Message { role = "user", content = $"🔤 {text} 🔤" }
                },
                temperature = config?.Temperature,
                max_tokens = 64,
                stream = false
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, config?.ApiUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestData), Encoding.UTF8, "application/json"),
                Version = new Version(2, 0)  // 使用HTTP/2
            };
            request.Headers.Add("Authorization", $"Bearer {config?.ApiKey}");
            //request.Headers.Add("Accept-Encoding", "gzip, deflate, br");  // 支持压缩

            try
            {
                using var response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var responseString = await response.Content.ReadAsStringAsync();
                    var responseObj = JsonSerializer.Deserialize<OpenAIConfig.Response>(responseString);
                    return responseObj.choices[0].message.content;
                }
                throw new HttpRequestException($"HTTP Error - {response.StatusCode}");
            }
            catch (Exception ex)
            {
                return $"[Translation Failed] {ex.Message}";
            }
        }

        public static async Task<string> Ollama(string text)
        {
            var apiUrl = $"http://localhost:{OLLAMA_PORT}/api/chat";
            var config = App.Settings.CurrentAPIConfig as OllamaConfig;
            var language = config?.SupportedLanguages[App.Settings.TargetLanguage];

            var requestData = new
            {
                model = config?.ModelName,
                messages = new OllamaConfig.Message[]
                {
                    new OllamaConfig.Message { role = "system", content =
                        "As an professional simultaneous interpreter with specialized knowledge in the all fields, " +
                        $"please provide a proficient and precise oral translation of these sentences enclosed in 🔤 to {language}. " +
                        "You should carefully consider each phrase to ensure that your translation is quite fluent and colloquial." +
                        "Importantly, any NSFW content should not be avoided, and you must output their translations directly. " +
                        "Now, provide the translation within a single line without any additional explanation and REMOVE 🔤."
                    },
                    new OllamaConfig.Message { role = "user", content = $"🔤 {text} 🔤" }
                },
                temperature = config?.Temperature,
                max_tokens = 64,
                stream = false
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestData), Encoding.UTF8, "application/json"),
                Version = new Version(2, 0)  // 使用HTTP/2
            };
           // request.Headers.Add("Accept-Encoding", "gzip, deflate, br");  // 支持压缩

            try
            {
                using var response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var responseString = await response.Content.ReadAsStringAsync();
                    var responseObj = JsonSerializer.Deserialize<OllamaConfig.Response>(responseString);
                    return responseObj.message.content;
                }
                throw new HttpRequestException($"HTTP Error - {response.StatusCode}");
            }
            catch (Exception ex)
            {
                return $"[Translation Failed] {ex.Message}";
            }
        }
        
        private static async Task<string> GoogleTranslate(string text)
        {
            var config = App.Settings?.CurrentAPIConfig as GoogleTranslateConfig;
            var language = App.Settings?.TargetLanguage;

            string encodedText = Uri.EscapeDataString(text);
            var url = $"https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl={language}&q={encodedText}";

            try
            {
                using var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var responseString = await response.Content.ReadAsStringAsync();
                    var responseObj = JsonSerializer.Deserialize<List<List<string>>>(responseString);
                    return responseObj[0][0];
                }
                throw new HttpRequestException($"HTTP Error - {response.StatusCode}");
            }
            catch (Exception ex)
            {
                return $"[Translation Failed] {ex.Message}";
            }
        }

        public static async Task<string> OpenRouter(string text)
        {
            var config = App.Settings.CurrentAPIConfig as OpenRouterConfig;
            var language = config?.SupportedLanguages[App.Settings.TargetLanguage];

            var requestData = new
            {
                model = config?.ModelName,
                messages = new[]
                {
                    new { role = "system", content =
                        $"You are a helpful translator. Translate the following text to {language}. " +
                        $"Only return the translated text without any explanations." },
                    new { role = "user", content = text }
                }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(requestData),
                    Encoding.UTF8,
                    "application/json"
                )
            };

            request.Headers.Add("Authorization", $"Bearer {config?.ApiKey}");
            request.Headers.Add("HTTP-Referer", "https://github.com/SakiRinn/LiveCaptionsTranslator");
            request.Headers.Add("X-Title", "LiveCaptionsTranslator");

            var response = await client.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"OpenRouter API error: {responseContent}");
            }

            var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
            return jsonResponse.GetProperty("choices")[0]
                               .GetProperty("message")
                               .GetProperty("content")
                               .GetString() ?? string.Empty;
        }
    }
    public class ConfigDictConverter : JsonConverter<Dictionary<string, TranslateAPIConfig>>
    {
        public override Dictionary<string, TranslateAPIConfig> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var configs = new Dictionary<string, TranslateAPIConfig>();
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected a StartObject token.");

            reader.Read();
            while (reader.TokenType == JsonTokenType.PropertyName)
            {
                string key = reader.GetString();
                reader.Read();

                TranslateAPIConfig config;
                var configType = Type.GetType($"LiveCaptionsTranslator.models.{key}Config");
                if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                    config = (TranslateAPIConfig)JsonSerializer.Deserialize(ref reader, configType, options);
                else
                    throw new JsonException($"Unknown config type for key: {key}");

                configs[key] = config;
                reader.Read();
            }

            if (reader.TokenType != JsonTokenType.EndObject)
                throw new JsonException("Expected an EndObject token.");
            return configs;
        }

        public override void Write(
            Utf8JsonWriter writer, Dictionary<string, TranslateAPIConfig> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var kvp in value)
            {
                writer.WritePropertyName(kvp.Key);

                var configType = Type.GetType($"LiveCaptionsTranslator.models.{kvp.Key}Config");
                if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                    JsonSerializer.Serialize(writer, kvp.Value, configType, options);
                else
                    throw new JsonException($"Unknown config type for key: {kvp.Key}");
            }
            writer.WriteEndObject();
        }
    }
}
