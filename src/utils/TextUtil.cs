using System.Text;
using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.utils
{
    public static class TextUtil
    {
        public static readonly char[] PUNC_EOS = ".?!。？！".ToCharArray();
        public static readonly char[] PUNC_COMMA = ",，、—\n".ToCharArray();
        
        public const int SHORT_THRESHOLD = 12;
        public const int MEDIUM_THRESHOLD = 32;
        public const int LONG_THRESHOLD = 160;
        public const int VERYLONG_THRESHOLD = 200;

        // 增加语义结束标记词
        private static readonly string[] SEMANTIC_EOS_WORDS = {
            "thank you", "thanks for", "in conclusion", "to summarize", 
            "谢谢", "总结", "结论", "最后", "以上", "ありがとう", "まとめ"
        };

        // 优化显示截断：使用更智能的方式
        public static string ShortenDisplaySentenceSmartly(string text, int maxByteLength)
        {
            if (Encoding.UTF8.GetByteCount(text) < maxByteLength)
                return text;
                
            // 首先尝试保留完整句子
            int lastEOSIndex = text.LastIndexOfAny(PUNC_EOS);
            if (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(text.Substring(lastEOSIndex + 1)) < maxByteLength)
                return text.Substring(lastEOSIndex + 1);
            
            // 其次尝试保留短语结构
            int lastCommaIndex = text.LastIndexOfAny(PUNC_COMMA);
            if (lastCommaIndex > 0 && Encoding.UTF8.GetByteCount(text.Substring(lastCommaIndex + 1)) < maxByteLength)
                return text.Substring(lastCommaIndex + 1);
            
            // 保留关键信息 - 优先保留开头和结尾，去除中间部分
            if (Encoding.UTF8.GetByteCount(text) >= maxByteLength * 2)
            {
                int prefixLength = maxByteLength / 3;
                int suffixLength = maxByteLength / 3;
                
                // 尝试在单词边界截断
                string prefix = GetTextWithinByteLimit(text, 0, prefixLength);
                string suffix = GetTextFromEndWithinByteLimit(text, suffixLength);
                
                return prefix + " [...] " + suffix;
            }
            
            // 最后使用简单的截断
            while (Encoding.UTF8.GetByteCount(text) >= maxByteLength)
            {
                int commaIndex = text.IndexOfAny(PUNC_COMMA);
                if (commaIndex < 0 || commaIndex + 1 >= text.Length)
                    break;
                text = text.Substring(commaIndex + 1);
            }
            return text;
        }
        
        // 原有的方法保留为兼容
        public static string ShortenDisplaySentence(string text, int maxByteLength)
        {
            return ShortenDisplaySentenceSmartly(text, maxByteLength);
        }
        
        // 获取指定字节长度范围内的文本，尽量在词边界截断
        private static string GetTextWithinByteLimit(string text, int startIndex, int byteLimit)
        {
            if (string.IsNullOrEmpty(text) || startIndex >= text.Length)
                return string.Empty;
                
            // 计算字节长度内可以包含的最大字符数
            int endIndex = startIndex;
            int byteCount = 0;
            
            while (endIndex < text.Length && byteCount < byteLimit)
            {
                char c = text[endIndex];
                byteCount += Encoding.UTF8.GetByteCount(new char[] { c });
                if (byteCount <= byteLimit)
                    endIndex++;
                else
                    break;
            }
            
            // 尝试在单词或语义边界截断
            if (endIndex < text.Length)
            {
                // 向前查找空格或标点作为边界
                for (int i = endIndex; i > startIndex; i--)
                {
                    if (char.IsWhiteSpace(text[i]) || IsEndPunctuation(text[i]))
                    {
                        endIndex = i + 1;
                        break;
                    }
                }
            }
            
            return text.Substring(startIndex, endIndex - startIndex);
        }
        
        // 从文本末尾获取指定字节长度的文本，尽量在词边界截断
        private static string GetTextFromEndWithinByteLimit(string text, int byteLimit)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
                
            // 计算字节长度内可以包含的最大字符数
            int startIndex = text.Length - 1;
            int byteCount = 0;
            
            while (startIndex >= 0 && byteCount < byteLimit)
            {
                char c = text[startIndex];
                byteCount += Encoding.UTF8.GetByteCount(new char[] { c });
                if (byteCount <= byteLimit)
                    startIndex--;
                else
                    break;
            }
            
            startIndex++; // 调整回有效位置
            
            // 尝试在单词或语义边界截断
            if (startIndex > 0)
            {
                // 向后查找空格或标点作为边界
                for (int i = startIndex; i < text.Length; i++)
                {
                    if (char.IsWhiteSpace(text[i]) || IsEndPunctuation(text[i]))
                    {
                        startIndex = i + 1;
                        break;
                    }
                }
            }
            
            return text.Substring(startIndex);
        }
        
        // 判断字符是否为结束标点
        private static bool IsEndPunctuation(char c)
        {
            return Array.IndexOf(PUNC_EOS, c) != -1 || Array.IndexOf(PUNC_COMMA, c) != -1;
        }

        public static string ReplaceNewlines(string text, int byteThreshold)
        {
            string[] splits = text.Split('\n');
            for (int i = 0; i < splits.Length; i++)
            {
                splits[i] = splits[i].Trim();
                if (i == splits.Length - 1)
                    continue;

                char lastChar = splits[i][^1];
                bool isCJ = (lastChar >= '\u4E00' && lastChar <= '\u9FFF') ||
                            (lastChar >= '\u3400' && lastChar <= '\u4DBF') ||
                            (lastChar >= '\u3040' && lastChar <= '\u30FF');
                bool isKorean = (lastChar >= '\uAC00' && lastChar <= '\uD7AF');

                if (Encoding.UTF8.GetByteCount(splits[i]) >= byteThreshold)
                    splits[i] += isCJ && !isKorean ? "。" : ". ";
                else
                    splits[i] += isCJ && !isKorean ? "——" : "—";
            }
            return string.Join("", splits);
        }

        // 改进相似度计算
        public static double Similarity(string text1, string text2)
        {
            if (string.IsNullOrEmpty(text1) || string.IsNullOrEmpty(text2))
                return 0.0;
                
            if (text1.StartsWith(text2) || text2.StartsWith(text1))
                return 1.0;
                
            // 首先比较长度差异
            double lengthRatio = (double)Math.Max(text1.Length, text2.Length) / 
                                Math.Min(text1.Length, text2.Length);
            if (lengthRatio > 1.5) // 长度差异过大
                return 0.0;
                
            // 计算编辑距离相似度
            int distance = LevenshteinDistance(text1, text2);
            int maxLen = Math.Max(text1.Length, text2.Length);
            double editSimilarity = (maxLen == 0) ? 1.0 : (1.0 - (double)distance / maxLen);
            
            // 计算单词重叠度（对英文和有空格的文本更有效）
            double wordOverlapSimilarity = CalculateWordOverlap(text1, text2);
            
            // 综合考虑两种相似度
            return (editSimilarity * 0.7) + (wordOverlapSimilarity * 0.3);
        }
        
        // 计算单词重叠度
        private static double CalculateWordOverlap(string text1, string text2)
        {
            var words1 = Regex.Split(text1.ToLower(), @"\W+").Where(w => !string.IsNullOrEmpty(w)).ToHashSet();
            var words2 = Regex.Split(text2.ToLower(), @"\W+").Where(w => !string.IsNullOrEmpty(w)).ToHashSet();
            
            if (words1.Count == 0 || words2.Count == 0)
                return 0.0;
                
            int overlapCount = words1.Intersect(words2).Count();
            return (double)overlapCount / Math.Max(words1.Count, words2.Count);
        }

        public static int LevenshteinDistance(string text1, string text2)
        {
            if (string.IsNullOrEmpty(text1))
                return string.IsNullOrEmpty(text2) ? 0 : text2.Length;
            if (string.IsNullOrEmpty(text2))
                return text1.Length;

            if (text1.Length > text2.Length)
                (text2, text1) = (text1, text2);

            int len1 = text1.Length;
            int len2 = text2.Length;

            int[] previous = new int[len1 + 1];
            int[] current = new int[len1 + 1];

            for (int i = 0; i <= len1; i++)
                previous[i] = i;
            for (int j = 1; j <= len2; j++)
            {
                current[0] = j;
                for (int i = 1; i <= len1; i++)
                {
                    int cost = (text1[i - 1] == text2[j - 1]) ? 0 : 1;
                    current[i] = Math.Min(
                        Math.Min(current[i - 1] + 1, previous[i] + 1),
                        previous[i - 1] + cost);
                }
                (current, previous) = (previous, current);
            }

            return previous[len1];
        }

        // 判断文本是否具有足够的语义完整性，适合翻译
        public static bool IsMeaningfulForTranslation(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
                
            // 检查是否以结束标点结尾
            if (Array.IndexOf(PUNC_EOS, text[^1]) != -1)
                return true;
                
            // 检查是否包含语义结束词
            foreach (var word in SEMANTIC_EOS_WORDS)
            {
                if (text.ToLower().Contains(word))
                    return true;
            }
            
            // 检查长度是否足够长，可能是一个完整的表达
            if (Encoding.UTF8.GetByteCount(text) > LONG_THRESHOLD)
                return true;
                
            return false;
        }
        
        // 提取有意义的文本段落，改进句子分割
        public static string ExtractMeaningfulSegment(string fullText)
        {
            if (string.IsNullOrEmpty(fullText))
                return string.Empty;
                
            // 首先检查最后一个完整句子
            int lastEOSIndex;
            if (Array.IndexOf(PUNC_EOS, fullText[^1]) != -1)
                lastEOSIndex = fullText[0..^1].LastIndexOfAny(PUNC_EOS);
            else
                lastEOSIndex = fullText.LastIndexOfAny(PUNC_EOS);
                
            string latestCaption = fullText.Substring(lastEOSIndex + 1);
            
            // 如果最后一段太短，尝试整合前面的内容
            if (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < SHORT_THRESHOLD)
            {
                // 找到前一个句子结束标点
                lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(PUNC_EOS);
                latestCaption = fullText.Substring(lastEOSIndex + 1);
            }
            
            // 检查语义完整性
            for (int i = 0; i < SEMANTIC_EOS_WORDS.Length; i++)
            {
                int wordIndex = fullText.ToLower().LastIndexOf(SEMANTIC_EOS_WORDS[i]);
                if (wordIndex >= 0 && wordIndex > lastEOSIndex)
                {
                    // 找到一个语义结束词，从这个词之后开始截取
                    int start = wordIndex + SEMANTIC_EOS_WORDS[i].Length;
                    latestCaption = fullText.Substring(start);
                    break;
                }
            }
            
            return latestCaption;
        }
        
        // 确保句子的完整性
        public static string EnsureSentenceCompleteness(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
                
            // 如果已经是完整句子，直接返回
            if (Array.IndexOf(PUNC_EOS, text[^1]) != -1)
                return text;
                
            // 分析是否有足够的上下文确定句子边界
            int lastEOS = text.LastIndexOfAny(PUNC_EOS);
            if (lastEOS != -1 && lastEOS < text.Length - SHORT_THRESHOLD)
            {
                // 如果最后的句尾标点后有足够长的内容，可能是新句子的开始
                return text.Substring(lastEOS + 1);
            }
            
            return text;
        }
        
        // 获取两个文本之间新增的内容
        public static string GetAddedContent(string oldText, string newText)
        {
            if (string.IsNullOrEmpty(oldText))
                return newText;
                
            if (newText.StartsWith(oldText))
                return newText.Substring(oldText.Length);
                
            // 尝试找到公共前缀的末尾
            int i = 0;
            while (i < oldText.Length && i < newText.Length && oldText[i] == newText[i])
                i++;
                
            return newText.Substring(i);
        }

        public static string NormalizeUrl(string url)
        {
            var protocolMatch = Regex.Match(url, @"^(https?:\/\/)");
            string protocol = protocolMatch.Success ? protocolMatch.Value : "";

            string rest = url.Substring(protocol.Length);
            rest = Regex.Replace(rest, @"\/{2,}", "/");
            rest = rest.TrimEnd('/');

            return protocol + rest;
        }
    }
}