using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private readonly object _lock = new object();

        // 将普通列表替换为线程安全集合
        private readonly ConcurrentDictionary<int, TranslationTask> _activeTasks = new ConcurrentDictionary<int, TranslationTask>();
        private int _nextTaskId = 0;
        
        // 批处理翻译请求
        private readonly ConcurrentQueue<(string text, DateTime timestamp)> _batchPendingTexts = new ConcurrentQueue<(string, DateTime)>();
        private readonly Timer _batchTranslationTimer;
        private const int BATCH_WINDOW_MS = 200; // 200ms的批处理窗口
        
        private string translatedText = string.Empty;
        
        // 性能监控变量
        private int totalTasksProcessed = 0;
        private int successfulTasks = 0;
        private DateTime lastTaskCompletionTime = DateTime.MinValue;
        private TimeSpan averageTaskDuration = TimeSpan.Zero;
        
        // 网络状态监控
        private readonly ExponentialMovingAverage _networkLatencyAvg = new ExponentialMovingAverage(0.3);
        private readonly ExponentialMovingAverage _translationSuccessRateAvg = new ExponentialMovingAverage(0.2);
        private int _consecutiveTimeouts = 0;

        public string Output => translatedText;
        
        // 性能指标属性
        public double SuccessRate => totalTasksProcessed > 0 ? (double)successfulTasks / totalTasksProcessed : 1.0;
        public TimeSpan AverageTaskDuration => averageTaskDuration;
        public double NetworkLatencyMs => _networkLatencyAvg.Average;
        public double TranslationSuccessRate => _translationSuccessRateAvg.Average;
        
        // 自适应超时时间
        public TimeSpan GetAdaptiveTimeout()
        {
            // 基于网络延迟自适应调整超时
            double baseTimeout = 5000; // 基础5秒
            
            if (_networkLatencyAvg.Average > 0)
            {
                // 将超时设置为平均延迟的3倍，最小2秒，最大15秒
                baseTimeout = Math.Min(Math.Max(_networkLatencyAvg.Average * 3, 2000), 15000);
                
                // 如果连续超时，逐步增加超时时间
                if (_consecutiveTimeouts > 0)
                {
                    baseTimeout *= (1 + Math.Min(_consecutiveTimeouts * 0.5, 2.0));
                }
            }
            
            return TimeSpan.FromMilliseconds(baseTimeout);
        }

        public TranslationTaskQueue()
        {
            // 初始化批处理定时器
            _batchTranslationTimer = new Timer(ProcessBatchTranslation, null, Timeout.Infinite, Timeout.Infinite);
        }
        
        /// <summary>
        /// 将文本加入翻译队列，支持批处理
        /// </summary>
        public void Enqueue(string text, bool enableBatching = true)
        {
            if (string.IsNullOrEmpty(text))
                return;
                
            if (enableBatching)
            {
                // 将文本加入批处理队列
                _batchPendingTexts.Enqueue((text, DateTime.Now));
                
                // 启动批处理定时器（如果尚未启动）
                _batchTranslationTimer.Change(BATCH_WINDOW_MS, Timeout.Infinite);
            }
            else
            {
                // 直接处理单个文本
                EnqueueTranslationTask(text);
            }
        }
        
        /// <summary>
        /// 批处理翻译请求
        /// </summary>
        private void ProcessBatchTranslation(object state)
        {
            try
            {
                if (_batchPendingTexts.IsEmpty)
                    return;
                    
                // 收集批处理窗口内的所有文本
                List<string> textsToProcess = new List<string>();
                DateTime cutoffTime = DateTime.Now.AddMilliseconds(-BATCH_WINDOW_MS);
                
                while (_batchPendingTexts.TryDequeue(out var item))
                {
                    textsToProcess.Add(item.text);
                }
                
                if (textsToProcess.Count == 0)
                    return;
                    
                // 如果只有一个文本，直接处理
                if (textsToProcess.Count == 1)
                {
                    EnqueueTranslationTask(textsToProcess[0]);
                    return;
                }
                
                // 多个文本合并处理
                // 找出最长的文本作为主要翻译目标
                string primaryText = textsToProcess.OrderByDescending(t => t.Length).First();
                EnqueueTranslationTask(primaryText);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"批处理翻译异常: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 将单个翻译任务加入队列
        /// </summary>
        private void EnqueueTranslationTask(string text)
        {
            // 使用对象池获取翻译任务
            var translationTask = TranslationTaskPool.Obtain();
            
            int taskId = Interlocked.Increment(ref _nextTaskId);
            
            // 初始化任务
            translationTask.Initialize(token => TranslateWithTimeout(text, token), text);
            
            // 限制最大任务数
            if (_activeTasks.Count >= 5)
            {
                // 取消最旧的任务
                var oldestTask = _activeTasks.OrderBy(x => x.Value.StartTime).FirstOrDefault();
                if (oldestTask.Value != null)
                {
                    oldestTask.Value.CTS?.Cancel();
                    _activeTasks.TryRemove(oldestTask.Key, out _);
                    TranslationTaskPool.Return(oldestTask.Value);
                }
            }
            
            // 添加到活动任务
            _activeTasks.TryAdd(taskId, translationTask);
            
            // 设置任务完成回调
            translationTask.Task?.ContinueWith(t => OnTaskCompleted(taskId, t), 
                TaskContinuationOptions.OnlyOnRanToCompletion);
        }
        
        /// <summary>
        /// 使用自适应超时进行翻译
        /// </summary>
        private async Task<string> TranslateWithTimeout(string text, CancellationToken token)
        {
            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                // 创建组合的取消令牌
                using var timeoutSource = new CancellationTokenSource(GetAdaptiveTimeout());
                using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutSource.Token);
                
                // 执行翻译
                string result = await Translator.TranslateWithContext(text, Translator.Setting.ApiName, linkedSource.Token)
                    .ConfigureAwait(false);
                
                // 更新网络延迟统计
                stopwatch.Stop();
                _networkLatencyAvg.AddValue(stopwatch.ElapsedMilliseconds);
                _consecutiveTimeouts = 0;
                _translationSuccessRateAvg.AddValue(1.0); // 成功
                
                return result;
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                
                // 判断是超时还是用户取消
                if (!token.IsCancellationRequested)
                {
                    // 超时
                    _consecutiveTimeouts++;
                    _translationSuccessRateAvg.AddValue(0.0); // 失败
                    return $"[翻译超时] 请检查网络连接或API服务状态";
                }
                
                return string.Empty;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _translationSuccessRateAvg.AddValue(0.0); // 失败
                return $"[翻译失败] {ex.Message}";
            }
        }

        /// <summary>
        /// 任务完成回调
        /// </summary>
        private async Task OnTaskCompleted(int taskId, Task<string> completedTask)
        {
            try
            {
                // 尝试获取任务
                if (!_activeTasks.TryRemove(taskId, out var translationTask))
                    return;
                
                // 取消所有较早的任务
                foreach (var oldTask in _activeTasks.Where(t => t.Value.StartTime < translationTask.StartTime))
                {
                    oldTask.Value.CTS?.Cancel();
                    _activeTasks.TryRemove(oldTask.Key, out _);
                    TranslationTaskPool.Return(oldTask.Value);
                }
                
                // 更新性能统计
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
                
                // 获取翻译结果
                string translatedResult = completedTask.Result;
                translatedText = translatedResult;
                
                // 记录任务完成时间
                lastTaskCompletionTime = DateTime.Now;
                successfulTasks++;
                
                // 日志操作
                bool isOverwrite = await Translator.IsOverwrite(translationTask.OriginalText)
                    .ConfigureAwait(false);
                    
                if (!isOverwrite)
                    await Translator.AddLogCard().ConfigureAwait(false);
                    
                await Translator.Log(translationTask.OriginalText, translatedResult, isOverwrite)
                    .ConfigureAwait(false);
                
                // 返回任务到对象池
                TranslationTaskPool.Return(translationTask);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"处理翻译任务完成回调时出错: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 清理所有任务
        /// </summary>
        public void ClearAllTasks()
        {
            // 取消批处理定时器
            _batchTranslationTimer.Change(Timeout.Infinite, Timeout.Infinite);
            
            // 清空批处理队列
            while (_batchPendingTexts.TryDequeue(out _)) { }
            
            // 取消所有活动任务
            foreach (var task in _activeTasks.Values)
            {
                task.CTS?.Cancel();
                TranslationTaskPool.Return(task);
            }
            
            _activeTasks.Clear();
        }
        
        /// <summary>
        /// 指数移动平均计算类
        /// </summary>
        private class ExponentialMovingAverage
        {
            private double _average = 0;
            private readonly double _alpha;
            private bool _hasValue = false;
            
            public double Average => _hasValue ? _average : 0;
            
            public ExponentialMovingAverage(double alpha)
            {
                _alpha = alpha;
            }
            
            public void AddValue(double value)
            {
                if (!_hasValue)
                {
                    _average = value;
                    _hasValue = true;
                }
                else
                {
                    _average = (_alpha * value) + ((1 - _alpha) * _average);
                }
            }
        }
    }
}