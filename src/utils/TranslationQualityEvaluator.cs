using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public class TranslationQualityEvaluator
    {
        public const int GoodQualityThreshold = 70;
        
        // 性能优化 - 使用并发字典存储评估历史
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> EvaluationHistory = 
            new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            
        // 性能优化 - 预编译正则表达式
        private static readonly Regex rxNumbersMatch = new Regex(@"\d+", RegexOptions.Compiled);
        private static readonly Regex rxEmailMatch = new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled);
        private static readonly Regex rxUrlMatch = new Regex(@"https?://\S+|www\.\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex rxCodeTokenMatch = new Regex(@"\b[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.Compiled);
        private static readonly Regex rxQuotedTokenMatch = new Regex(@"[\"'']([A-Za-z0-9_\-\.]{2,})[\"'']", RegexOptions.Compiled);
        private static readonly Regex rxDuplicateWordsPattern = new Regex(@"\b(\w+)\b(?:\s+\1\b)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex rxConsecutivePunctuation = new Regex(@"[,.?!;，。？！；]{2,}", RegexOptions.Compiled);

        /// <summary>
        /// 评估翻译质量分数 (0-100)
        /// </summary>
        public static int EvaluateQuality(string sourceText, string translation)
        {
            if (string.IsNullOrEmpty(sourceText) || string.IsNullOrEmpty(translation))
                return 0;

            int score = 100;
            
            // 1. 长度比例检�?
            double lengthRatio = (double)translation.Length / sourceText.Length;
            if (lengthRatio < 0.5 || lengthRatio > 2.0)
                score -= 20;
            
            // 2. 完整性检�?- 确保所有数字和重要实体都被保留
            if (!CheckEntityPreservation(sourceText, translation))
                score -= 15;
            
            // 3. 格式一致性检�?
            if (!CheckFormatConsistency(sourceText, translation))
                score -= 10;
            
            // 4. 语句终止符匹配检�?
            if (!CheckEndingPunctuation(sourceText, translation))
                score -= 5;

            // 5. 流畅度评�?(使用简化的启发式规�?
            score -= (100 - EstimateTextFluency(translation)) / 5;
            
            return Math.Max(0, Math.Min(100, score));
        }

        /// <summary>
        /// Enhanced quality evaluation with entity/term/format consistency.
        /// </summary>
        public static int EvaluateQualityEnhanced(string sourceText, string translation)
        {
            if (string.IsNullOrEmpty(sourceText) || string.IsNullOrEmpty(translation))
                return 0;

            int score = 100;

            double lengthRatio = (double)translation.Length / sourceText.Length;
            if (lengthRatio < 0.5 || lengthRatio > 2.0)
                score -= 20;

            int entityRetentionScore = EvaluateEntityRetentionScore(sourceText, translation);
            if (entityRetentionScore < 60)
                score -= 25;
            else if (entityRetentionScore < 75)
                score -= 15;
            else if (entityRetentionScore < 90)
                score -= 8;

            if (!CheckFormatConsistencyEnhanced(sourceText, translation))
                score -= 12;

            if (!CheckEndingPunctuation(sourceText, translation))
                score -= 5;

            score -= (100 - EstimateTextFluency(translation)) / 5;

            return Math.Max(0, Math.Min(100, score));
        }

        /// <summary>
        /// <summary>
        /// 性能优化 - 轻量级质量评�?(减少计算成本)
        /// </summary>
        public static int EvaluateQualityLightweight(string sourceText, string translation)
        {
            if (string.IsNullOrEmpty(sourceText) || string.IsNullOrEmpty(translation))
                return 0;

            int score = 100;
            
            // 1. 长度比例检�?(保留)
            double lengthRatio = (double)translation.Length / sourceText.Length;
            if (lengthRatio < 0.5 || lengthRatio > 2.0)
                score -= 20;
            
            // 2. 简化的实体检�?- 仅检查数字匹�?
            if (rxNumbersMatch.Matches(sourceText).Count > 0)
            {
                var sourceNumbers = rxNumbersMatch.Matches(sourceText).Cast<Match>().Select(m => m.Value).ToArray();
                var translationNumbers = rxNumbersMatch.Matches(translation).Cast<Match>().Select(m => m.Value).ToArray();
                
                if (sourceNumbers.Length > 0 && translationNumbers.Length < sourceNumbers.Length / 2)
                {
                    score -= 15;
                }
            }
            
            // 3. 简化的格式检�?- 仅检查问号和感叹�?
            bool isSourceQuestion = sourceText.Contains("?") || sourceText.Contains("�?);
            bool isTranslationQuestion = translation.Contains("?") || translation.Contains("�?);
            
            if (isSourceQuestion != isTranslationQuestion)
                score -= 10;
            
            bool isSourceExclamation = sourceText.Contains("!") || sourceText.Contains("�?);
            bool isTranslationExclamation = translation.Contains("!") || translation.Contains("�?);
            
            if (isSourceExclamation != isTranslationExclamation)
                score -= 10;
            
            // 4. 终止符匹配检�?(保留)
            char[] sourceEndChars = { '.', '?', '!', '�?, '�?, '�? };
            
            bool sourceHasEnding = sourceText.Length > 0 && sourceEndChars.Contains(sourceText[sourceText.Length - 1]);
            bool translationHasEnding = translation.Length > 0 && sourceEndChars.Contains(translation[translation.Length - 1]);
            
            if (sourceHasEnding && !translationHasEnding)
                score -= 5;
            
            // 5. 简化的流畅度检�?- 仅检查重复单词和过短文本
            if (rxDuplicateWordsPattern.IsMatch(translation))
                score -= 10;
                
            if (translation.Length < 10 && translation.Length < sourceText.Length / 3)
                score -= 10;
            
            return Math.Max(0, Math.Min(100, score));
        }

        /// <summary>
        /// Enhanced quality evaluation with entity/term/format consistency.
        /// </summary>
        public static int EvaluateQualityEnhanced(string sourceText, string translation)
        {
            if (string.IsNullOrEmpty(sourceText) || string.IsNullOrEmpty(translation))
                return 0;

            int score = 100;

            double lengthRatio = (double)translation.Length / sourceText.Length;
            if (lengthRatio < 0.5 || lengthRatio > 2.0)
                score -= 20;

            int entityRetentionScore = EvaluateEntityRetentionScore(sourceText, translation);
            if (entityRetentionScore < 60)
                score -= 25;
            else if (entityRetentionScore < 75)
                score -= 15;
            else if (entityRetentionScore < 90)
                score -= 8;

            if (!CheckFormatConsistencyEnhanced(sourceText, translation))
                score -= 12;

            if (!CheckEndingPunctuation(sourceText, translation))
                score -= 5;

            score -= (100 - EstimateTextFluency(translation)) / 5;

            return Math.Max(0, Math.Min(100, score));
        }

        /// <summary>
        /// <summary>
        /// 检查重要实体（数字、日期等）是否在翻译中保�?
        /// </summary>
        private static bool CheckEntityPreservation(string sourceText, string translation)
        {
            return EvaluateEntityRetentionScore(sourceText, translation) >= 70;
        }

        private static int EvaluateEntityRetentionScore(string sourceText, string translation)
        {
            var tokens = ExtractProtectedTokens(sourceText);
            if (tokens.Count == 0)
                return 100;

            int preserved = tokens.Count(t => ContainsToken(translation, t));
            double ratio = (double)preserved / tokens.Count;

            if (ratio >= 0.9) return 100;
            if (ratio >= 0.75) return 85;
            if (ratio >= 0.6) return 70;
            if (ratio >= 0.4) return 55;
            return 30;
        }

        private static HashSet<string> ExtractProtectedTokens(string sourceText)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in rxNumbersMatch.Matches(sourceText))
                tokens.Add(m.Value);

            foreach (Match m in rxEmailMatch.Matches(sourceText))
                tokens.Add(m.Value);

            foreach (Match m in rxUrlMatch.Matches(sourceText))
                tokens.Add(m.Value);

            foreach (Match m in rxQuotedTokenMatch.Matches(sourceText))
            {
                if (m.Groups.Count > 1)
                    tokens.Add(m.Groups[1].Value);
            }

            foreach (Match m in rxCodeTokenMatch.Matches(sourceText))
            {
                string token = m.Value;
                if (IsLikelyTermToken(token))
                    tokens.Add(token);
            }

            return tokens;
        }

        private static bool ContainsToken(string text, string token)
        {
            if (string.IsNullOrEmpty(token))
                return true;

            bool isNumeric = token.All(char.IsDigit);
            if (isNumeric)
                return text.Contains(token, StringComparison.Ordinal);

            return text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsLikelyTermToken(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length < 2)
                return false;

            if (token.Any(char.IsDigit) || token.Contains("_"))
                return true;

            bool hasUpper = token.Any(char.IsUpper);
            bool hasLower = token.Any(char.IsLower);

            if (hasUpper && hasLower)
                return true;

            if (token.All(char.IsUpper) && token.Length >= 2)
                return true;

            return false;
        }

        private static bool CheckFormatConsistencyEnhanced(string sourceText, string translation)
        {
            if (!CheckFormatConsistency(sourceText, translation))
                return false;

            if (!CheckLineBreakConsistency(sourceText, translation))
                return false;

            if (!CheckBracketBalance(sourceText, translation))
                return false;

            if (!CheckQuoteBalance(sourceText, translation))
                return false;

            return true;
        }

        private static bool CheckLineBreakConsistency(string sourceText, string translation)
        {
            int sourceLines = sourceText.Count(c => c == '\n');
            int translationLines = translation.Count(c => c == '\n');

            if (sourceLines == 0 && translationLines == 0)
                return true;

            return Math.Abs(sourceLines - translationLines) <= 1;
        }

        private static bool CheckBracketBalance(string sourceText, string translation)
        {
            var pairs = new (char open, char close)[]
            {
                ('(', ')'),
                ('[', ']'),
                ('{', '}'),
                ('<', '>')
            };

            foreach (var pair in pairs)
            {
                int sourceOpen = sourceText.Count(c => c == pair.open);
                int sourceClose = sourceText.Count(c => c == pair.close);
                int translationOpen = translation.Count(c => c == pair.open);
                int translationClose = translation.Count(c => c == pair.close);

                if (sourceOpen == 0 && sourceClose == 0)
                    continue;

                if (sourceOpen != translationOpen || sourceClose != translationClose)
                    return false;
            }

            return true;
        }

        private static bool CheckQuoteBalance(string sourceText, string translation)
        {
            int sourceDoubleQuotes = sourceText.Count(c => c == '"');
            int translationDoubleQuotes = translation.Count(c => c == '"');
            if (sourceDoubleQuotes > 0 && sourceDoubleQuotes != translationDoubleQuotes)
                return false;

            int sourceSingleQuotes = sourceText.Count(c => c == '\'');
            int translationSingleQuotes = translation.Count(c => c == '\'');
            if (sourceSingleQuotes > 0 && sourceSingleQuotes != translationSingleQuotes)
                return false;

            return true;
        }
private static bool CheckFormatConsistency(string sourceText, string translation)
        {
            // 检查问句是否仍然是问句
            bool isSourceQuestion = sourceText.EndsWith("?") || sourceText.EndsWith("�?);
            bool isTranslationQuestion = translation.EndsWith("?") || translation.EndsWith("�?);
            
            if (isSourceQuestion != isTranslationQuestion)
                return false;
            
            // 检查感叹句是否保留
            bool isSourceExclamation = sourceText.EndsWith("!") || sourceText.EndsWith("�?);
            bool isTranslationExclamation = translation.EndsWith("!") || translation.EndsWith("�?);
            
            if (isSourceExclamation != isTranslationExclamation)
                return false;
            
            return true;
        }

        /// <summary>
        /// 检查句子终止符是否匹配
        /// </summary>
        private static bool CheckEndingPunctuation(string sourceText, string translation)
        {
            char[] sourceEndChars = { '.', '?', '!', '�?, '�?, '�? };
            
            bool sourceHasEnding = sourceText.Length > 0 && sourceEndChars.Contains(sourceText[sourceText.Length - 1]);
            bool translationHasEnding = translation.Length > 0 && sourceEndChars.Contains(translation[translation.Length - 1]);
            
            return sourceHasEnding == translationHasEnding;
        }

        /// <summary>
        /// 估计文本流畅度的简单启发式评分
        /// </summary>
        private static int EstimateTextFluency(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            
            int score = 100;
            
            // 检查重复单�?- 使用正则表达式提高性能
            if (rxDuplicateWordsPattern.IsMatch(text))
            {
                score -= 15;
            }
            
            // 检查句子长度，过短的句子可能翻译不完整
            if (text.Length < 10)
                score -= 10;
            
            // 检查连续标点符号，可能表示翻译质量问题
            if (rxConsecutivePunctuation.IsMatch(text))
                score -= 10;
            
            return Math.Max(0, Math.Min(100, score));
        }

        /// <summary>
        /// Enhanced quality evaluation with entity/term/format consistency.
        /// </summary>
        public static int EvaluateQualityEnhanced(string sourceText, string translation)
        {
            if (string.IsNullOrEmpty(sourceText) || string.IsNullOrEmpty(translation))
                return 0;

            int score = 100;

            double lengthRatio = (double)translation.Length / sourceText.Length;
            if (lengthRatio < 0.5 || lengthRatio > 2.0)
                score -= 20;

            int entityRetentionScore = EvaluateEntityRetentionScore(sourceText, translation);
            if (entityRetentionScore < 60)
                score -= 25;
            else if (entityRetentionScore < 75)
                score -= 15;
            else if (entityRetentionScore < 90)
                score -= 8;

            if (!CheckFormatConsistencyEnhanced(sourceText, translation))
                score -= 12;

            if (!CheckEndingPunctuation(sourceText, translation))
                score -= 5;

            score -= (100 - EstimateTextFluency(translation)) / 5;

            return Math.Max(0, Math.Min(100, score));
        }

        /// <summary>
        /// <summary>
        /// 记录API和翻译质量的关系
        /// </summary>
        public static void RecordQualityForAPI(string apiName, int qualityScore, string sourceLanguage, string targetLanguage)
        {
            string key = $"{apiName}_{sourceLanguage}_{targetLanguage}";
            
            // 使用并发字典的原子操作更新评�?
            EvaluationHistory.AddOrUpdate(
                key, 
                qualityScore, 
                (_, existingScore) => (int)(existingScore * 0.9 + qualityScore * 0.1)
            );
        }

        /// <summary>
        /// 获取指定语言对的最佳翻译API
        /// </summary>
        public static string GetBestAPIForLanguagePair(string sourceLanguage, string targetLanguage, List<string> availableAPIs)
        {
            var candidates = availableAPIs
                .Select(api => new
                {
                    API = api,
                    Score = EvaluationHistory.TryGetValue($"{api}_{sourceLanguage}_{targetLanguage}", out int score) ? score : 50
                })
                .OrderByDescending(x => x.Score)
                .ToList();

            return candidates.FirstOrDefault()?.API ?? availableAPIs.FirstOrDefault();
        }

        /// <summary>
        /// 检查翻译质量并提供改进建议
        /// </summary>
        public static (string ImprovedTranslation, string ApiSuggestion) GetImprovedTranslation(
            string originalTranslation, string sourceText, string currentApi, int qualityScore)
        {
            if (qualityScore >= GoodQualityThreshold)
                return (originalTranslation, currentApi); // 质量足够�?
            
            // 根据不同问题类型给出不同改进建议
            string improvedTranslation = originalTranslation;
            string apiSuggestion = currentApi;
            
            // 检查是否缺少数字和实体
            if (rxNumbersMatch.Matches(sourceText).Count > 0)
            {
                var sourceNumbers = rxNumbersMatch.Matches(sourceText).Cast<Match>().Select(m => m.Value).ToArray();
                var translationNumbers = rxNumbersMatch.Matches(originalTranslation).Cast<Match>().Select(m => m.Value).ToArray();
                
                if (sourceNumbers.Length > 0 && translationNumbers.Length < sourceNumbers.Length / 2)
                {
                    // 如果使用LLM类API，可能需要增强prompt中对保留实体的要�?
                    if (currentApi == "OpenAI" || currentApi == "Ollama")
                    {
                        apiSuggestion = "Google"; // 尝试使用传统翻译API可能更好保留实体
                    }
                }
            }
            
            // 如果是格式问题，修复一些常见错�?
            if (!CheckFormatConsistency(sourceText, originalTranslation))
            {
                // 修复问句和感叹句格式
                if (sourceText.EndsWith("?") && !originalTranslation.EndsWith("?") && !originalTranslation.EndsWith("�?))
                    improvedTranslation += "?";
                else if (sourceText.EndsWith("�?) && !originalTranslation.EndsWith("!") && !originalTranslation.EndsWith("�?))
                    improvedTranslation += "!";
            }
            
            // 如果语言是中日韩等，可能需要特殊处�?
            if (TextUtil.isCJChar(sourceText.FirstOrDefault()) && 
                currentApi != "Google" && currentApi != "Google2")
            {
                apiSuggestion = "Google"; // 对亚洲语言，Google可能有更好表�?
            }
            
            return (improvedTranslation, apiSuggestion);
        }
    }
}






