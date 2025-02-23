using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Automation;
using LiveCaptionsTranslator.models.CaptionProcessing;

namespace LiveCaptionsTranslator.models
{
    public class RealtimeOptimizer
    {
        // 字幕优化
        private int _emptyCount = 0;
        private int _sameTextCount = 0;
        private AutomationElement? _cachedElement;
        private DateTime _lastElementAccess;
        private readonly TimeSpan _elementCacheTimeout = TimeSpan.FromMilliseconds(30); // 减少缓存超时
        
        // 预测性缓存
        private readonly Dictionary<string, AutomationElement> _elementCache = new(3);
        private DateTime _lastPredictiveUpdate = DateTime.MinValue;
        private readonly TimeSpan _predictiveUpdateInterval = TimeSpan.FromMilliseconds(150);
        
        // 翻译优化
        private readonly Queue<string> _textBuffer = new(10); // 增加缓冲区大小
        private float _lastConfidence = 0;
        private readonly Dictionary<string, float> _patterns = new();
        private readonly SentenceProcessor _sentenceProcessor;
        
        // 自适应延迟
        private readonly Queue<(DateTime time, int delay)> _delayHistory = new(30);
        private float _avgProcessingTime = 15;
        private readonly object _delayLock = new();
        
        // 性能监控
        private readonly Stopwatch _perfWatch = new();
        private readonly Queue<int> _delayStats = new(200);
        private readonly object _statsLock = new();

        public RealtimeOptimizer()
        {
            _sentenceProcessor = new SentenceProcessor();
            _perfWatch.Start();
            InitializeAdaptiveDelay();
        }

        private void InitializeAdaptiveDelay()
        {
            for (int i = 0; i < 5; i++)
            {
                _delayHistory.Enqueue((DateTime.Now, 10));
            }
            UpdateAverageProcessingTime();
        }

        private void UpdateAverageProcessingTime()
        {
            if (_delayHistory.Count < 2) return;

            var times = _delayHistory.ToArray();
            float totalTime = 0;
            int count = 0;

            for (int i = 1; i < times.Length; i++)
            {
                var timeDiff = (times[i].time - times[i - 1].time).TotalMilliseconds;
                if (timeDiff > 0 && timeDiff < 1000) // 过滤异常值
                {
                    totalTime += (float)timeDiff;
                    count++;
                }
            }

            if (count > 0)
            {
                _avgProcessingTime = totalTime / count;
            }
        }

        public int GetOptimalDelay(string text, string prevText)
        {
            lock (_delayLock)
            {
                int baseDelay;
                if (string.IsNullOrEmpty(text))
                {
                    baseDelay = Math.Min(15 + _emptyCount++ * 4, 80);
                }
                else if (text == prevText)
                {
                    baseDelay = Math.Min(12 + _sameTextCount++ * 2, 40);
                }
                else
                {
                    _emptyCount = _sameTextCount = 0;
                    baseDelay = Math.Max(5, (int)(_avgProcessingTime * 0.8));
                }

                // 应用自适应因子
                float adaptiveFactor = CalculateAdaptiveFactor();
                int finalDelay = (int)(baseDelay * adaptiveFactor);

                // 更新历史记录
                _delayHistory.Enqueue((DateTime.Now, finalDelay));
                if (_delayHistory.Count > 10)
                {
                    _delayHistory.Dequeue();
                    UpdateAverageProcessingTime();
                }

                // 更新统计
                lock (_statsLock)
                {
                    _delayStats.Enqueue(finalDelay);
                    if (_delayStats.Count > 100)
                        _delayStats.Dequeue();
                }

                return finalDelay;
            }
        }

        private float CalculateAdaptiveFactor()
        {
            if (_delayHistory.Count < 2) return 1.0f;

            var recentDelays = _delayHistory.TakeLast(3).ToArray();
            float avgDelay = (float)recentDelays.Average(x => x.delay);
            float variance = (float)(recentDelays.Sum(x => Math.Pow(x.delay - avgDelay, 2)) / recentDelays.Length);

            // 根据方差调整因子
            float varianceFactor = Math.Min(1.0f, 10.0f / (float)(1 + Math.Sqrt(variance)));

            // 考虑处理时间趋势
            float trendFactor = _avgProcessingTime < 15 ? 0.9f : 1.1f;

            return varianceFactor * trendFactor;
        }

        public async Task<string> GetCaptionTextAsync(AutomationElement? window)
        {
            if (window == null) return string.Empty;

            try
            {
                // 检查预测性缓存是否需要更新
                if (DateTime.Now - _lastPredictiveUpdate > _predictiveUpdateInterval)
                {
                    await UpdatePredictiveCacheAsync(window);
                }

                // 首先尝试使用主缓存
                if (_cachedElement != null && DateTime.Now - _lastElementAccess <= _elementCacheTimeout)
                {
                    string text = _cachedElement.Current.Name;
                    if (!string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }

                // 尝试使用预测性缓存
                foreach (var element in _elementCache.Values)
                {
                    try
                    {
                        string text = element.Current.Name;
                        if (!string.IsNullOrEmpty(text))
                        {
                            _cachedElement = element;
                            _lastElementAccess = DateTime.Now;
                            return text;
                        }
                    }
                    catch { continue; }
                }

                // 如果缓存都失败，重新获取元素
                _cachedElement = LiveCaptionsHandler.FindElementByAId(window, "CaptionTextBlock");
                _lastElementAccess = DateTime.Now;

                if (_cachedElement != null)
                {
                    string text = _cachedElement.Current.Name;
                    if (!string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }

                // 最后尝试
                await Task.Delay(30); // 减少重试延迟
                _cachedElement = LiveCaptionsHandler.FindElementByAId(window, "CaptionTextBlock");
                return _cachedElement?.Current.Name ?? string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Caption retrieval error: {ex.Message}");
                _cachedElement = null;
                return string.Empty;
            }
        }

        private async Task UpdatePredictiveCacheAsync(AutomationElement window)
        {
            try
            {
                _lastPredictiveUpdate = DateTime.Now;

                // 异步更新预测性缓存
                await Task.Run(() =>
                {
                    var newElements = new Dictionary<string, AutomationElement>();
                    var potentialIds = new[] { "CaptionTextBlock", "TranslatedTextBlock", "SubtitleBlock" };

                    foreach (var id in potentialIds)
                    {
                        try
                        {
                            var element = LiveCaptionsHandler.FindElementByAId(window, id);
                            if (element != null)
                            {
                                newElements[id] = element;
                            }
                        }
                        catch { continue; }
                    }

                    // 更新缓存
                    foreach (var kvp in newElements)
                    {
                        _elementCache[kvp.Key] = kvp.Value;
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Predictive cache update error: {ex.Message}");
            }
        }

        public async Task<bool> ShouldTranslateAsync(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            // 完整句子立即翻译
            if (_sentenceProcessor.IsCompleteSentence(text))
            {
                _lastConfidence = 1.0f;
                return true;
            }

            // 自然停顿检查
            if (_sentenceProcessor.HasNaturalPause(text))
            {
                _lastConfidence = 0.8f;
                return true;
            }

            // 长度检查
            if (text.Length >= 30) // 降低长度阈值
            {
                _lastConfidence = 0.6f;
                return true;
            }

            // 预测评分
            float completenessScore = CalculateCompleteness(text);
            _lastConfidence = completenessScore;

            // 更新模式
            UpdatePatterns(text);

            // 更宽松的阈值检查
            return completenessScore >= 0.4f; // 降低完整度阈值
        }

        private float CalculateCompleteness(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            float score = 0;
            
            // 基础分数
            score += 0.3f;
            
            // 句子完整性检查
            if (_sentenceProcessor.IsCompleteSentence(text))
                score += 0.4f;
                
            // 自然停顿检查
            if (_sentenceProcessor.HasNaturalPause(text))
                score += 0.2f;
                
            // 模式匹配
            if (_patterns.TryGetValue(text, out float patternScore))
                score += patternScore * 0.1f;

            return Math.Min(score, 1.0f);
        }

        private void UpdatePatterns(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            _textBuffer.Enqueue(text);
            if (_textBuffer.Count > 3)
                _textBuffer.Dequeue();

            // 更新模式得分
            if (_patterns.ContainsKey(text))
                _patterns[text] = Math.Min(_patterns[text] + 0.2f, 1.0f); // 加快学习速度
            else
                _patterns[text] = 0.2f; // 提高初始分数
        }

        private int GetDynamicThreshold(float confidence)
        {
            return confidence switch
            {
                > 0.7f => 30,  // 降低高置信度阈值
                > 0.5f => 40,  // 降低中等置信度阈值
                _ => 50        // 降低低置信度阈值
            };
        }

        public PerformanceMetrics GetPerformanceMetrics()
        {
            lock (_statsLock)
            {
                return new PerformanceMetrics
                {
                    AverageDelay = _delayStats.Count > 0 ? _delayStats.Average() : 0,
                    MaxDelay = _delayStats.Count > 0 ? _delayStats.Max() : 0,
                    MinDelay = _delayStats.Count > 0 ? _delayStats.Min() : 0,
                    LastConfidence = _lastConfidence
                };
            }
        }
    }

    public class PerformanceMetrics
    {
        public double AverageDelay { get; set; }
        public int MaxDelay { get; set; }
        public int MinDelay { get; set; }
        public float LastConfidence { get; set; }
    }
}
