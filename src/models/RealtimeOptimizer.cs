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
        private readonly TimeSpan _elementCacheTimeout = TimeSpan.FromMilliseconds(100);
        
        // 翻译优化
        private readonly Queue<string> _textBuffer = new(3);
        private float _lastConfidence = 0;
        private readonly Dictionary<string, float> _patterns = new();
        private readonly SentenceProcessor _sentenceProcessor;
        
        // 性能监控
        private readonly Stopwatch _perfWatch = new();
        private readonly Queue<int> _delayStats = new(100);
        private readonly object _statsLock = new();

        public RealtimeOptimizer()
        {
            _sentenceProcessor = new SentenceProcessor();
            _perfWatch.Start();
        }

        public int GetOptimalDelay(string text, string prevText)
        {
            lock (_statsLock)
            {
                int delay;
                if (string.IsNullOrEmpty(text))
                {
                    delay = Math.Min(20 + _emptyCount++ * 5, 100); // 更大的基础延迟
                }
                else if (text == prevText)
                {
                    delay = Math.Min(15 + _sameTextCount++ * 3, 50);
                }
                else
                {
                    _emptyCount = _sameTextCount = 0;
                    delay = 10; // 增加最小延迟
                }

                _delayStats.Enqueue(delay);
                if (_delayStats.Count > 100)
                    _delayStats.Dequeue();

                return delay;
            }
        }

        public async Task<string> GetCaptionTextAsync(AutomationElement? window)
        {
            if (window == null) return string.Empty;

            try
            {
                // 首先尝试使用AutomationElement
                if (_cachedElement == null || DateTime.Now - _lastElementAccess > _elementCacheTimeout)
                {
                    _cachedElement = LiveCaptionsHandler.FindElementByAId(window, "CaptionTextBlock");
                    _lastElementAccess = DateTime.Now;
                }

                string text = _cachedElement?.Current.Name ?? string.Empty;
                
                // 如果获取到文本，立即返回
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }

                // 如果获取失败，重置缓存
                _cachedElement = null;
                
                // 等待短暂时间后重试
                await Task.Delay(50);
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

        public async Task<bool> ShouldTranslateAsync(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            // 完整句子立即翻译
            if (_sentenceProcessor.IsCompleteSentence(text))
                return true;

            // 预测评分
            float completenessScore = CalculateCompleteness(text);
            _lastConfidence = completenessScore;

            // 更新模式
            UpdatePatterns(text);

            // 更宽松的阈值检查
            return text.Length >= GetDynamicThreshold(completenessScore) * 0.8;
        }

        private float CalculateCompleteness(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            float score = 0;
            
            // 1. 句子完整性检查
            if (_sentenceProcessor.IsCompleteSentence(text))
                score += 0.5f;
                
            // 2. 自然停顿检查
            if (_sentenceProcessor.HasNaturalPause(text))
                score += 0.3f;
                
            // 3. 模式匹配
            if (_patterns.TryGetValue(text, out float patternScore))
                score += patternScore * 0.2f;

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
                _patterns[text] = Math.Min(_patterns[text] + 0.1f, 1.0f);
            else
                _patterns[text] = 0.1f;
        }

        private int GetDynamicThreshold(float confidence)
        {
            return confidence switch
            {
                > 0.7f => 50,  // 高置信度
                > 0.5f => 70,  // 中等置信度
                _ => 90        // 低置信度
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
