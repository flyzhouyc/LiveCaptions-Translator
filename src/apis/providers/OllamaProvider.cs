using System.Net.Http;
using System.Text;
using System.Text.Json;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.apis.providers
{
    public class OllamaProvider : ITranslateProvider
    {
        public string Name => "Ollama";
        public bool IsLLMBased => true;
        public bool NoConfigRequired => false;
        public bool SupportsStreaming => false;

        public async Task<TranslateResult> TranslateAsync(
            string text, CancellationToken token, Action<string>? onPartial = null)
        {
            if (!TryGetConfig(out OllamaConfig config))
                return TranslateResult.Error("Invalid Ollama config", Name);

            string language = OllamaConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl + "/api/chat");

            var messages = TranslateAPI.BuildLLMMessages(language, text);

            var requestData = LLMRequestDataFactory.Create("Ollama", config.ModelName, messages, config.Temperature);
            requestData.keep_alive = config.Keep_alive;
            string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await TranslateDispatcher.SharedClient.PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return TranslateResult.Timeout(Name);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return TranslateResult.Error(ex.Message, Name);
            }

            try
            {
                if (response.IsSuccessStatusCode)
                {
                    string responseString = await response.Content.ReadAsStringAsync();
                    var responseObj = JsonSerializer.Deserialize<OllamaConfig.Response>(responseString);
                    var output = responseObj?.message?.content;
                    if (string.IsNullOrEmpty(output))
                        return TranslateResult.Error("Unexpected response format", Name);
                    return TranslateResult.Ok(RegexPatterns.ModelThinking().Replace(output, ""), Name);
                }
                return TranslateResult.Error($"HTTP Error - {response.StatusCode}", Name);
            }
            finally
            {
                response.Dispose();
            }
        }

        private static bool TryGetConfig<T>(out T config) where T : TranslateAPIConfig =>
            TranslateAPI.TryGetConfig("Ollama", out config);
    }
}