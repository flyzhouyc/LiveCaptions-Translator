using System.Net.Http;
using System.Text.Json;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.apis.providers
{
    public class GoogleProvider : ITranslateProvider
    {
        public string Name => "Google";
        public bool IsLLMBased => false;
        public bool NoConfigRequired => true;
        public bool SupportsStreaming => false;

        public async Task<TranslateResult> TranslateAsync(
            string text, CancellationToken token, Action<string>? onPartial = null)
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
                using var timeoutCts = new CancellationTokenSource(TranslateDispatcher.RequestTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
                response = await TranslateDispatcher.SharedClient.GetAsync(url, linkedCts.Token);
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
                    var responseObj = JsonSerializer.Deserialize<List<List<string>>>(responseString);
                    var result = responseObj?.FirstOrDefault()?.FirstOrDefault();
                    return result != null
                        ? TranslateResult.Ok(result, Name)
                        : TranslateResult.Error("Unexpected API response format", Name);
                }
                return TranslateResult.Error($"HTTP Error - {response.StatusCode}", Name);
            }
            finally
            {
                response.Dispose();
            }
        }
    }

    public class Google2Provider : ITranslateProvider
    {
        public string Name => "Google2";
        public bool IsLLMBased => false;
        public bool NoConfigRequired => true;
        public bool SupportsStreaming => false;

        public async Task<TranslateResult> TranslateAsync(
            string text, CancellationToken token, Action<string>? onPartial = null)
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
                using var timeoutCts = new CancellationTokenSource(TranslateDispatcher.RequestTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
                response = await TranslateDispatcher.SharedClient.SendAsync(request, linkedCts.Token);
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
                    string responseBody = await response.Content.ReadAsStringAsync();
                    using var jsonDoc = JsonDocument.Parse(responseBody);
                    var root = jsonDoc.RootElement;

                    if (root.TryGetProperty("translateResponse", out JsonElement translateResponse))
                    {
                        var result = translateResponse.GetProperty("translateText").GetString();
                        return result != null
                            ? TranslateResult.Ok(result, Name)
                            : TranslateResult.Error("Unexpected API response format", Name);
                    }
                    return TranslateResult.Error("Unexpected API response format", Name);
                }
                return TranslateResult.Error($"HTTP Error - {response.StatusCode}", Name);
            }
            finally
            {
                response.Dispose();
            }
        }
    }
}