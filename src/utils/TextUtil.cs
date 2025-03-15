using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace LiveCaptionsTranslator.utils
{
    public static class TextUtil
    {
        // 标点符号定义
        public static readonly char[] PUNC_EOS = ".?!。？！".ToCharArray();
        public static readonly char[] PUNC_COMMA = ",，、—\n".ToCharArray();
        // 使用数组初始化方式避免字符串转义问题
        public static readonly char[] PUNC_QUOTES = new char[] { 
            '"', '\'', '"', '"', ''', ''', '「', '」', '『', '』', 
            '(', ')', '（', '）', '[', ']', '【', '】', '{', '}'
        };
        
        // 长度阈值常量
        public const int SHORT_THRESHOLD = 12;
        public const int MEDIUM_THRESHOLD = 32;
        public const int LONG_THRESHOLD = 160;
        public const int VERYLONG_THRESHOLD = 200;
        
        // ====== 优化1: 预编译正则表达式 ======
        private static readonly Regex UppercaseDotPattern = new Regex(@"(?<=[A-Z])\s*\.\s*(?=[A-Z])", RegexOptions.Compiled);
        private static readonly Regex PunctuationPattern = new Regex(@"\s*([.!?,])\s*", RegexOptions.Compiled);
        private static readonly Regex CJKPunctuationPattern = new Regex(@"\s*([。！？，、])\s*", RegexOptions.Compiled);
        private static readonly Regex SentenceBoundaryPattern = new Regex(
            @"(?<=[.!?。！？])\s+(?=[A-Z\p{Lu}])", RegexOptions.Compiled);
        private static readonly Regex MultipleSpacesPattern = new Regex(@"\s{2,}", RegexOptions.Compiled);
        private static readonly Regex AbbreviationPattern = new Regex(
            @"\b(Mr|Mrs|Dr|Prof|Inc|Ltd|Co|Corp|vs|etc|i\.e|e\.g)\.", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        // URL标准化
        private static readonly Regex ProtocolRegex = new Regex(@"^(https?:\/\/)", RegexOptions.Compiled);
        private static readonly Regex MultipleSlashRegex = new Regex(@"\/{2,}", RegexOptions.Compiled);
        
        // ====== 优化2: 语言检测缓存 ======
        private static readonly ConcurrentDictionary<char, int> CharLanguageCache = new ConcurrentDictionary<char, int>();
        
        // ====== 优化3: 相似度计算缓存 ======
        private static readonly ConcurrentDictionary<string, double> SimilarityCache = 
            new ConcurrentDictionary<string, double>(StringComparer.Ordinal);

        /// <summary>
        /// 缩短显示句子，确保不超过最大字节长度
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static string ShortenDisplaySentence(string text, int maxByteLength)
        {
            if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) < maxByteLength)
                return text;

            // 使用Span优化字符串处理，减少内存分配
            ReadOnlySpan<char> textSpan = text.AsSpan();
            int startPos = 0;
            
            // 二分查找最佳截断点，避免多次截断和编码转换
            int low = 0;
            int high = text.Length;
            
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                
                if (Encoding.UTF8.GetByteCount(textSpan.Slice(mid)) < maxByteLength)
                    high = mid;
                else
                    low = mid + 1;
            }
            
            startPos = low;
            
            // 如果找到有效位置，尝试在标点符号处截断
            if (startPos > 0 && startPos < text.Length - 10)
            {
                // 向前寻找最近的标点符号
                for (int i = startPos; i < Math.Min(startPos + 20, text.Length); i++)
                {
                    if (IsPunctuation(text[i]))
                    {
                        startPos = i + 1;
                        break;
                    }
                }
            }
            
            return startPos >= text.Length ? text : text.Substring(startPos);
        }

        /// <summary>
        /// 替换文本中的换行符，根据上下文智能添加标点
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static string ReplaceNewlines(string text, int byteThreshold)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains('\n'))
                return text;
                
            string[] splits = text.Split('\n');
            var result = new StringBuilder(text.Length);
            
            for (int i = 0; i < splits.Length; i++)
            {
                string split = splits[i].Trim();
                if (string.IsNullOrEmpty(split))
                    continue;
                    
                if (i > 0)
                {
                    // 检查上一行是否已经以标点结尾
                    if (result.Length > 0)
                    {
                        char lastChar = result[result.Length - 1];
                        if (!IsPunctuation(lastChar))
                        {
                            // 根据文本长度和语言确定使用的标点
                            if (Encoding.UTF8.GetByteCount(split) >= byteThreshold)
                            {
                                bool isCJ = DetectLanguage(split) == LanguageType.CJK;
                                result.Append(isCJ ? "。" : ". ");
                            }
                            else
                            {
                                bool isCJ = DetectLanguage(split) == LanguageType.CJK;
                                result.Append(isCJ ? "，" : ", ");
                            }
                        }
                    }
                }
                
                result.Append(split);
            }
            
            return result.ToString();
        }

        /// <summary>
        /// 智能分段：将长文本分割成有意义的句子
        /// </summary>
        public static List<string> SmartSegmentation(string text)
        {
            if (string.IsNullOrEmpty(text))
                return new List<string>();
                
            // 预处理替换缩写中的句点，防止错误分割
            string processedText = AbbreviationPattern.Replace(text, m => m.Value.Replace(".", "@@DOT@@"));
            
            // 检测语言类型
            var languageType = DetectLanguage(text);
            
            // 根据语言类型选择分段策略
            string[] segments;
            if (languageType == LanguageType.CJK)
            {
                // 中日韩文本分段，通过句末标点符号分割
                segments = Regex.Split(processedText, @"(?<=[。！？])")
                                .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            }
            else
            {
                // 西文分段，考虑大写字母开头的新句子
                segments = SentenceBoundaryPattern.Split(processedText)
                                .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            }
            
            // 后处理还原缩写中的句点
            return segments.Select(s => s.Replace("@@DOT@@", ".").Trim()).ToList();
        }

        /// <summary>
        /// 合并短句段为更有意义的单位
        /// </summary>
        public static List<string> MergeShortSegments(List<string> segments, int minLength = 15)
        {
            if (segments == null || segments.Count <= 1)
                return segments ?? new List<string>();
                
            var result = new List<string>();
            StringBuilder current = new StringBuilder();
            
            foreach (var segment in segments)
            {
                if (current.Length == 0)
                {
                    current.Append(segment);
                }
                else if (current.Length < minLength || Encoding.UTF8.GetByteCount(segment) < SHORT_THRESHOLD)
                {
                    // 如果当前累积文本较短，或新段落较短，则合并
                    current.Append(" ").Append(segment);
                }
                else
                {
                    // 当前段落足够长，添加到结果并重置
                    result.Add(current.ToString());
                    current.Clear().Append(segment);
                }
            }
            
            // 添加最后一个段落
            if (current.Length > 0)
                result.Add(current.ToString());
                
            return result;
        }

        /// <summary>
        /// 改进的文本处理方法，使用正则表达式
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static string ProcessTextWithRegex(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
                
            // 1. 移除缩写之外的大写字母之间的点
            string result = UppercaseDotPattern.Replace(text, "");
            
            // 2. 处理标点周围的空白
            result = PunctuationPattern.Replace(result, "$1 ");
            result = CJKPunctuationPattern.Replace(result, "$1");
            
            // 3. 合并多个空格
            result = MultipleSpacesPattern.Replace(result, " ");
            
            return result;
        }

        /// <summary>
        /// 检测文本主要语言类型
        /// </summary>
        public static LanguageType DetectLanguage(string text)
        {
            if (string.IsNullOrEmpty(text))
                return LanguageType.Other;
                
            // 分析文本中的字符分布
            int cjkCount = 0;
            int koreanCount = 0;
            int latinCount = 0;
            int otherCount = 0;
            
            foreach (char c in text)
            {
                // 使用缓存避免重复计算
                int charType = CharLanguageCache.GetOrAdd(c, ch => {
                    if (IsChineseOrJapaneseChar(ch)) return 1;
                    if (IsKoreanChar(ch)) return 2;
                    if (IsLatinChar(ch)) return 3;
                    return 0;
                });
                
                switch (charType)
                {
                    case 1: cjkCount++; break;
                    case 2: koreanCount++; break;
                    case 3: latinCount++; break;
                    default: otherCount++; break;
                }
            }
            
            // 确定主要语言
            int total = cjkCount + koreanCount + latinCount;
            if (total == 0) return LanguageType.Other;
            
            if (cjkCount > total * 0.5) return LanguageType.CJK;
            if (koreanCount > total * 0.5) return LanguageType.Korean;
            if (latinCount > total * 0.5) return LanguageType.Latin;
            
            return LanguageType.Mixed;
        }

        /// <summary>
        /// 计算两个文本的相似度，使用混合算法，优化短文本和长文本的相似度计算
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static double Similarity(string text1, string text2)
        {
            // 快速路径检查
            if (text1 == text2)
                return 1.0;
                
            if (string.IsNullOrEmpty(text1) || string.IsNullOrEmpty(text2))
                return 0.0;
                
            if (text1.StartsWith(text2) || text2.StartsWith(text1))
                return 1.0;
            
            // 使用缓存避免重复计算
            string cacheKey = text1.Length <= text2.Length ? 
                              $"{text1}|{text2}" : $"{text2}|{text1}";
            
            if (SimilarityCache.TryGetValue(cacheKey, out double cachedSimilarity))
                return cachedSimilarity;
                
            // 如果长度相差太多，直接返回低相似度
            int lenDiff = Math.Abs(text1.Length - text2.Length);
            int maxLen = Math.Max(text1.Length, text2.Length);
            if (lenDiff > maxLen / 2)
                return 0.2;
            
            double similarity;
            
            // 根据文本长度选择不同算法
            if (maxLen < 20)
            {
                // 短文本使用Jaro-Winkler距离，对拼写相似的短文本更敏感
                similarity = CalculateJaroWinklerSimilarity(text1, text2);
            }
            else if (maxLen < 50)
            {
                // 中等长度文本使用改进的Levenshtein距离 + N-gram
                double levenshtein = OptimizedLevenshteinSimilarity(text1, text2);
                double ngram = CalculateNGramSimilarity(text1, text2, 2);
                similarity = (levenshtein * 0.7) + (ngram * 0.3); // 混合结果
            }
            else
            {
                // 长文本主要使用N-gram相似度，计算更高效
                similarity = CalculateNGramSimilarity(text1, text2, 3);
            }
            
            // 将结果存入缓存
            SimilarityCache.TryAdd(cacheKey, similarity);
            
            // 保持缓存大小合理
            if (SimilarityCache.Count > 500)
            {
                // 随机移除一些缓存项
                foreach (var key in SimilarityCache.Keys.Take(100))
                {
                    SimilarityCache.TryRemove(key, out _);
                }
            }
            
            return similarity;
        }

        /// <summary>
        /// Jaro-Winkler相似度算法，对短文本和拼写错误更敏感
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static double CalculateJaroWinklerSimilarity(string s1, string s2)
        {
            // Jaro距离计算
            int len1 = s1.Length;
            int len2 = s2.Length;
            
            if (len1 == 0 || len2 == 0)
                return 0.0;
                
            // 匹配窗口大小
            int matchDistance = Math.Max(len1, len2) / 2 - 1;
            if (matchDistance < 0) matchDistance = 0;
            
            bool[] s1Matches = new bool[len1];
            bool[] s2Matches = new bool[len2];
            
            // 计算匹配字符数
            int matches = 0;
            for (int i = 0; i < len1; i++)
            {
                int start = Math.Max(0, i - matchDistance);
                int end = Math.Min(i + matchDistance + 1, len2);
                
                for (int j = start; j < end; j++)
                {
                    if (!s2Matches[j] && s1[i] == s2[j])
                    {
                        s1Matches[i] = true;
                        s2Matches[j] = true;
                        matches++;
                        break;
                    }
                }
            }
            
            if (matches == 0)
                return 0.0;
                
            // 计算转位字符数
            int transpositions = 0;
            int k = 0;
            
            for (int i = 0; i < len1; i++)
            {
                if (s1Matches[i])
                {
                    while (!s2Matches[k]) k++;
                    
                    if (s1[i] != s2[k])
                        transpositions++;
                        
                    k++;
                }
            }
            
            transpositions /= 2;
            
            // 计算Jaro距离
            double jaro = ((double)matches / len1 + 
                          (double)matches / len2 + 
                          (double)(matches - transpositions) / matches) / 3.0;
            
            // Jaro-Winkler增强：对共享前缀的字符串给予更高权重
            int prefixLength = 0;
            int maxPrefixLength = Math.Min(4, Math.Min(len1, len2));
            
            while (prefixLength < maxPrefixLength && s1[prefixLength] == s2[prefixLength])
                prefixLength++;
                
            // 缩放因子，标准值为0.1
            double scalingFactor = 0.1;
            
            return jaro + (prefixLength * scalingFactor * (1.0 - jaro));
        }

        /// <summary>
        /// 计算N-Gram相似度，适用于长文本
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static double CalculateNGramSimilarity(string s1, string s2, int n)
        {
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
                return 0.0;
                
            if (s1.Length < n || s2.Length < n)
                return OptimizedLevenshteinSimilarity(s1, s2);
                
            // 生成n-gram集合
            var ngrams1 = GenerateNGrams(s1, n);
            var ngrams2 = GenerateNGrams(s2, n);
            
            // 计算交集大小
            int intersection = ngrams1.Intersect(ngrams2).Count();
            
            // 计算并集大小
            int union = ngrams1.Count + ngrams2.Count - intersection;
            
            // Jaccard系数作为相似度
            return (double)intersection / union;
        }

        /// <summary>
        /// 生成文本的N-Gram集合
        /// </summary>
        private static HashSet<string> GenerateNGrams(string text, int n)
        {
            var result = new HashSet<string>();
            
            if (text.Length < n)
                return result;
                
            for (int i = 0; i <= text.Length - n; i++)
            {
                result.Add(text.Substring(i, n));
            }
            
            return result;
        }

        /// <summary>
        /// 优化的编辑距离相似度计算
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static double OptimizedLevenshteinSimilarity(string text1, string text2)
        {
            // 优化的编辑距离计算
            int distance = LevenshteinDistance(text1, text2);
            int maxLen = Math.Max(text1.Length, text2.Length);
            return (maxLen == 0) ? 1.0 : (1.0 - (double)distance / maxLen);
        }

        /// <summary>
        /// 优化的Levenshtein距离计算
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static int LevenshteinDistance(string text1, string text2)
        {
            if (string.IsNullOrEmpty(text1))
                return string.IsNullOrEmpty(text2) ? 0 : text2.Length;
            if (string.IsNullOrEmpty(text2))
                return text1.Length;

            // 确保text1是较短的字符串以优化空间使用
            if (text1.Length > text2.Length)
                (text2, text1) = (text1, text2);

            ReadOnlySpan<char> s1 = text1.AsSpan();
            ReadOnlySpan<char> s2 = text2.AsSpan();
            
            int len1 = s1.Length;
            int len2 = s2.Length;

            // 仅使用两行数组来减少内存使用
            Span<int> prevRow = stackalloc int[len1 + 1];
            Span<int> currRow = stackalloc int[len1 + 1];

            // 初始化第一行
            for (int i = 0; i <= len1; i++)
                prevRow[i] = i;
                
            // 填充矩阵
            for (int j = 1; j <= len2; j++)
            {
                currRow[0] = j;
                
                for (int i = 1; i <= len1; i++)
                {
                    int cost = (s1[i - 1] == s2[j - 1]) ? 0 : 1;
                    
                    // 取三个操作中代价最小的
                    currRow[i] = Math.Min(
                        Math.Min(currRow[i - 1] + 1, prevRow[i] + 1),
                        prevRow[i - 1] + cost);
                        
                    // 额外考虑转置操作（如"ab"和"ba"）
                    if (i > 1 && j > 1 && s1[i - 1] == s2[j - 2] && s1[i - 2] == s2[j - 1])
                        currRow[i] = Math.Min(currRow[i], prevRow[i - 2] + 1);
                }
                
                // 交换当前行和前一行
                var temp = prevRow;
                prevRow = currRow;
                currRow = temp;
            }

            return prevRow[len1];
        }

        /// <summary>
        /// 优化的URL标准化
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static string NormalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;
                
            var protocolMatch = ProtocolRegex.Match(url);
            string protocol = protocolMatch.Success ? protocolMatch.Value : "";

            ReadOnlySpan<char> rest = url.AsSpan(protocol.Length);
            string processedRest = MultipleSlashRegex.Replace(rest.ToString(), "/").TrimEnd('/');

            return protocol + processedRest;
        }

        /// <summary>
        /// 检查字符是否是标点符号
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPunctuation(char c)
        {
            return Array.IndexOf(PUNC_EOS, c) != -1 || 
                   Array.IndexOf(PUNC_COMMA, c) != -1 || 
                   Array.IndexOf(PUNC_QUOTES, c) != -1 ||
                   char.IsPunctuation(c);
        }
        
        /// <summary>
        /// 检查字符是否为中文或日文字符
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsChineseOrJapaneseChar(char c)
        {
            return (c >= '\u4E00' && c <= '\u9FFF') ||  // 中文基本字符
                   (c >= '\u3400' && c <= '\u4DBF') ||  // 中文扩展字符
                   (c >= '\u3040' && c <= '\u30FF');    // 日文平假名和片假名
        }

        /// <summary>
        /// 检查字符是否为韩文字符
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsKoreanChar(char c)
        {
            return (c >= '\uAC00' && c <= '\uD7AF');    // 韩文字符范围
        }
        
        /// <summary>
        /// 检查字符是否为拉丁字符（大多数西方语言）
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsLatinChar(char c)
        {
            return (c >= 'A' && c <= 'Z') ||            // 拉丁大写字母
                   (c >= 'a' && c <= 'z') ||            // 拉丁小写字母
                   (c >= '\u00C0' && c <= '\u00FF') ||  // 带变音符的拉丁字母
                   (c >= '\u0100' && c <= '\u017F');    // 拉丁字母扩展
        }
        
        /// <summary>
        /// 主要语言类型枚举
        /// </summary>
        public enum LanguageType
        {
            CJK,      // 中文/日文
            Korean,   // 韩文
            Latin,    // 拉丁语系（英文、法文等）
            Mixed,    // 混合
            Other     // 其他
        }
        
        /// <summary>
        /// 清除内部缓存
        /// </summary>
        public static void ClearCaches()
        {
            CharLanguageCache.Clear();
            SimilarityCache.Clear();
        }
    }
}