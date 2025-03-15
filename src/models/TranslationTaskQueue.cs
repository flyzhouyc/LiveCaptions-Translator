using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private readonly object _lock = new object();

        private readonly List<TranslationTask> tasks;
        private string translatedText;
        
        // 任务优先级权重，较新的任务权重更高
        private const int MAX_CONCURRENT_TASKS = 3; // 最大并发任务数

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
                // 保持活跃任务数量在限制范围内
                if (tasks.Count >= MAX_CONCURRENT_TASKS)
                {
                    // 获取最老的任务(最低优先级)
                    var oldestTask = tasks.OrderBy(t => t.Priority).FirstOrDefault();
                    if (oldestTask != null)
                    {
                        // 只在任务未完成时才取消
                        if (!oldestTask.Task.IsCompleted)
                            oldestTask.CTS.Cancel();
                        tasks.Remove(oldestTask);
                    }
                }
                
                // 为新任务分配最高优先级
                newTranslationTask.Priority = tasks.Count > 0 ? 
                    tasks.Max(t => t.Priority) + 1 : 1;
                    
                tasks.Add(newTranslationTask);
            }
            
            // Run `OnTaskCompleted` in a new thread.
            newTranslationTask.Task.ContinueWith(
                task => OnTaskCompleted(newTranslationTask),
                TaskContinuationOptions.OnlyOnRanToCompletion
            );
        }

        private async Task OnTaskCompleted(TranslationTask translationTask)
        {
            bool isHighestPriority = false;
            
            lock (_lock)
            {
                // 检查任务是否仍然是优先级最高的任务
                var highestPriorityTask = tasks.OrderByDescending(t => t.Priority).FirstOrDefault();
                isHighestPriority = highestPriorityTask == translationTask;
                
                // 任务完成后从队列中移除
                tasks.Remove(translationTask);
            }
            
            // 只有优先级最高的任务才更新输出文本
            if (isHighestPriority)
            {
                translatedText = translationTask.Task.Result;
                
                // Log after translation.
                bool isOverwrite = await Translator.IsOverwrite(translationTask.OriginalText);
                if (!isOverwrite)
                    await Translator.AddLogCard();
                await Translator.Log(translationTask.OriginalText, translatedText, isOverwrite);
            }
        }
    }

    public class TranslationTask
    {
        public Task<string> Task { get; }
        public string OriginalText { get; }
        public CancellationTokenSource CTS { get; }
        public int Priority { get; set; } // 增加优先级属性

        public TranslationTask(Func<CancellationToken, Task<string>> worker, 
            string originalText, CancellationTokenSource cts)
        {
            Task = worker(cts.Token);
            OriginalText = originalText;
            CTS = cts;
            Priority = 0; // 默认优先级
        }
    }
}