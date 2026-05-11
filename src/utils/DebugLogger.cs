using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace LiveCaptionsTranslator.utils
{
    /// <summary>
    /// Debug logger for translation pipeline analysis.
    /// Enabled via "DebugLog": true in setting.json.
    /// Writes timestamped events to debug_YYYYMMDD_HHmmss.log in the app directory.
    /// </summary>
    public static class DebugLogger
    {
        private static StreamWriter? writer;
        private static readonly object writeLock = new();
        private static bool isEnabled;

        // Statistics for summary report
        private static int totalAsrSnapshots;
        private static int totalEnqueued;
        private static int totalDequeued;
        private static int totalSkipped;
        private static int totalDropped;
        private static int totalTranslated;
        private static int punctuationTriggers;
        private static int idleIntervalTriggers;
        private static int clauseBoundaryTriggers;
        private static int partialStableTriggers;
        private static int debounceCancelled;
        private static long totalEnqueuedBytes;
        private static int totalTranslationMs;
        private static int translationCount;

        public static bool IsEnabled => isEnabled;

        public static void Initialize()
        {
            isEnabled = Translator.Setting?.DebugLog ?? false;
            if (!isEnabled) return;

            try
            {
                string filename = $"debug_{DateTime.Now:yyyyMMdd_HHmmss}.log";
                string path = Path.Combine(Directory.GetCurrentDirectory(), filename);
                writer = new StreamWriter(path, false, Encoding.UTF8) { AutoFlush = true };
                Log("SESSION_START", $"DebugLog initialized. Settings: MaxIdleInterval={Translator.Setting?.MaxIdleInterval}, NumContexts={Translator.Setting?.NumContexts}, ContextAware={Translator.Setting?.ContextAware}, ExpandedContext={Translator.Setting?.ExpandedContext}");

                // Ensure summary is written even on unhandled exceptions
                AppDomain.CurrentDomain.UnhandledException += (s, e) => WriteSummaryAndClose();
            }
            catch (Exception ex)
            {
                isEnabled = false;
                AppLogger.Warning("Failed to initialize DebugLogger.", ex);
            }
        }

        public static void Log(string category, string message)
        {
            if (!isEnabled || writer == null) return;
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            lock (writeLock)
            {
                try
                {
                    writer.WriteLine($"[{timestamp}] [{category}] {message}");
                }
                catch { }
            }
        }

        public static void LogAsrSnapshot(string fullText)
        {
            if (!isEnabled) return;
            Interlocked.Increment(ref totalAsrSnapshots);
            // Only log every 10th snapshot to avoid flooding (ASR updates every 25ms)
            if (totalAsrSnapshots % 10 == 0)
                Log("ASR", $"len={fullText.Length} tail=\"{Tail(fullText, 80)}\"");
        }

        public static void LogEnqueue(string text, string trigger)
        {
            if (!isEnabled) return;
            Interlocked.Increment(ref totalEnqueued);
            Interlocked.Add(ref totalEnqueuedBytes, Encoding.UTF8.GetByteCount(text));

            switch (trigger)
            {
                case "punctuation": Interlocked.Increment(ref punctuationTriggers); break;
                case "idleInterval": Interlocked.Increment(ref idleIntervalTriggers); break;
                case "clauseBoundary": Interlocked.Increment(ref clauseBoundaryTriggers); break;
                case "partialStable": Interlocked.Increment(ref partialStableTriggers); break;
            }

            Log("ENQUEUE", $"trigger={trigger} len={text.Length} text=\"{Truncate(text, 100)}\"");
        }

        public static void LogDequeue(string text)
        {
            if (!isEnabled) return;
            Interlocked.Increment(ref totalDequeued);
            Log("DEQUEUE", $"len={text.Length} text=\"{Truncate(text, 100)}\"");
        }

        public static void LogSkip(string text, string reason)
        {
            if (!isEnabled) return;
            Interlocked.Increment(ref totalSkipped);
            Log("SKIP", $"reason={reason} len={text.Length} text=\"{Truncate(text, 80)}\"");
        }

        public static void LogDrop(int count)
        {
            if (!isEnabled) return;
            Interlocked.Add(ref totalDropped, count);
            Log("DROP", $"count={count} (queue overflow)");
        }

        public static void LogDebounceCancelled()
        {
            if (!isEnabled) return;
            Interlocked.Increment(ref debounceCancelled);
        }

        public static void LogTranslation(string source, string result, int elapsedMs)
        {
            if (!isEnabled) return;
            Interlocked.Increment(ref totalTranslated);
            Interlocked.Increment(ref translationCount);
            Interlocked.Add(ref totalTranslationMs, elapsedMs);
            Log("TRANSLATE", $"elapsed={elapsedMs}ms src=\"{Truncate(source, 60)}\" → \"{Truncate(result, 60)}\"");
        }

        public static void WriteSummaryAndClose()
        {
            if (!isEnabled || writer == null) return;

            int avgLen = totalEnqueued > 0 ? (int)(totalEnqueuedBytes / totalEnqueued) : 0;
            int avgTransMs = translationCount > 0 ? totalTranslationMs / translationCount : 0;
            int totalTriggers = punctuationTriggers + idleIntervalTriggers + clauseBoundaryTriggers + partialStableTriggers;
            double idleRatio = totalTriggers > 0 ? (double)idleIntervalTriggers / totalTriggers * 100 : 0;
            double punctRatio = totalTriggers > 0 ? (double)punctuationTriggers / totalTriggers * 100 : 0;
            double clauseRatio = totalTriggers > 0 ? (double)clauseBoundaryTriggers / totalTriggers * 100 : 0;
            double partialRatio = totalTriggers > 0 ? (double)partialStableTriggers / totalTriggers * 100 : 0;

            Log("SUMMARY", "=== Session Statistics ===");
            Log("SUMMARY", $"ASR snapshots captured: {totalAsrSnapshots}");
            Log("SUMMARY", $"Sentences enqueued: {totalEnqueued}");
            Log("SUMMARY", $"Sentences dequeued (translated): {totalDequeued}");
            Log("SUMMARY", $"Sentences skipped (superseded): {totalSkipped}");
            Log("SUMMARY", $"Sentences dropped (queue overflow): {totalDropped}");
            Log("SUMMARY", $"Debounce cancelled (ASR mid-update): {debounceCancelled}");
            Log("SUMMARY", $"Translations completed: {totalTranslated}");
            Log("SUMMARY", $"Average enqueued sentence length: {avgLen} bytes");
            Log("SUMMARY", $"Average translation latency: {avgTransMs} ms");
            Log("SUMMARY", $"--- Trigger breakdown ---");
            Log("SUMMARY", $"Punctuation triggers: {punctuationTriggers} ({punctRatio:F1}%)");
            Log("SUMMARY", $"Clause boundary triggers: {clauseBoundaryTriggers} ({clauseRatio:F1}%)");
            Log("SUMMARY", $"Partial stable triggers: {partialStableTriggers} ({partialRatio:F1}%)");
            Log("SUMMARY", $"MaxIdleInterval triggers: {idleIntervalTriggers} ({idleRatio:F1}%)");

            // Parameter recommendations
            Log("RECOMMEND", "=== Parameter Recommendations ===");
            if (totalDropped > 0)
                Log("RECOMMEND", $"Queue overflow occurred {totalDropped} times. Translation API may be too slow for the speaker's pace.");

            if (avgLen > 150)
                Log("RECOMMEND", $"Average sentence length is long ({avgLen} bytes). Sentences may be too large for quick translation. Clause boundary detection is working well.");

            if (idleRatio > 40)
                Log("RECOMMEND", $"Idle triggers are high ({idleRatio:F0}%). Speaker has many pauses. Current MaxIdleInterval is appropriate.");

            Log("SESSION_END", "DebugLog session closed.");

            lock (writeLock)
            {
                try
                {
                    writer.Flush();
                    writer.Close();
                    writer = null;
                }
                catch { }
            }

            isEnabled = false; // Prevent double-call from UnhandledException + App.Exit
        }

        private static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\n", "\\n").Replace("\r", "");
            return text.Length <= maxLen ? text : text.Substring(0, maxLen) + "...";
        }

        private static string Tail(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\n", "\\n").Replace("\r", "");
            return text.Length <= maxLen ? text : "..." + text.Substring(text.Length - maxLen);
        }
    }
}
