using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private const int MaxQueuedTasks = 8;
        private readonly object _lock = new object();
        private readonly SemaphoreSlim completionLock = new(1, 1);
        private readonly List<TranslationTask> tasks;

        private (string translatedText, bool isChoke) output;
        public (string translatedText, bool isChoke) Output
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
            output = (string.Empty, false);
        }

        public void Enqueue(Func<CancellationToken, Task<(string, bool)>> worker, string originalText)
        {
            var newTranslationTask = new TranslationTask(worker, originalText, new CancellationTokenSource());
            var droppedTasks = new List<TranslationTask>();

            lock (_lock)
            {
                tasks.Add(newTranslationTask);
                while (tasks.Count > MaxQueuedTasks)
                {
                    droppedTasks.Add(tasks[0]);
                    tasks.RemoveAt(0);
                }
            }

            foreach (var droppedTask in droppedTasks)
                droppedTask.Cancel();
            if (droppedTasks.Count > 0)
            {
                AppLogger.Warning($"Dropped {droppedTasks.Count} stale translation task(s).");
                foreach (var dt in droppedTasks)
                    DebugLogger.Log("TASK_DROP", $"cancelled_before_complete: \"{dt.OriginalText.Substring(0, Math.Min(dt.OriginalText.Length, 60))}\"");
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
            TranslationTask translationTask, (string translatedText, bool isChoke) taskOutput)
        {
            (string translatedText, bool isChoke) completedOutput;
            lock (_lock)
            {
                var index = tasks.IndexOf(translationTask);
                if (index < 0)
                    return;

                for (int i = 0; i < index; i++)
                {
                    DebugLogger.Log("TASK_SUPERSEDE", $"older_task_cancelled: \"{tasks[i].OriginalText.Substring(0, Math.Min(tasks[i].OriginalText.Length, 60))}\"");
                    tasks[i].Cancel();
                }
                tasks.RemoveRange(0, index + 1);

                output = taskOutput;
                completedOutput = output;
            }

            var translatedText = completedOutput.translatedText;

            await completionLock.WaitAsync();
            try
            {
                bool isOverwrite = await Translator.IsOverwrite(translationTask.OriginalText);
                if (!isOverwrite)
                {
                    // #5: Add context directly from in-memory data instead of reading SQLite
                    Translator.AddContextDirect(translationTask.OriginalText, translatedText);
                }
                else
                {
                    // Overwrite case: update the last context entry with the newer translation
                    Translator.UpdateLastContext(translationTask.OriginalText, translatedText);
                }
                await Translator.Log(translationTask.OriginalText, translatedText, isOverwrite);
            }
            finally
            {
                completionLock.Release();
            }
        }
    }

    public class TranslationTask : IDisposable
    {
        public Task<(string, bool)> Task { get; }
        public string OriginalText { get; }
        public CancellationTokenSource CTS { get; }
        private int disposed;

        public TranslationTask(Func<CancellationToken, Task<(string, bool)>> worker,
            string originalText, CancellationTokenSource cts)
        {
            Task = worker(cts.Token);
            OriginalText = originalText;
            CTS = cts;
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
