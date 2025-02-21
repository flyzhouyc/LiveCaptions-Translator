using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;

namespace LiveCaptionsTranslator.models.CaptionProcessing
{
    public static class SentenceProcessor
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
            @"[,;:\-—]\s|\.{3}\s|\n|\r\n",
            RegexOptions.Compiled
        );

        /// <summary>
        /// 判断一个文本是否是一个完整的句子
        /// </summary>
        public static bool IsCompleteSentence(string text)
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
        public static bool HasNaturalPause(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return NATURAL_PAUSE_REGEX.IsMatch(text);
        }

        /// <summary>
        /// 将文本拆分为完整句子
        /// </summary>
        public static List<string> SplitIntoCompleteSentences(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();

            var sentences = new List<string>();
            var currentSentence = new StringBuilder();
            var words = text.Split(' ');

            foreach (var word in words)
            {
                currentSentence.Append(word).Append(' ');
                var current = currentSentence.ToString().Trim();

                if (IsCompleteSentence(current))
                {
                    sentences.Add(current);
                    currentSentence.Clear();
                }
                else if (HasNaturalPause(current) && currentSentence.Length > 50)
                {
                    // 如果遇到自然停顿且积累了足够长度的文本，也考虑拆分
                    sentences.Add(current);
                    currentSentence.Clear();
                }
            }

            if (currentSentence.Length > 0)
            {
                sentences.Add(currentSentence.ToString().Trim());
            }

            return sentences;
        }

        /// <summary>
        /// 合并句子片段，直到形成完整句子
        /// </summary>
        public static string? AccumulateSentence(string currentText, string newFragment, int maxLength = 300)
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
        public static int FindLastNaturalPause(string text)
        {
            var match = NATURAL_PAUSE_REGEX.Match(text);
            var lastMatch = match;
            while (match.Success)
            {
                lastMatch = match;
                match = match.NextMatch();
            }
            return lastMatch.Success ? lastMatch.Index : -1;
        }
    }
}
