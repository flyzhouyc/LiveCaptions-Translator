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
                translatedText = result;
                
                // 获取覆写状态
                bool isOverwrite = await Translator.IsOverwrite(translationTask.OriginalText);
                
                // 直接添加到日志卡片 - 重要修复：无条件添加日志卡片
                var entry = new TranslationHistoryEntry
                {
                    Timestamp = DateTime.Now.ToString("MM/dd HH:mm"),
                    TimestampFull = DateTime.Now.ToString("MM/dd/yy, HH:mm:ss"),
                    SourceText = translationTask.OriginalText,
                    TranslatedText = string.IsNullOrEmpty(result) ? "[No translation result]" : result,
                    TargetLanguage = App.Setting?.TargetLanguage ?? "N/A",
                    ApiUsed = App.Setting?.ApiName ?? "N/A"
                };
                
                // 先添加到本地显示
                lock (App.Caption._logLock)
                {
                    if (App.Caption.LogCards.Count >= App.Setting?.MainWindow.CaptionLogMax)
                        App.Caption.LogCards.Dequeue();
                        
                    App.Caption.LogCards.Enqueue(entry);
                    
                    // 直接在主线程上触发属性更改通知
                    App.Current.Dispatcher.BeginInvoke(() => {
                        App.Caption.OnPropertyChanged("DisplayLogCards");
                    });
                }
                
                // 再保存到数据库
                await Translator.Log(translationTask.OriginalText, result, isOverwrite);
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