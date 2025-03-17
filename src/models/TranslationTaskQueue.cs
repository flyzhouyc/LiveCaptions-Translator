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
        
        // 优化1: 记录最新任务ID，用于取消旧任务
        private long _lastTaskId = 0;
        private readonly ConcurrentDictionary<long, TranslationTask> _taskMap = 
            new ConcurrentDictionary<long, TranslationTask>();
            
        // 优化2: 增量翻译缓存
        private readonly ConcurrentDictionary<string, IncrementalCache> _incrementalCache = 
            new ConcurrentDictionary<string, IncrementalCache>();
            
        // 优化3: 处理状态跟踪
        private bool _isProcessing = false;
        
        // 优化4: 字符级别相似度分析
        private readonly DiffMatchPatch _diffTool = new DiffMatchPatch();

        // 构造函数
        public TranslationTaskQueue(int cacheSize = 200)
        {
            _translationCache = new LRUCache<string, CacheEntry>(cacheSize);
            _memoryWatcher = new MemoryWatcher(OnMemoryPressure);
            
            // 启动预翻译任务
            _predictionTimer = new Timer(ProcessPredictions, null, _predictionInterval, _predictionInterval);
        }

        public string Output => _translatedText;
        
        public bool IsProcessing => _isProcessing;

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
            
            // 生成任务ID
            long taskId = Interlocked.Increment(ref _lastTaskId);
            
            var newTranslationTask = new TranslationTask(worker, originalText, cts, actualPriority, taskId);
            _taskMap[taskId] = newTranslationTask;
            
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
        /// 添加增量翻译任务
        /// </summary>
        public void EnqueueIncrementalTranslation(string previousText, string currentText, 
                                                string previousTranslation, TaskPriority basePriority = TaskPriority.High)
        {
            // 如果之前没有翻译结果，回退到常规翻译
            if (string.IsNullOrEmpty(previousTranslation))
            {
                Enqueue(token => Task.Run(() => Translator.Translate(currentText, token), token), 
                        currentText, basePriority);
                return;
            }
            
            // 确定增量部分
            string incrementalText = ExtractTextIncrement(previousText, currentText);
            
            // 如果增量部分为空或太小，使用完整翻译
            if (string.IsNullOrEmpty(incrementalText) || incrementalText.Length < 3)
            {
                Enqueue(token => Task.Run(() => Translator.Translate(currentText, token), token), 
                        currentText, basePriority);
                return;
            }
            
            // 构造增量翻译任务
            var cts = new CancellationTokenSource();
            long taskId = Interlocked.Increment(ref _lastTaskId);
            
            var incrementalWorker = new Func<CancellationToken, Task<string>>(token => 
                Task.Run(() => Translator.TranslateIncremental(
                    currentText, incrementalText, previousTranslation, token), token));
            
            // 提高增量翻译的优先级
            TaskPriority actualPriority = Math.Max(basePriority, TaskPriority.High);
            
            var newTask = new TranslationTask(incrementalWorker, currentText, cts, actualPriority, taskId, true);
            _taskMap[taskId] = newTask;
            
            lock (_lock)
            {
                // 先取消低优先级任务
                CancelLowerPriorityTasks(TaskPriority.Normal);
                
                // 保存增量信息到缓存
                _incrementalCache[currentText] = new IncrementalCache
                {
                    PreviousText = previousText,
                    PreviousTranslation = previousTranslation,
                    IncrementalText = incrementalText,
                    Timestamp = DateTime.Now
                };
                
                _tasks.Add(newTask);
            }
            
            // 立即处理
            ProcessQueue();
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
                        _taskMap.TryRemove(_tasks[i].Id, out _);
                        _tasks.RemoveAt(i);
                    }
                }
            }
        }
        
        /// <summary>
        /// 取消所有较旧的任务
        /// </summary>
        public void CancelOlderTasks(int keepCount = 1)
        {
            lock (_lock)
            {
                if (_tasks.Count <= keepCount)
                    return;
                    
                // 保留最新的 keepCount 个任务
                var tasksToKeep = _tasks.OrderByDescending(t => t.Id).Take(keepCount).ToList();
                
                // 取消其他任务
                foreach (var task in _tasks.Except(tasksToKeep).ToList())
                {
                    task.CTS.Cancel();
                    _taskMap.TryRemove(task.Id, out _);
                    _tasks.Remove(task);
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
                if ((DateTime.Now - cacheEntry.Timestamp).TotalMinutes < 30)
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
                _isProcessing = true;
                
                while (true)
                {
                    TranslationTask? currentTask = null;
                    lock (_lock)
                    {
                        if (_tasks.Count == 0)
                        {
                            _isProcessing = false;
                            return;
                        }
                            
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
                            _taskMap.TryRemove(currentTask.Id, out _);
                        }
                    }
                }
            }
            finally
            {
                _isProcessing = false;
                _processSemaphore.Release();
            }
        }

        /// <summary>
        /// 提取两个文本之间的增量部分
        /// </summary>
        private string ExtractTextIncrement(string oldText, string newText)
        {
            if (string.IsNullOrEmpty(oldText))
                return newText;
                
            if (string.IsNullOrEmpty(newText))
                return string.Empty;
                
            if (oldText == newText)
                return string.Empty;
                
            if (newText.Length <= oldText.Length)
                return string.Empty;
                
            // 优化: 使用差异比较算法找出变化部分
            var diffs = _diffTool.diff_main(oldText, newText);
            _diffTool.diff_cleanupSemantic(diffs);
            
            StringBuilder incremental = new StringBuilder();
            
            foreach (var diff in diffs)
            {
                if (diff.operation == DiffMatchPatch.Operation.INSERT)
                {
                    incremental.Append(diff.text);
                }
            }
            
            // 如果增量为空，尝试使用简单的后缀提取
            if (incremental.Length == 0 && newText.StartsWith(oldText))
            {
                return newText.Substring(oldText.Length);
            }
            
            return incremental.ToString();
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
        /// 添加翻译结果到缓存
        /// </summary>
        private void AddToCache(string key, string value)
        {
            var entry = new CacheEntry
            {
                TranslatedText = value,
                Timestamp = DateTime.Now
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
                new TaskMetrics { TranslationCount = 1, LastTranslated = DateTime.Now, AverageTime = translationTime },
                (_, existing) => {
                    existing.TranslationCount++;
                    existing.LastTranslated = DateTime.Now;
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
            if (string.IsNullOrEmpty(currentText) || !IsCompleteSentence(currentText))
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
            _incrementalCache.Clear();
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
        public long Id { get; }
        public bool IsIncremental { get; }
        public DateTime CreatedAt { get; } = DateTime.Now;

        public TranslationTask(Func<CancellationToken, Task<string>> worker, 
            string originalText, CancellationTokenSource cts, 
            TranslationTaskQueue.TaskPriority priority = TranslationTaskQueue.TaskPriority.Normal, 
            long id = 0, bool isIncremental = false)
        {
            Task = worker(cts.Token);
            OriginalText = originalText;
            CTS = cts;
            Priority = priority;
            Id = id;
            IsIncremental = isIncremental;
        }
    }
    
    /// <summary>
    /// 增量翻译缓存
    /// </summary>
    public class IncrementalCache
    {
        public string PreviousText { get; set; } = string.Empty;
        public string PreviousTranslation { get; set; } = string.Empty;
        public string IncrementalText { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
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
    
    /// <summary>
    /// 差异比较工具类 (改编自Google Diff-Match-Patch)
    /// </summary>
    public class DiffMatchPatch
    {
        // 定义操作类型
        public enum Operation
        {
            DELETE,
            INSERT,
            EQUAL
        }

        // 差异项结构
        public class Diff
        {
            public Operation operation;
            public string text;

            public Diff(Operation operation, string text)
            {
                this.operation = operation;
                this.text = text;
            }
        }

        // 找出两个文本之间的差异
        public List<Diff> diff_main(string text1, string text2)
        {
            // 快速检查边界情况
            if (text1 == text2)
            {
                var result = new List<Diff>();
                if (!string.IsNullOrEmpty(text1))
                {
                    result.Add(new Diff(Operation.EQUAL, text1));
                }
                return result;
            }

            // 如果一个字符串为空，结果很简单
            if (string.IsNullOrEmpty(text1))
            {
                return new List<Diff> { new Diff(Operation.INSERT, text2) };
            }
            if (string.IsNullOrEmpty(text2))
            {
                return new List<Diff> { new Diff(Operation.DELETE, text1) };
            }

            // 将长字符串作为第一个参数，短字符串作为第二个参数
            string longText = text1.Length > text2.Length ? text1 : text2;
            string shortText = text1.Length > text2.Length ? text2 : text1;

            // 检查较长字符串是否包含较短字符串
            int index = longText.IndexOf(shortText);
            if (index != -1)
            {
                // 较短字符串是较长字符串的子串
                var result = new List<Diff>();
                Operation op = (text1.Length > text2.Length) ? Operation.DELETE : Operation.INSERT;
                
                // 如果前面有不同，添加
                if (index > 0)
                {
                    result.Add(new Diff(op, longText.Substring(0, index)));
                }
                
                // 添加相同部分
                result.Add(new Diff(Operation.EQUAL, shortText));
                
                // 如果后面有不同，添加
                if (index + shortText.Length < longText.Length)
                {
                    result.Add(new Diff(op, longText.Substring(index + shortText.Length)));
                }
                
                // 如果text1是较短的字符串，需要翻转操作
                if (text1.Length <= text2.Length)
                {
                    for (int i = 0; i < result.Count; i++)
                    {
                        if (result[i].operation == Operation.INSERT)
                        {
                            result[i].operation = Operation.DELETE;
                        }
                        else if (result[i].operation == Operation.DELETE)
                        {
                            result[i].operation = Operation.INSERT;
                        }
                    }
                }
                
                return result;
            }

            // 找到共同前缀
            int commonPrefixLength = diff_commonPrefix(text1, text2);
            string commonPrefix = text1.Substring(0, commonPrefixLength);
            text1 = text1.Substring(commonPrefixLength);
            text2 = text2.Substring(commonPrefixLength);

            // 找到共同后缀
            int commonSuffixLength = diff_commonSuffix(text1, text2);
            string commonSuffix = text1.Substring(text1.Length - commonSuffixLength);
            text1 = text1.Substring(0, text1.Length - commonSuffixLength);
            text2 = text2.Substring(0, text2.Length - commonSuffixLength);

            // 递归计算中间部分的差异
            var diffs = compute_diff(text1, text2);

            // 如果有共同前缀，添加到结果前面
            if (commonPrefixLength > 0)
            {
                diffs.Insert(0, new Diff(Operation.EQUAL, commonPrefix));
            }

            // 如果有共同后缀，添加到结果后面
            if (commonSuffixLength > 0)
            {
                diffs.Add(new Diff(Operation.EQUAL, commonSuffix));
            }

            return diffs;
        }

        // 简化实现：计算两个字符串的差异
        private List<Diff> compute_diff(string text1, string text2)
        {
            var diffs = new List<Diff>();

            // 边界情况处理
            if (string.IsNullOrEmpty(text1))
            {
                if (!string.IsNullOrEmpty(text2))
                {
                    diffs.Add(new Diff(Operation.INSERT, text2));
                }
                return diffs;
            }

            if (string.IsNullOrEmpty(text2))
            {
                diffs.Add(new Diff(Operation.DELETE, text1));
                return diffs;
            }

            // 简单方法：删除text1，插入text2
            diffs.Add(new Diff(Operation.DELETE, text1));
            diffs.Add(new Diff(Operation.INSERT, text2));

            return diffs;
        }

        // 找出两个字符串的共同前缀长度
        private int diff_commonPrefix(string text1, string text2)
        {
            int length = Math.Min(text1.Length, text2.Length);
            for (int i = 0; i < length; i++)
            {
                if (text1[i] != text2[i])
                {
                    return i;
                }
            }
            return length;
        }

        // 找出两个字符串的共同后缀长度
        private int diff_commonSuffix(string text1, string text2)
        {
            int length = Math.Min(text1.Length, text2.Length);
            for (int i = 0; i < length; i++)
            {
                if (text1[text1.Length - i - 1] != text2[text2.Length - i - 1])
                {
                    return i;
                }
            }
            return length;
        }

        // 清理语义上不必要的差异
        public void diff_cleanupSemantic(List<Diff> diffs)
        {
            if (diffs.Count == 0) return;
            
            // 合并相邻的DELETE和INSERT为一个可能的EQUAL
            int index = 0;
            while (index < diffs.Count - 1)
            {
                if (diffs[index].operation == Operation.DELETE &&
                    index + 1 < diffs.Count &&
                    diffs[index + 1].operation == Operation.INSERT)
                {
                    string deleteText = diffs[index].text;
                    string insertText = diffs[index + 1].text;
                    
                    // 计算删除和插入的重叠部分
                    int deleteLength = deleteText.Length;
                    int insertLength = insertText.Length;
                    
                    // 至少有3个字符相同
                    int overlapLength = diff_commonPrefix(deleteText, insertText);
                    if (overlapLength >= 3)
                    {
                        // 有共同前缀，将其分离为EQUAL
                        string prefix = deleteText.Substring(0, overlapLength);
                        string restDelete = deleteText.Substring(overlapLength);
                        string restInsert = insertText.Substring(overlapLength);
                        
                        // 替换为 EQUAL + DELETE + INSERT
                        diffs.RemoveAt(index);
                        diffs.RemoveAt(index); // 注意索引变化
                        
                        if (!string.IsNullOrEmpty(prefix))
                        {
                            diffs.Insert(index, new Diff(Operation.EQUAL, prefix));
                            index++;
                        }
                        
                        if (!string.IsNullOrEmpty(restDelete))
                        {
                            diffs.Insert(index, new Diff(Operation.DELETE, restDelete));
                            index++;
                        }
                        
                        if (!string.IsNullOrEmpty(restInsert))
                        {
                            diffs.Insert(index, new Diff(Operation.INSERT, restInsert));
                        }
                    }
                    else
                    {
                        // 尝试找共同后缀
                        overlapLength = diff_commonSuffix(deleteText, insertText);
                        if (overlapLength >= 3)
                        {
                            // 有共同后缀，将其分离为EQUAL
                            string suffix = deleteText.Substring(deleteLength - overlapLength);
                            string restDelete = deleteText.Substring(0, deleteLength - overlapLength);
                            string restInsert = insertText.Substring(0, insertLength - overlapLength);
                            
                            // 替换为 DELETE + INSERT + EQUAL
                            diffs.RemoveAt(index);
                            diffs.RemoveAt(index);
                            
                            if (!string.IsNullOrEmpty(restDelete))
                            {
                                diffs.Insert(index, new Diff(Operation.DELETE, restDelete));
                                index++;
                            }
                            
                            if (!string.IsNullOrEmpty(restInsert))
                            {
                                diffs.Insert(index, new Diff(Operation.INSERT, restInsert));
                                index++;
                            }
                            
                            if (!string.IsNullOrEmpty(suffix))
                            {
                                diffs.Insert(index, new Diff(Operation.EQUAL, suffix));
                            }
                        }
                    }
                }
                index++;
            }
            
            // 合并相邻的相同操作
            index = 0;
            while (index < diffs.Count - 1)
            {
                if (diffs[index].operation == diffs[index + 1].operation)
                {
                    diffs[index].text += diffs[index + 1].text;
                    diffs.RemoveAt(index + 1);
                }
                else
                {
                    index++;
                }
            }
        }
    }
}