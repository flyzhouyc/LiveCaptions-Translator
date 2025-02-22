using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;

namespace LiveCaptionsTranslator.models.CaptionProcessing
{
    public class SentenceProcessor
    {
        // 英语句子结束标点符号
        private static readonly char[] SENTENCE_ENDINGS = { '.', '!', '?' };

        // 常见缩写词列表
        public static readonly HashSet<string> ABBREVIATIONS = new HashSet<string> 
        { 
            "Mr.", "Mrs.", "Ms.", "Dr.", "Prof.", "Sr.", "Jr.", "Ltd.", "Co.", "Inc.", 
            "St.", "Ave.", "Blvd.", "Rd.", "Ph.D.", "M.D.", "B.A.", "M.A.", "i.e.", "e.g.",
            "etc.", "vs.", "a.m.", "p.m.", "U.S.", "U.K.", "E.U."
        };

        // 检测句子是否完整的正则表达式
        private static readonly Regex COMPLETE_SENTENCE_REGEX = new Regex(
            @"^(?:[A-Z][^.!?]*?|""[A-Z][^.!?]*?""|'[A-Z][^.!?]*?'|\([A-Z][^.!?]*?\)|\[[A-Z][^.!?]*?\])[.!?](?:\s*[""'\)\]])*$",
            RegexOptions.Compiled
        );

        // 检测自然停顿的正则表达式
        private static readonly Regex NATURAL_PAUSE_REGEX = new Regex(
            @"[,;:\-—]\s|\.{3}\s|\n|\r\n|\s\-\s|\s\–\s|\s\…\s|\(\s|\)\s|\s\(|\s\)|\s\-\-\s",
            RegexOptions.Compiled
        );

        // 检测语音停顿的正则表达式
        private static readonly Regex SPEECH_PAUSE_REGEX = new Regex(
            @"\s{2,}|(?<=\w)\s+(?=\w{3,})|(?<=\w{3,})\s+(?=\w)",
            RegexOptions.Compiled
        );

        // 最大累积时间
        private static readonly TimeSpan MAX_ACCUMULATION_TIME = TimeSpan.FromSeconds(5);
        private DateTime _lastSplitTime = DateTime.MinValue;

        /// <summary>
        /// 判断一个文本是否是一个完整的句子
        /// </summary>
        public virtual bool IsCompleteSentence(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            // 去除首尾空白
            text = text.Trim();

            // 检查是否以句子结束标点结尾
            if (!SENTENCE_ENDINGS.Contains(text[^1])) return false;

            // 检查是否是缩写词
            foreach (var abbr in ABBREVIATIONS)
            {
                if (text.EndsWith(abbr, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // 使用正则表达式进一步验证
            return COMPLETE_SENTENCE_REGEX.IsMatch(text);
        }

        /// <summary>
        /// 检查是否存在自然停顿点
        /// </summary>
        public virtual bool HasNaturalPause(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            
            // 检查标准的自然停顿
            if (NATURAL_PAUSE_REGEX.IsMatch(text)) return true;
            
            // 检查语音停顿
            if (SPEECH_PAUSE_REGEX.IsMatch(text)) return true;
            
            // 检查时间累积
            if (DateTime.Now - _lastSplitTime > MAX_ACCUMULATION_TIME)
            {
                _lastSplitTime = DateTime.Now;
                return true;
            }
            
            return false;
        }

        /// <summary>
        /// 将文本拆分为完整句子
        /// </summary>
        public virtual List<string> SplitIntoCompleteSentences(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();

            var sentences = new List<string>();
            var currentSentence = new StringBuilder();
            var words = text.Split(' ');
            var currentLength = 0;
            var lastSplitTime = DateTime.Now;

            foreach (var word in words)
            {
                currentSentence.Append(word).Append(' ');
                var current = currentSentence.ToString().Trim();
                currentLength += word.Length;

                bool shouldSplit = false;
                
                // 检查完整句子
                if (IsCompleteSentence(current))
                {
                    shouldSplit = true;
                }
                // 检查自然停顿
                else if (HasNaturalPause(current))
                {
                    // 慢速时降低长度要求
                    int minLength = DateTime.Now.Subtract(lastSplitTime).TotalSeconds > 2 ? 20 : 40;
                    shouldSplit = currentLength >= minLength;
                }
                // 检查时间累积
                else if (DateTime.Now - lastSplitTime > MAX_ACCUMULATION_TIME && currentLength > 10)
                {
                    shouldSplit = true;
                }

                if (shouldSplit)
                {
                    sentences.Add(current);
                    currentSentence.Clear();
                    currentLength = 0;
                    lastSplitTime = DateTime.Now;
                    _lastSplitTime = lastSplitTime;
                }
            }

            if (currentSentence.Length > 0)
            {
                var remaining = currentSentence.ToString().Trim();
                if (!string.IsNullOrEmpty(remaining))
                {
                    sentences.Add(remaining);
                }
            }

            return sentences;
        }

        /// <summary>
        /// 合并句子片段，直到形成完整句子
        /// </summary>
        public virtual string? AccumulateSentence(string currentText, string newFragment, int maxLength = 300)
        {
            if (string.IsNullOrWhiteSpace(newFragment)) return null;

            string combinedText = (currentText + " " + newFragment).Trim();

            // 如果组合后的文本超过最大长度，检查是否可以在自然停顿点截断
            if (combinedText.Length > maxLength)
            {
                var lastPause = FindLastNaturalPause(combinedText);
                if (lastPause > 0)
                {
                    return combinedText[..lastPause].Trim();
                }
                combinedText = newFragment;
            }

            // 检查是否形成完整句子或达到自然停顿点
            if (IsCompleteSentence(combinedText) || 
                (HasNaturalPause(combinedText) && combinedText.Length > 50))
            {
                return combinedText;
            }

            return null;
        }

        /// <summary>
        /// 查找最后一个自然停顿点的位置
        /// </summary>
        public virtual int FindLastNaturalPause(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;

            // 首先检查标准的自然停顿
            var standardMatch = NATURAL_PAUSE_REGEX.Match(text);
            var lastStandardMatch = standardMatch;
            while (standardMatch.Success)
            {
                lastStandardMatch = standardMatch;
                standardMatch = standardMatch.NextMatch();
            }

            // 然后检查语音停顿
            var speechMatch = SPEECH_PAUSE_REGEX.Match(text);
            var lastSpeechMatch = speechMatch;
            while (speechMatch.Success)
            {
                lastSpeechMatch = speechMatch;
                speechMatch = speechMatch.NextMatch();
            }

            // 选择最后出现的停顿点
            int standardIndex = lastStandardMatch.Success ? lastStandardMatch.Index : -1;
            int speechIndex = lastSpeechMatch.Success ? lastSpeechMatch.Index : -1;

            if (standardIndex >= 0 && speechIndex >= 0)
            {
                return Math.Max(standardIndex, speechIndex);
            }
            return Math.Max(standardIndex, speechIndex);
        }
    }
}
