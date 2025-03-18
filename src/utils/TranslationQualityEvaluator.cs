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
        private const int GOOD_QUALITY_THRESHOLD = 70;
        private static readonly Dictionary<string, int> EvaluationHistory = new Dictionary<string, int>();
        private static readonly object _lockObject = new object();

        /// <summary>
        /// 评估翻译质量分数 (0-100)
        /// </summary>
        public static int EvaluateQuality(string sourceText, string translation)
        {
            if (string.IsNullOrEmpty(sourceText) || string.IsNullOrEmpty(translation))
                return 0;

            int score = 100;
            
            // 1. 长度比例检查
            double lengthRatio = (double)translation.Length / sourceText.Length;
            if (lengthRatio < 0.5 || lengthRatio > 2.0)
                score -= 20;
            
            // 2. 完整性检查 - 确保所有数字和重要实体都被保留
            if (!CheckEntityPreservation(sourceText, translation))
                score -= 15;
            
            // 3. 格式一致性检查
            if (!CheckFormatConsistency(sourceText, translation))
                score -= 10;
            
            // 4. 语句终止符匹配检查
            if (!CheckEndingPunctuation(sourceText, translation))
                score -= 5;

            // 5. 流畅度评分 (使用简化的启发式规则)
            score -= (100 - EstimateTextFluency(translation)) / 5;
            
            return Math.Max(0, Math.Min(100, score));
        }

        /// <summary>
        /// 检查重要实体（数字、日期等）是否在翻译中保留
        /// </summary>
        private static bool CheckEntityPreservation(string sourceText, string translation)
        {
            // 检查数字是否保留
            var sourceNumbers = Regex.Matches(sourceText, @"\d+").Cast<Match>().Select(m => m.Value);
            var translationNumbers = Regex.Matches(translation, @"\d+").Cast<Match>().Select(m => m.Value);
            
            // 检查至少80%的数字是否保留
            if (sourceNumbers.Count() > 0)
            {
                int preservedCount = sourceNumbers.Intersect(translationNumbers).Count();
                if ((double)preservedCount / sourceNumbers.Count() < 0.8)
                    return false;
            }
            
            // 检查常见格式如邮件、URL等是否保留
            var emailRegex = new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}");
            var sourceEmails = emailRegex.Matches(sourceText).Cast<Match>().Select(m => m.Value);
            var translationEmails = emailRegex.Matches(translation).Cast<Match>().Select(m => m.Value);
            
            if (sourceEmails.Any() && !translationEmails.Any())
                return false;
            
            return true;
        }

        /// <summary>
        /// 检查格式一致性，如标点符号、大小写等
        /// </summary>
        private static bool CheckFormatConsistency(string sourceText, string translation)
        {
            // 检查问句是否仍然是问句
            bool isSourceQuestion = sourceText.EndsWith("?") || sourceText.EndsWith("？");
            bool isTranslationQuestion = translation.EndsWith("?") || translation.EndsWith("？");
            
            if (isSourceQuestion != isTranslationQuestion)
                return false;
            
            // 检查感叹句是否保留
            bool isSourceExclamation = sourceText.EndsWith("!") || sourceText.EndsWith("！");
            bool isTranslationExclamation = translation.EndsWith("!") || translation.EndsWith("！");
            
            if (isSourceExclamation != isTranslationExclamation)
                return false;
            
            return true;
        }

        /// <summary>
        /// 检查句子终止符是否匹配
        /// </summary>
        private static bool CheckEndingPunctuation(string sourceText, string translation)
        {
            char[] sourceEndChars = { '.', '?', '!', '。', '？', '！' };
            
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
            
            // 检查重复单词
            var words = text.Split(new[] { ' ', ',', '.', '?', '!', ';', '，', '。', '？', '！', '；' }, 
                StringSplitOptions.RemoveEmptyEntries);
            
            var wordGroups = words.GroupBy(w => w.ToLower())
                                 .Where(g => g.Count() > 1)
                                 .Select(g => new { Word = g.Key, Count = g.Count() });
            
            foreach (var group in wordGroups)
            {
                if (group.Count > 2 && group.Word.Length > 3) // 连续重复三次以上且不是短词
                    score -= 5 * (group.Count - 2);
            }
            
            // 检查句子长度，过短的句子可能翻译不完整
            if (text.Length < 10 && text.Length < text.Length / 3)
                score -= 10;
            
            // 检查连续标点符号，可能表示翻译质量问题
            if (Regex.IsMatch(text, @"[,.?!;，。？！；]{2,}"))
                score -= 10;
            
            return Math.Max(0, Math.Min(100, score));
        }

        /// <summary>
        /// 记录API和翻译质量的关系
        /// </summary>
        public static void RecordQualityForAPI(string apiName, int qualityScore, string sourceLanguage, string targetLanguage)
        {
            lock (_lockObject)
            {
                string key = $"{apiName}_{sourceLanguage}_{targetLanguage}";
                if (!EvaluationHistory.ContainsKey(key))
                    EvaluationHistory[key] = qualityScore;
                else
                    EvaluationHistory[key] = (EvaluationHistory[key] * 9 + qualityScore) / 10; // 加权平均
            }
        }

        /// <summary>
        /// 获取指定语言对的最佳翻译API
        /// </summary>
        public static string GetBestAPIForLanguagePair(string sourceLanguage, string targetLanguage, List<string> availableAPIs)
        {
            lock (_lockObject)
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
        }

        /// <summary>
        /// 检查翻译质量并提供改进建议
        /// </summary>
        public static (string ImprovedTranslation, string ApiSuggestion) GetImprovedTranslation(
            string originalTranslation, string sourceText, string currentApi, int qualityScore)
        {
            if (qualityScore >= GOOD_QUALITY_THRESHOLD)
                return (originalTranslation, currentApi); // 质量足够好
            
            // 根据不同问题类型给出不同改进建议
            string improvedTranslation = originalTranslation;
            string apiSuggestion = currentApi;
            
            // 检查是否缺少数字和实体
            if (!CheckEntityPreservation(sourceText, originalTranslation))
            {
                // 如果使用LLM类API，可能需要增强prompt中对保留实体的要求
                if (currentApi == "OpenAI" || currentApi == "Ollama")
                {
                    apiSuggestion = "Google"; // 尝试使用传统翻译API可能更好保留实体
                }
            }
            
            // 如果是格式问题，修复一些常见错误
            if (!CheckFormatConsistency(sourceText, originalTranslation))
            {
                // 修复问句和感叹句格式
                if (sourceText.EndsWith("?") && !originalTranslation.EndsWith("?") && !originalTranslation.EndsWith("？"))
                    improvedTranslation += "?";
                else if (sourceText.EndsWith("！") && !originalTranslation.EndsWith("!") && !originalTranslation.EndsWith("！"))
                    improvedTranslation += "!";
            }
            
            // 如果语言是中日韩等，可能需要特殊处理
            if (TextUtil.isCJChar(sourceText.FirstOrDefault()) && 
                currentApi != "Google" && currentApi != "Google2")
            {
                apiSuggestion = "Google"; // 对亚洲语言，Google可能有更好表现
            }
            
            return (improvedTranslation, apiSuggestion);
        }
    }
}