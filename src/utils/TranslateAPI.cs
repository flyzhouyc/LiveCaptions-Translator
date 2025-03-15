using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public static class TranslateAPI
    {
        /*
         * The key of this field is used as the content for `translateAPIBox` in the `SettingPage`.
         * If you'd like to add a new API, please insert the key-value pair here.
         */
        public static readonly Dictionary<string, Func<string, CancellationToken, Task<string>>>
            TRANSLATE_FUNCTIONS = new()
        {
            { "Google", Google },
            { "Google2", Google2 },
            { "Ollama", Ollama },
            { "OpenAI", OpenAI },
            { "DeepL", DeepL },
            { "OpenRouter", OpenRouter },
        };

        public static Func<string, CancellationToken, Task<string>> TranslateFunction
        {
            get => TRANSLATE_FUNCTIONS[Translator.Setting.ApiName];
        }
        public static string Prompt
        {
            get => Translator.Setting.Prompt;
        }

        // 为每个API创建单独的HttpClient，避免冲突
        private static readonly Dictionary<string, HttpClient> ApiClients = new Dictionary<string, HttpClient>
        {
            { "Google", new HttpClient() { Timeout = TimeSpan.FromSeconds(5) } },
            { "Google2", new HttpClient() { Timeout = TimeSpan.FromSeconds(5) } },
            { "Ollama", new HttpClient() { Timeout = TimeSpan.FromSeconds(10) } },
            { "OpenAI", new HttpClient() { Timeout = TimeSpan.FromSeconds(8) } },
            { "DeepL", new HttpClient() { Timeout = TimeSpan.FromSeconds(5) } },
            { "OpenRouter", new HttpClient() { Timeout = TimeSpan.FromSeconds(8) } },
        };
        
        // 失败计数器和冷却时间
        private static readonly Dictionary<string, int> FailureCounter = new Dictionary<string, int>();
        private static readonly Dictionary<string, DateTime> CooldownUntil = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, SemaphoreSlim> ApiSemaphores = new Dictionary<string, SemaphoreSlim>
        {
            { "Google", new SemaphoreSlim(3, 3) },
            { "Google2", new SemaphoreSlim(3, 3) },
            { "Ollama", new SemaphoreSlim(1, 1) },
            { "OpenAI", new SemaphoreSlim(3, 3) },
            { "DeepL", new SemaphoreSlim(3, 3) },
            { "OpenRouter", new SemaphoreSlim(3, 3) },
        };

        private static readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
        private static readonly Dictionary<string, (string Result, DateTime Timestamp)> TranslationCache = 
            new Dictionary<string, (string, DateTime)>(100);
        private const int CACHE_MAX_SIZE = 100;
        private const int CACHE_TTL_MINUTES = 30;
        
        public static async Task<string> WithRetry(string apiName, Func<Task<string>> translateFunc, 
            int maxRetries = 2, CancellationToken token = default)
        {
            // 检查API是否在冷却期
            if (CooldownUntil.TryGetValue(apiName, out var cooldownTime) && cooldownTime > DateTime.Now)
            {
                return $"[API {apiName} is temporarily unavailable. Please try again in {(cooldownTime - DateTime.Now).TotalSeconds:0} seconds]";
            }
            
            // 获取信号量控制并发
            var semaphore = ApiSemaphores[apiName];
            if (!await semaphore.WaitAsync(500, token)) // 等待最多0.5秒
            {
                return $"[{apiName} API is busy, please try again]";
            }
            
            try
            {
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    try
                    {
                        var result = await translateFunc();
                        // 成功，重置失败计数
                        FailureCounter[apiName] = 0;
                        return result;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // 任务被取消
                        return string.Empty;
                    }
                    catch (Exception ex) when (attempt < maxRetries - 1)
                    {
                        // 记录错误但继续重试
                        Console.WriteLine($"[Warning] {apiName} translation attempt {attempt + 1} failed: {ex.Message}");
                        await Task.Delay(200 * (attempt + 1), token); // 指数退避
                    }
                }
                
                // 所有重试都失败
                if (!FailureCounter.ContainsKey(apiName))
                    FailureCounter[apiName] = 0;
                    
                FailureCounter[apiName]++;
                
                // 连续失败次数过多，进入冷却期
                if (FailureCounter[apiName] >= 5)
                {
                    CooldownUntil[apiName] = DateTime.Now.AddSeconds(30);
                    FailureCounter[apiName] = 0;
                    return $"[{apiName} API temporarily unavailable. Will retry in 30 seconds]";
                }
                
                return $"[Translation Failed] After {maxRetries} retries";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] {apiName} translation failed: {ex.Message}");
                return $"[Translation Failed] {ex.Message}";
            }
            finally
            {
                semaphore.Release();
            }
        }

        public static async Task<string> OpenAI(string text, CancellationToken token = default)
        {
            // 检查缓存
            string cacheKey = $"OpenAI:{text}";
            if (await CheckCache(cacheKey, token) is string cachedResult)
                return cachedResult;
                
            return await WithRetry("OpenAI", async () =>
            {
                var config = Translator.Setting.CurrentAPIConfig as OpenAIConfig;
                string language = config.SupportedLanguages.TryGetValue(Translator.Setting.TargetLanguage, out var langValue) 
                    ? langValue 
                    : Translator.Setting.TargetLanguage; 

                var requestData = new
                {
                    model = config?.ModelName,
                    messages = new BaseLLMConfig.Message[]
                    {
                        new BaseLLMConfig.Message { role = "system", content = string.Format(Prompt, language)},
                        new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" }
                    },
                    temperature = config?.Temperature,
                    max_tokens = 256, // 增加 token 数量避免翻译被截断
                    stream = false
                };

                string jsonContent = JsonSerializer.Serialize(requestData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                
                var client = ApiClients["OpenAI"];
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {config?.ApiKey}");

                var response = await client.PostAsync(TextUtil.NormalizeUrl(config?.ApiUrl), content, token);

                if (response.IsSuccessStatusCode)
                {
                    string responseString = await response.Content.ReadAsStringAsync();
                    var responseObj = JsonSerializer.Deserialize<OpenAIConfig.Response>(responseString);
                    var result = responseObj.choices[0].message.content;
                    
                    // 添加到缓存
                    await AddToCache(cacheKey, result, token);
                    
                    return result;
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"HTTP Error - {response.StatusCode}: {errorContent}");
                }
            }, 3, token);
        }

        public static async Task<string> Ollama(string text, CancellationToken token = default)
        {
            // 检查缓存
            string cacheKey = $"Ollama:{text}";
            if (await CheckCache(cacheKey, token) is string cachedResult)
                return cachedResult;
                
            return await WithRetry("Ollama", async () =>
            {
                var config = Translator.Setting?.CurrentAPIConfig as OllamaConfig;
                var apiUrl = $"http://localhost:{config.Port}/api/chat";
                string language = config.SupportedLanguages.TryGetValue(Translator.Setting.TargetLanguage, out var langValue) 
                    ? langValue 
                    : Translator.Setting.TargetLanguage; 

                var requestData = new
                {
                    model = config?.ModelName,
                    messages = new BaseLLMConfig.Message[]
                    {
                        new BaseLLMConfig.Message { role = "system", content = string.Format(Prompt, language)},
                        new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" }
                    },
                    temperature = config?.Temperature,
                    max_tokens = 256,
                    stream = false
                };

                string jsonContent = JsonSerializer.Serialize(requestData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                
                var client = ApiClients["Ollama"];
                client.DefaultRequestHeaders.Clear();

                var response = await client.PostAsync(apiUrl, content, token);

                if (response.IsSuccessStatusCode)
                {
                    string responseString = await response.Content.ReadAsStringAsync();
                    var responseObj = JsonSerializer.Deserialize<OllamaConfig.Response>(responseString);
                    var result = responseObj.message.content;
                    
                    // 添加到缓存
                    await AddToCache(cacheKey, result, token);
                    
                    return result;
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"HTTP Error - {response.StatusCode}: {errorContent}");
                }
            }, 2, token);
        }

        public static async Task<string> Google(string text, CancellationToken token = default)
        {
            // 检查缓存
            string cacheKey = $"Google:{text}";
            if (await CheckCache(cacheKey, token) is string cachedResult)
                return cachedResult;
                
            return await WithRetry("Google", async () =>
            {
                var language = Translator.Setting?.TargetLanguage;

                string encodedText = Uri.EscapeDataString(text);
                var url = $"https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl={language}&q={encodedText}";

                var client = ApiClients["Google"];
                var response = await client.GetAsync(url, token);

                if (response.IsSuccessStatusCode)
                {
                    string responseString = await response.Content.ReadAsStringAsync();

                    var responseObj = JsonSerializer.Deserialize<List<List<string>>>(responseString);

                    string translatedText = responseObj[0][0];
                    
                    // 添加到缓存
                    await AddToCache(cacheKey, translatedText, token);
                    
                    return translatedText;
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"HTTP Error - {response.StatusCode}: {errorContent}");
                }
            }, 3, token);
        }

        public static async Task<string> Google2(string text, CancellationToken token = default)
        {
            // 检查缓存
            string cacheKey = $"Google2:{text}";
            if (await CheckCache(cacheKey, token) is string cachedResult)
                return cachedResult;
                
            return await WithRetry("Google2", async () =>
            {
                string apiKey = "AIzaSyA6EEtrDCfBkHV8uU2lgGY-N383ZgAOo7Y";
                var language = Translator.Setting?.TargetLanguage;
                string strategy = "2";

                string encodedText = Uri.EscapeDataString(text);
                string url = $"https://dictionaryextension-pa.googleapis.com/v1/dictionaryExtensionData?" +
                             $"language={language}&" +
                             $"key={apiKey}&" +
                             $"term={encodedText}&" +
                             $"strategy={strategy}";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("x-referer", "chrome-extension://mgijmajocgfcbeboacabfgobmjgjcoja");

                var client = ApiClients["Google2"];
                var response = await client.SendAsync(request, token);

                if (response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();

                    using var jsonDoc = JsonDocument.Parse(responseBody);
                    var root = jsonDoc.RootElement;

                    if (root.TryGetProperty("translateResponse", out JsonElement translateResponse))
                    {
                        string translatedText = translateResponse.GetProperty("translateText").GetString();
                        
                        // 添加到缓存
                        await AddToCache(cacheKey, translatedText, token);
                        
                        return translatedText;
                    }
                    else
                        throw new InvalidOperationException("Unexpected API response format");
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"HTTP Error - {response.StatusCode}: {errorContent}");
                }
            }, 3, token);
        }
        
        public static async Task<string> OpenRouter(string text, CancellationToken token = default)
        {
            // 检查缓存
            string cacheKey = $"OpenRouter:{text}";
            if (await CheckCache(cacheKey, token) is string cachedResult)
                return cachedResult;
                
            return await WithRetry("OpenRouter", async () =>
            {
                var config = Translator.Setting.CurrentAPIConfig as OpenRouterConfig;
                var language = config?.SupportedLanguages[Translator.Setting.TargetLanguage];
                var apiUrl = "https://openrouter.ai/api/v1/chat/completions";

                var requestData = new
                {
                    model = config?.ModelName,
                    messages = new[]
                    {
                        new { role = "system", content = string.Format(Prompt, language)},
                        new { role = "user", content = $"🔤 {text} 🔤" }
                    },
                    max_tokens = 256
                };

                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(requestData),
                        Encoding.UTF8,
                        "application/json"
                    )
                };

                request.Headers.Add("Authorization", $"Bearer {config?.ApiKey}");
                request.Headers.Add("HTTP-Referer", "https://github.com/SakiRinn/LiveCaptions-Translator");
                request.Headers.Add("X-Title", "LiveCaptionsTranslator");

                var client = ApiClients["OpenRouter"];
                var response = await client.SendAsync(request, token);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
                    var result = jsonResponse.GetProperty("choices")[0]
                                       .GetProperty("message")
                                       .GetProperty("content")
                                       .GetString() ?? string.Empty;
                                       
                    // 添加到缓存
                    await AddToCache(cacheKey, result, token);
                    
                    return result;
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"HTTP Error - {response.StatusCode}: {errorContent}");
                }
            }, 2, token);
        }
        
        public static async Task<string> DeepL(string text, CancellationToken token = default)
        {
            // 检查缓存
            string cacheKey = $"DeepL:{text}";
            if (await CheckCache(cacheKey, token) is string cachedResult)
                return cachedResult;
                
            return await WithRetry("DeepL", async () =>
            {
                var config = Translator.Setting.CurrentAPIConfig as DeepLConfig;
                string language = config.SupportedLanguages.TryGetValue(Translator.Setting.TargetLanguage, out var langValue) 
                    ? langValue 
                    : Translator.Setting.TargetLanguage;

                var requestData = new
                {
                    text = new[] { text },
                    target_lang = language
                };

                string jsonContent = JsonSerializer.Serialize(requestData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var client = ApiClients["DeepL"];
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Add("Authorization", $"DeepL-Auth-Key {config?.ApiKey}");

                string apiUrl = string.IsNullOrEmpty(config?.ApiUrl) ? 
                    "https://api.deepl.com/v2/translate" : TextUtil.NormalizeUrl(config.ApiUrl);

                var response = await client.PostAsync(apiUrl, content, token);

                if (response.IsSuccessStatusCode)
                {
                    string responseString = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(responseString);
            
                    if (doc.RootElement.TryGetProperty("translations", out var translations) &&
                        translations.ValueKind == JsonValueKind.Array && translations.GetArrayLength() > 0)
                    {
                        var result = translations[0].GetProperty("text").GetString();
                        
                        // 添加到缓存
                        await AddToCache(cacheKey, result, token);
                        
                        return result;
                    }
                    throw new InvalidOperationException("No valid translation in response");
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"HTTP Error - {response.StatusCode}: {errorContent}");
                }
            }, 3, token);
        }
        
        private static async Task<string> CheckCache(string key, CancellationToken token)
        {
            await _cacheLock.WaitAsync(token);
            try
            {
                if (TranslationCache.TryGetValue(key, out var cacheEntry))
                {
                    if ((DateTime.Now - cacheEntry.Timestamp).TotalMinutes < CACHE_TTL_MINUTES)
                    {
                        return cacheEntry.Result;
                    }
                    else
                    {
                        // 缓存过期，移除
                        TranslationCache.Remove(key);
                    }
                }
                return null;
            }
            finally
            {
                _cacheLock.Release();
            }
        }
        
        private static async Task AddToCache(string key, string result, CancellationToken token)
        {
            await _cacheLock.WaitAsync(token);
            try
            {
                // 如果缓存已满，移除最旧的条目
                if (TranslationCache.Count >= CACHE_MAX_SIZE)
                {
                    var oldestKey = TranslationCache
                        .OrderBy(x => x.Value.Timestamp)
                        .First().Key;
                        
                    TranslationCache.Remove(oldestKey);
                }
                
                TranslationCache[key] = (result, DateTime.Now);
            }
            finally
            {
                _cacheLock.Release();
            }
        }
        
        public static void ClearCache()
        {
            _cacheLock.Wait();
            try
            {
                TranslationCache.Clear();
            }
            finally
            {
                _cacheLock.Release();
            }
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
                    config = (TranslateAPIConfig)JsonSerializer.Deserialize(ref reader, typeof(TranslateAPIConfig), options);

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
                    JsonSerializer.Serialize(writer, kvp.Value, typeof(TranslateAPIConfig), options);
            }
            writer.WriteEndObject();
        }
    }
}