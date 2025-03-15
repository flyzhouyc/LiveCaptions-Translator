using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    /// <summary>
    /// 优化的翻译任务队列，支持高级缓存、智能调度和内存管理
    /// </summary>
    public class TranslationTaskQueue
    {
        private readonly object _lock = new object();
        private readonly List<TranslationTask> _tasks = new List<TranslationTask>();
        private readonly SemaphoreSlim _processSemaphore = new SemaphoreSlim(1, 1);
        private string _translatedText = string.Empty;
        
        // LRU缓存实现
        private readonly LRUCache<string, CacheEntry> _translationCache;
        
        // 内存监控
        private readonly MemoryWatcher _memoryWatcher;

        // 用于预翻译的任务队列
        private readonly ConcurrentQueue<string> _predictionQueue = new ConcurrentQueue<string>();
        private readonly Timer _predictionTimer;
        private readonly int _predictionInterval = 500; // 毫秒
        
        // 任务指标
        private readonly ConcurrentDictionary<string, TaskMetrics> _taskMetrics = 
            new ConcurrentDictionary<string, TaskMetrics>();
        
        // 添加语言检测缓存
        private readonly ConcurrentDictionary<string, TextUtil.LanguageType> _languageCache = 
            new ConcurrentDictionary<string, TextUtil.LanguageType>();
        
        // 任务优先级
        public enum TaskPriority { Low = 0, Normal = 50, High = 100, Critical = 200 }

        // 构造函数
        public TranslationTaskQueue(int cacheSize = 200)
        {
            _translationCache = new LRUCache<string, CacheEntry>(cacheSize);
            _memoryWatcher = new MemoryWatcher(OnMemoryPressure);
            
            // 启动预翻译任务
            _predictionTimer = new Timer(ProcessPredictions, null, _predictionInterval, _predictionInterval);
        }

        public string Output => _translatedText;

        /// <summary>
        /// 将翻译任务加入队列
        /// </summary>
        public void Enqueue(Func<CancellationToken, Task<string>> worker, string originalText, TaskPriority basePriority = TaskPriority.Normal)
        {
            // 检查缓存
            if (TryGetFromCache(originalText, out string cachedTranslation))
            {
                _translatedText = cachedTranslation;
                return;
            }

            var cts = new CancellationTokenSource();
            
            // 计算实际优先级
            TaskPriority actualPriority = CalculateTaskPriority(originalText, basePriority);
            
            var newTranslationTask = new TranslationTask(worker, originalText, cts, actualPriority);
            
            bool isFirstTask = false;
            
            lock (_lock)
            {
                // 如果是句末标点，取消所有低优先级的任务
                bool isCompleteTask = Array.IndexOf(TextUtil.PUNC_EOS, originalText[^1]) != -1;
                if (isCompleteTask && actualPriority >= TaskPriority.High)
                {
                    CancelLowerPriorityTasks(actualPriority);
                }
                else if (actualPriority >= TaskPriority.Normal)
                {
                    CancelLowerPriorityTasks(TaskPriority.Low);
                }
                
                isFirstTask = _tasks.Count == 0;
                _tasks.Add(newTranslationTask);
                
                // 维护任务指标
                UpdateTaskMetrics(originalText);
            }
            
            // 如果是首个任务或高优先级任务，立即启动处理
            if (isFirstTask || actualPriority >= TaskPriority.High)
            {
                ProcessQueue();
            }
            
            // 根据当前文本预测下一个可能的文本并加入预翻译队列
            PredictNextText(originalText);
        }

        /// <summary>
        /// 基于文本特性计算任务优先级
        /// </summary>
        private TaskPriority CalculateTaskPriority(string text, TaskPriority basePriority)
        {
            int priorityScore = (int)basePriority;
            
            // 增加完整句子的优先级
            if (text.Length > 0 && Array.IndexOf(TextUtil.PUNC_EOS, text[^1]) != -1)
            {
                priorityScore += 50;
            }
            
            // 根据文本长度调整优先级
            if (text.Length < TextUtil.SHORT_THRESHOLD)
            {
                priorityScore -= 10; // 非常短的文本降低优先级
            }
            else if (text.Length > TextUtil.LONG_THRESHOLD)
            {
                priorityScore += 30; // 长文本提高优先级
            }
            
            // 检查语言类型
            var languageType = _languageCache.GetOrAdd(text, t => TextUtil.DetectLanguage(t));
            
            // 根据历史翻译频率调整优先级
            if (_taskMetrics.TryGetValue(text, out var metrics) && metrics.TranslationCount > 3)
            {
                priorityScore += 20; // 经常翻译的文本提高优先级
            }
            
            // 将分数转换回枚举值
            if (priorityScore >= (int)TaskPriority.Critical) return TaskPriority.Critical;
            if (priorityScore >= (int)TaskPriority.High) return TaskPriority.High;
            if (priorityScore >= (int)TaskPriority.Normal) return TaskPriority.Normal;
            return TaskPriority.Low;
        }

        /// <summary>
        /// 取消低于指定优先级的任务
        /// </summary>
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

        /// <summary>
        /// 尝试从缓存获取翻译结果
        /// </summary>
        private bool TryGetFromCache(string originalText, out string translation)
        {
            if (_translationCache.TryGet(originalText, out var cacheEntry))
            {
                // 检查缓存项是否过期
                if ((DateTime.UtcNow - cacheEntry.Timestamp).TotalMinutes < 30)
                {
                    translation = cacheEntry.TranslatedText;
                    return true;
                }
                
                // 过期缓存项将被移除
                _translationCache.Remove(originalText);
            }
            
            translation = string.Empty;
            return false;
        }

        /// <summary>
        /// 处理翻译队列
        /// </summary>
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
                        var stopwatch = Stopwatch.StartNew();
                        string result = await currentTask.Task;
                        stopwatch.Stop();
                        
                        // 检查任务是否已被取消
                        if (currentTask.CTS.Token.IsCancellationRequested)
                            continue;
                            
                        _translatedText = result;
                        
                        // 添加到缓存
                        AddToCache(currentTask.OriginalText, result);
                        
                        // 更新任务指标
                        UpdateTaskMetrics(currentTask.OriginalText, stopwatch.ElapsedMilliseconds);
                        
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

        /// <summary>
        /// 添加翻译结果到缓存
        /// </summary>
        private void AddToCache(string key, string value)
        {
            var entry = new CacheEntry
            {
                TranslatedText = value,
                Timestamp = DateTime.UtcNow
            };
            
            _translationCache.Add(key, entry);
        }
        
        /// <summary>
        /// 更新任务指标
        /// </summary>
        private void UpdateTaskMetrics(string text, long translationTime = 0)
        {
            _taskMetrics.AddOrUpdate(
                text,
                new TaskMetrics { TranslationCount = 1, LastTranslated = DateTime.UtcNow, AverageTime = translationTime },
                (_, existing) => {
                    existing.TranslationCount++;
                    existing.LastTranslated = DateTime.UtcNow;
                    if (translationTime > 0)
                    {
                        existing.AverageTime = (existing.AverageTime * (existing.TranslationCount - 1) + translationTime) 
                                                / existing.TranslationCount;
                    }
                    return existing;
                });
            
            // 清理过旧的指标
            if (_taskMetrics.Count > 500)
            {
                var oldMetrics = _taskMetrics
                    .OrderBy(kv => kv.Value.LastTranslated)
                    .Take(100)
                    .Select(kv => kv.Key)
                    .ToList();
                    
                foreach (var key in oldMetrics)
                {
                    _taskMetrics.TryRemove(key, out _);
                }
            }
        }
        
        /// <summary>
        /// 预测下一个可能的文本并加入预翻译队列
        /// </summary>
        private void PredictNextText(string currentText)
        {
            // 只对完整句子进行预测
            if (currentText.Length == 0 || !IsCompleteSentence(currentText))
                return;
                
            // 使用简单的历史分析预测下一个可能的文本
            var predictions = GetPredictions(currentText);
            
            foreach (var prediction in predictions)
            {
                // 避免重复添加
                if (!_predictionQueue.Contains(prediction))
                {
                    _predictionQueue.Enqueue(prediction);
                }
            }
            
            // 限制预测队列大小
            while (_predictionQueue.Count > 10)
            {
                _predictionQueue.TryDequeue(out _);
            }
        }
        
        /// <summary>
        /// 处理预翻译队列
        /// </summary>
        private async void ProcessPredictions(object state)
        {
            // 检查是否有空闲资源进行预翻译
            if (_tasks.Count > 0 || !_predictionQueue.TryDequeue(out string textToPredict))
                return;
            
            // 检查是否已经在缓存中
            if (_translationCache.Contains(textToPredict))
                return;
                
            try
            {
                // 使用低优先级执行预翻译
                string result = await Translator.Translate(textToPredict);
                
                // 将结果添加到缓存
                AddToCache(textToPredict, result);
            }
            catch (Exception)
            {
                // 忽略预翻译错误
            }
        }
        
        /// <summary>
        /// 获取基于当前文本的预测
        /// </summary>
        private List<string> GetPredictions(string currentText)
        {
            var predictions = new List<string>();
            
            // 基于历史频率查找可能的后续文本
            var frequentFollowers = _taskMetrics
                .Where(kv => kv.Value.TranslationCount >= 2) // 只考虑翻译过多次的文本
                .OrderByDescending(kv => kv.Value.TranslationCount)
                .Take(3)
                .Select(kv => kv.Key)
                .ToList();
                
            predictions.AddRange(frequentFollowers);
            
            // 如果文本以问号结尾，生成常见的回答开头
            if (currentText.EndsWith("?") || currentText.EndsWith("？"))
            {
                predictions.Add("Yes, ");
                predictions.Add("No, ");
                predictions.Add("I think ");
            }
            
            return predictions;
        }
        
        /// <summary>
        /// 判断是否为完整句子
        /// </summary>
        private bool IsCompleteSentence(string text)
        {
            return text.Length > 0 && Array.IndexOf(TextUtil.PUNC_EOS, text[^1]) != -1;
        }
        
        /// <summary>
        /// 内存压力处理
        /// </summary>
        private void OnMemoryPressure(MemoryPressureLevel level)
        {
            Console.WriteLine($"[Memory] Detected memory pressure: {level}");
            
            switch (level)
            {
                case MemoryPressureLevel.Low:
                    // 轻度压力：减小缓存
                    _translationCache.Resize(_translationCache.Capacity * 3 / 4);
                    break;
                case MemoryPressureLevel.Medium:
                    // 中度压力：大幅减小缓存并清理非活跃资源
                    _translationCache.Resize(_translationCache.Capacity / 2);
                    _languageCache.Clear();
                    break;
                case MemoryPressureLevel.High:
                    // 高压力：紧急释放资源
                    _translationCache.Clear();
                    _languageCache.Clear();
                    _taskMetrics.Clear();
                    
                    // 清空预测队列
                    while (_predictionQueue.TryDequeue(out _)) { }
                    
                    // 取消低优先级任务
                    CancelLowerPriorityTasks(TaskPriority.High);
                    break;
            }
            
            // 手动触发GC
            GC.Collect(2, GCCollectionMode.Optimized, false);
        }

        /// <summary>
        /// 清除缓存
        /// </summary>
        public void ClearCache()
        {
            _translationCache.Clear();
            _languageCache.Clear();
            _taskMetrics.Clear();
        }
        
        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            _predictionTimer?.Dispose();
            _memoryWatcher.Dispose();
        }
    }

    /// <summary>
    /// 翻译任务定义
    /// </summary>
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
    
    /// <summary>
    /// LRU缓存实现
    /// </summary>
    public class LRUCache<TKey, TValue>
    {
        private readonly object _lock = new object();
        private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _cacheMap;
        private readonly LinkedList<CacheItem> _cacheList;
        private int _capacity;
        
        public int Capacity => _capacity;
        public int Count => _cacheMap.Count;
        
        public LRUCache(int capacity)
        {
            _capacity = capacity;
            _cacheMap = new Dictionary<TKey, LinkedListNode<CacheItem>>(capacity);
            _cacheList = new LinkedList<CacheItem>();
        }
        
        public bool TryGet(TKey key, out TValue value)
        {
            lock (_lock)
            {
                if (_cacheMap.TryGetValue(key, out var node))
                {
                    // 移到链表头部（最近使用）
                    _cacheList.Remove(node);
                    _cacheList.AddFirst(node);
                    
                    value = node.Value.Value;
                    return true;
                }
                
                value = default;
                return false;
            }
        }
        
        public void Add(TKey key, TValue value)
        {
            lock (_lock)
            {
                // 如果已存在，先移除
                if (_cacheMap.TryGetValue(key, out var existingNode))
                {
                    _cacheList.Remove(existingNode);
                    _cacheMap.Remove(key);
                }
                
                // 检查容量，移除最不常用的项
                if (_cacheMap.Count >= _capacity)
                {
                    RemoveLeastUsed();
                }
                
                // 添加新项到链表头部
                var cacheItem = new CacheItem(key, value);
                var newNode = _cacheList.AddFirst(cacheItem);
                _cacheMap.Add(key, newNode);
            }
        }
        
        public void Remove(TKey key)
        {
            lock (_lock)
            {
                if (_cacheMap.TryGetValue(key, out var node))
                {
                    _cacheList.Remove(node);
                    _cacheMap.Remove(key);
                }
            }
        }
        
        public bool Contains(TKey key)
        {
            lock (_lock)
            {
                return _cacheMap.ContainsKey(key);
            }
        }
        
        public void Clear()
        {
            lock (_lock)
            {
                _cacheMap.Clear();
                _cacheList.Clear();
            }
        }
        
        public void Resize(int newCapacity)
        {
            lock (_lock)
            {
                if (newCapacity < 10) newCapacity = 10; // 最小容量
                
                _capacity = newCapacity;
                
                // 如果当前项数超过新容量，移除多余项
                while (_cacheMap.Count > _capacity)
                {
                    RemoveLeastUsed();
                }
            }
        }
        
        private void RemoveLeastUsed()
        {
            var last = _cacheList.Last;
            if (last != null)
            {
                _cacheMap.Remove(last.Value.Key);
                _cacheList.RemoveLast();
            }
        }
        
        private class CacheItem
        {
            public TKey Key { get; }
            public TValue Value { get; }
            
            public CacheItem(TKey key, TValue value)
            {
                Key = key;
                Value = value;
            }
        }
    }
    
    /// <summary>
    /// 缓存条目
    /// </summary>
    public class CacheEntry
    {
        public string TranslatedText { get; set; }
        public DateTime Timestamp { get; set; }
    }
    
    /// <summary>
    /// 任务指标
    /// </summary>
    public class TaskMetrics
    {
        public int TranslationCount { get; set; }
        public DateTime LastTranslated { get; set; }
        public double AverageTime { get; set; }
    }
    
    /// <summary>
    /// 内存压力级别
    /// </summary>
    public enum MemoryPressureLevel
    {
        Low,
        Medium,
        High
    }
    
    /// <summary>
    /// 内存监控器
    /// </summary>
    public class MemoryWatcher : IDisposable
    {
        private readonly Timer _timer;
        private readonly Action<MemoryPressureLevel> _pressureCallback;
        private long _lastUsedMemory;
        
        public MemoryWatcher(Action<MemoryPressureLevel> pressureCallback)
        {
            _pressureCallback = pressureCallback;
            _lastUsedMemory = GC.GetTotalMemory(false);
            
            // 每10秒检查一次内存使用情况
            _timer = new Timer(CheckMemoryUsage, null, 10000, 10000);
        }
        
        private void CheckMemoryUsage(object state)
        {
            long currentMemory = GC.GetTotalMemory(false);
            
            // 获取系统内存信息
            GetPhysicallyInstalledSystemMemory(out long totalMemoryKb);
            long totalMemoryMb = totalMemoryKb / 1024;
            
            // 使用当前进程的内存使用量
            long workingSetMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
            
            double memoryUsagePercent = ((double)workingSetMb / totalMemoryMb) * 100;
            double memoryGrowthRate = ((double)currentMemory - _lastUsedMemory) / _lastUsedMemory;
            
            _lastUsedMemory = currentMemory;
            
            // 确定内存压力级别
            MemoryPressureLevel pressureLevel = MemoryPressureLevel.Low;
            
            if (memoryUsagePercent > 80 || memoryGrowthRate > 0.5)
            {
                pressureLevel = MemoryPressureLevel.High;
            }
            else if (memoryUsagePercent > 50 || memoryGrowthRate > 0.2)
            {
                pressureLevel = MemoryPressureLevel.Medium;
            }
            else if (memoryUsagePercent > 30 || memoryGrowthRate > 0.1)
            {
                pressureLevel = MemoryPressureLevel.Low;
            }
            else
            {
                // 内存使用正常，不需要操作
                return;
            }
            
            // 触发回调
            _pressureCallback(pressureLevel);
        }
        
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);
        
        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}