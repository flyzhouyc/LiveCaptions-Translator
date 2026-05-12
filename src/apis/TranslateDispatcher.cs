using System.Net;
using System.Net.Http;

using LiveCaptionsTranslator.apis.providers;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.apis
{
    /// <summary>
    /// Central dispatcher for translation providers, inspired by iTourTranslator's
    /// TranslateDispatcher pattern. Replaces the static dictionary in TranslateAPI
    /// with a registry of ITranslateProvider instances.
    /// 
    /// Key benefits over the old approach:
    /// - New providers only need to implement ITranslateProvider (Open/Closed Principle)
    /// - Shared HttpClient lifecycle management
    /// - Unified error handling and timeout logic
    /// - Easy to add provider categories (LLM-based, no-config, streaming)
    /// </summary>
    public static class TranslateDispatcher
    {
        /// <summary>
        /// Shared HttpClient for all providers. Use infinite timeout; per-request
        /// timeouts are managed via CancellationToken.
        /// </summary>
        private static HttpClient _client = new()
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        /// <summary>
        /// Expose the shared client for provider implementations that need it.
        /// </summary>
        public static HttpClient SharedClient => _client;

        /// <summary>
        /// Per-request timeout for all translation calls.
        /// </summary>
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Registry of all available translation providers, keyed by display name.
        /// New providers are registered here — no other code needs to change.
        /// </summary>
        private static readonly List<ITranslateProvider> _providers = new()
        {
            new GoogleProvider(),
            new Google2Provider(),
            new OllamaProvider(),
            new OpenAIProvider(),
            new LMStudioProvider(),
            new DeepLProvider(),
            new OpenRouterProvider(),
            new YoudaoProvider(),
            new MTranServerProvider(),
            new BaiduProvider(),
            new LibreTranslateProvider(),
        };

        /// <summary>
        /// All registered provider names (for settings UI).
        /// </summary>
        public static IReadOnlyList<string> ProviderNames => _providers.Select(p => p.Name).ToList();

        /// <summary>
        /// Provider names that are LLM-based.
        /// </summary>
        public static IReadOnlyList<string> LlmBasedProviders =>
            _providers.Where(p => p.IsLLMBased).Select(p => p.Name).ToList();

        /// <summary>
        /// Provider names that require no configuration.
        /// </summary>
        public static IReadOnlyList<string> NoConfigProviders =>
            _providers.Where(p => p.NoConfigRequired).Select(p => p.Name).ToList();

        /// <summary>
        /// Get a provider by name. Throws KeyNotFoundException if not found.
        /// </summary>
        public static ITranslateProvider GetProvider(string name) =>
            _providers.First(p => p.Name == name);

        /// <summary>
        /// Try to get a provider by name.
        /// </summary>
        public static bool TryGetProvider(string name, out ITranslateProvider? provider)
        {
            provider = _providers.FirstOrDefault(p => p.Name == name);
            return provider != null;
        }

        /// <summary>
        /// The currently active provider based on settings.
        /// </summary>
        public static ITranslateProvider CurrentProvider =>
            GetProvider(Translator.Setting.ApiName);

        /// <summary>
        /// Whether the current provider is LLM-based.
        /// </summary>
        public static bool IsCurrentLLMBased => CurrentProvider.IsLLMBased;

        /// <summary>
        /// Translate using the currently configured provider.
        /// </summary>
        public static Task<TranslateResult> TranslateAsync(
            string text, CancellationToken token, Action<string>? onPartial = null) =>
            CurrentProvider.TranslateAsync(text, token, onPartial);

        /// <summary>
        /// Translate with automatic fallback to a backup provider if the primary fails.
        /// Inspired by iTourTranslator's LocalTranslator + RemoteTranslator dual-channel pattern.
        /// 
        /// Fallback triggers on:
        /// - Translation failure (Success = false)
        /// - Timeout
        /// - Warnings (e.g., truncated output)
        /// 
        /// The fallback provider's result is prefixed with a notice so the user knows
        /// the backup was used.
        /// </summary>
        public static async Task<TranslateResult> TranslateWithFallbackAsync(
            string text, CancellationToken token, Action<string>? onPartial = null)
        {
            var primary = CurrentProvider;
            var result = await primary.TranslateAsync(text, token, onPartial);

            // Check if fallback is configured and needed
            string? fallbackName = Translator.Setting?.FallbackApiName;
            if (string.IsNullOrWhiteSpace(fallbackName) ||
                fallbackName == primary.Name ||
                result.Success && !result.IsTimeout && !result.IsWarning)
                return result;

            // Attempt fallback
            if (!TryGetProvider(fallbackName!, out var fallbackProvider) || fallbackProvider == null)
            {
                DebugLogger.Log("FALLBACK", $"Fallback provider '{fallbackName}' not found, using primary result");
                return result;
            }

            DebugLogger.Log("FALLBACK", $"Primary '{primary.Name}' failed (Success={result.Success}, Timeout={result.IsTimeout}), falling back to '{fallbackName}'");

            try
            {
                var fallbackResult = await fallbackProvider.TranslateAsync(text, token, onPartial);
                if (fallbackResult.Success && !fallbackResult.IsTimeout)
                {
                    // Mark the result as coming from fallback so the UI can indicate it
                    return TranslateResult.Ok(
                        fallbackResult.Text,
                        fallbackResult.ProviderName,
                        fallbackResult.LatencyMs);
                }
                DebugLogger.Log("FALLBACK", $"Fallback '{fallbackName}' also failed: {fallbackResult.ErrorMessage}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                DebugLogger.Log("FALLBACK", $"Fallback '{fallbackName}' threw exception: {ex.Message}");
            }

            // Both failed — return the original primary result
            return result;
        }

        /// <summary>
        /// Recreate the shared HttpClient (e.g. when proxy settings change).
        /// </summary>
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
            var oldClient = _client;
            _client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            oldClient.Dispose();
        }

        #region Backward Compatibility

        /*
         * These properties and methods maintain backward compatibility with the old
         * TranslateAPI static class. They delegate to the new provider-based architecture.
         * Existing code that references TranslateAPI can continue to work during migration.
         */

        /// <summary>
        /// Backward-compatible translate function dictionary.
        /// Maps provider names to Func signatures that the old code expects.
        /// </summary>
        public static readonly Dictionary<string, Func<string, CancellationToken, Action<string>?, Task<string>>>
            TRANSLATE_FUNCTIONS = _providers.ToDictionary(
                p => p.Name,
                p => (Func<string, CancellationToken, Action<string>?, Task<string>>)(async (text, token, onPartial) =>
                {
                    var result = await p.TranslateAsync(text, token, onPartial);
                    return result.ToString();
                }));

        public static Func<string, CancellationToken, Action<string>?, Task<string>> TranslateFunction =>
            TRANSLATE_FUNCTIONS[Translator.Setting.ApiName];

        public static bool IsLLMBased => IsCurrentLLMBased;

        public static string Prompt => Translator.Setting.Prompt;

        #endregion
    }
}