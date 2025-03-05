namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private readonly object _lock = new object();

        // 存储任务队列
        private readonly List<TranslationTask> tasks;
        
        // 存储完成的翻译结果，保持最新的几个
        private readonly Queue<string> completedResults;
        
        // 最大保留的已完成翻译结果数量
        private const int MaxCompletedResults = 3;
        
        // 当前输出
        private string output;

        public string Output
        {
            get => output;
        }

        public TranslationTaskQueue()
        {
            tasks = new List<TranslationTask>();
            completedResults = new Queue<string>();
            output = string.Empty;
        }

        public void Enqueue(Func<CancellationToken, Task<string>> worker)
        {
            var newTranslationTask = new TranslationTask(worker, new CancellationTokenSource(), DateTime.Now);
            lock (_lock)
            {
                // 最多保留5个任务在队列中，避免资源浪费
                const int maxQueueSize = 5;
                while (tasks.Count >= maxQueueSize)
                {
                    // 取消并移除最旧的任务
                    var oldestTask = tasks.OrderBy(t => t.CreationTime).First();
                    oldestTask.CTS.Cancel();
                    tasks.Remove(oldestTask);
                }
                
                tasks.Add(newTranslationTask);
            }
            
            newTranslationTask.Task.ContinueWith(
                task => OnTaskCompleted(newTranslationTask),
                TaskContinuationOptions.OnlyOnRanToCompletion
            );
        }

        private void OnTaskCompleted(TranslationTask translationTask)
        {
            lock (_lock)
            {
                if (translationTask.Task.IsCanceled || translationTask.Task.IsFaulted)
                    return;

                // 获取任务结果
                string result = translationTask.Task.Result;
                
                // 只有结果非空才处理
                if (!string.IsNullOrEmpty(result))
                {
                    // 将结果添加到完成队列
                    completedResults.Enqueue(result);
                    
                    // 保持队列在最大容量范围内
                    while (completedResults.Count > MaxCompletedResults)
                    {
                        completedResults.Dequeue();
                    }
                    
                    // 更新当前输出为最新的结果
                    output = result;
                }
                
                // 从任务列表中移除当前任务
                tasks.Remove(translationTask);
                
                // 找出所有已经完成的任务并移除
                var completedTasks = tasks.Where(t => t.Task.IsCompleted).ToList();
                foreach (var task in completedTasks)
                {
                    tasks.Remove(task);
                }
            }
        }
        
        // 在需要时可以获取备选的翻译结果（如果最新的翻译结果不可用）
        public string GetLatestAvailableResult()
        {
            lock (_lock)
            {
                // 优先返回当前输出
                if (!string.IsNullOrEmpty(output))
                {
                    return output;
                }
                
                // 如果当前输出为空，则返回队列中最新的结果
                return completedResults.Count > 0 ? completedResults.Last() : string.Empty;
            }
        }
    }

    public class TranslationTask
    {
        public Task<string> Task { get; }
        public CancellationTokenSource CTS { get; }
        public DateTime CreationTime { get; }

        public TranslationTask(Func<CancellationToken, Task<string>> worker, CancellationTokenSource cts, DateTime creationTime)
        {
            Task = worker(cts.Token);
            CTS = cts;
            CreationTime = creationTime;
        }
    }
}