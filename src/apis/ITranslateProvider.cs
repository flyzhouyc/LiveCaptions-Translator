namespace LiveCaptionsTranslator.apis
{
    /// <summary>
    /// Unified interface for all translation providers, inspired by iTourTranslator's
    /// ITranslateService pattern. Each API (Google, DeepL, OpenAI, etc.) implements
    /// this interface, making it easy to add new providers without modifying existing code.
    /// </summary>
    public interface ITranslateProvider
    {
        /// <summary>
        /// Display name shown in the settings UI (e.g. "Google", "DeepL", "OpenAI").
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Whether this provider is an LLM-based API that uses prompts and context.
        /// </summary>
        bool IsLLMBased { get; }

        /// <summary>
        /// Whether this provider requires no API key or configuration to use.
        /// </summary>
        bool NoConfigRequired { get; }

        /// <summary>
        /// Whether this provider supports streaming (SSE) for progressive output.
        /// </summary>
        bool SupportsStreaming { get; }

        /// <summary>
        /// Translate the given text.
        /// </summary>
        /// <param name="text">Source text to translate</param>
        /// <param name="token">Cancellation token</param>
        /// <param name="onPartial">Optional callback for streaming providers to report partial results</param>
        /// <returns>A structured <see cref="TranslateResult"/></returns>
        Task<TranslateResult> TranslateAsync(string text, CancellationToken token, Action<string>? onPartial = null);
    }

    /// <summary>
    /// Structured translation result model, replacing raw string returns.
    /// Carries success/failure status, the translated text, and optional metadata
    /// like latency, provider name, and error details.
    /// Inspired by iTourTranslator's richer response models.
    /// </summary>
    public class TranslateResult
    {
        public bool Success { get; init; }
        public string Text { get; init; } = string.Empty;
        public string ProviderName { get; init; } = string.Empty;
        public long? LatencyMs { get; init; }
        public string? ErrorMessage { get; init; }
        public bool IsWarning { get; init; }
        public bool IsTimeout { get; init; }

        /// <summary>
        /// Implicit conversion to string for backward compatibility with existing code
        /// that expects string returns from translation functions.
        /// </summary>
        public static implicit operator string(TranslateResult result) => result.ToString();

        public override string ToString()
        {
            if (Success && !IsWarning)
                return LatencyMs.HasValue ? $"[{LatencyMs,4} ms] {Text}" : Text;
            if (IsWarning)
                return LatencyMs.HasValue ? $"[{LatencyMs,4} ms] [WARNING] {Text}" : $"[WARNING] {Text}";
            if (IsTimeout)
                return "[ERROR] Translation Failed: The request was canceled due to timeout (> 30 seconds), " +
                       "please use a faster API or check network connection.";
            return !string.IsNullOrEmpty(ErrorMessage)
                ? $"[ERROR] Translation Failed: {ErrorMessage}"
                : "[ERROR] Translation Failed: Unknown error";
        }

        // Factory methods
        public static TranslateResult Ok(string text, string providerName, long? latencyMs = null) =>
            new() { Success = true, Text = text, ProviderName = providerName, LatencyMs = latencyMs };

        public static TranslateResult Warn(string text, string providerName, long? latencyMs = null) =>
            new() { Success = true, Text = text, ProviderName = providerName, LatencyMs = latencyMs, IsWarning = true };

        public static TranslateResult Error(string message, string providerName) =>
            new() { Success = false, ErrorMessage = message, ProviderName = providerName };

        public static TranslateResult Timeout(string providerName) =>
            new() { Success = false, IsTimeout = true, ProviderName = providerName };
    }
}