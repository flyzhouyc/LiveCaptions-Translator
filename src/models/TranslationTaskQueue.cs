using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private const int MaxQueuedTasks = 8;
        private readonly object _lock = new object();
        private readonly SemaphoreSlim completionLock = new(1, 1);
        private readonly List<TranslationTask> tasks;

        private TranslationOutput output;
        private long lastOutputSegmentId;
        public TranslationOutput Output
        {
            get
            {
                lock (_lock)
                    return output;
            }
        }

        public TranslationTaskQueue()
        {
            tasks = new List<TranslationTask>();
            output = TranslationOutput.Empty;
        }

        public void Enqueue(Func<CancellationToken, Task<string>> worker, CaptionSegment segment)
        {
            var newTranslationTask = new TranslationTask(worker, segment, new CancellationTokenSource());
            var droppedTasks = new List<TranslationTask>();

            lock (_lock)
            {
                tasks.Add(newTranslationTask);
                while (tasks.Count > MaxQueuedTasks)
                {
                    int dropIndex = tasks.FindIndex(task => task.Segment.IsPartial);
                    if (dropIndex < 0)
                        dropIndex = 0;

                    droppedTasks.Add(tasks[dropIndex]);
                    tasks.RemoveAt(dropIndex);
                }
            }

            foreach (var droppedTask in droppedTasks)
                droppedTask.Cancel();
            if (droppedTasks.Count > 0)
            {
                AppLogger.Warning($"Dropped {droppedTasks.Count} stale translation task(s).");
                foreach (var dt in droppedTasks)
                    DebugLogger.Log("TASK_DROP", $"cancelled_before_complete final={dt.Segment.IsFinal} text=\"{dt.Segment.SourceText.Substring(0, Math.Min(dt.Segment.SourceText.Length, 60))}\"");
            }

            _ = newTranslationTask.Task.ContinueWith(
                async task =>
                {
                    try
                    {
                        if (task.IsCanceled)
                            return;
                        if (task.IsFaulted)
                        {
                            AppLogger.Warning("Translation task faulted.", task.Exception);
                            return;
                        }

                        var taskOutput = await task;
                        await OnTaskCompleted(newTranslationTask, taskOutput);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Failed to complete translation task.", ex);
                    }
                    finally
                    {
                        newTranslationTask.Dispose();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default
            ).Unwrap();
        }

        private async Task OnTaskCompleted(
            TranslationTask translationTask, string translatedText)
        {
            CaptionSegment segment = translationTask.Segment;
            bool shouldUpdateOutput = false;
            lock (_lock)
            {
                var index = tasks.IndexOf(translationTask);
                if (index < 0)
                    return;

                for (int i = index - 1; i >= 0; i--)
                {
                    if (!tasks[i].Segment.IsPartial)
                        continue;

                    DebugLogger.Log("TASK_SUPERSEDE", $"older_partial_cancelled: \"{tasks[i].Segment.SourceText.Substring(0, Math.Min(tasks[i].Segment.SourceText.Length, 60))}\"");
                    tasks[i].Cancel();
                    tasks.RemoveAt(i);
                }

                tasks.Remove(translationTask);

                if (segment.Id >= lastOutputSegmentId)
                {
                    output = new TranslationOutput(
                        segment.Id,
                        segment.Index,
                        segment.SourceText,
                        translatedText,
                        segment.IsFinal,
                        segment.Trigger);
                    lastOutputSegmentId = segment.Id;
                    shouldUpdateOutput = true;
                }
            }

            if (!shouldUpdateOutput)
            {
                // A newer segment has already advanced the output; this result is stale.
                // Skip both display update AND history logging to avoid out-of-order
                // entries appended to SQLite history.
                DebugLogger.Log("TASK_SUPERSEDE", $"older_result_not_displayed: \"{segment.SourceText.Substring(0, Math.Min(segment.SourceText.Length, 60))}\"");
                return;
            }

            if (!segment.IsFinal)
                return;

            await completionLock.WaitAsync();
            try
            {
                bool isOverwrite = await Translator.IsOverwrite(segment.SourceText);
                if (!isOverwrite)
                {
                    // #5: Add context directly from in-memory data instead of reading SQLite
                    Translator.AddContextDirect(segment.SourceText, translatedText);
                }
                else
                {
                    // Overwrite case: update the last context entry with the newer translation
                    Translator.UpdateLastContext(segment.SourceText, translatedText);
                }
                await Translator.Log(segment.SourceText, translatedText, isOverwrite);
            }
            finally
            {
                completionLock.Release();
            }
        }
    }

    public class TranslationTask : IDisposable
    {
        private static readonly SemaphoreSlim FinalExecutionLock = new(1, 1);

        public Task<string> Task { get; }
        public CaptionSegment Segment { get; }
        public CancellationTokenSource CTS { get; }
        private int disposed;

        public TranslationTask(Func<CancellationToken, Task<string>> worker,
            CaptionSegment segment, CancellationTokenSource cts)
        {
            Segment = segment;
            CTS = cts;
            Task = RunWorker(worker, segment, cts.Token);
        }

        private static async Task<string> RunWorker(
            Func<CancellationToken, Task<string>> worker,
            CaptionSegment segment,
            CancellationToken token)
        {
            if (!segment.IsFinal)
                return await worker(token);

            await FinalExecutionLock.WaitAsync(token);
            try
            {
                return await worker(token);
            }
            finally
            {
                FinalExecutionLock.Release();
            }
        }

        public void Cancel()
        {
            try
            {
                CTS.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            CTS.Dispose();
        }
    }
}
