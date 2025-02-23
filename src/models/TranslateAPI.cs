using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

namespace LiveCaptionsTranslator.models
{
    public static class TranslateAPI
    {
        private static readonly PersistentCache _cache = new();
        private static readonly BatchTranslationProcessor _batchProcessor;
        private static readonly HttpClient client = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(5),  // 减少超时时间
            DefaultRequestHeaders = { ConnectionClose = false }  // 保持连接
        };
        private static readonly Dictionary<string, (int failures, DateTime lastFailure, DateTime cooldownUntil)> _apiHealthStatus =
            new Dictionary<string, (int failures, DateTime lastFailure, DateTime cooldownUntil)>();
        private static int _currentAPIIndex = 0;
        private static readonly string[] _apiPriority = new[] { "OpenAI", "Ollama", "GoogleTranslate" };

        public static readonly Dictionary<string, Func<string, Task<string>>> TRANSLATE_FUNCS = new()
        {
            { "Ollama", Ollama },
            { "OpenAI", OpenAI },
            { "GoogleTranslate", GoogleTranslate }
        };

        public static Func<string, Task<string>> TranslateFunc
        {
            get => async (text) => await TranslateWithCacheAsync(text);
        }

        static TranslateAPI()
        {
            _batchProcessor = new BatchTranslationProcessor(
                async (text) =>
                {
                    const int maxAttempts = 3;
                    Exception lastException = null;

                    for (int attempt = 0; attempt < maxAttempts; attempt++)
                    {
                        try
                        {
                            return await _cache.GetOrTranslateAsync(text, GetSelectedTranslateMethod());
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
                },
                maxBatchSize: 5,
                maxWaitMilliseconds: 500,
                maxConcurrentBatches: Environment.ProcessorCount * 2
            );
        }

        public static async Task<string> TranslateWithCacheAsync(string text)
        {
            return await _batchProcessor.EnqueueTranslationAsync(text);
        }

        public static async Task DisposeAsync()
        {
            await _batchProcessor.DisposeAsync();
            if (_cache is IAsyncDisposable disposableCache)
            {
                await disposableCache.DisposeAsync();
            }
        }

        private static void SwitchToNextAPI()
        {
            _currentAPIIndex = (_currentAPIIndex + 1) % _apiPriority.Length;
            App.Settings.ApiName = _apiPriority[_currentAPIIndex];
            _apiHealthStatus[App.Settings.ApiName] = (0, DateTime.MinValue, DateTime.Now.AddMinutes(5)); // Add cooldown period
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
