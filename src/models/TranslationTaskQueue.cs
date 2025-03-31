using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private readonly object _lock = new object();

        // 优化：限制最大任务数量，防止过多任务导致资源耗尽
        private const int MAX_QUEUE_SIZE = 5;
        
        private readonly List<TranslationTask> tasks;
        private string translatedText;
        
        // 性能监控变量
        private int totalTasksProcessed = 0;
        private int successfulTasks = 0;
        private DateTime lastTaskCompletionTime = DateTime.MinValue;
        private TimeSpan averageTaskDuration = TimeSpan.Zero;

        public string Output
        {
            get => translatedText;
        }
        
        // 性能指标属性
        public double SuccessRate => totalTasksProcessed > 0 ? (double)successfulTasks / totalTasksProcessed : 1.0;
        public TimeSpan AverageTaskDuration => averageTaskDuration;

        public TranslationTaskQueue()
        {
            tasks = new List<TranslationTask>();
            translatedText = string.Empty;
        }

        public void Enqueue(Func<CancellationToken, Task<string>> worker, string originalText)
        {
            // 优化：控制队列大小，防止资源耗尽
            lock (_lock)
            {
                // 如果队列已满，取消最旧的任务
                if (tasks.Count >= MAX_QUEUE_SIZE)
                {
                    for (int i = 0; i < tasks.Count - MAX_QUEUE_SIZE + 1; i++)
                    {
                        tasks[i].CTS.Cancel();
                        Console.WriteLine($"[性能优化] 队列过长，取消旧翻译任务");
                    }
                    
                    // 清理已取消的任务
                    tasks.RemoveAll(t => t.CTS.IsCancellationRequested);
                }
                
                // 创建新任务并添加到队列
                var newTranslationTask = new TranslationTask(worker, originalText, new CancellationTokenSource());
                tasks.Add(newTranslationTask);
                
                // 记录任务开始时间
                newTranslationTask.StartTime = DateTime.Now;
            }
            
            var task = tasks.Last();
            // Run `OnTaskCompleted` in a new thread.
            task.Task.ContinueWith(
                t => OnTaskCompleted(task),
                TaskContinuationOptions.OnlyOnRanToCompletion
            );
        }

        private async Task OnTaskCompleted(TranslationTask translationTask)
        {
            bool isSuccess = false;
            try
            {
                lock (_lock)
                {
                    var index = tasks.IndexOf(translationTask);
                    if (index < 0) return; // 任务可能已被移除
                    
                    // 取消所有更早的任务
                    for (int i = 0; i < index; i++)
                    {
                        if (!tasks[i].CTS.IsCancellationRequested)
                            tasks[i].CTS.Cancel();
                    }
                    
                    // 移除所有已完成的任务和本任务
                    for (int i = index; i >= 0; i--)
                    {
                        tasks.RemoveAt(i);
                    }
                }
                
                // 更新性能指标
                totalTasksProcessed++;
                
                // 更新任务持续时间统计
                if (translationTask.StartTime != DateTime.MinValue)
                {
                    TimeSpan taskDuration = DateTime.Now - translationTask.StartTime;
                    
                    // 计算移动平均
                    if (averageTaskDuration == TimeSpan.Zero)
                        averageTaskDuration = taskDuration;
                    else
                        averageTaskDuration = TimeSpan.FromMilliseconds(
                            (averageTaskDuration.TotalMilliseconds * 0.8) + (taskDuration.TotalMilliseconds * 0.2));
                }
                
                translatedText = translationTask.Task.Result;
                
                // 更新最后完成时间
                lastTaskCompletionTime = DateTime.Now;
                
                // 如果任务顺利完成，标记成功
                isSuccess = true;
                
                // Log after translation.
                bool isOverwrite = await Translator.IsOverwrite(translationTask.OriginalText);
                if (!isOverwrite)
                    await Translator.AddLogCard();
                await Translator.Log(translationTask.OriginalText, translatedText, isOverwrite);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[错误] 翻译任务完成处理失败: {ex.Message}");
            }
            finally
            {
                // 无论成功与否都更新计数
                if (isSuccess)
                    successfulTasks++;
            }
        }
        
        // 优化：添加资源清理方法
        public void ClearAllTasks()
        {
            lock (_lock)
            {
                foreach (var task in tasks)
                {
                    if (!task.CTS.IsCancellationRequested)
                        task.CTS.Cancel();
                }
                tasks.Clear();
            }
        }
    }

    public class TranslationTask
    {
        public Task<string> Task { get; }
        public string OriginalText { get; }
        public CancellationTokenSource CTS { get; }
        public DateTime StartTime { get; set; } = DateTime.MinValue;

        public TranslationTask(Func<CancellationToken, Task<string>> worker, 
            string originalText, CancellationTokenSource cts)
        {
            Task = worker(cts.Token);
            OriginalText = originalText;
            CTS = cts;
        }
    }
}