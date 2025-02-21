using System;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;

namespace LiveCaptionsTranslator.models.CaptionProcessing
{
    public static class SentenceProcessor
    {
        // 英语句子结束标点符号
        private static readonly char[] SENTENCE_ENDINGS = { '.', '!', '?' };

        // 检测句子是否完整的正则表达式
        private static readonly Regex COMPLETE_SENTENCE_REGEX = new Regex(
            @"^[A-Z].*?[.!?]$", 
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

            // 使用正则表达式进一步验证
            return COMPLETE_SENTENCE_REGEX.IsMatch(text);
        }

        /// <summary>
        /// 将文本拆分为完整句子
        /// </summary>
        public static List<string> SplitIntoCompleteSentences(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();

            // 使用正则表达式拆分句子
            var sentences = Regex.Split(text, @"(?<=[.!?])\s+")
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            return sentences.Where(IsCompleteSentence).ToList();
        }

        /// <summary>
        /// 合并句子片段，直到形成完整句子
        /// </summary>
        public static string? AccumulateSentence(string currentText, string newFragment, int maxLength = 300)
        {
            if (string.IsNullOrWhiteSpace(newFragment)) return null;

            string combinedText = (currentText + " " + newFragment).Trim();

            // 如果组合后的文本超过最大长度，重置
            if (combinedText.Length > maxLength)
            {
                combinedText = newFragment;
            }

            // 如果是完整句子，返回
            return IsCompleteSentence(combinedText) ? combinedText : null;
        }
    }
}
