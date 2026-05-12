using System.Diagnostics;
using System.IO;
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

        private static readonly Queue<CaptionSegment> pendingSegmentQueue = new();
        private static readonly object pendingSegmentLock = new();
        private static readonly SemaphoreSlim pendingSegmentSignal = new(0, int.MaxValue);
        private static readonly TranslationTaskQueue translationTaskQueue = new();
        private static readonly CaptionSegmentStabilizer segmentStabilizer = new();
        private static readonly CaptionAligner captionAligner = new();
        private static readonly TimeSpan OverlayCaptionUpdateInterval = TimeSpan.FromMilliseconds(220);
        private static readonly TimeSpan PartialTranslationInterval = TimeSpan.FromMilliseconds(500);
        private const int MaxPendingSegmentQueueLength = 8;

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
            var overlayCaptionUpdate = Stopwatch.StartNew();

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

                fullText = NormalizeCaptionSnapshot(fullText);

                DebugLogger.LogAsrSnapshot(fullText);

                // Prevent adding the last sentence from previous running to log cards
                // before the first sentence is completed.
                if (fullText.IndexOfAny(TextUtil.PUNC_EOS) == -1 && Caption.HasContexts)
                    ClearContexts();

                TimeSpan idleFinalDelay = TimeSpan.FromMilliseconds(
                    Math.Max(400, Setting.MaxIdleInterval * 20));
                var update = segmentStabilizer.Process(
                    fullText,
                    partialStableDelay: TimeSpan.FromMilliseconds(250),
                    partialTranslationInterval: PartialTranslationInterval,
                    idleFinalDelay: idleFinalDelay);
                if (string.IsNullOrEmpty(update.LatestCaption))
                {
                    if (token.WaitHandle.WaitOne(25))
                        break;
                    continue;
                }

                string latestCaption = update.LatestCaption;
                string overlayOriginalCaption = BuildOverlayOriginalCaption(
                    fullText,
                    latestCaption,
                    update.LatestCaptionBoundaryIndex);
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

                if (string.CompareOrdinal(Caption.OriginalCaption, update.TranslationCandidate) != 0)
                    Caption.OriginalCaption = update.TranslationCandidate;

                foreach (var segment in update.Segments)
                {
                    // Register source segment with the aligner for tracking
                    captionAligner.AddSourceSegment(segment);
                    EnqueuePendingSegment(segment);
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
                await pendingSegmentSignal.WaitAsync(TimeSpan.FromSeconds(2), token);

                // Translate
                if (TryDequeuePendingSegment(out CaptionSegment segment))
                {
                    if (LogOnlyFlag)
                    {
                        if (!segment.IsFinal)
                            continue;

                        bool isOverwrite = await IsOverwrite(segment.SourceText);
                        await LogOnly(segment.SourceText, isOverwrite);
                    }
                    else
                    {
                        translationTaskQueue.Enqueue(
                            token => Translate(segment, token),
                            segment);
                    }
                }
            }
        }

        public static void DisplayLoop(CancellationToken token = default)
        {
            while (!token.IsCancellationRequested)
            {
                var output = translationTaskQueue.Output;
                string translatedText = output.TranslatedText;

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

                    // Align translation with its source segment
                    // Strip latency prefix [xxxx ms] before aligning
                    var prefixMatch = RegexPatterns.NoticePrefixAndTranslation().Match(translatedText);
                    string cleanTranslation = prefixMatch.Success ? prefixMatch.Groups[2].Value.Trim() : translatedText;
                    bool isError = translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]");

                    // Overlay window - use aligned display for synced view
                    if (isError)
                    {
                        Caption.OverlayCurrentTranslation = Caption.TranslatedCaption;
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(cleanTranslation))
                            captionAligner.UpdateTranslation(output.SegmentId, cleanTranslation, output.IsFinal);

                        var alignedDisplay = captionAligner.GetAlignedDisplay(
                            output.SegmentId,
                            historyCount: Math.Max(1, Setting.DisplaySentences - 1));

                        // Update overlay original caption from aligner (shows history + current aligned)
                        if (!string.IsNullOrEmpty(alignedDisplay.OverlayOriginalText))
                            Caption.OverlayOriginalCaption = alignedDisplay.OverlayOriginalText;

                        // Update overlay translation from aligner (shows history + current aligned)
                        var match = RegexPatterns.NoticePrefixAndTranslation().Match(translatedText);
                        Caption.OverlayNoticePrefix = match.Groups[1].Value.Trim();
                        Caption.OverlayCurrentTranslation = !string.IsNullOrEmpty(alignedDisplay.OverlayTranslatedText)
                            ? alignedDisplay.OverlayTranslatedText
                            : match.Groups[2].Value.Trim();
                    }

                    // Log display update
                    if (!string.IsNullOrEmpty(previousTranslation) && !output.IsFinal)
                        DebugLogger.Log("DISPLAY", $"RAPID_UPDATE prev=\"{previousTranslation.Substring(Math.Max(0, previousTranslation.Length - 40))}\" → new=\"{translatedText.Substring(0, Math.Min(translatedText.Length, 40))}\"");
                }
                else if (!string.IsNullOrEmpty(translatedText) &&
                         string.CompareOrdinal(Caption.TranslatedCaption, translatedText) == 0)
                {
                    DebugLogger.Log("DISPLAY", $"DUPLICATE_SKIPPED text=\"{translatedText.Substring(0, Math.Min(translatedText.Length, 60))}\"");
                }

                // #3: Dynamic choke - skip if there are pending translations waiting
                if (output.IsFinal && !HasPendingSegments())
                {
                    if (token.WaitHandle.WaitOne(300))
                        break;
                }
                if (token.WaitHandle.WaitOne(40))
                    break;
            }
        }

        private static string NormalizeCaptionSnapshot(string fullText)
        {
            fullText = RegexPatterns.Acronym().Replace(fullText, "$1$2");
            fullText = RegexPatterns.AcronymWithWords().Replace(fullText, "$1 $2");
            fullText = RegexPatterns.PunctuationSpace().Replace(fullText, "$1 ");
            fullText = RegexPatterns.CJPunctuationSpace().Replace(fullText, "$1");
            return TextUtil.ReplaceNewlines(fullText, TextUtil.MEDIUM_THRESHOLD);
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

        private static void EnqueuePendingSegment(CaptionSegment segment)
        {
            if (string.IsNullOrWhiteSpace(segment.SourceText))
                return;

            lock (pendingSegmentLock)
            {
                if (pendingSegmentQueue.Count > 0)
                {
                    var last = pendingSegmentQueue.Last();
                    if (last.IsPartial == segment.IsPartial &&
                        string.CompareOrdinal(last.SourceText, segment.SourceText) == 0)
                        return;
                }

                if (segment.IsPartial &&
                    pendingSegmentQueue.Any(item =>
                        item.IsFinal &&
                        string.CompareOrdinal(item.SourceText, segment.SourceText) == 0))
                    return;


                int droppedCount = 0;
                while (pendingSegmentQueue.Count >= MaxPendingSegmentQueueLength)
                {
                    DropOldestPendingSegment();
                    droppedCount++;
                }
                if (droppedCount > 0)
                {
                    AppLogger.Warning($"Dropped {droppedCount} stale pending caption item(s).");
                    DebugLogger.LogDrop(droppedCount);
                }

                pendingSegmentQueue.Enqueue(segment);
            }

            DebugLogger.LogEnqueue(segment.SourceText, segment.Trigger);

            pendingSegmentSignal.Release();
        }

        private static bool TryDequeuePendingSegment(out CaptionSegment segment)
        {
            lock (pendingSegmentLock)
            {
                while (pendingSegmentQueue.Count > 0)
                {
                    segment = pendingSegmentQueue.Dequeue();
                    if (segment.IsFinal || pendingSegmentQueue.Count == 0)
                    {
                        DebugLogger.LogDequeue(segment.SourceText);
                        return true;
                    }
                    DebugLogger.LogSkip(segment.SourceText, "partial_superseded_by_newer");
                }
            }

            segment = null!;
            return false;
        }

        private static bool HasPendingSegments()
        {
            lock (pendingSegmentLock)
                return pendingSegmentQueue.Count > 0;
        }

        private static bool IsCompleteSentence(string text)
        {
            return TextUtil.HasTerminalSentenceBoundary(text);
        }

        private static void DropOldestPendingSegment()
        {
            if (pendingSegmentQueue.Count == 0)
                return;

            var items = pendingSegmentQueue.ToList();
            int dropIndex = items.FindIndex(item => item.IsPartial);
            if (dropIndex < 0)
                dropIndex = 0;

            pendingSegmentQueue.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                if (i != dropIndex)
                    pendingSegmentQueue.Enqueue(items[i]);
            }
        }

        public static async Task<string> Translate(
            CaptionSegment segment, CancellationToken token = default)
        {
            string text = segment.SourceText;
            string translatedText;
            var debugSw = DebugLogger.IsEnabled ? Stopwatch.StartNew() : null;

            // Streaming progress hook: providers that stream tokens (OpenAI SSE)
            // call this on each accumulated chunk so the UI can render typing-style
            // updates without waiting for the full translation.
            Action<string> onPartial = partial =>
            {
                if (string.IsNullOrEmpty(partial))
                    return;
                translationTaskQueue.UpdateStreamingOutput(segment, partial.Replace("🔤", ""));
            };

            try
            {
                var sw = Setting.MainWindow.LatencyShow ? Stopwatch.StartNew() : null;

                // Use fallback-enabled translation if a fallback API is configured
                if (!string.IsNullOrWhiteSpace(Setting.FallbackApiName))
                {
                    var fallbackResult = await TranslateDispatcher.TranslateWithFallbackAsync(text, token, onPartial);
                    translatedText = fallbackResult.ToString().Replace("🔤", "");
                }
                else
                {
                    // ContextAware/ExpandedContext logic is handled inside BuildLLMMessages for LLM APIs;
                    // non-LLM APIs simply receive the raw text via their own methods.
                    translatedText = await TranslateAPI.TranslateFunction(text, token, onPartial);
                    translatedText = translatedText.Replace("🔤", "");
                }

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
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (debugSw != null)
            {
                debugSw.Stop();
                DebugLogger.LogTranslation(text, translatedText, (int)debugSw.ElapsedMilliseconds);
            }

            return translatedText;
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
