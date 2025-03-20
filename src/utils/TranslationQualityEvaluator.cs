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
        private const int GOOD_QUALITY_THRESHOLD = 75;
        
        // 性能优化 - 使用并发字典存储评估历史
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> EvaluationHistory = 
            new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            
        // 针对API-语言对的评分缓存
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> ApiLanguagePairScores =
            new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            
        // 性能优化 - 预编译正则表达式
        private static readonly Regex rxNumbersMatch = new Regex(@"\d+", RegexOptions.Compiled);
        private static readonly Regex rxEmailMatch = new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled);
        private static readonly Regex rxDuplicateWordsPattern = new Regex(@"\b(\w+)\b(?:\s+\1\b)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex rxConsecutivePunctuation = new Regex(@"[,.?!;，。？！；]{2,}", RegexOptions.Compiled);
        private static readonly Regex rxDuplicatedCharacters = new Regex(@"(.)\1{3,}", RegexOptions.Compiled);
        private static readonly Regex rxUrlPattern = new Regex(@"https?://[^\s]+", RegexOptions.Compiled);
        
        // 新增：跟踪各种错误类型以便更细致的质量评估
        private static readonly Dictionary<string, Dictionary<string, int>> ApiErrorTypeCounts = 
            new Dictionary<string, Dictionary<string, int>>();

        /// <summary>
        /// 评估翻译质量分数 (0-100)
        /// </summary>
        public static int EvaluateQuality(string sourceText, string translation)
        {
            if (string.IsNullOrEmpty(sourceText) || string.IsNullOrEmpty(translation))
                return 0;

            int score = 100;
            
            // 1. 长度比例检查 - 更精确的计算
            double lengthRatio = (double)translation.Length / sourceText.Length;
            bool isCJKTarget = ContainsCJK(translation);
            bool isCJKSource = ContainsCJK(sourceText);
            
            // 针对不同语言对调整合理的长度比例区间
            double minRatio = 0.5;
            double maxRatio = 2.0;
            
            // 处理中文/日文/韩文等CJK语言的特殊情况
            if (isCJKSource && !isCJKTarget) {
                // CJK -> 非CJK (如中文->英文)，通常结果会更长
                minRatio = 1.0;
                maxRatio = 3.0;
            } else if (!isCJKSource && isCJKTarget) {
                // 非CJK -> CJK (如英文->中文)，通常结果会更短
                minRatio = 0.3;
                maxRatio = 1.2;
            }
            
            if (lengthRatio < minRatio || lengthRatio > maxRatio) {
                score -= 20;
                TrackErrorType("length_ratio", translation);
            }
            
            // 2. 完整性检查 - 确保所有数字和重要实体都被保留
            if (!CheckEntityPreservation(sourceText, translation)) {
                score -= 20;
                TrackErrorType("entity_loss", translation);
            }
            
            // 3. 格式一致性检查
            if (!CheckFormatConsistency(sourceText, translation)) {
                score -= 10;
                TrackErrorType("format_inconsistency", translation);
            }
            
            // 4. 语句终止符匹配检查
            if (!CheckEndingPunctuation(sourceText, translation)) {
                score -= 5;
                TrackErrorType("punctuation_mismatch", translation);
            }
            
            // 5. 检查URL和特殊格式是否保留
            if (!CheckSpecialContentPreservation(sourceText, translation)) {
                score -= 10;
                TrackErrorType("special_content_loss", translation);
            }

            // 6. 流畅度评分 
            int fluencyScore = EstimateTextFluency(translation);
            score -= (100 - fluencyScore) / 4;
            if (fluencyScore < 70) {
                TrackErrorType("low_fluency", translation);
            }
            
            // 7. 检查明显的翻译错误
            if (CheckForCommonTranslationErrors(translation)) {
                score -= 15;
                TrackErrorType("common_errors", translation);
            }
            
            return Math.Max(0, Math.Min(100, score));
        }

        /// <summary>
        /// 性能优化 - 轻量级质量评估 (减少计算成本)
        /// </summary>
        public static int EvaluateQualityLightweight(string sourceText, string translation)
        {
            if (string.IsNullOrEmpty(sourceText) || string.IsNullOrEmpty(translation))
                return 0;

            int score = 100;
            
            // 1. 长度比例检查 (保留但优化)
            double lengthRatio = (double)translation.Length / sourceText.Length;
            bool isCJKTarget = ContainsCJK(translation);
            bool isCJKSource = ContainsCJK(sourceText);
            
            // 针对不同语言对调整合理的长度比例区间
            double minRatio = 0.5;
            double maxRatio = 2.0;
            
            if (isCJKSource && !isCJKTarget) {
                minRatio = 1.0;
                maxRatio = 3.0;
            } else if (!isCJKSource && isCJKTarget) {
                minRatio = 0.3;
                maxRatio = 1.2;
            }
            
            if (lengthRatio < minRatio || lengthRatio > maxRatio)
                score -= 20;
            
            // 2. 简化的实体检查 - 仅检查数字匹配
            if (rxNumbersMatch.Matches(sourceText).Count > 0)
            {
                var sourceNumbers = rxNumbersMatch.Matches(sourceText).Cast<Match>().Select(m => m.Value).ToArray();
                var translationNumbers = rxNumbersMatch.Matches(translation).Cast<Match>().Select(m => m.Value).ToArray();
                
                if (sourceNumbers.Length > 0 && translationNumbers.Length < sourceNumbers.Length / 2)
                {
                    score -= 15;
                }
            }
            
            // 3. 简化的格式检查
            bool isSourceQuestion = sourceText.Contains("?") || sourceText.Contains("？");
            bool isTranslationQuestion = translation.Contains("?") || translation.Contains("？");
            
            if (isSourceQuestion != isTranslationQuestion)
                score -= 10;
            
            // 4. 终止符匹配检查
            char[] sourceEndChars = { '.', '?', '!', '。', '？', '！' };
            
            bool sourceHasEnding = sourceText.Length > 0 && sourceEndChars.Contains(sourceText[sourceText.Length - 1]);
            bool translationHasEnding = translation.Length > 0 && sourceEndChars.Contains(translation[translation.Length - 1]);
            
            if (sourceHasEnding && !translationHasEnding)
                score -= 5;
            
            // 5. 简化的流畅度检查
            if (rxDuplicateWordsPattern.IsMatch(translation))
                score -= 10;
                
            if (rxDuplicatedCharacters.IsMatch(translation))
                score -= 15;
                
            if (translation.Length < 10 && translation.Length < sourceText.Length / 3)
                score -= 10;
            
            return Math.Max(0, Math.Min(100, score));
        }

        /// <summary>
        /// 检查文本是否包含CJK(中文、日文、韩文)字符
        /// </summary>
        private static bool ContainsCJK(string text)
        {
            return text.Any(c => 
                (c >= 0x4E00 && c <= 0x9FFF) ||   // CJK统一汉字
                (c >= 0x3040 && c <= 0x309F) ||   // 平假名
                (c >= 0x30A0 && c <= 0x30FF) ||   // 片假名
                (c >= 0xAC00 && c <= 0xD7A3));    // 韩文
        }

        /// <summary>
        /// 检查重要实体（数字、日期等）是否在翻译中保留
        /// </summary>
        private static bool CheckEntityPreservation(string sourceText, string translation)
        {
            // 检查数字是否保留
            var sourceNumbers = rxNumbersMatch.Matches(sourceText).Cast<Match>().Select(m => m.Value).ToList();
            var translationNumbers = rxNumbersMatch.Matches(translation).Cast<Match>().Select(m => m.Value).ToList();
            
            // 检查至少80%的数字是否保留
            if (sourceNumbers.Count > 0)
            {
                int preservedCount = 0;
                foreach (var num in sourceNumbers)
                {
                    if (translationNumbers.Contains(num))
                    {
                        preservedCount++;
                        // 从translationNumbers中移除已匹配的数字，避免重复计数
                        translationNumbers.Remove(num);
                    }
                }
                
                if ((double)preservedCount / sourceNumbers.Count < 0.8)
                    return false;
            }
            
            // 检查常见格式如邮件地址是否保留
            var sourceEmails = rxEmailMatch.Matches(sourceText).Cast<Match>().Select(m => m.Value);
            var translationEmails = rxEmailMatch.Matches(translation).Cast<Match>().Select(m => m.Value);
            
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
            
            // 检查首字母大写保留（对于非CJK目标语言）
            if (!ContainsCJK(translation) && 
                char.IsUpper(sourceText[0]) && 
                translation.Length > 0 && 
                char.IsLower(translation[0]))
            {
                return false;
            }
            
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
        /// 检查URL和特殊格式内容是否保留
        /// </summary>
        private static bool CheckSpecialContentPreservation(string sourceText, string translation)
        {
            // 检查URL保留
            var sourceUrls = rxUrlPattern.Matches(sourceText).Cast<Match>().Select(m => m.Value);
            var translationUrls = rxUrlPattern.Matches(translation).Cast<Match>().Select(m => m.Value);
            
            if (sourceUrls.Any() && !translationUrls.Any())
                return false;
                
            // 检查其他可能的特殊格式（如文件名、路径等）
            var filePathPattern = new Regex(@"[a-zA-Z]:\\[^\\/:*?""<>|\r\n]+", RegexOptions.Compiled);
            var sourceFilePaths = filePathPattern.Matches(sourceText).Cast<Match>().Select(m => m.Value);
            var translationFilePaths = filePathPattern.Matches(translation).Cast<Match>().Select(m => m.Value);
            
            if (sourceFilePaths.Any() && !translationFilePaths.Any())
                return false;
            
            return true;
        }
        
        /// <summary>
        /// 检查常见的翻译错误
        /// </summary>
        private static bool CheckForCommonTranslationErrors(string translation)
        {
            // 检查明显的机器翻译错误模式
            
            // 检查重复内容
            if (rxDuplicateWordsPattern.IsMatch(translation))
                return true;
            
            // 检查异常重复的字符
            if (rxDuplicatedCharacters.IsMatch(translation))
                return true;
                
            // 检查未翻译的文本标记
            if (translation.Contains("Translation:") || 
                translation.Contains("Translated text:") ||
                translation.Contains("[Translated]") ||
                translation.Contains("🔤"))
                return true;
                
            // 检查可能的半翻译问题（混合了原语言和目标语言）
            // 这个检测比较复杂，可能需要根据具体语言对进行定制
            
            return false;
        }

        /// <summary>
        /// 估计文本流畅度的启发式评分
        /// </summary>
        private static int EstimateTextFluency(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            
            int score = 100;
            
            // 检查重复单词
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
                
            // 检查重复字符，可能是机器翻译错误
            if (rxDuplicatedCharacters.IsMatch(text))
                score -= 15;
                
            // 检查语言混合问题（针对拉丁字母语言）
            if (!ContainsCJK(text) && Regex.IsMatch(text, @"[\u4E00-\u9FFF\u3040-\u30FF\uAC00-\uD7A3]"))
            {
                score -= 20; // 拉丁文本不应该出现亚洲字符
            }
            
            // 检查语言混合问题（针对CJK语言）
            if (ContainsCJK(text) && text.Length > 20)
            {
                // 对于预期是CJK的文本，如果拉丁字母太多（除了常见的英文术语），可能是翻译问题
                int cjkCount = text.Count(c => 
                    (c >= 0x4E00 && c <= 0x9FFF) ||   // CJK统一汉字
                    (c >= 0x3040 && c <= 0x309F) ||   // 平假名
                    (c >= 0x30A0 && c <= 0x30FF) ||   // 片假名
                    (c >= 0xAC00 && c <= 0xD7A3));    // 韩文
                
                double cjkRatio = (double)cjkCount / text.Length;
                if (cjkRatio < 0.6) // 如果CJK字符少于60%，可能存在问题
                {
                    score -= 10;
                }
            }
            
            return Math.Max(0, Math.Min(100, score));
        }
        
        /// <summary>
        /// 跟踪错误类型，用于API质量改进
        /// </summary>
        private static void TrackErrorType(string errorType, string translation)
        {
            string apiName = Translator.Setting.ApiName;
            if (!ApiErrorTypeCounts.ContainsKey(apiName))
            {
                ApiErrorTypeCounts[apiName] = new Dictionary<string, int>();
            }
            
            if (!ApiErrorTypeCounts[apiName].ContainsKey(errorType))
            {
                ApiErrorTypeCounts[apiName][errorType] = 0;
            }
            
            ApiErrorTypeCounts[apiName][errorType]++;
        }

        /// <summary>
        /// 记录API和翻译质量的关系
        /// </summary>
        public static void RecordQualityForAPI(string apiName, int qualityScore, string sourceLanguage, string targetLanguage)
        {
            string key = $"{apiName}_{sourceLanguage}_{targetLanguage}";
            
            // 使用并发字典的原子操作更新评分
            EvaluationHistory.AddOrUpdate(
                key, 
                qualityScore, 
                (_, existingScore) => (int)(existingScore * 0.9 + qualityScore * 0.1)
            );
            
            // 更新API-语言对评分
            ApiLanguagePairScores.AddOrUpdate(
                key,
                qualityScore,
                (_, existingScore) => (int)(existingScore * 0.8 + qualityScore * 0.2)
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
                    Score = ApiLanguagePairScores.TryGetValue($"{api}_{sourceLanguage}_{targetLanguage}", out int score) ? score : 50
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
            if (qualityScore >= GOOD_QUALITY_THRESHOLD)
                return (originalTranslation, currentApi); // 质量足够好
            
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
                    // 如果使用LLM类API且数字丢失，可能需要使用更精确的API
                    if (IsLLMBasedAPI(currentApi))
                    {
                        apiSuggestion = "Google"; // 对于保留数字，传统翻译API通常表现更好
                    }
                }
            }
            
            // 检查URL丢失
            var sourceUrls = rxUrlPattern.Matches(sourceText).Cast<Match>().Select(m => m.Value).ToArray();
            var translationUrls = rxUrlPattern.Matches(originalTranslation).Cast<Match>().Select(m => m.Value).ToArray();
            
            if (sourceUrls.Length > 0 && translationUrls.Length == 0)
            {
                // URL丢失，使用Google翻译可能更好
                apiSuggestion = "Google";
                
                // 尝试修复URL丢失的问题
                improvedTranslation = originalTranslation;
                foreach (var url in sourceUrls)
                {
                    if (!originalTranslation.Contains(url))
                    {
                        // 尝试在句子末尾添加URL
                        improvedTranslation += " " + url;
                    }
                }
            }
            
            // 检测并修复重复词问题
            var duplicates = rxDuplicateWordsPattern.Matches(originalTranslation);
            if (duplicates.Count > 0)
            {
                improvedTranslation = rxDuplicateWordsPattern.Replace(improvedTranslation, "$1");
            }
            
            // 检测并修复错误标点
            improvedTranslation = rxConsecutivePunctuation.Replace(improvedTranslation, m => m.Value[0].ToString());
            
            // 如果是格式问题，修复一些常见错误
            if (!CheckFormatConsistency(sourceText, originalTranslation))
            {
                // 修复问句和感叹句格式
                if (sourceText.EndsWith("?") && !improvedTranslation.EndsWith("?") && !improvedTranslation.EndsWith("？"))
                    improvedTranslation += "?";
                else if (sourceText.EndsWith("！") && !improvedTranslation.EndsWith("!") && !improvedTranslation.EndsWith("！"))
                    improvedTranslation += "!";
                else if (sourceText.EndsWith(".") && !improvedTranslation.EndsWith(".") && !improvedTranslation.EndsWith("。"))
                    improvedTranslation += ".";
                
                // 修复首字母大写保留
                if (!ContainsCJK(improvedTranslation) && 
                    char.IsUpper(sourceText[0]) && 
                    improvedTranslation.Length > 0 && 
                    char.IsLower(improvedTranslation[0]))
                {
                    improvedTranslation = char.ToUpper(improvedTranslation[0]) + improvedTranslation.Substring(1);
                }
            }
            
            // 如果语言是中日韩等亚洲语言，可能需要特殊处理
            if ((ContainsCJK(sourceText) || ContainsCJK(improvedTranslation)) && 
                currentApi != "Google" && currentApi != "Google2")
            {
                // 检查API是否频繁出现特定错误类型
                if (ApiErrorTypeCounts.TryGetValue(currentApi, out var errorCounts))
                {
                    // 如果该API在亚洲语言上有较高的错误率，建议切换
                    int totalErrorCount = errorCounts.Values.Sum();
                    if (totalErrorCount > 10) // 有足够的样本进行判断
                    {
                        apiSuggestion = "Google"; // 对亚洲语言，Google通常有更好表现
                    }
                }
            }
            
            // 检查翻译后文本是否包含原始的标记或提示词痕迹
            if (improvedTranslation.Contains("Translation:") || 
                improvedTranslation.Contains("Translated text:") ||
                improvedTranslation.Contains("🔤"))
            {
                // 清理翻译残留
                improvedTranslation = Regex.Replace(improvedTranslation, @"Translation:\s*", "", RegexOptions.IgnoreCase);
                improvedTranslation = Regex.Replace(improvedTranslation, @"Translated text:\s*", "", RegexOptions.IgnoreCase);
                improvedTranslation = improvedTranslation.Replace("🔤", "");
                
                // 如果是LLM模型出现这类问题，建议尝试不同API
                if (IsLLMBasedAPI(currentApi))
                {
                    apiSuggestion = currentApi == "OpenAI" ? "Google" : "OpenAI";
                }
            }
            
            return (improvedTranslation, apiSuggestion);
        }
        
        /// <summary>
        /// 判断是否为基于LLM的API
        /// </summary>
        private static bool IsLLMBasedAPI(string apiName)
        {
            return apiName == "OpenAI" || apiName == "Ollama" || apiName == "OpenRouter";
        }
        
        /// <summary>
        /// 分析特定语言对的常见错误，用于提高翻译质量
        /// </summary>
        public static Dictionary<string, int> GetErrorAnalysisForLanguagePair(string sourceLanguage, string targetLanguage)
        {
            var result = new Dictionary<string, int>();
            
            foreach (var apiEntry in ApiErrorTypeCounts)
            {
                string apiName = apiEntry.Key;
                var errorCounts = apiEntry.Value;
                
                string key = $"{apiName}_{sourceLanguage}_{targetLanguage}";
                if (ApiLanguagePairScores.TryGetValue(key, out _))
                {
                    foreach (var errorType in errorCounts)
                    {
                        string errorKey = $"{apiName}_{errorType.Key}";
                        if (!result.ContainsKey(errorKey))
                            result[errorKey] = 0;
                            
                        result[errorKey] += errorType.Value;
                    }
                }
            }
            
            return result;
        }
    }
}