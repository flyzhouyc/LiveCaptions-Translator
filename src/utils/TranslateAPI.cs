using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions; // 添加此行以引入 Regex 类

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

        private static readonly HttpClient client = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        // 新增方法：根据指定API名称翻译文本
        public static async Task<string> TranslateWithAPI(string text, string apiName, CancellationToken token = default)
        {
            if (TRANSLATE_FUNCTIONS.TryGetValue(apiName, out var translateFunc))
            {
                return await translateFunc(text, token);
            }
            else
            {
                return await TranslateFunction(text, token);
            }
        }

        public static async Task<string> OpenAI(string text, CancellationToken token = default)
        {
            var config = Translator.Setting.CurrentAPIConfig as OpenAIConfig;
            string language = config.SupportedLanguages.TryGetValue(Translator.Setting.TargetLanguage, out var langValue) 
                ? langValue 
                : Translator.Setting.TargetLanguage; 
            
            // 检测文本是否包含上下文标记
            bool hasContext = text.Contains("Previous sentences (context):");
            string effectivePrompt;
            
            if (hasContext)
            {
                // 使用更适合处理上下文的增强提示词
                effectivePrompt = "As a professional simultaneous interpreter with specialized knowledge in all fields, " +
                                 "provide a fluent and precise translation considering both the context and the current sentence. " +
                                 $"Translate only the current sentence to {language}, ensuring continuity with previous context. " +
                                 "Maintain the original meaning without omissions or alterations. " +
                                 "Respond only with the translated sentence without additional explanations." +
                                 "REMOVE all 🔤 when you output.";
            }
            else
            {
                // 使用标准提示词
                effectivePrompt = string.Format(Prompt, language);
            }
            
            var requestData = new
            {
                model = config?.ModelName,
                messages = new BaseLLMConfig.Message[]
                {
                    new BaseLLMConfig.Message { role = "system", content = effectivePrompt },
                    new BaseLLMConfig.Message { role = "user", content = text }
                },
                temperature = config?.Temperature,
                max_tokens = 64,
                stream = false
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {config?.ApiKey}");

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(TextUtil.NormalizeUrl(config?.ApiUrl), content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[Translation Failed] {ex.Message}";
                return string.Empty;
            }
            catch (Exception ex)
            {
                return $"[Translation Failed] {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<OpenAIConfig.Response>(responseString);
                return responseObj.choices[0].message.content;
            }
            else
                return $"[Translation Failed] HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> Ollama(string text, CancellationToken token = default)
        {
            try
            {
                var config = Translator.Setting?.CurrentAPIConfig as OllamaConfig;
                if (config == null)
                    return "[Translation Failed] OllamaConfig is null";

                // 确保端口号有效
                if (config.Port <= 0 || config.Port > 65535)
                    return "[Translation Failed] Invalid port number. Please check settings.";

                // 确保有模型名称
                if (string.IsNullOrEmpty(config.ModelName))
                    return "[Translation Failed] Model name is missing. Please set a model in settings.";

                var apiUrl = $"http://localhost:{config.Port}/api/chat";
                
                string language = config.SupportedLanguages.TryGetValue(Translator.Setting.TargetLanguage, out var langValue) 
                    ? langValue 
                    : Translator.Setting.TargetLanguage;

                // 检测文本是否包含上下文标记
                bool hasContext = text.Contains("Previous sentences (context):");
                string effectivePrompt;
                
                if (hasContext)
                {
                    // 使用更适合处理上下文的增强提示词
                    effectivePrompt = "As a professional simultaneous interpreter with specialized knowledge in all fields, " +
                                    "provide a fluent and precise translation considering both the context and the current sentence. " +
                                    $"Translate only the current sentence to {language}, ensuring continuity with previous context. " +
                                    "Maintain the original meaning without omissions or alterations. " +
                                    "Respond only with the translated sentence without additional explanations." +
                                    "REMOVE all 🔤 when you output.";
                }
                else
                {
                    // 使用标准提示词
                    effectivePrompt = string.Format(Prompt, language);
                }

                // 简化请求格式，确保与 Ollama API 兼容
                var requestData = new
                {
                    model = config.ModelName,
                    messages = new[] 
                    {
                        new { role = "system", content = effectivePrompt },
                        new { role = "user", content = text }
                    },
                    options = new 
                    {
                        temperature = config.Temperature
                    },
                    stream = false
                };

                string jsonContent = JsonSerializer.Serialize(requestData);
                Console.WriteLine($"Ollama Request: {jsonContent}"); // 调试用

                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                client.DefaultRequestHeaders.Clear();

                HttpResponseMessage response;
                try
                {
                    response = await client.PostAsync(apiUrl, content, token);
                }
                catch (HttpRequestException ex)
                {
                    return $"[Translation Failed] Ollama service not available. Make sure it's running at port {config.Port}. Error: {ex.Message}";
                }
                catch (OperationCanceledException ex)
                {
                    if (ex.Message.StartsWith("The request"))
                        return $"[Translation Failed] Request timeout: {ex.Message}";
                    return string.Empty;
                }
                catch (Exception ex)
                {
                    return $"[Translation Failed] {ex.Message}";
                }

                if (response.IsSuccessStatusCode)
                {
                    string responseString = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Ollama Response: {responseString}"); // 调试用

                    try
                    {
                        // 使用 JsonDocument 进行更灵活的解析
                        using var doc = JsonDocument.Parse(responseString);
                        var root = doc.RootElement;

                        // 尝试多种可能的路径获取内容
                        if (root.TryGetProperty("message", out var message) && 
                            message.TryGetProperty("content", out var content1))
                        {
                            return content1.GetString();
                        }
                        else if (root.TryGetProperty("choices", out var choices) && 
                                choices.ValueKind == JsonValueKind.Array &&
                                choices.GetArrayLength() > 0 &&
                                choices[0].TryGetProperty("message", out var choiceMsg) &&
                                choiceMsg.TryGetProperty("content", out var content2))
                        {
                            return content2.GetString();
                        }
                        else
                        {
                            // 返回原始响应用于调试
                            return $"[Translation Failed] Could not parse response. Raw response: {responseString.Substring(0, Math.Min(100, responseString.Length))}...";
                        }
                    }
                    catch (JsonException ex)
                    {
                        return $"[Translation Failed] JSON parsing error: {ex.Message}. Raw response: {responseString.Substring(0, Math.Min(100, responseString.Length))}...";
                    }
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    return $"[Translation Failed] HTTP Error - {response.StatusCode}. Details: {errorContent}";
                }
            }
            catch (Exception ex)
            {
                return $"[Translation Failed] Unexpected error in Ollama translation: {ex.Message}";
            }
        }

        private static async Task<string> Google(string text, CancellationToken token = default)
        {
            var language = Translator.Setting?.TargetLanguage;
            
            // 如果文本包含上下文提示，只翻译当前句子部分
            if (text.Contains("Current sentence to translate:"))
            {
                var match = Regex.Match(text, @"Current sentence to translate:\s*🔤\s*(.*?)\s*🔤", RegexOptions.Singleline);
                if (match.Success)
                {
                    text = match.Groups[1].Value;
                }
            }
            else if (text.Contains("🔤"))
            {
                // 如果文本包含标记但没有完整的上下文结构，提取标记内容
                var match = Regex.Match(text, @"🔤\s*(.*?)\s*🔤", RegexOptions.Singleline);
                if (match.Success)
                {
                    text = match.Groups[1].Value;
                }
            }

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
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[Translation Failed] {ex.Message}";
                return string.Empty;
            }
            catch (Exception ex)
            {
                return $"[Translation Failed] {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();

                var responseObj = JsonSerializer.Deserialize<List<List<string>>>(responseString);

                string translatedText = responseObj[0][0];
                return translatedText;
            }
            else
                return $"[Translation Failed] HTTP Error - {response.StatusCode}";
        }

        private static async Task<string> Google2(string text, CancellationToken token = default)
        {
            string apiKey = "AIzaSyA6EEtrDCfBkHV8uU2lgGY-N383ZgAOo7Y";
            var language = Translator.Setting?.TargetLanguage;
            string strategy = "2";
            
            // 如果文本包含上下文提示，只翻译当前句子部分
            if (text.Contains("Current sentence to translate:"))
            {
                var match = Regex.Match(text, @"Current sentence to translate:\s*🔤\s*(.*?)\s*🔤", RegexOptions.Singleline);
                if (match.Success)
                {
                    text = match.Groups[1].Value;
                }
            }
            else if (text.Contains("🔤"))
            {
                // 如果文本包含标记但没有完整的上下文结构，提取标记内容
                var match = Regex.Match(text, @"🔤\s*(.*?)\s*🔤", RegexOptions.Singleline);
                if (match.Success)
                {
                    text = match.Groups[1].Value;
                }
            }

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
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[Translation Failed] {ex.Message}";
                return string.Empty;
            }
            catch (Exception ex)
            {
                return $"[Translation Failed] {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseBody = await response.Content.ReadAsStringAsync();

                using var jsonDoc = JsonDocument.Parse(responseBody);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("translateResponse", out JsonElement translateResponse))
                {
                    string translatedText = translateResponse.GetProperty("translateText").GetString();
                    return translatedText;
                }
                else
                    return "[Translation Failed] Unexpected API response format";
            }
            else
                return $"[Translation Failed] HTTP Error - {response.StatusCode}";
        }
        
        public static async Task<string> OpenRouter(string text, CancellationToken token = default)
        {
            var config = Translator.Setting.CurrentAPIConfig as OpenRouterConfig;
            var language = config?.SupportedLanguages[Translator.Setting.TargetLanguage];
            var apiUrl = "https://openrouter.ai/api/v1/chat/completions";

            // 检测文本是否包含上下文标记
            bool hasContext = text.Contains("Previous sentences (context):");
            string effectivePrompt;
            
            if (hasContext)
            {
                // 使用更适合处理上下文的增强提示词
                effectivePrompt = "As a professional simultaneous interpreter with specialized knowledge in all fields, " +
                                 "provide a fluent and precise translation considering both the context and the current sentence. " +
                                 $"Translate only the current sentence to {language}, ensuring continuity with previous context. " +
                                 "Maintain the original meaning without omissions or alterations. " +
                                 "Respond only with the translated sentence without additional explanations." +
                                 "REMOVE all 🔤 when you output.";
            }
            else
            {
                // 使用标准提示词
                effectivePrompt = string.Format(Prompt, language);
            }

            var requestData = new
            {
                model = config?.ModelName,
                messages = new[]
                {
                    new { role = "system", content = effectivePrompt },
                    new { role = "user", content = text }
                }
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
            request.Headers.Add("HTTP-Referer", "https://github.com/SakiRinn/LiveCaptionsTranslator");
            request.Headers.Add("X-Title", "LiveCaptionsTranslator");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[Translation Failed] {ex.Message}";
                return string.Empty;
            }
            catch (Exception ex)
            {
                return $"[Translation Failed] {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
                return jsonResponse.GetProperty("choices")[0]
                                   .GetProperty("message")
                                   .GetProperty("content")
                                   .GetString() ?? string.Empty;
            }
            else
                return $"[Translation Failed] HTTP Error - {response.StatusCode}";
        }
        
        public static async Task<string> DeepL(string text, CancellationToken token = default)
        {
            var config = Translator.Setting.CurrentAPIConfig as DeepLConfig;
            string language = config.SupportedLanguages.TryGetValue(Translator.Setting.TargetLanguage, out var langValue) 
                ? langValue 
                : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            // 如果文本包含上下文提示，只翻译当前句子部分
            if (text.Contains("Current sentence to translate:"))
            {
                var match = Regex.Match(text, @"Current sentence to translate:\s*🔤\s*(.*?)\s*🔤", RegexOptions.Singleline);
                if (match.Success)
                {
                    text = match.Groups[1].Value;
                }
            }
            else if (text.Contains("🔤"))
            {
                // 如果文本包含标记但没有完整的上下文结构，提取标记内容
                var match = Regex.Match(text, @"🔤\s*(.*?)\s*🔤", RegexOptions.Singleline);
                if (match.Success)
                {
                    text = match.Groups[1].Value;
                }
            }

            var requestData = new
            {
                text = new[] { text },
                target_lang = language
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", $"DeepL-Auth-Key {config?.ApiKey}");

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[Translation Failed] {ex.Message}";
                return string.Empty;
            }
            catch (Exception ex)
            {
                return $"[Translation Failed] {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);
        
                if (doc.RootElement.TryGetProperty("translations", out var translations) &&
                    translations.ValueKind == JsonValueKind.Array && translations.GetArrayLength() > 0)
                {
                    return translations[0].GetProperty("text").GetString();
                }
                return "[Translation Failed] No valid feedback";
            }
            else
                return $"[Translation Failed] HTTP Error - {response.StatusCode}";
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