namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private readonly object _lock = new object();

        private readonly List<TranslationTask> tasks;
        private string output;

        public string Output
        {
            get => output;
        }

        public TranslationTaskQueue()
        {
            tasks = new List<TranslationTask>();
            output = string.Empty;
        }

        public void Enqueue(Func<CancellationToken, Task<string>> worker)
        {
            // 为每个任务设置超时
            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(3)); // 3秒超时
            
            var newTranslationTask = new TranslationTask(worker, cts);
            lock (_lock)
            {
                // 如果队列太长，取消较早的任务
                if (tasks.Count >= 3)
                {
                    tasks[0].CTS.Cancel();
                    tasks.RemoveAt(0);
                }
                tasks.Add(newTranslationTask);
            }
    
            newTranslationTask.Task.ContinueWith(
                task => OnTaskCompleted(newTranslationTask),
                TaskContinuationOptions.OnlyOnRanToCompletion
            );
    
            // 处理超时或失败的情况
            newTranslationTask.Task.ContinueWith(task => {
                if (task.IsFaulted || task.IsCanceled)
                {
                    lock (_lock)
                    {
                        tasks.Remove(newTranslationTask);
                    }
                }
            }, TaskContinuationOptions.NotOnRanToCompletion);
        }

        private void OnTaskCompleted(TranslationTask translationTask)
        {
            lock (_lock)
            {
                var index = tasks.IndexOf(translationTask);
                if (index < 0) return; // 任务可能已被移除

                // 只取消旧任务，保留最新的任务
                for (int i = 0; i < index; i++)
                    tasks[i].CTS.Cancel();
            
                // 清除已完成和已取消的任务
                tasks.RemoveRange(0, index + 1);
            
                // 更新输出
                if (!string.IsNullOrEmpty(translationTask.Task.Result))
                {
                    output = translationTask.Task.Result;
                }
            }
        }
    }

    public class TranslationTask
    {
        public Task<string> Task { get; }
        public CancellationTokenSource CTS { get; }

        public TranslationTask(Func<CancellationToken, Task<string>> worker, CancellationTokenSource cts)
        {
            Task = worker(cts.Token);
            CTS = cts;
        }
    }
}