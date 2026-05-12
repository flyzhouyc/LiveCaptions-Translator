using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.apis.providers
{
    /// <summary>
    /// OpenAI provider with SSE streaming support.
    /// </summary>
    public class OpenAIProvider : ITranslateProvider
    {
        public string Name => "OpenAI";
        public bool IsLLMBased => true;
        public bool NoConfigRequired => false;
        public bool SupportsStreaming => true;

        public async Task<TranslateResult> TranslateAsync(
            string text, CancellationToken token, Action<string>? onPartial = null)
        {
            // Delegate to existing TranslateAPI implementation for now
            string result = await TranslateAPI.OpenAI(text, token, onPartial);
            return ResultConverter.StringToResult(result, Name);
        }
    }

    /// <summary>
    /// LMStudio local LLM provider.
    /// </summary>
    public class LMStudioProvider : ITranslateProvider
    {
        public string Name => "LMStudio";
        public bool IsLLMBased => true;
        public bool NoConfigRequired => false;
        public bool SupportsStreaming => false;

        public async Task<TranslateResult> TranslateAsync(
            string text, CancellationToken token, Action<string>? onPartial = null)
        {
            string result = await TranslateAPI.LMStudio(text, token);
            return ResultConverter.StringToResult(result, Name);
        }
    }

    /// <summary>
    /// DeepL translation provider.
    /// </summary>
    public class DeepLProvider : ITranslateProvider
    {
        public string Name => "DeepL";
        public bool IsLLMBased => false;
        public bool NoConfigRequired => false;
        public bool SupportsStreaming => false;

        public async Task<TranslateResult> TranslateAsync(
            string text, CancellationToken token, Action<string>? onPartial = null)
        {
            string result = await TranslateAPI.DeepL(text, token);
            return ResultConverter.StringToResult(result, Name);
        }
    }

    /// <summary>
    /// OpenRouter LLM provider.
    /// </summary>
    public class OpenRouterProvider : ITranslateProvider
    {
        public string Name => "OpenRouter";
        public bool IsLLMBased => true;
        public bool NoConfigRequired => false;
        public bool SupportsStreaming => false;

        public async Task<TranslateResult> TranslateAsync(
            string text, CancellationToken token, Action<string>? onPartial = null)
        {
            string result = await TranslateAPI.OpenRouter(text, token);
            return ResultConverter.StringToResult(result, Name);
        }
    }

    /// <summary>
    /// Youdao translation provider.
    /// </summary>
    public class YoudaoProvider : ITranslateProvider
    {
        public string Name => "Youdao";
        public bool IsLLMBased => false;
        public bool NoConfigRequired => false;
        public bool SupportsStreaming => false;

        public async Task<TranslateResult> TranslateAsync(
            string text, CancellationToken token, Action<string>? onPartial = null)
        {
            string result = await TranslateAPI.Youdao(text, token);
            return ResultConverter.StringToResult(result, Name);
        }
    }

    /// <summary>
    /// MTranServer translation provider.
    /// </summary>
    public class MTranServerProvider : ITranslateProvider
    {
        public string Name => "MTranServer";
        public bool IsLLMBased => false;
        public bool NoConfigRequired => false;
        public bool SupportsStreaming => false;

        public async Task<TranslateResult> TranslateAsync(
            string text, CancellationToken token, Action<string>? onPartial = null)
        {
            string result = await TranslateAPI.MTranServer(text, token);
            return ResultConverter.StringToResult(result, Name);
        }
    }

    /// <summary>
    /// Baidu translation provider.
    /// </summary>
    public class BaiduProvider : ITranslateProvider
    {
        public string Name => "Baidu";
        public bool IsLLMBased => false;
        public bool NoConfigRequired => false;
        public bool SupportsStreaming => false;

        public async Task<TranslateResult> TranslateAsync(
            string text, CancellationToken token, Action<string>? onPartial = null)
        {
            string result = await TranslateAPI.Baidu(text, token);
            return ResultConverter.StringToResult(result, Name);
        }
    }

    /// <summary>
    /// LibreTranslate self-hosted translation provider.
    /// </summary>
    public class LibreTranslateProvider : ITranslateProvider
    {
        public string Name => "LibreTranslate";
        public bool IsLLMBased => false;
        public bool NoConfigRequired => false;
        public bool SupportsStreaming => false;

        public async Task<TranslateResult> TranslateAsync(
            string text, CancellationToken token, Action<string>? onPartial = null)
        {
            string result = await TranslateAPI.LibreTranslate(text, token);
            return ResultConverter.StringToResult(result, Name);
        }
    }

    /// <summary>
    /// Helper to convert legacy string results to structured TranslateResult.
    /// This enables gradual migration from string-based to result-based returns.
    /// </summary>
    internal static class ResultConverter
    {
        internal static TranslateResult StringToResult(string result, string providerName)
        {
            if (result.StartsWith("[ERROR] Translation Failed: The request was canceled due to timeout"))
                return TranslateResult.Timeout(providerName);

            if (result.StartsWith("[ERROR]"))
                return TranslateResult.Error(result.Substring("[ERROR] Translation Failed: ".Length), providerName);

            if (result.StartsWith("[WARNING]"))
                return TranslateResult.Warn(result.Substring("[WARNING] ".Length), providerName);

            // Check for latency prefix like "[ 123 ms] "
            if (result.StartsWith("[") && result.Contains(" ms] "))
            {
                int closeBracket = result.IndexOf(" ms] ");
                string latencyPart = result.Substring(1, closeBracket - 1).Trim();
                string textPart = result.Substring(closeBracket + 5).TrimStart();
                if (long.TryParse(latencyPart, out long latencyMs))
                    return TranslateResult.Ok(textPart, providerName, latencyMs);
            }

            return TranslateResult.Ok(result, providerName);
        }
    }
}

// Make the helper accessible to other providers in the same namespace
namespace LiveCaptionsTranslator.apis.providers
{
    // Non-internal alias for use within the providers folder
    internal static partial class ResultConverterInternal
    {
        internal static TranslateResult StringToResult(string result, string providerName)
            => ResultConverter.StringToResult(result, providerName);
    }
}