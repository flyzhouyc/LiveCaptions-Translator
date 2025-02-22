using System;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.models.CaptionProcessing
{
    public class CaptionTextProcessor
    {
        public static readonly char[] PUNC_EOS = ".?!。？！".ToCharArray();
        public static readonly char[] PUNC_COMMA = ",，、—\n".ToCharArray();
        
        // Moved to App.Settings.MinCaptionBytes
        private const int MAX_CAPTION_BYTES = 170;
        private readonly Dictionary<string, int> _languageLengthCache = new();
        private readonly Dictionary<string, DateTime> _lastProcessingTimes = new();
        private float _currentSpeedFactor = 1.0f;
        private DateTime _lastSpeedUpdate = DateTime.MinValue;
        private readonly TimeSpan _speedUpdateInterval = TimeSpan.FromSeconds(1);

        private int GetOptimalCaptionLength(string text)
        {
            if (!App.Settings.UseAutomaticOptimalLength)
            {
                return App.Settings.OptimalCaptionLength;
            }

            var targetLang = App.Settings.TargetLanguage;
            
            // 使用缓存的语言基础长度
            if (!_languageLengthCache.TryGetValue(targetLang, out int baseLength))
            {
                baseLength = targetLang switch
                {
                    "zh-CN" or "zh-TW" or "ja" or "ko" => 75,  // 东亚语言字符较少
                    "en" => 120,                                // 英语需要更多字符
                    _ => 100                                    // 其他语言默认值
                };
                _languageLengthCache[targetLang] = baseLength;
            }

            // 定期更新语速因子
            var now = DateTime.Now;
            if (now - _lastSpeedUpdate > _speedUpdateInterval)
            {
                UpdateSpeedFactor();
                _lastSpeedUpdate = now;
            }

            // 应用语速和用户调整因子
            float adjustedLength = baseLength * _currentSpeedFactor * (float)App.Settings.OptimalLengthAdjustmentFactor;

            // 确保在合理范围内
            return Math.Clamp((int)adjustedLength, 50, 200);
        }

        private void UpdateSpeedFactor()
        {
            if (_sentenceProcessor is MLSentenceProcessor mlProcessor)
            {
                var speechRate = mlProcessor.GetCurrentSpeechRate();
                if (speechRate > 0)
                {
                    // 使用平滑过渡
                    float targetFactor = (float)Math.Clamp(3.0 / speechRate, 0.7, 1.5);
                    _currentSpeedFactor = _currentSpeedFactor * 0.7f + targetFactor * 0.3f;
                }
            }
        }
        private static readonly TimeSpan MAX_WAIT_TIME = TimeSpan.FromSeconds(2);
        private DateTime _lastTranslationTime = DateTime.MinValue;
        private readonly SentenceProcessor _sentenceProcessor;
        private static readonly CaptionTextProcessor _instance = new CaptionTextProcessor();

        public static CaptionTextProcessor Instance => _instance;

        private CaptionTextProcessor()
        {
            _sentenceProcessor = new SentenceProcessor();
        }

        private readonly char[] _newlineChars = new[] { '\r', '\n' };
        private readonly StringBuilder _sharedBuilder = new(256);

        public string ProcessFullText(string fullText)
        {
            if (string.IsNullOrEmpty(fullText)) return string.Empty;

            lock (_sharedBuilder)
            {
                _sharedBuilder.Clear();
                _sharedBuilder.Append(fullText);
                
                // 优化的标点处理
                int length = _sharedBuilder.Length;
                for (int i = 0; i < length - 1; i++)
                {
                    char current = _sharedBuilder[i];
                    if (Array.IndexOf(PUNC_EOS, current) != -1)
                    {
                        // 检查并移除后续的换行符
                        int j = i + 1;
                        while (j < length && Array.IndexOf(_newlineChars, _sharedBuilder[j]) != -1)
                        {
                            _sharedBuilder.Remove(j, 1);
                            length--;
                        }
                    }
                }

                // 高效的空白字符处理
                bool inWhitespace = false;
                int writeIndex = 0;
                for (int readIndex = 0; readIndex < length; readIndex++)
                {
                    char c = _sharedBuilder[readIndex];
                    if (char.IsWhiteSpace(c))
                    {
                        if (!inWhitespace)
                        {
                            _sharedBuilder[writeIndex++] = ' ';
                            inWhitespace = true;
                        }
                    }
                    else
                    {
                        _sharedBuilder[writeIndex++] = c;
                        inWhitespace = false;
                    }
                }

                // 移除尾部空白
                while (writeIndex > 0 && char.IsWhiteSpace(_sharedBuilder[writeIndex - 1]))
                {
                    writeIndex--;
                }

                _sharedBuilder.Length = writeIndex;
                return _sharedBuilder.ToString();
            }
        }

        private readonly HashSet<string> _abbreviationCache = new(StringComparer.OrdinalIgnoreCase);
        private bool _abbreviationCacheInitialized;

        public int GetLastEOSIndex(string fullText)
        {
            if (string.IsNullOrEmpty(fullText)) return -1;

            // 延迟初始化缩写缓存
            if (!_abbreviationCacheInitialized)
            {
                lock (_abbreviationCache)
                {
                    if (!_abbreviationCacheInitialized)
                    {
                        foreach (var abbr in SentenceProcessor.ABBREVIATIONS)
                        {
                            _abbreviationCache.Add(abbr);
                        }
                        _abbreviationCacheInitialized = true;
                    }
                }
            }

            ReadOnlySpan<char> span = fullText.AsSpan();
            ReadOnlySpan<char> eosSpan = PUNC_EOS.AsSpan();
            
            // 快速路径：检查最后一个字符
            if (span.Length > 0 && eosSpan.Contains(span[^1]))
            {
                return span.Length - 1;
            }

            // 优化的反向搜索
            for (int i = span.Length - 1; i >= 0; i--)
            {
                if (eosSpan.Contains(span[i]))
                {
                    // 仅对句点进行缩写检查
                    if (span[i] == '.' && i > 0)
                    {
                        // 高效的缩写检查
                        bool isAbbreviation = false;
                        int start = Math.Max(0, i - 5); // 最长缩写词假设为5个字符
                        var checkSpan = span[start..(i + 1)];
                        
                        foreach (var abbr in _abbreviationCache)
                        {
                            if (checkSpan.ToString().EndsWith(abbr, StringComparison.OrdinalIgnoreCase))
                            {
                                isAbbreviation = true;
                                break;
                            }
                        }
                        
                        if (!isAbbreviation)
                        {
                            return i;
                        }
                    }
                    else
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        public string ExtractLatestCaption(string fullText, int lastEOSIndex)
        {
            if (string.IsNullOrEmpty(fullText)) return string.Empty;
            if (lastEOSIndex < -1) return fullText;

            ReadOnlySpan<char> span = fullText.AsSpan(lastEOSIndex + 1);
            string latestCaption = span.ToString().Trim();

            // 确保字幕长度适中
            if (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < App.Settings.MinCaptionBytes)
            {
                // 尝试包含前一个句子
                var prevEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(PUNC_EOS);
                if (prevEOSIndex >= 0)
                {
                    span = fullText.AsSpan(prevEOSIndex + 1);
                    latestCaption = span.ToString().Trim();
                }
            }

            // 如果字幕过长，尝试在自然停顿点截断
            if (Encoding.UTF8.GetByteCount(latestCaption) > MAX_CAPTION_BYTES)
            {
                var lastPause = _sentenceProcessor.FindLastNaturalPause(latestCaption);
                if (lastPause > 0)
                {
                    latestCaption = latestCaption[..lastPause].Trim();
                }
                else
                {
                    // 如果没有找到自然停顿点，使用逗号等标点
                    int commaIndex = latestCaption.LastIndexOfAny(PUNC_COMMA);
                    if (commaIndex > 0)
                    {
                        latestCaption = latestCaption[..commaIndex].Trim();
                    }
                }
            }

            return latestCaption;
        }

        public bool ShouldTriggerTranslation(string caption, ref int syncCount, int maxSyncInterval, int minTranslationLength)
        {
            var currentTime = DateTime.UtcNow;
            var timeSinceLastTranslation = currentTime - _lastTranslationTime;

            // 检查是否是完整句子
            bool isComplete = _sentenceProcessor.IsCompleteSentence(caption);
            
            // 检查是否有自然停顿
            bool hasNaturalPause = _sentenceProcessor.HasNaturalPause(caption);

            // 检查字幕长度是否达到最佳长度
            bool isOptimalLength = caption.Length >= GetOptimalCaptionLength(caption);

            // 检查是否超过最大等待时间
            bool maxWaitTimeExceeded = timeSinceLastTranslation >= MAX_WAIT_TIME;

            bool shouldTranslate = false;

            if (isComplete)
            {
                // 完整句子优先翻译
                shouldTranslate = true;
            }
            else if (hasNaturalPause && caption.Length >= minTranslationLength)
            {
                // 有自然停顿且长度足够
                shouldTranslate = true;
            }
            else if (isOptimalLength)
            {
                // 达到最佳长度
                shouldTranslate = true;
            }
            else if (syncCount > maxSyncInterval || maxWaitTimeExceeded)
            {
                // 超过同步间隔或最大等待时间
                shouldTranslate = true;
            }

            if (shouldTranslate)
            {
                syncCount = 0;
                _lastTranslationTime = currentTime;
            }

            return shouldTranslate;
        }
    }
}
