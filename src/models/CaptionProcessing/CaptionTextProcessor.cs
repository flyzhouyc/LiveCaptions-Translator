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
        private int GetOptimalCaptionLength(string text)
        {
            if (!App.Settings.UseAutomaticOptimalLength)
            {
                return App.Settings.OptimalCaptionLength;
            }

            // 基础长度基于目标语言
            int baseLength = App.Settings.TargetLanguage switch
            {
                "zh-CN" or "zh-TW" or "ja" or "ko" => 75,  // 东亚语言字符较少
                "en" => 120,                                // 英语需要更多字符
                _ => 100                                    // 其他语言默认值
            };

            // 根据语速调整
            if (_sentenceProcessor is MLSentenceProcessor mlProcessor)
            {
                var speechRate = mlProcessor.GetCurrentSpeechRate();
                if (speechRate > 0)
                {
                    // 语速快时减少长度，语速慢时增加长度
                    double speedFactor = 3.0 / speechRate; // 3词/秒作为基准
                    baseLength = (int)(baseLength * Math.Clamp(speedFactor, 0.7, 1.5));
                }
            }

            // 应用用户自定义的调整因子
            baseLength = (int)(baseLength * App.Settings.OptimalLengthAdjustmentFactor);

            // 确保在合理范围内
            return Math.Clamp(baseLength, 50, 200);
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

        public string ProcessFullText(string fullText)
        {
            if (string.IsNullOrEmpty(fullText)) return string.Empty;

            StringBuilder sb = new StringBuilder(fullText);
            
            // 处理换行符和标点符号
            foreach (char eos in PUNC_EOS)
            {
                int index = sb.ToString().IndexOf($"{eos}\n");
                while (index != -1)
                {
                    sb[index + 1] = eos;
                    sb.Remove(index, 1);
                    index = sb.ToString().IndexOf($"{eos}\n");
                }
            }

            // 处理多余的空白字符
            string processed = sb.ToString().Trim();
            processed = Regex.Replace(processed, @"\s+", " ");
            
            return processed;
        }

        public int GetLastEOSIndex(string fullText)
        {
            if (string.IsNullOrEmpty(fullText)) return -1;

            ReadOnlySpan<char> span = fullText.AsSpan();
            
            // 优化：如果最后一个字符是句子结束符，直接返回
            if (span.Length > 0 && PUNC_EOS.AsSpan().Contains(span[^1]))
            {
                return span.Length - 1;
            }

            // 从后向前查找最后一个句子结束符
            for (int i = span.Length - 1; i >= 0; i--)
            {
                if (PUNC_EOS.AsSpan().Contains(span[i]))
                {
                    // 检查是否是缩写词的一部分
                    bool isAbbreviation = false;
                    if (span[i] == '.' && i > 0)
                    {
                        var potentialAbbr = span[(i-1)..].ToString();
                        foreach (var abbr in SentenceProcessor.ABBREVIATIONS)
                        {
                            if (potentialAbbr.StartsWith(abbr, StringComparison.OrdinalIgnoreCase))
                            {
                                isAbbreviation = true;
                                break;
                            }
                        }
                    }
                    if (!isAbbreviation)
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
