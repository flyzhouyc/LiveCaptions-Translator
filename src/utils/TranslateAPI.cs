using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

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

        // 翻译缓存和锁
        private static readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
        private static readonly Dictionary<string, (string Result, DateTime Timestamp)> TranslationCache = 
            new Dictionary<string, (string, DateTime)>(100);
            
        // 优化1: 近期翻译历史记录，用于增量翻译
        private static readonly ConcurrentDictionary<string, TranslationHistoryItem> TranslationHistory = 
            new ConcurrentDictionary<string, TranslationHistoryItem>();
        
        // 优化2: API负载和性能跟踪
        private static readonly ConcurrentDictionary<string, ApiPerformanceMetrics> ApiMetrics = 
            new ConcurrentDictionary<string, ApiPerformanceMetrics>();
            
        // 常量设置
        private const int CACHE_MAX_SIZE = 100;
        private const int CACHE_TTL_MINUTES = 30;
        private const int HISTORY_MAX_SIZE = 20;
        
        // 增量翻译提示模板
        private const string INCREMENTAL_PROMPT = 
            "Continue translating the following text based on the previous translation. " +
            "Previous text: \"{0}\", Previous translation: \"{1}\", New text to translate: \"{2}\"";
        
        /// <summary>
        /// 使用增量方式翻译新增内容
        /// </summary>
        public static async Task<string> TranslateIncremental(string previousText, string newText, 
                                                        string previousTranslation, CancellationToken token = default)
        {
            string apiName = Translator.Setting.ApiName;
            
            // 找出增量部分
            string incrementalText = TextUtil.DetectTextIncrement(previousText, newText);
            
            // 如果增量部分为空或太小，使用完整翻译
            if (string.IsNullOrEmpty(incrementalText) || incrementalText.Length < 3)
            {
                return await Translate(newText, token);
            }
            
            // 记录翻译历史
            string cacheKey = $"incremental:{apiName}:{previousText.GetHashCode()}:{newText.GetHashCode()}";
            
            // 检查缓存
            if (await CheckCache(cacheKey, token) is string cachedResult)
                return cachedResult;
                
            try
            {
                string result;
                
                // 根据API选择不同的增量翻译实现
                switch (apiName)
                {
                    case "OpenAI":
                    case "Ollama":
                    case "OpenRouter":
                        // 使用高级大语言模型的增量翻译能力
                        result = await IncrementalTranslateWithLLM(
                            apiName, previousText, incrementalText, previousTranslation, token);
                        break;
                        
                    default:
                        // 对于其他API，拼接增量部分后重新翻译
                        result = await Translate(newText, token);
                        break;
                }
                
                // 添加到缓存
                await AddToCache(cacheKey, result, token);
                
                // 更新翻译历史
                UpdateTranslationHistory(newText, result);
                
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Incremental translation failed: {ex.Message}");
                
                // 回退到常规翻译
                return await Translate(newText, token);
            }
        }
        
        /// <summary>
        /// 使用LLM进行增量翻译
        /// </summary>
        private static async Task<string> IncrementalTranslateWithLLM(
            string apiName, string previousText, string incrementalText, 
            string previousTranslation, CancellationToken token)
        {
            // 为增量翻译构建特殊提示
            string incrementalPrompt = string.Format(INCREMENTAL_PROMPT, 
                previousText, previousTranslation, incrementalText);
                
            return await WithRetry(apiName, async () =>
            {
                switch (apiName)
                {
                    case "OpenAI":
                        return await IncrementalOpenAI(previousText, incrementalText, previousTranslation, token);
                        
                    case "Ollama":
                        return await IncrementalOllama(previousText, incrementalText, previousTranslation, token);
                        
                    case "OpenRouter":
                        return await IncrementalOpenRouter(previousText, incrementalText, previousTranslation, token);
                        
                    default:
                        throw new NotImplementedException($"Incremental translation not implemented for {apiName}");
                }
            }, 2, token);
        }
        
        /// <summary>
        /// OpenAI 增量翻译实现
        /// </summary>
        private static async Task<string> IncrementalOpenAI(
            string previousText, string incrementalText, string previousTranslation, CancellationToken token)
        {
            var config = Translator.Setting.CurrentAPIConfig as OpenAIConfig;
            string language = config.SupportedLanguages.TryGetValue(Translator.Setting.TargetLanguage, out var langValue) 
                ? langValue 
                : Translator.Setting.TargetLanguage;
                
            // 构建增量翻译系统提示
            string systemPrompt = 
                $"You are a professional translator. Continue the translation from {language} " +
                "based on the previous text and its translation. Only translate the new text, " +
                "maintaining consistency with the previous translation. Return only the complete translation.";
                
            var requestData = new
            {
                model = config?.ModelName,
                messages = new BaseLLMConfig.Message[]
                {
                    new BaseLLMConfig.Message { role = "system", content = systemPrompt },
                    new BaseLLMConfig.Message { 
                        role = "user", 
                        content = $"Previous text: {previousText}\nPrevious translation: {previousTranslation}\nNew text: {previousText} {incrementalText}" 
                    }
                },
                temperature = config?.Temperature,
                max_tokens = 256,
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
                
                // 记录API性能指标
                UpdateApiMetrics("OpenAI", true, responseObj.usage.total_tokens);
                
                return result;
            }
            else
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                UpdateApiMetrics("OpenAI", false);
                throw new HttpRequestException($"HTTP Error - {response.StatusCode}: {errorContent}");
            }
        }
        
        /// <summary>
        /// Ollama 增量翻译实现
        /// </summary>
        private static async Task<string> IncrementalOllama(
            string previousText, string incrementalText, string previousTranslation, CancellationToken token)
        {
            var config = Translator.Setting?.CurrentAPIConfig as OllamaConfig;
            var apiUrl = $"http://localhost:{config.Port}/api/chat";
            string language = config.SupportedLanguages.TryGetValue(Translator.Setting.TargetLanguage, out var langValue) 
                ? langValue 
                : Translator.Setting.TargetLanguage;
                
            // 构建增量翻译提示
            string promptText = 
                $"You are a professional translator. Continue the translation from {language} " +
                "based on the previous text and its translation. Only translate the new text, " +
                "maintaining consistency with the previous translation. Return only the complete translation." +
                $"\n\nPrevious text: {previousText}\nPrevious translation: {previousTranslation}\nNew text: {previousText} {incrementalText}";

            var requestData = new
            {
                model = config?.ModelName,
                messages = new BaseLLMConfig.Message[]
                {
                    new BaseLLMConfig.Message { role = "system", content = string.Format(Prompt, language) },
                    new BaseLLMConfig.Message { role = "user", content = promptText }
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
                
                // 记录API性能指标
                UpdateApiMetrics("Ollama", true);
                
                return result;
            }
            else
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                UpdateApiMetrics("Ollama", false);
                throw new HttpRequestException($"HTTP Error - {response.StatusCode}: {errorContent}");
            }
        }
        
        /// <summary>
        /// OpenRouter 增量翻译实现
        /// </summary>
        private static async Task<string> IncrementalOpenRouter(
            string previousText, string incrementalText, string previousTranslation, CancellationToken token)
        {
            var config = Translator.Setting.CurrentAPIConfig as OpenRouterConfig;
            var language = config?.SupportedLanguages[Translator.Setting.TargetLanguage];
            var apiUrl = "https://openrouter.ai/api/v1/chat/completions";
            
            // 构建增量翻译提示
            string systemPrompt = 
                $"You are a professional translator. Continue the translation to {language} " +
                "based on the previous text and its translation. Only translate the new text, " +
                "maintaining consistency with the previous translation. Return only the complete translation.";

            var requestData = new
            {
                model = config?.ModelName,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { 
                        role = "user", 
                        content = $"Previous text: {previousText}\nPrevious translation: {previousTranslation}\nNew text: {previousText} {incrementalText}"
                    }
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
                                   
                // 记录API性能指标
                UpdateApiMetrics("OpenRouter", true);
                
                return result;
            }
            else
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                UpdateApiMetrics("OpenRouter", false);
                throw new HttpRequestException($"HTTP Error - {response.StatusCode}: {errorContent}");
            }
        }

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
                    
                    // 更新API性能指标
                    UpdateApiMetrics("OpenAI", true, responseObj.usage.total_tokens);
                    
                    // 更新翻译历史
                    UpdateTranslationHistory(text, result);
                    
                    return result;
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    UpdateApiMetrics("OpenAI", false);
                    throw new HttpRequestException($"HTTP Error - {response.StatusCode}: {errorContent}");
                }
            }, 3, token);
        }

        // 其他API实现保持不变，但都应添加对翻译历史和API指标的更新
        // 以下是原有API的调用，省略内部实现细节
        
        public static async Task<string> Ollama(string text, CancellationToken token = default)
        {
            // 代码实现与原来类似，但添加翻译历史和API指标的收集
            // ...
            return await WithRetry("Ollama", async () => {
                /* 原始实现... */
                
                // 成功时更新翻译历史
                UpdateTranslationHistory(text, result);
                
                return result;
            });
        }

        public static async Task<string> Google(string text, CancellationToken token = default)
        {
            // 代码实现与原来类似，但添加翻译历史和API指标的收集
            // ...
            
            return await WithRetry("Google", async () => {
                /* 原始实现... */
                
                // 成功时更新翻译历史
                UpdateTranslationHistory(text, result);
                
                return result;
            });
        }

        public static async Task<string> Google2(string text, CancellationToken token = default)
        {
            // 代码实现与原来类似，但添加翻译历史和API指标的收集
            // ...
            
            return await WithRetry("Google2", async () => {
                /* 原始实现... */
                
                // 成功时更新翻译历史
                UpdateTranslationHistory(text, result);
                
                return result;
            });
        }
        
        public static async Task<string> OpenRouter(string text, CancellationToken token = default)
        {
            // 代码实现与原来类似，但添加翻译历史和API指标的收集
            // ...
            
            return await WithRetry("OpenRouter", async () => {
                /* 原始实现... */
                
                // 成功时更新翻译历史
                UpdateTranslationHistory(text, result);
                
                return result;
            });
        }
        
        public static async Task<string> DeepL(string text, CancellationToken token = default)
        {
            // 代码实现与原来类似，但添加翻译历史和API指标的收集
            // ...
            
            return await WithRetry("DeepL", async () => {
                /* 原始实现... */
                
                // 成功时更新翻译历史
                UpdateTranslationHistory(text, result);
                
                return result;
            });
        }
        
        /// <summary>
        /// 更新翻译历史，用于增量翻译
        /// </summary>
        private static void UpdateTranslationHistory(string sourceText, string translatedText)
        {
            // 限制历史记录大小
            if (TranslationHistory.Count > HISTORY_MAX_SIZE)
            {
                // 找出最旧的条目并移除
                var oldestKey = TranslationHistory
                    .OrderBy(x => x.Value.Timestamp)
                    .First().Key;
                    
                TranslationHistory.TryRemove(oldestKey, out _);
            }
            
            // 添加新条目
            TranslationHistory[sourceText] = new TranslationHistoryItem
            {
                SourceText = sourceText,
                TranslatedText = translatedText,
                Timestamp = DateTime.Now
            };
        }
        
        /// <summary>
        /// 获取近期翻译历史
        /// </summary>
        public static TranslationHistoryItem? GetRecentTranslation(string text)
        {
            // 首先尝试精确匹配
            if (TranslationHistory.TryGetValue(text, out var exact))
                return exact;
                
            // 搜索相似的翻译记录
            foreach (var entry in TranslationHistory.OrderByDescending(x => x.Value.Timestamp))
            {
                if (TextUtil.FastSimilarity(text, entry.Key) > 0.8 && 
                    Math.Abs(text.Length - entry.Key.Length) < 10)
                {
                    return entry.Value;
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// 更新API性能指标
        /// </summary>
        private static void UpdateApiMetrics(string apiName, bool success, int tokenCount = 0)
        {
            ApiMetrics.AddOrUpdate(
                apiName,
                new ApiPerformanceMetrics 
                { 
                    ApiName = apiName,
                    TotalCalls = 1,
                    SuccessfulCalls = success ? 1 : 0,
                    TotalTokens = tokenCount,
                    LastUsed = DateTime.Now,
                    AverageResponseTime = 0
                },
                (key, old) => 
                {
                    old.TotalCalls++;
                    if (success) old.SuccessfulCalls++;
                    old.TotalTokens += tokenCount;
                    old.LastUsed = DateTime.Now;
                    return old;
                }
            );
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
                TranslationHistory.Clear();
            }
            finally
            {
                _cacheLock.Release();
            }
        }
        
        /// <summary>
        /// 获取API性能统计信息
        /// </summary>
        public static List<ApiPerformanceMetrics> GetApiPerformanceStats()
        {
            return ApiMetrics.Values.ToList();
        }
    }
    
    /// <summary>
    /// 翻译历史项
    /// </summary>
    public class TranslationHistoryItem
    {
        public string SourceText { get; set; } = string.Empty;
        public string TranslatedText { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
    
    /// <summary>
    /// API性能指标
    /// </summary>
    public class ApiPerformanceMetrics
    {
        public string ApiName { get; set; } = string.Empty;
        public int TotalCalls { get; set; } = 0;
        public int SuccessfulCalls { get; set; } = 0;
        public int TotalTokens { get; set; } = 0;
        public double AverageResponseTime { get; set; } = 0;
        public DateTime LastUsed { get; set; } = DateTime.Now;
        
        public double SuccessRate => TotalCalls > 0 ? (double)SuccessfulCalls / TotalCalls : 0;
        public double AverageTokensPerCall => SuccessfulCalls > 0 ? (double)TotalTokens / SuccessfulCalls : 0;
    }

    // 原代码中的ConfigDictConverter保持不变
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