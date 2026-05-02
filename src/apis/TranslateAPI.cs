using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.apis
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
            { "LMStudio", LMStudio },
            { "DeepL", DeepL },
            { "OpenRouter", OpenRouter },
            { "Youdao", Youdao },
            { "MTranServer", MTranServer },
            { "Baidu", Baidu },
            { "LibreTranslate", LibreTranslate },
        };
        public static readonly List<string> LLM_BASED_APIS = new()
        {
            "Ollama", "OpenAI", "OpenRouter", "LMStudio"
        };
        public static readonly List<string> NO_CONFIG_APIS = new()
        {
            "Google", "Google2"
        };

        public static Func<string, CancellationToken, Task<string>> TranslateFunction =>
            TRANSLATE_FUNCTIONS[Translator.Setting.ApiName];
        public static bool IsLLMBased => LLM_BASED_APIS.Contains(Translator.Setting.ApiName);
        public static string Prompt => Translator.Setting.Prompt;

        private static readonly HttpClient client = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        private const string TimeoutFailureMessage =
            "[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
            "please use a faster API or check network connection.";

        private static async Task<HttpResponseMessage> PostAsync(
            string url, HttpContent content, CancellationToken token, string? authorization = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };

            if (!string.IsNullOrWhiteSpace(authorization))
                request.Headers.TryAddWithoutValidation("Authorization", authorization);

            return await client.SendAsync(request, token);
        }

        private static bool TryGetConfig<T>(string apiName, out T config) where T : TranslateAPIConfig
        {
            if (Translator.Setting[apiName] is T typedConfig)
            {
                config = typedConfig;
                return true;
            }

            config = null!;
            return false;
        }

        private static List<BaseLLMConfig.Message> BuildLLMMessages(string language, string text)
        {
            var messages = new List<BaseLLMConfig.Message>
            {
                new BaseLLMConfig.Message { role = "system", content = string.Format(Prompt, language) }
            };

            if (Translator.Setting.ContextAware)
            {
                foreach (var entry in Translator.Caption.AwareContexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]"))
                        continue;

                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");
                    messages.Add(new BaseLLMConfig.Message { role = "user", content = $"🔤 {entry.SourceText} 🔤" });
                    messages.Add(new BaseLLMConfig.Message { role = "assistant", content = translatedText });
                }
            }

            messages.Add(new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" });
            return messages;
        }

        public static async Task<string> OpenAI(string text, CancellationToken token = default)
        {
            if (!TryGetConfig("OpenAI", out OpenAIConfig config))
                return "[ERROR] Translation Failed: Invalid OpenAI config";

            string language = OpenAIConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;

            var messages = BuildLLMMessages(language, text);

            HttpResponseMessage response;
            try
            {
                int fallbackIndex = config.FallbackIndex;
                while (true)
                {
                    var requestData = LLMRequestDataFactory.Create(fallbackIndex,
                        config.ModelName, messages, config.Temperature);
                    string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
                    var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                    response = await PostAsync(TextUtil.NormalizeUrl(config.ApiUrl), content, token,
                        $"Bearer {config.ApiKey}");
                    if (response.StatusCode != HttpStatusCode.BadRequest &&
                        response.StatusCode != HttpStatusCode.UnprocessableEntity)
                        break;
                    await Task.Delay(15, token);

                    fallbackIndex++;
                    if (fallbackIndex >= LLMRequestDataFactory.FallbackCount)
                    {
                        fallbackIndex = 0;
                        break;
                    }
                }

                config.FallbackIndex = fallbackIndex;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return TimeoutFailureMessage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<OpenAIConfig.Response>(responseString);
                var output = responseObj?.choices.FirstOrDefault()?.message.content;
                if (string.IsNullOrEmpty(output))
                    return "[ERROR] Translation Failed: Unexpected response format";

                return RegexPatterns.ModelThinking().Replace(output, "");
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> Ollama(string text, CancellationToken token = default)
        {
            if (!TryGetConfig("Ollama", out OllamaConfig config))
                return "[ERROR] Translation Failed: Invalid Ollama config";

            string language = OllamaConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl + "/api/chat");

            var messages = BuildLLMMessages(language, text);

            var requestData = LLMRequestDataFactory.Create("Ollama", config.ModelName, messages, config.Temperature);
            requestData.keep_alive = config.Keep_alive;
            string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return TimeoutFailureMessage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<OllamaConfig.Response>(responseString);
                var output = responseObj?.message.content;
                if (string.IsNullOrEmpty(output))
                    return "[ERROR] Translation Failed: Unexpected response format";

                return RegexPatterns.ModelThinking().Replace(output, "");
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> LMStudio(string text, CancellationToken token = default)
        {
            if (!TryGetConfig("LMStudio", out LMStudioConfig config))
                return "[ERROR] Translation Failed: Invalid LMStudio config";

            string language = LMStudioConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl) + "/chat/completions";

            var requestData = new
            {
                model = config.ModelName,
                messages = BuildLLMMessages(language, text),
                temperature = config.Temperature,
                max_tokens = 128,
                stream = false
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return TimeoutFailureMessage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);
                var root = doc.RootElement;

                if (root.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array &&
                    choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var messageContent))
                {
                    return RegexPatterns.ModelThinking().Replace(messageContent.GetString() ?? "", "");
                }

                // Compatibility fallback for older LMStudio native responses.
                if (root.TryGetProperty("output", out var outputArray) &&
                    outputArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in outputArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var typeProp) &&
                            typeProp.GetString() == "message" &&
                            item.TryGetProperty("content", out var contentProp))
                        {
                            return RegexPatterns.ModelThinking().Replace(contentProp.GetString() ?? "", "");
                        }
                    }
                }

                return "[ERROR] Translation Failed: Unexpected response format";
            }
            else
            {
                string body = await response.Content.ReadAsStringAsync();
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}: {body}";
            }
        }

        public static async Task<string> OpenRouter(string text, CancellationToken token = default)
        {
            if (!TryGetConfig("OpenRouter", out OpenRouterConfig config))
                return "[ERROR] Translation Failed: Invalid OpenRouter config";

            string language = OpenRouterConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = "https://openrouter.ai/api/v1/chat/completions";

            var messages = BuildLLMMessages(language, text);

            var requestData = LLMRequestDataFactory.Create("OpenRouter", config.ModelName, messages, config.Temperature);

            string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await PostAsync(apiUrl, content, token, $"Bearer {config.ApiKey}");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return TimeoutFailureMessage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
                var output = jsonResponse.GetProperty("choices")[0]
                                         .GetProperty("message")
                                         .GetProperty("content")
                                         .GetString() ?? string.Empty;
                return RegexPatterns.ModelThinking().Replace(output, "");
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> Google(string text, CancellationToken token = default)
        {
            var language = Translator.Setting.TargetLanguage;

            string encodedText = Uri.EscapeDataString(text);
            var url = $"https://clients5.google.com/translate_a/t?" +
                      $"client=dict-chrome-ex&sl=auto&" +
                      $"tl={language}&" +
                      $"q={encodedText}";

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return TimeoutFailureMessage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();

                var responseObj = JsonSerializer.Deserialize<List<List<string>>>(responseString);

                return responseObj?.FirstOrDefault()?.FirstOrDefault() ??
                       "[ERROR] Translation Failed: Unexpected API response format";
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> Google2(string text, CancellationToken token = default)
        {
            string apiKey = "AIzaSyA6EEtrDCfBkHV8uU2lgGY-N383ZgAOo7Y";
            var language = Translator.Setting.TargetLanguage;
            string strategy = "2";

            string encodedText = Uri.EscapeDataString(text);
            string url = $"https://dictionaryextension-pa.googleapis.com/v1/dictionaryExtensionData?" +
                         $"language={language}&" +
                         $"key={apiKey}&" +
                         $"term={encodedText}&" +
                         $"strategy={strategy}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-referer", "chrome-extension://mgijmajocgfcbeboacabfgobmjgjcoja");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return TimeoutFailureMessage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseBody = await response.Content.ReadAsStringAsync();

                using var jsonDoc = JsonDocument.Parse(responseBody);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("translateResponse", out JsonElement translateResponse))
                {
                    return translateResponse.GetProperty("translateText").GetString() ??
                           "[ERROR] Translation Failed: Unexpected API response format";
                }
                else
                    return "[ERROR] Translation Failed: Unexpected API response format";
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> DeepL(string text, CancellationToken token = default)
        {
            if (!TryGetConfig("DeepL", out DeepLConfig config))
                return "[ERROR] Translation Failed: Invalid DeepL config";

            string language = DeepLConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                text = new[] { text },
                target_lang = language
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await PostAsync(apiUrl, content, token, $"DeepL-Auth-Key {config.ApiKey}");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return TimeoutFailureMessage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);

                if (doc.RootElement.TryGetProperty("translations", out var translations) &&
                    translations.ValueKind == JsonValueKind.Array && translations.GetArrayLength() > 0)
                {
                    return translations[0].GetProperty("text").GetString() ??
                           "[ERROR] Translation Failed: No valid feedback";
                }
                return "[ERROR] Translation Failed: No valid feedback";
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }


        public static async Task<string> Youdao(string text, CancellationToken token = default)
        {
            if (!TryGetConfig("Youdao", out YoudaoConfig config))
                return "[ERROR] Translation Failed: Invalid Youdao config";

            string language = YoudaoConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;

            string salt = Guid.NewGuid().ToString("N");
            string sign = BitConverter.ToString(
                MD5.Create().ComputeHash(
                    Encoding.UTF8.GetBytes($"{config.AppKey}{text}{salt}{config.AppSecret}"))).Replace("-", "").ToLower();

            var parameters = new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = "auto",
                ["to"] = language,
                ["appKey"] = config.AppKey,
                ["salt"] = salt,
                ["sign"] = sign
            };

            var content = new FormUrlEncodedContent(parameters);

            HttpResponseMessage response;
            try
            {
                response = await PostAsync(config.ApiUrl, content, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return TimeoutFailureMessage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<YoudaoConfig.TranslationResult>(responseString);
                if (responseObj == null)
                    return "[ERROR] Translation Failed: Unexpected response format";

                if (responseObj.errorCode != "0")
                    return $"[ERROR] Translation Failed: Youdao Error - {responseObj.errorCode}";

                return responseObj.translation?.FirstOrDefault() ?? "[ERROR] Translation Failed: No content";
            }
            else
            {
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
            }
        }

        public static async Task<string> MTranServer(string text, CancellationToken token = default)
        {
            if (!TryGetConfig("MTranServer", out MTranServerConfig config))
                return "[ERROR] Translation Failed: Invalid MTranServer config";

            string targetLanguage = MTranServerConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string sourceLanguage = config.SourceLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                text = text,
                to = targetLanguage,
                from = sourceLanguage
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await PostAsync(apiUrl, content, token, $"Bearer {config.ApiKey}");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return TimeoutFailureMessage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<MTranServerConfig.Response>(responseString);
                return responseObj?.result ?? "[ERROR] Translation Failed: Unexpected response format";
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> Baidu(string text, CancellationToken token = default)
        {
            if (!TryGetConfig("Baidu", out BaiduConfig config))
                return "[ERROR] Translation Failed: Invalid Baidu config";

            string language = BaiduConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;

            string salt = Guid.NewGuid().ToString("N");
            string sign = BitConverter.ToString(
                MD5.Create().ComputeHash(
                    Encoding.UTF8.GetBytes($"{config.AppId}{text}{salt}{config.AppSecret}"))).Replace("-", "").ToLower();

            var parameters = new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = "auto",
                ["to"] = language,
                ["appid"] = config.AppId,
                ["salt"] = salt,
                ["sign"] = sign
            };

            var content = new FormUrlEncodedContent(parameters);

            HttpResponseMessage response;
            try
            {
                response = await PostAsync(config.ApiUrl, content, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return TimeoutFailureMessage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<BaiduConfig.TranslationResult>(responseString);
                if (responseObj == null)
                    return "[ERROR] Translation Failed: Unexpected response format";

                if (responseObj.error_code is not null && responseObj.error_code != "0")
                    return $"[ERROR] Translation Failed: Baidu Error - {responseObj.error_code}";

                return responseObj.trans_result?.FirstOrDefault()?.dst ?? "[ERROR] Translation Failed: No content";
            }
            else
            {
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
            }
        }

        public static async Task<string> LibreTranslate(string text, CancellationToken token = default)
        {
            if (!TryGetConfig("LibreTranslate", out LibreTranslateConfig config))
                return "[ERROR] Translation Failed: Invalid LibreTranslate config";

            string targetLanguage = LibreTranslateConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                q = text,
                target = targetLanguage,
                source = "auto",
                format = "text",
                api_key = config.ApiKey
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return TimeoutFailureMessage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<LibreTranslateConfig.Response>(responseString);
                return responseObj?.translatedText ?? "[ERROR] Translation Failed: Unexpected response format";
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }
    }

    public class ConfigDictConverter : JsonConverter<Dictionary<string, List<TranslateAPIConfig>>>
    {
        public override Dictionary<string, List<TranslateAPIConfig>> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected a StartObject token.");
            var configs = new Dictionary<string, List<TranslateAPIConfig>>();

            reader.Read();
            while (reader.TokenType == JsonTokenType.PropertyName)
            {
                string? key = reader.GetString();
                if (string.IsNullOrEmpty(key))
                    throw new JsonException("Expected a non-empty property name.");

                reader.Read();

                var configType = Type.GetType($"LiveCaptionsTranslator.models.{key}Config");
                TranslateAPIConfig config;

                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    var list = new List<TranslateAPIConfig>();
                    reader.Read();

                    while (reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                            config = JsonSerializer.Deserialize(ref reader, configType, options) as TranslateAPIConfig
                                     ?? new TranslateAPIConfig();
                        else
                            config = JsonSerializer.Deserialize<TranslateAPIConfig>(ref reader, options)
                                     ?? new TranslateAPIConfig();

                        list.Add(config);
                        reader.Read();
                    }
                    configs[key] = list;
                }
                else
                    throw new JsonException("Expected a StartObject token or a StartArray token.");

                reader.Read();
            }

            if (reader.TokenType != JsonTokenType.EndObject)
                throw new JsonException("Expected an EndObject token.");
            return configs;
        }

        public override void Write(
            Utf8JsonWriter writer, Dictionary<string, List<TranslateAPIConfig>> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var kvp in value)
            {
                writer.WritePropertyName(kvp.Key);
                var configType = Type.GetType($"LiveCaptionsTranslator.models.{kvp.Key}Config");

                if (kvp.Value is IEnumerable<TranslateAPIConfig> configList)
                {
                    writer.WriteStartArray();
                    foreach (var config in configList)
                    {
                        if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                            JsonSerializer.Serialize(writer, config, configType, options);
                        else
                            JsonSerializer.Serialize(writer, config, typeof(TranslateAPIConfig), options);
                    }
                    writer.WriteEndArray();
                }
                else
                    throw new JsonException($"Unsupported config type: {kvp.Value.GetType()}");
            }
            writer.WriteEndObject();
        }
    }
}
