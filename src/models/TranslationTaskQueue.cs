using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private readonly object _lock = new object();
        private readonly List<TranslationTask> _tasks = new List<TranslationTask>();
        private readonly SemaphoreSlim _processSemaphore = new SemaphoreSlim(1, 1);
        private string _translatedText = string.Empty;
        private readonly Dictionary<string, string> _translationCache = new Dictionary<string, string>(100);
        private const int MAX_CACHE_SIZE = 100;
        
        // 添加任务优先级
        public enum TaskPriority { Low, Normal, High }

        public string Output => _translatedText;

        public void Enqueue(Func<CancellationToken, Task<string>> worker, string originalText, TaskPriority priority = TaskPriority.Normal)
        {
            // 检查缓存
            if (_translationCache.TryGetValue(originalText, out string cachedTranslation))
            {
                _translatedText = cachedTranslation;
                return;
            }

            var cts = new CancellationTokenSource();
            var newTranslationTask = new TranslationTask(worker, originalText, cts, priority);
            
            bool isFirstTask = false;
            lock (_lock)
            {
                // 如果是句末标点，取消所有低优先级的任务
                bool isCompleteTask = Array.IndexOf(TextUtil.PUNC_EOS, originalText[^1]) != -1;
                if (isCompleteTask && priority == TaskPriority.High)
                {
                    CancelLowerPriorityTasks(TaskPriority.High);
                }
                else if (priority == TaskPriority.Normal)
                {
                    CancelLowerPriorityTasks(TaskPriority.Normal);
                }
                
                isFirstTask = _tasks.Count == 0;
                _tasks.Add(newTranslationTask);
            }
            
            // 如果是首个任务或高优先级任务，立即启动处理
            if (isFirstTask || priority == TaskPriority.High)
            {
                ProcessQueue();
            }
        }

        private void CancelLowerPriorityTasks(TaskPriority threshold)
        {
            lock (_lock)
            {
                for (int i = _tasks.Count - 1; i >= 0; i--)
                {
                    if (_tasks[i].Priority < threshold)
                    {
                        _tasks[i].CTS.Cancel();
                        _tasks.RemoveAt(i);
                    }
                }
            }
        }

        private async void ProcessQueue()
        {
            if (!await _processSemaphore.WaitAsync(0)) // 不阻塞，检查是否已有线程在处理
                return;
                
            try
            {
                while (true)
                {
                    TranslationTask? currentTask = null;
                    lock (_lock)
                    {
                        if (_tasks.Count == 0)
                            return;
                            
                        // 找出优先级最高的任务
                        int highestPriorityIndex = 0;
                        for (int i = 1; i < _tasks.Count; i++)
                        {
                            if (_tasks[i].Priority > _tasks[highestPriorityIndex].Priority)
                                highestPriorityIndex = i;
                        }
                        
                        currentTask = _tasks[highestPriorityIndex];
                    }

                    try
                    {
                        string result = await currentTask.Task;
                        
                        // 检查任务是否已被取消
                        if (currentTask.CTS.Token.IsCancellationRequested)
                            continue;
                            
                        _translatedText = result;
                        
                        // 添加到缓存
                        UpdateCache(currentTask.OriginalText, result);
                        
                        // 记录到历史
                        bool isOverwrite = await Translator.IsOverwrite(currentTask.OriginalText);
                        if (!isOverwrite)
                            await Translator.AddLogCard();
                        await Translator.Log(currentTask.OriginalText, result, isOverwrite);
                    }
                    catch (OperationCanceledException)
                    {
                        // 任务被取消，不做处理
                    }
                    catch (Exception ex)
                    {
                        _translatedText = $"[Translation Failed] {ex.Message}";
                    }
                    finally
                    {
                        lock (_lock)
                        {
                            _tasks.Remove(currentTask);
                        }
                    }
                }
            }
            finally
            {
                _processSemaphore.Release();
            }
        }

        private void UpdateCache(string key, string value)
        {
            if (_translationCache.Count >= MAX_CACHE_SIZE)
            {
                // 简单的 LRU：移除第一个元素
                if (_translationCache.Count > 0)
                {
                    var firstKey = _translationCache.Keys.First();
                    _translationCache.Remove(firstKey);
                }
            }
            
            _translationCache[key] = value;
        }
        
        public void ClearCache()
        {
            _translationCache.Clear();
        }
    }

    public class TranslationTask
    {
        public Task<string> Task { get; }
        public string OriginalText { get; }
        public CancellationTokenSource CTS { get; }
        public TranslationTaskQueue.TaskPriority Priority { get; }

        public TranslationTask(Func<CancellationToken, Task<string>> worker, 
            string originalText, CancellationTokenSource cts, 
            TranslationTaskQueue.TaskPriority priority = TranslationTaskQueue.TaskPriority.Normal)
        {
            Task = worker(cts.Token);
            OriginalText = originalText;
            CTS = cts;
            Priority = priority;
        }
    }
}