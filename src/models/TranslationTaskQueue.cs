using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private readonly object _lock = new object();

        private readonly List<TranslationTask> tasks;
        private string translatedText;
        private readonly int _maxRetainedTasks = 2; // 最多保留的任务数，避免所有任务都被取消

        public string Output
        {
            get => translatedText;
        }

        public TranslationTaskQueue()
        {
            tasks = new List<TranslationTask>();
            translatedText = string.Empty;
        }

        public void Enqueue(Func<CancellationToken, Task<string>> worker, string originalText)
        {
            var newTranslationTask = new TranslationTask(worker, originalText, new CancellationTokenSource());
            lock (_lock)
            {
                tasks.Add(newTranslationTask);
            }
            // 运行OnTaskCompleted在新线程中
            newTranslationTask.Task.ContinueWith(
                task => {
                    if (task.IsCompletedSuccessfully)
                        OnTaskCompleted(newTranslationTask);
                    else if (task.IsFaulted && task.Exception != null)
                        HandleTaskException(newTranslationTask, task.Exception);
                }
            );
        }

        private async Task OnTaskCompleted(TranslationTask translationTask)
        {
            lock (_lock)
            {
                var index = tasks.IndexOf(translationTask);
                if (index < 0) return; // 任务可能已被移除
                
                // 只取消较旧的任务，保留最近的几个任务避免全部丢失
                int tasksToKeep = Math.Min(_maxRetainedTasks, tasks.Count);
                int cancelThreshold = tasks.Count - tasksToKeep;
                
                // 取消旧任务
                for (int i = 0; i < Math.Min(cancelThreshold, index); i++)
                {
                    try { tasks[i].CTS.Cancel(); } catch { /* 忽略取消错误 */ }
                }
                
                // 移除当前任务
                tasks.RemoveAt(index);
            }

            // 更新翻译结果
            if (!translationTask.CTS.IsCancellationRequested)
            {
                string result = translationTask.Task.Result;
                translatedText = result; // 更新翻译文本
                
                // 记录到历史并更新UI
                bool isOverwrite = await Translator.IsOverwrite(translationTask.OriginalText);
                
                // 确保非空结果也记录
                await Translator.Log(translationTask.OriginalText, result, isOverwrite);
                
                // 无论是否覆盖，都尝试添加到显示卡片
                await App.Caption.AddLogCard();
            }
        }
        
        private void HandleTaskException(TranslationTask task, AggregateException exception)
        {
            // 记录异常但不崩溃
            Console.WriteLine($"翻译任务异常: {exception.InnerException?.Message}");
            
            // 如果因为取消而失败，不做特殊处理
            if (exception.InnerException is OperationCanceledException)
                return;
                
            // 对于其他异常，设置一个错误消息
            translatedText = $"[Translation Error: {exception.InnerException?.Message}]";
        }
    }

    public class TranslationTask
    {
        public Task<string> Task { get; }
        public string OriginalText { get; }
        public CancellationTokenSource CTS { get; }

        public TranslationTask(Func<CancellationToken, Task<string>> worker, 
            string originalText, CancellationTokenSource cts)
        {
            Task = worker(cts.Token);
            OriginalText = originalText;
            CTS = cts;
        }
    }
}