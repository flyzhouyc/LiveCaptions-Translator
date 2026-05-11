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
         *
         * Third parameter `onPartial` is an optional progressive callback: providers
         * that stream tokens (currently only OpenAI via SSE) invoke it with the
         * accumulated translation so far so the UI can display tokens as they arrive.
         * Non-streaming providers ignore it.
         */
        public static readonly Dictionary<string, Func<string, CancellationToken, Action<string>?, Task<string>>>
            TRANSLATE_FUNCTIONS = new()
        {
            { "Google",         (text, token, _) => Google(text, token) },
            { "Google2",        (text, token, _) => Google2(text, token) },
            { "Ollama",         (text, token, _) => Ollama(text, token) },
            { "OpenAI",         OpenAI },
            { "LMStudio",       (text, token, _) => LMStudio(text, token) },
            { "DeepL",          (text, token, _) => DeepL(text, token) },
            { "OpenRouter",     (text, token, _) => OpenRouter(text, token) },
            { "Youdao",         (text, token, _) => Youdao(text, token) },
            { "MTranServer",    (text, token, _) => MTranServer(text, token) },
            { "Baidu",          (text, token, _) => Baidu(text, token) },
            { "LibreTranslate", (text, token, _) => LibreTranslate(text, token) },
        };
        public static readonly List<string> LLM_BASED_APIS = new()
        {
            "Ollama", "OpenAI", "OpenRouter", "LMStudio"
        };
        public static readonly List<string> NO_CONFIG_APIS = new()
        {
            "Google", "Google2"
        };

        public static Func<string, CancellationToken, Action<string>?, Task<string>> TranslateFunction =>
            TRANSLATE_FUNCTIONS[Translator.Setting.ApiName];
        public static bool IsLLMBased => LLM_BASED_APIS.Contains(Translator.Setting.ApiName);
        public static string Prompt => Translator.Setting.Prompt;

        private static HttpClient client = new HttpClient()
        {
            // Use infinite timeout for HttpClient; per-request timeouts are managed via CancellationToken
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        public static void RecreateHttpClient()
        {
            var proxyUrl = Translator.Setting?.ProxyUrl;
            HttpMessageHandler handler;
            if (!string.IsNullOrWhiteSpace(proxyUrl))
            {
                handler = new SocketsHttpHandler
                {
                    Proxy = new WebProxy(proxyUrl),
                    UseProxy = true
                };
            }
            else
            {
                handler = new SocketsHttpHandler { UseProxy = false };
            }
            var oldClient = client;
            client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            oldClient.Dispose();
        }
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
        private const string TimeoutFailureMessage =
            "[ERROR] Translation Failed: The request was canceled due to timeout (> 30 seconds), " +
            "please use a faster API or check network connection.";

        private static async Task<HttpResponseMessage> PostAsync(
            string url, HttpContent content, CancellationToken token, string? authorization = null)
        {
            using var timeoutCts = new CancellationTokenSource(RequestTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };

            if (!string.IsNullOrWhiteSpace(authorization))
                request.Headers.TryAddWithoutValidation("Authorization", authorization);

            return await client.SendAsync(request, linkedCts.Token);
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
                // Use Replace instead of string.Format to avoid FormatException when user-edited prompts contain { or }
                new BaseLLMConfig.Message { role = "system", content = Prompt.Replace("{0}", language, StringComparison.Ordinal) }
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

            // Expanded Context: prepend surrounding ASR text as context hint so LLM can
            // better judge sentence boundaries and resolve ambiguity.
            if (Translator.Setting.ExpandedContext)
            {
                string previousContext = Translator.Caption.AwareContextsCaption;
                if (!string.IsNullOrEmpty(previousContext))
                    messages.Add(new BaseLLMConfig.Message { role = "user", content = $"[ASR context]: {previousContext}\n🔤 {text} 🔤" });
                else
                    messages.Add(new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" });
            }
            else
            {
                messages.Add(new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" });
            }

            return messages;
        }

        public static async Task<string> OpenAI(
            string text, CancellationToken token = default, Action<string>? onPartial = null)
        {
            if (!TryGetConfig("OpenAI", out OpenAIConfig config))
                return "[ERROR] Translation Failed: Invalid OpenAI config";

            string language = OpenAIConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;

            var messages = BuildLLMMessages(language, text);

            // #10: SSE Streaming for reduced time-to-first-byte
            // Use a linked CancellationToken with per-request timeout
            using var timeoutCts = new CancellationTokenSource(RequestTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            var requestToken = linkedCts.Token;

            HttpResponseMessage response;
            try
            {
                var requestData = new
                {
                    model = config.ModelName,
                    messages,
                    temperature = config.Temperature,
                    max_completion_tokens = 256,
                    stream = true
                };
                string jsonContent = JsonSerializer.Serialize(requestData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, TextUtil.NormalizeUrl(config.ApiUrl))
                {
                    Content = content
                };
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.ApiKey}");

                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
            {
                return TimeoutFailureMessage;
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

            try
            {
                if (!response.IsSuccessStatusCode)
                    return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";

                // Read SSE stream and assemble tokens
                var sb = new StringBuilder();
                string? finishReason = null;
                using var stream = await response.Content.ReadAsStreamAsync(requestToken);
                using var reader = new System.IO.StreamReader(stream);

                string? line;
                while ((line = await reader.ReadLineAsync(requestToken)) != null)
                {
                    if (token.IsCancellationRequested)
                        throw new OperationCanceledException(token);

                    if (string.IsNullOrEmpty(line))
                        continue;
                    if (!line.StartsWith("data: "))
                        continue;

                    string data = line.Substring(6).Trim();
                    if (data == "[DONE]")
                        break;

                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("choices", out var choices) &&
                            choices.GetArrayLength() > 0)
                        {
                            var choice = choices[0];
                            if (choice.TryGetProperty("finish_reason", out var finishReasonElement) &&
                                finishReasonElement.ValueKind != JsonValueKind.Null)
                                finishReason = finishReasonElement.GetString();

                            if (choice.TryGetProperty("delta", out var deltaObj) &&
                                deltaObj.TryGetProperty("content", out var contentToken2))
                            {
                                string? chunk = contentToken2.GetString();
                                if (!string.IsNullOrEmpty(chunk))
                                {
                                    sb.Append(chunk);
                                    if (onPartial != null)
                                    {
                                        // Strip reasoning-model thinking tokens before reporting,
                                        // so the streaming UI doesn't flash <think>…</think> content.
                                        string streamed = RegexPatterns.ModelThinking()
                                            .Replace(sb.ToString(), "").TrimStart();
                                        if (streamed.Length > 0)
                                            onPartial(streamed);
                                    }
                                }
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Skip malformed SSE chunks
                    }
                }

                string output = sb.ToString();
                if (string.IsNullOrEmpty(output))
                    return "[ERROR] Translation Failed: Empty streaming response";

                output = RegexPatterns.ModelThinking().Replace(output, "").Trim();
                if (string.CompareOrdinal(finishReason, "length") == 0)
                    return $"[WARNING] Translation truncated by token limit. {output}";

                return output;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
            {
                return TimeoutFailureMessage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: Stream error - {ex.Message}";
            }
            finally
            {
                response.Dispose();
            }
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

            try
            {
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
            finally
            {
                response.Dispose();
            }
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
                max_tokens = 256,
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

            try
            {
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
            finally
            {
                response.Dispose();
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

            try
            {
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
            finally
            {
                response.Dispose();
            }
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
                using var timeoutCts = new CancellationTokenSource(RequestTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
                response = await client.GetAsync(url, linkedCts.Token);
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

            try
            {
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
            finally
            {
                response.Dispose();
            }
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
                using var timeoutCts = new CancellationTokenSource(RequestTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
                response = await client.SendAsync(request, linkedCts.Token);
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

            try
            {
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
            finally
            {
                response.Dispose();
            }
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

            try
            {
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
            finally
            {
                response.Dispose();
            }
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

            try
            {
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
            finally
            {
                response.Dispose();
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

            try
            {
                if (response.IsSuccessStatusCode)
                {
                    string responseString = await response.Content.ReadAsStringAsync();
                    var responseObj = JsonSerializer.Deserialize<MTranServerConfig.Response>(responseString);
                    return responseObj?.result ?? "[ERROR] Translation Failed: Unexpected response format";
                }
                else
                    return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
            }
            finally
            {
                response.Dispose();
            }
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

            try
            {
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
            finally
            {
                response.Dispose();
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

            try
            {
                if (response.IsSuccessStatusCode)
                {
                    string responseString = await response.Content.ReadAsStringAsync();
                    var responseObj = JsonSerializer.Deserialize<LibreTranslateConfig.Response>(responseString);
                    return responseObj?.translatedText ?? "[ERROR] Translation Failed: Unexpected response format";
                }
                else
                    return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
            }
            finally
            {
                response.Dispose();
            }
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
