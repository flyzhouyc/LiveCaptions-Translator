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
        
        // 预编译正则表达式以提高性能
        private static readonly Regex UppercaseDotPattern = new Regex(@"(?<=[A-Z])\s*\.\s*(?=[A-Z])", RegexOptions.Compiled);
        private static readonly Regex PunctuationPattern = new Regex(@"\s*([.!?,])\s*", RegexOptions.Compiled);
        private static readonly Regex CJKPunctuationPattern = new Regex(@"\s*([。！？，、])\s*", RegexOptions.Compiled);

        public static string ShortenDisplaySentence(string text, int maxByteLength)
        {
            if (Encoding.UTF8.GetByteCount(text) < maxByteLength)
                return text;

            // 在超出长度时使用查找而非多次截断，提高效率
            int startPos = 0;
            while (Encoding.UTF8.GetByteCount(text.Substring(startPos)) >= maxByteLength)
            {
                int commaIndex = text.IndexOfAny(PUNC_COMMA, startPos);
                if (commaIndex < 0 || commaIndex + 1 >= text.Length)
                    break;
                startPos = commaIndex + 1;
            }
            
            return text.Substring(startPos);
        }

        public static string ReplaceNewlines(string text, int byteThreshold)
        {
            if (!text.Contains('\n'))
                return text;
                
            string[] splits = text.Split('\n');
            StringBuilder result = new StringBuilder(text.Length);
            
            for (int i = 0; i < splits.Length; i++)
            {
                string split = splits[i].Trim();
                if (string.IsNullOrEmpty(split))
                    continue;
<<<<<<< HEAD
                    
                if (i > 0)
                {
                    // 检查上一行是否已经以标点结尾
                    char lastChar = result[result.Length - 1];
                    if (Array.IndexOf(PUNC_EOS, lastChar) == -1 && Array.IndexOf(PUNC_COMMA, lastChar) == -1)
                    {
                        if (Encoding.UTF8.GetByteCount(split) >= byteThreshold)
                        {
                            bool isCJ = IsChineseOrJapanese(split);
                            result.Append(isCJ ? "。" : ". ");
                        }
                        else
                        {
                            bool isCJ = IsChineseOrJapanese(split);
                            result.Append(isCJ ? "——" : "—");
                        }
                    }
                }
                
                result.Append(split);
=======

                char lastChar = splits[i][^1];
                bool isCJ = (lastChar >= '\u4E00' && lastChar <= '\u9FFF') ||
                            (lastChar >= '\u3400' && lastChar <= '\u4DBF') ||
                            (lastChar >= '\u3040' && lastChar <= '\u30FF');
                bool isKorean = (lastChar >= '\uAC00' && lastChar <= '\uD7AF');

                if (Encoding.UTF8.GetByteCount(splits[i]) >= byteThreshold)
                    splits[i] += isCJ && !isKorean ? "。" : ". ";
                else
                    splits[i] += isCJ && !isKorean ? "——" : "—";
>>>>>>> b6661e87da83c8b28c6c1afb387deca63143704e
            }
            
            return result.ToString();
        }

<<<<<<< HEAD
        // 检查是否为中文或日文字符
        private static bool IsChineseOrJapanese(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
                
            char lastChar = text[^1];
            return (lastChar >= '\u4E00' && lastChar <= '\u9FFF') ||
                   (lastChar >= '\u3400' && lastChar <= '\u4DBF') ||
                   (lastChar >= '\u3040' && lastChar <= '\u30FF');
        }

        private static bool IsKorean(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
                
            char lastChar = text[^1];
            return (lastChar >= '\uAC00' && lastChar <= '\uD7AF');
        }

        // 改进的正则替换方法，减少字符串分配
        public static string ProcessTextWithRegex(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
                
            // 1. 移除大写字母之间的点
            string result = UppercaseDotPattern.Replace(text, "");
            
            // 2. 处理标点周围的空白
            result = PunctuationPattern.Replace(result, "$1 ");
            result = CJKPunctuationPattern.Replace(result, "$1");
            
            return result;
        }

=======
>>>>>>> b6661e87da83c8b28c6c1afb387deca63143704e
        public static double Similarity(string text1, string text2)
        {
            // 快速路径检查
            if (text1 == text2)
                return 1.0;
                
            if (text1.Length == 0 || text2.Length == 0)
                return 0.0;
                
            if (text1.StartsWith(text2) || text2.StartsWith(text1))
                return 1.0;
                
            // 如果长度相差太多，直接返回低相似度
            int lenDiff = Math.Abs(text1.Length - text2.Length);
            int maxLen = Math.Max(text1.Length, text2.Length);
            if (lenDiff > maxLen / 2)
                return 0.2;
                
            // 使用更高效的相似度计算
            return OptimizedLevenshteinSimilarity(text1, text2);
        }

        private static double OptimizedLevenshteinSimilarity(string text1, string text2)
        {
            // 优化的编辑距离计算
            int distance = LevenshteinDistance(text1, text2);
            int maxLen = Math.Max(text1.Length, text2.Length);
            return (maxLen == 0) ? 1.0 : (1.0 - (double)distance / maxLen);
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

            // 仅使用两行来减少内存使用
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

        // 优化的URL标准化
        private static readonly Regex ProtocolRegex = new Regex(@"^(https?:\/\/)", RegexOptions.Compiled);
        private static readonly Regex MultipleSlashRegex = new Regex(@"\/{2,}", RegexOptions.Compiled);
        
        public static string NormalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;
                
            var protocolMatch = ProtocolRegex.Match(url);
            string protocol = protocolMatch.Success ? protocolMatch.Value : "";

            string rest = url.Substring(protocol.Length);
            rest = MultipleSlashRegex.Replace(rest, "/");
            rest = rest.TrimEnd('/');

            return protocol + rest;
        }
    }
}