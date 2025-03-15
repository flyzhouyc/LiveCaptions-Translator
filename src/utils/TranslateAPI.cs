﻿using System.Net.Http;
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
        
        // 为不同API创建不同的HttpClient实例，避免共享超时设置
        private static readonly Dictionary<string, HttpClient> apiClients = new Dictionary<string, HttpClient>();
        
        // 获取适合文本长度的超时时间
        private static TimeSpan GetDynamicTimeout(string text)
        {
            // 基本超时5秒
            int baseTimeoutSeconds = 5;
            
            // 根据文本长度增加超时时间，每100个字符增加1秒，最大30秒
            int extraSeconds = Math.Min(text.Length / 100, 25);
            
            return TimeSpan.FromSeconds(baseTimeoutSeconds + extraSeconds);
        }
        
        // 获取或创建API的HttpClient
        private static HttpClient GetClientForApi(string apiName, string text)
        {
            if (!apiClients.ContainsKey(apiName))
            {
                apiClients[apiName] = new HttpClient();
            }
            
            // 设置动态超时
            apiClients[apiName].Timeout = GetDynamicTimeout(text);
            
            return apiClients[apiName];
        }

        public static async Task<string> OpenAI(string text, CancellationToken token = default)
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
                max_tokens = 128, // 增加最大token数以处理更长的文本
                stream = false
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            
            // 获取动态超时的客户端
            var client = GetClientForApi("OpenAI", text);
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
                max_tokens = 128, // 增加最大token数
                stream = false
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            
            // 获取动态超时的客户端
            var client = GetClientForApi("Ollama", text);
            client.DefaultRequestHeaders.Clear();

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
                var responseObj = JsonSerializer.Deserialize<OllamaConfig.Response>(responseString);
                return responseObj.message.content;
            }
            else
                return $"[Translation Failed] HTTP Error - {response.StatusCode}";
        }

        private static async Task<string> Google(string text, CancellationToken token = default)
        {
            var language = Translator.Setting?.TargetLanguage;

            string encodedText = Uri.EscapeDataString(text);
            var url = $"https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl={language}&q={encodedText}";

            // 获取动态超时的客户端
            var client = GetClientForApi("Google", text);

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

s           tring encodedText = Uri.EscapeDataString(text);
            string url = $"https://dictionaryextension-pa.googleapis.com/v1/dictionaryExtensionData?" +
                         $"language={language}&" +
                         $"key={apiKey}&" +
                         $"term={encodedText}&" +
                         $"strategy={strategy}";

            // 获取动态超时的客户端
            var client = GetClientForApi("Google2", text);
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

            var requestData = new
            {
                model = config?.ModelName,
                messages = new[]
                {
                    new { role = "system", content = string.Format(Prompt, language)},
                    new { role = "user", content = $"🔤 {text} 🔤" }
                },
                temperature = config?.Temperature,
                max_tokens = 128 // 增加最大token数
            };

            // 获取动态超时的客户端
            var client = GetClientForApi("OpenRouter", text);
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

            var requestData = new
            {
                text = new[] { text },
                target_lang = language
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // 获取动态超时的客户端
            var client = GetClientForApi("DeepL", text);
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", $"DeepL-Auth-Key {config?.ApiKey}");

            string apiUrl = string.IsNullOrEmpty(config?.ApiUrl) ? 
                "https://api.deepl.com/v2/translate" : TextUtil.NormalizeUrl(config.ApiUrl);

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
            {
                return $"[Translation Failed] HTTP Error - {response.StatusCode}";
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