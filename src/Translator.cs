using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Automation;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public static class Translator
    {
        private static AutomationElement? window = null;
        private static Caption caption = null!;
        private static Setting setting = null!;

        private static readonly Queue<string> pendingTextQueue = new();
        private static readonly object pendingTextLock = new();
        private static readonly SemaphoreSlim pendingTextSignal = new(0, int.MaxValue);
        private static readonly TranslationTaskQueue translationTaskQueue = new();
        private static readonly TimeSpan OverlayCaptionUpdateInterval = TimeSpan.FromMilliseconds(220);
        private static readonly TimeSpan PartialTranslationInterval = TimeSpan.FromMilliseconds(650);
        private const int MaxPendingTextQueueLength = 8;

        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }
        public static Caption Caption => caption;
        public static Setting Setting => setting;

        public static bool LogOnlyFlag { get; set; } = false;
        public static bool FirstUseFlag { get; set; } = false;

        public static event Action? TranslationLogged;

        static Translator()
        {
            try
            {
                window = LiveCaptionsHandler.LaunchLiveCaptions();
                if (window != null)
                {
                    LiveCaptionsHandler.FixLiveCaptions(window);
                    LiveCaptionsHandler.HideLiveCaptions(window);
                }
                else
                {
                    AppLogger.Warning("LiveCaptions window was not found during startup.");
                }
            }
            catch (Exception ex)
            {
                window = null;
                AppLogger.Warning("LiveCaptions failed to start during startup; it will be retried by TranslateLoop.", ex);
            }

            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), models.Setting.FILENAME)))
                FirstUseFlag = true;

            caption = models.Caption.GetInstance();
            setting = Setting.Load();
        }

        public static void SyncLoop(CancellationToken token = default)
        {
            int idleCount = 0;
            int syncCount = 0;
            var overlayCaptionUpdate = Stopwatch.StartNew();
            var partialTranslationUpdate = Stopwatch.StartNew();

            // Debounce: delay punctuation triggers to avoid translating ASR mid-word updates
            string? debouncePendingText = null;
            int debounceTicksRemaining = 0;
            const int DebounceTickCount = 8; // 8 × 25ms = 200ms

            while (!token.IsCancellationRequested)
            {
                if (Window == null)
                {
                    if (token.WaitHandle.WaitOne(2000))
                        break;
                    continue;
                }

                string fullText = string.Empty;
                try
                {
                    // Check LiveCaptions.exe still alive
                    var info = Window.Current;
                    var name = info.Name;
                    // Get the text recognized by LiveCaptions (10-20ms)
                    fullText = LiveCaptionsHandler.GetCaptions(Window);
                }
                catch (ElementNotAvailableException)
                {
                    Window = null;
                    continue;
                }
                if (string.IsNullOrEmpty(fullText))
                {
                    if (token.WaitHandle.WaitOne(80))
                        break;
                    continue;
                }

                // Preprocess
                fullText = RegexPatterns.Acronym().Replace(fullText, "$1$2");
                fullText = RegexPatterns.AcronymWithWords().Replace(fullText, "$1 $2");
                fullText = RegexPatterns.PunctuationSpace().Replace(fullText, "$1 ");
                fullText = RegexPatterns.CJPunctuationSpace().Replace(fullText, "$1");
                // Note: For certain languages (such as Japanese), LiveCaptions excessively uses `\n`.
                // Replace redundant `\n` within sentences with comma or period.
                fullText = TextUtil.ReplaceNewlines(fullText, TextUtil.MEDIUM_THRESHOLD);

                DebugLogger.LogAsrSnapshot(fullText);

                // Prevent adding the last sentence from previous running to log cards
                // before the first sentence is completed.
                if (fullText.IndexOfAny(TextUtil.PUNC_EOS) == -1 && Caption.HasContexts)
                    ClearContexts();

                // Get the last sentence.
                int lastEOSIndex;
                if (Array.IndexOf(TextUtil.PUNC_EOS, fullText[^1]) != -1)
                {
                    // Last char is punctuation — validate it's a real sentence end
                    if (fullText[^1] == '.' && !TextUtil.IsSentenceEndingPeriod(fullText, fullText.Length - 1))
                        lastEOSIndex = TextUtil.FindLastSentenceBoundary(fullText); //末尾.不算句末，在整个文本中找有效边界
                    else
                        lastEOSIndex = TextUtil.FindLastSentenceBoundary(fullText[0..^1]); // 末尾标点有效，找倒数第二个边界
                }
                else
                    lastEOSIndex = TextUtil.FindLastSentenceBoundary(fullText);
                string latestCaption = fullText.Substring(lastEOSIndex + 1);

                // If the last sentence is too short, extend it by adding the previous sentence.
                // Note: LiveCaptions may generate multiple characters including EOS at once.
                if (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < TextUtil.SHORT_THRESHOLD)
                {
                    lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                    latestCaption = fullText.Substring(lastEOSIndex + 1);
                }

                string overlayOriginalCaption = BuildOverlayOriginalCaption(fullText, latestCaption, lastEOSIndex);
                if (IsCompleteSentence(latestCaption) ||
                    overlayCaptionUpdate.Elapsed >= OverlayCaptionUpdateInterval)
                {
                    Caption.OverlayOriginalCaption = overlayOriginalCaption;
                    overlayCaptionUpdate.Restart();
                }

                // `DisplayOriginalCaption`: The sentence to be displayed on Main Window.
                if (string.CompareOrdinal(Caption.DisplayOriginalCaption, latestCaption) != 0)
                {
                    Caption.DisplayOriginalCaption = latestCaption;
                    // If the last sentence is too long, truncate it when displayed.
                    Caption.DisplayOriginalCaption =
                        TextUtil.ShortenDisplaySentence(Caption.DisplayOriginalCaption, TextUtil.VERYLONG_THRESHOLD);
                }

                // Prepare for `OriginalCaption`. If Expanded, only retain the complete sentence.
                int lastEOS = latestCaption.LastIndexOfAny(TextUtil.PUNC_EOS);
                if (lastEOS != -1)
                    latestCaption = latestCaption.Substring(0, lastEOS + 1);
                // `OriginalCaption`: The sentence to be really translated.
                if (string.CompareOrdinal(Caption.OriginalCaption, latestCaption) != 0)
                {
                    Caption.OriginalCaption = latestCaption;

                    idleCount = 0;
                    if (Array.IndexOf(TextUtil.PUNC_EOS, Caption.OriginalCaption[^1]) != -1)
                    {
                        // Debounce: don't enqueue immediately, wait to confirm ASR is stable
                        debouncePendingText = Caption.OriginalCaption;
                        debounceTicksRemaining = DebounceTickCount;
                        syncCount = 0;
                        partialTranslationUpdate.Restart();
                    }
                    else if (Encoding.UTF8.GetByteCount(Caption.OriginalCaption) >= TextUtil.SHORT_THRESHOLD)
                        syncCount++;

                    // Text changed — cancel any pending debounce (ASR is still updating)
                    if (debouncePendingText != null &&
                        string.CompareOrdinal(debouncePendingText, Caption.OriginalCaption) != 0 &&
                        !Array.Exists(TextUtil.PUNC_EOS, c => Caption.OriginalCaption.Length > 0 && Caption.OriginalCaption[^1] == c))
                    {
                        string cancelledTail = debouncePendingText.Length > 40
                            ? debouncePendingText.Substring(debouncePendingText.Length - 40)
                            : debouncePendingText;
                        DebugLogger.Log("DEBOUNCE", $"cancelled: text changed from \"...{cancelledTail}\"");
                        DebugLogger.LogDebounceCancelled();
                        debouncePendingText = null;
                        debounceTicksRemaining = 0;
                    }
                }
                else
                {
                    idleCount++;

                    // Debounce countdown: text is stable, tick down
                    if (debouncePendingText != null && debounceTicksRemaining > 0)
                    {
                        debounceTicksRemaining--;
                        if (debounceTicksRemaining == 0)
                        {
                            // Confirmed stable — enqueue now
                            EnqueuePendingText(debouncePendingText, "punctuation");
                            debouncePendingText = null;
                        }
                    }
                }

                // `TranslateFlag` determines whether this sentence should be translated.
                // When `OriginalCaption` remains unchanged, `idleCount` +1; when `OriginalCaption` changes, `MaxSyncInterval` +1.
                if (syncCount > Setting.MaxSyncInterval ||
                    idleCount == Setting.MaxIdleInterval)
                {
                    bool idleFlush = idleCount == Setting.MaxIdleInterval;
                    if (idleFlush || partialTranslationUpdate.Elapsed >= PartialTranslationInterval)
                    {
                        syncCount = 0;
                        EnqueuePendingText(Caption.OriginalCaption, idleFlush ? "idleInterval" : "syncInterval");
                        partialTranslationUpdate.Restart();
                    }
                }

                if (token.WaitHandle.WaitOne(25))
                    break;
            }
        }

        public static async Task TranslateLoop(CancellationToken token = default)
        {
            while (!token.IsCancellationRequested)
            {
                // Check LiveCaptions.exe still alive
                if (Window == null)
                {
                    Caption.DisplayTranslatedCaption = "[WARNING] LiveCaptions was unexpectedly closed, restarting...";
                    Window = LiveCaptionsHandler.LaunchLiveCaptions();
                    Caption.DisplayTranslatedCaption = "";
                }

                // #2: Event-driven wait instead of polling with Sleep(40)
                // Wait up to 2s for a signal; if Window is null we still loop to retry LiveCaptions
                await pendingTextSignal.WaitAsync(TimeSpan.FromSeconds(2), token);

                // Translate
                if (TryDequeuePendingText(out string originalSnapshot))
                {
                    if (LogOnlyFlag)
                    {
                        bool isOverwrite = await IsOverwrite(originalSnapshot);
                        await LogOnly(originalSnapshot, isOverwrite);
                    }
                    else
                    {
                        translationTaskQueue.Enqueue(token => Task.Run(
                            () => Translate(originalSnapshot, token), token), originalSnapshot);
                    }
                }
            }
        }

        public static void DisplayLoop(CancellationToken token = default)
        {
            while (!token.IsCancellationRequested)
            {
                var (translatedText, isChoke) = translationTaskQueue.Output;

                if (LogOnlyFlag)
                {
                    Caption.TranslatedCaption = string.Empty;
                    Caption.DisplayTranslatedCaption = "[Paused]";
                    Caption.OverlayNoticePrefix = "[Paused]";
                    Caption.OverlayCurrentTranslation = string.Empty;
                }
                else if (!string.IsNullOrEmpty(RegexPatterns.NoticePrefix().Replace(
                             translatedText, string.Empty).Trim()) &&
                         string.CompareOrdinal(Caption.TranslatedCaption, translatedText) != 0)
                {
                    string previousTranslation = Caption.TranslatedCaption;

                    // Main page
                    Caption.TranslatedCaption = translatedText;
                    Caption.DisplayTranslatedCaption =
                        TextUtil.ShortenDisplaySentence(Caption.TranslatedCaption, TextUtil.VERYLONG_THRESHOLD);

                    // Overlay window
                    if (Caption.TranslatedCaption.Contains("[ERROR]") || Caption.TranslatedCaption.Contains("[WARNING]"))
                        Caption.OverlayCurrentTranslation = Caption.TranslatedCaption;
                    else
                    {
                        var match = RegexPatterns.NoticePrefixAndTranslation().Match(Caption.TranslatedCaption);
                        Caption.OverlayNoticePrefix = match.Groups[1].Value.Trim();
                        Caption.OverlayCurrentTranslation = match.Groups[2].Value.Trim();
                    }

                    // Log display update
                    if (!string.IsNullOrEmpty(previousTranslation) && !isChoke)
                        DebugLogger.Log("DISPLAY", $"RAPID_UPDATE prev=\"{previousTranslation.Substring(Math.Max(0, previousTranslation.Length - 40))}\" → new=\"{translatedText.Substring(0, Math.Min(translatedText.Length, 40))}\"");
                }
                else if (!string.IsNullOrEmpty(translatedText) &&
                         string.CompareOrdinal(Caption.TranslatedCaption, translatedText) == 0)
                {
                    DebugLogger.Log("DISPLAY", $"DUPLICATE_SKIPPED text=\"{translatedText.Substring(0, Math.Min(translatedText.Length, 60))}\"");
                }

                // #3: Dynamic choke - skip if there are pending translations waiting
                if (isChoke && !HasPendingText())
                {
                    if (token.WaitHandle.WaitOne(300))
                        break;
                }
                if (token.WaitHandle.WaitOne(40))
                    break;
            }
        }

        private static string BuildOverlayOriginalCaption(string fullText, string latestCaption, int lastEOSIndex)
        {
            if (lastEOSIndex <= 0 || Setting == null || Caption == null || !Caption.HasContexts)
                return latestCaption;

            int displaySentences = Math.Min(Setting.DisplaySentences, Caption.ContextCount);
            if (displaySentences <= 0)
                return latestCaption;

            int currentStartIndex = lastEOSIndex + 1;
            int previousStartEOSIndex = lastEOSIndex;
            for (int historyCount = displaySentences; historyCount > 0 && previousStartEOSIndex > 0; historyCount--)
                previousStartEOSIndex = fullText[0..previousStartEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);

            int previousStartIndex = previousStartEOSIndex + 1;
            if (previousStartIndex >= currentStartIndex)
                return latestCaption;

            string previousCaption = fullText.Substring(previousStartIndex, currentStartIndex - previousStartIndex).Trim();
            string currentCaption = latestCaption.TrimStart();

            return string.IsNullOrEmpty(previousCaption)
                ? latestCaption
                : previousCaption + Environment.NewLine + currentCaption;
        }

        private static void EnqueuePendingText(string text, string trigger = "unknown")
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            lock (pendingTextLock)
            {
                if (pendingTextQueue.Count > 0 && string.CompareOrdinal(pendingTextQueue.Last(), text) == 0)
                    return;

                int droppedCount = 0;
                while (pendingTextQueue.Count >= MaxPendingTextQueueLength)
                {
                    pendingTextQueue.Dequeue();
                    droppedCount++;
                }
                if (droppedCount > 0)
                {
                    AppLogger.Warning($"Dropped {droppedCount} stale pending caption item(s).");
                    DebugLogger.LogDrop(droppedCount);
                }

                pendingTextQueue.Enqueue(text);
            }

            DebugLogger.LogEnqueue(text, trigger);

            // #2: Signal TranslateLoop that new text is available
            pendingTextSignal.Release();
        }

        private static bool TryDequeuePendingText(out string originalSnapshot)
        {
            lock (pendingTextLock)
            {
                while (pendingTextQueue.Count > 0)
                {
                    originalSnapshot = pendingTextQueue.Dequeue();
                    if (IsCompleteSentence(originalSnapshot) || pendingTextQueue.Count == 0)
                    {
                        DebugLogger.LogDequeue(originalSnapshot);
                        return true;
                    }
                    DebugLogger.LogSkip(originalSnapshot, "superseded_by_newer");
                }
            }

            originalSnapshot = string.Empty;
            return false;
        }

        private static bool HasPendingText()
        {
            lock (pendingTextLock)
                return pendingTextQueue.Count > 0;
        }

        private static bool IsCompleteSentence(string text)
        {
            return !string.IsNullOrEmpty(text) && Array.IndexOf(TextUtil.PUNC_EOS, text[^1]) != -1;
        }

        public static async Task<(string, bool)> Translate(string text, CancellationToken token = default)
        {
            string translatedText;
            bool isChoke = IsCompleteSentence(text);
            var debugSw = DebugLogger.IsEnabled ? Stopwatch.StartNew() : null;

            try
            {
                var sw = Setting.MainWindow.LatencyShow ? Stopwatch.StartNew() : null;

                // ContextAware/ExpandedContext logic is handled inside BuildLLMMessages for LLM APIs;
                // non-LLM APIs simply receive the raw text via their own methods.
                translatedText = await TranslateAPI.TranslateFunction(text, token);
                translatedText = translatedText.Replace("🔤", "");

                if (sw != null)
                {
                    sw.Stop();
                    translatedText = $"[{sw.ElapsedMilliseconds,4} ms] " + translatedText;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ($"[ERROR] Translation Failed: {ex.Message}", isChoke);
            }

            if (debugSw != null)
            {
                debugSw.Stop();
                DebugLogger.LogTranslation(text, translatedText, (int)debugSw.ElapsedMilliseconds);
            }

            return (translatedText, isChoke);
        }

        public static async Task Log(string originalText, string translatedText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            string targetLanguage, apiName;
            if (Setting != null)
            {
                targetLanguage = Setting.TargetLanguage;
                apiName = Setting.ApiName;
            }
            else
            {
                targetLanguage = "N/A";
                apiName = "N/A";
            }

            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, translatedText, targetLanguage, apiName);
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task LogOnly(string originalText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, "N/A", "N/A", "LogOnly");
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task AddContexts(CancellationToken token = default)
        {
            var lastLog = await SQLiteHistoryLogger.LoadLastTranslation(token);
            if (lastLog == null)
                return;

            Caption?.AddContext(lastLog);

            Caption?.OnPropertyChanged("DisplayLogCards");
            Caption?.OnPropertyChanged("OverlayPreviousTranslation");
        }

        // #5: Add context directly from in-memory data, avoiding SQLite read
        public static void AddContextDirect(string sourceText, string translatedText)
        {
            if (string.IsNullOrEmpty(translatedText) ||
                translatedText.Contains("[ERROR]") ||
                translatedText.Contains("[WARNING]"))
                return;

            var entry = new TranslationHistoryEntry
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                TimestampFull = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                SourceText = sourceText,
                TranslatedText = translatedText,
                TargetLanguage = Setting?.TargetLanguage ?? "N/A",
                ApiUsed = Setting?.ApiName ?? "N/A"
            };

            Caption?.AddContext(entry);
            Caption?.OnPropertyChanged("DisplayLogCards");
            Caption?.OnPropertyChanged("OverlayPreviousTranslation");
        }

        // #5 fix: When overwriting, update the last context entry instead of skipping
        public static void UpdateLastContext(string sourceText, string translatedText)
        {
            if (string.IsNullOrEmpty(translatedText) ||
                translatedText.Contains("[ERROR]") ||
                translatedText.Contains("[WARNING]"))
                return;

            var entry = new TranslationHistoryEntry
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                TimestampFull = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                SourceText = sourceText,
                TranslatedText = translatedText,
                TargetLanguage = Setting?.TargetLanguage ?? "N/A",
                ApiUsed = Setting?.ApiName ?? "N/A"
            };

            Caption?.UpdateLastContext(entry);
            Caption?.OnPropertyChanged("DisplayLogCards");
            Caption?.OnPropertyChanged("OverlayPreviousTranslation");
        }

        public static void ClearContexts()
        {
            Caption?.ClearContexts();

            Caption?.OnPropertyChanged("DisplayLogCards");
            Caption?.OnPropertyChanged("OverlayPreviousTranslation");
        }

        // If this text is too similar to the last one, overwrite it when logging.
        public static async Task<bool> IsOverwrite(string originalText, CancellationToken token = default)
        {
            string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(token);
            if (lastOriginalText == null)
                return false;

            // #4: Length ratio protection - if one text is more than 2x longer, they are different sentences
            int maxLen = Math.Max(originalText.Length, lastOriginalText.Length);
            int minLen = Math.Min(originalText.Length, lastOriginalText.Length);
            if (minLen > 0 && maxLen > minLen * 2)
                return false;

            string truncatedOriginal = originalText.Substring(0, minLen);
            string truncatedLast = lastOriginalText.Substring(0, minLen);

            double similarity = TextUtil.Similarity(truncatedOriginal, truncatedLast);
            return similarity > TextUtil.SIM_THRESHOLD;
        }
    }
}
