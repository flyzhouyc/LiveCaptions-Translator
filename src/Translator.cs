using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public static class Translator
    {
        private static AutomationElement? window = null;
        private static Caption? caption = null;
        private static Setting? setting = null;
        private static readonly Queue<string> pendingTextQueue = new();
        private static readonly Queue<string> contextHistory = new(5); // 保存最近5句话作为上下文
        private static readonly Dictionary<string, int> apiQualityScores = new();
        private static string lastRecommendedApi = string.Empty;
        private static readonly Random random = new Random();

        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }
        public static Caption? Caption => caption;
        public static Setting? Setting => setting;
        public static bool LogOnlyFlag { get; set; } = false;
        
        public static event Action? TranslationLogged;

        static Translator()
        {
            window = LiveCaptionsHandler.LaunchLiveCaptions();
            LiveCaptionsHandler.FixLiveCaptions(Window);
            LiveCaptionsHandler.HideLiveCaptions(Window);
            
            caption = Caption.GetInstance();
            setting = Setting.Load();
        }

        public static void SyncLoop()
        {
            int idleCount = 0;
            int syncCount = 0;

            while (true)
            {
                if (Window == null)
                {
                    Thread.Sleep(2000);
                    continue;
                }

                string fullText = string.Empty;
                try
                {
                    // Check LiveCaptions.exe still alive
                    var info = Window.Current;
                    var name = info.Name;
                    // Get the text recognized by LiveCaptions (10-20ms)
                    fullText = LiveCaptionsHandler.GetCaptions(Window);     
                }
                catch (ElementNotAvailableException)
                {
                    Window = null;
                    continue;
                }
                if (string.IsNullOrEmpty(fullText))
                    continue;

                // Note: For certain languages (such as Japanese), LiveCaptions excessively uses `\n`.
                // Preprocess - remove the `.` between two uppercase letters. (Cope with acronym)
                fullText = Regex.Replace(fullText, @"([A-Z])\s*\.\s*([A-Z])(?![A-Za-z]+)", "$1$2");
                fullText = Regex.Replace(fullText, @"([A-Z])\s*\.\s*([A-Z])(?=[A-Za-z]+)", "$1 $2");
                // Preprocess - Remove redundant `\n` around punctuation.
                fullText = Regex.Replace(fullText, @"\s*([.!?,])\s*", "$1 ");
                fullText = Regex.Replace(fullText, @"\s*([。！？，、])\s*", "$1");
                // Preprocess - Replace redundant `\n` within sentences with comma or period.
                fullText = TextUtil.ReplaceNewlines(fullText, TextUtil.MEDIUM_THRESHOLD);
                
                // Prevent adding the last sentence from previous running to log cards
                // before the first sentence is completed.
                if (fullText.IndexOfAny(TextUtil.PUNC_EOS) == -1 && Caption.LogCards.Count > 0)
                {
                    Caption.LogCards.Clear();
                    Caption.OnPropertyChanged("DisplayLogCards");
                }

                // Get the last sentence.
                int lastEOSIndex;
                if (Array.IndexOf(TextUtil.PUNC_EOS, fullText[^1]) != -1)
                    lastEOSIndex = fullText[0..^1].LastIndexOfAny(TextUtil.PUNC_EOS);
                else
                    lastEOSIndex = fullText.LastIndexOfAny(TextUtil.PUNC_EOS);
                string latestCaption = fullText.Substring(lastEOSIndex + 1);
                
                // If the last sentence is too short, extend it by adding the previous sentence.
                // Note: LiveCaptions may generate multiple characters including EOS at once.
                if (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < TextUtil.SHORT_THRESHOLD)
                {
                    lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                    latestCaption = fullText.Substring(lastEOSIndex + 1);
                }
                
                // `OverlayOriginalCaption`: The sentence to be displayed on Overlay Window.
                Caption.OverlayOriginalCaption = latestCaption;
                for (int historyCount = Math.Min(Setting.OverlayWindow.HistoryMax, Caption.LogCards.Count);
                     historyCount > 0 && lastEOSIndex > 0; 
                     historyCount--)
                {
                    lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                    Caption.OverlayOriginalCaption = fullText.Substring(lastEOSIndex + 1);
                }

                // `DisplayOriginalCaption`: The sentence to be displayed on Main Window.
                if (Caption.DisplayOriginalCaption.CompareTo(latestCaption) != 0)
                {
                    Caption.DisplayOriginalCaption = latestCaption;
                    // If the last sentence is too long, truncate it when displayed.
                    Caption.DisplayOriginalCaption = 
                        TextUtil.ShortenDisplaySentence(Caption.DisplayOriginalCaption, TextUtil.VERYLONG_THRESHOLD);
                }

                // Prepare for `OriginalCaption`. If Expanded, only retain the complete sentence.
                int lastEOS = latestCaption.LastIndexOfAny(TextUtil.PUNC_EOS);
                if (lastEOS != -1)
                    latestCaption = latestCaption.Substring(0, lastEOS + 1);
                // `OriginalCaption`: The sentence to be really translated.
                if (Caption.OriginalCaption.CompareTo(latestCaption) != 0)
                {
                    Caption.OriginalCaption = latestCaption;
                    
                    idleCount = 0;
                    if (Array.IndexOf(TextUtil.PUNC_EOS, Caption.OriginalCaption[^1]) != -1)
                    {
                        syncCount = 0;
                        pendingTextQueue.Enqueue(Caption.OriginalCaption);
                    }
                    else if (Encoding.UTF8.GetByteCount(Caption.OriginalCaption) >= TextUtil.SHORT_THRESHOLD)
                        syncCount++;
                }
                else
                    idleCount++;

                // `TranslateFlag` determines whether this sentence should be translated.
                // When `OriginalCaption` remains unchanged, `idleCount` +1; when `OriginalCaption` changes, `MaxSyncInterval` +1.
                if (syncCount > Setting.MaxSyncInterval ||
                    idleCount == Setting.MaxIdleInterval)
                {
                    syncCount = 0;
                    pendingTextQueue.Enqueue(Caption.OriginalCaption);
                }
                Thread.Sleep(25);
            }
        }

        public static async Task TranslateLoop()
        {
            var translationTaskQueue = new TranslationTaskQueue();

            while (true)
            {
                if (Window == null)
                {
                    Caption.DisplayTranslatedCaption = "[WARNING] LiveCaptions was unexpectedly closed, restarting...";
                    Window = LiveCaptionsHandler.LaunchLiveCaptions();
                    Caption.DisplayTranslatedCaption = "";
                }

                if (pendingTextQueue.Count > 0)
                {
                    var originalSnapshot = pendingTextQueue.Dequeue();

                    if (LogOnlyFlag)
                    {
                        bool isOverwrite = await IsOverwrite(originalSnapshot);
                        await LogOnly(originalSnapshot, isOverwrite);
                    }
                    else
                    {
                        // 更新上下文历史
                        UpdateContextHistory(originalSnapshot);

                        // 确定使用哪个API - 智能选择或尝试建议的API
                        string apiToUse = DetermineApiToUse();

                        translationTaskQueue.Enqueue(token => Task.Run(
                            () => TranslateWithContext(originalSnapshot, apiToUse, token), token), originalSnapshot);
                    }

                    if (LogOnlyFlag)
                    {
                        Caption.TranslatedCaption = string.Empty;
                        Caption.DisplayTranslatedCaption = "[Paused]";
                        Caption.OverlayTranslatedCaption = "[Paused]";
                    }
                    else if (!string.IsNullOrEmpty(translationTaskQueue.Output))
                    {
                        Caption.TranslatedCaption = translationTaskQueue.Output;
                        Caption.DisplayTranslatedCaption = 
                            TextUtil.ShortenDisplaySentence(Caption.TranslatedCaption, TextUtil.VERYLONG_THRESHOLD);
                        
                        var match = Regex.Match(Caption.TranslatedCaption, @"^(\[.+\] )?(.*)$");
                        string noticePrefix = match.Groups[1].Value;
                        string translatedText = match.Groups[2].Value;
                        Caption.OverlayTranslatedCaption = noticePrefix + Caption.OverlayTranslatedPrefix + translatedText;
                    }

                    // If the original sentence is a complete sentence, pause for better visual experience.
                    if (Array.IndexOf(TextUtil.PUNC_EOS, originalSnapshot[^1]) != -1)
                        Thread.Sleep(600);
                }
                Thread.Sleep(40);
            }
        }

        // 更新用于上下文的历史句子
        private static void UpdateContextHistory(string sentence)
        {
            if (!string.IsNullOrWhiteSpace(sentence))
            {
                // 保持队列最大长度为5
                while (contextHistory.Count >= 5)
                    contextHistory.Dequeue();
                
                contextHistory.Enqueue(sentence);
            }
        }

        // 确定当前应该使用哪个API
        private static string DetermineApiToUse()
        {
            string currentApi = Translator.Setting.ApiName;
            
            // 有推荐API且质量评分高于当前API时，有20%概率尝试推荐的API
            if (!string.IsNullOrEmpty(lastRecommendedApi) && 
                lastRecommendedApi != currentApi && 
                apiQualityScores.TryGetValue(lastRecommendedApi, out int recommendedScore) &&
                (!apiQualityScores.TryGetValue(currentApi, out int currentScore) || recommendedScore > currentScore) &&
                random.Next(100) < 20)
            {
                return lastRecommendedApi;
            }
            
            // 否则使用用户设置的API
            return currentApi;
        }

        // 带上下文的翻译方法
        public static async Task<string> TranslateWithContext(string text, string apiName, CancellationToken token = default)
        {
            try
            {
                var sw = Setting.MainWindow.LatencyShow ? Stopwatch.StartNew() : null;
                
                string translatedText;
                string sourceLanguage = "auto";
                string targetLanguage = Setting.TargetLanguage;
                
                // 构建上下文文本 (仅对LLM类API)
                if (IsLLMBasedAPI(apiName) && contextHistory.Count > 1)
                {
                    // 为LLM创建带上下文的提示词
                    string contextPrompt = CreateContextPrompt(text, apiName);
                    translatedText = await TranslateAPI.TranslateWithAPI(contextPrompt, apiName, token);
                }
                else
                {
                    // 对传统API使用普通翻译方法
                    translatedText = await TranslateAPI.TranslateWithAPI(text, apiName, token);
                }
                
                if (sw != null)
                {
                    sw.Stop();
                    translatedText = $"[{sw.ElapsedMilliseconds} ms] " + translatedText;
                }

                // 评估翻译质量
                int qualityScore = TranslationQualityEvaluator.EvaluateQuality(text, translatedText);
                UpdateApiQualityScore(apiName, qualityScore);
                
                // 对低质量翻译尝试改进
                if (qualityScore < 70)
                {
                    var (improvedTranslation, apiSuggestion) = TranslationQualityEvaluator.GetImprovedTranslation(
                        translatedText, text, apiName, qualityScore);
                    
                    if (improvedTranslation != translatedText)
                    {
                        translatedText = improvedTranslation;
                    }
                    
                    if (apiSuggestion != apiName)
                    {
                        // 记录API建议，但不立即切换，让用户或之后的翻译决定是否采用
                        lastRecommendedApi = apiSuggestion;
                    }
                }
                
                return translatedText;
            }
            catch (OperationCanceledException)
            {
                return string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Translation failed: {ex.Message}");
                return $"[Translation Failed] {ex.Message}";
            }
        }

        // 创建包含上下文的提示词
        private static string CreateContextPrompt(string text, string apiName)
        {
            StringBuilder contextBuilder = new StringBuilder();
            
            // 仅使用最近的2-3句话作为上下文
            int contextSentencesToUse = Math.Min(contextHistory.Count - 1, 2); // 减1是因为当前句子已经在队列里了
            if (contextSentencesToUse > 0)
            {
                contextBuilder.AppendLine("Previous sentences (context):");
                
                var contextSentences = contextHistory.Take(contextSentencesToUse).ToArray();
                for (int i = 0; i < contextSentences.Length; i++)
                {
                    contextBuilder.AppendLine($"- {contextSentences[i]}");
                }
                
                contextBuilder.AppendLine("\nCurrent sentence to translate:");
            }
            
            contextBuilder.Append("🔤 ").Append(text).Append(" 🔤");
            
            return contextBuilder.ToString();
        }

        // 判断是否为基于LLM的API
        private static bool IsLLMBasedAPI(string apiName)
        {
            return apiName == "OpenAI" || apiName == "Ollama" || apiName == "OpenRouter";
        }

        // 更新API质量评分
        private static void UpdateApiQualityScore(string apiName, int qualityScore)
        {
            if (!apiQualityScores.ContainsKey(apiName))
            {
                apiQualityScores[apiName] = qualityScore;
            }
            else
            {
                // 使用加权平均值更新评分，新分数权重为0.2
                apiQualityScores[apiName] = (int)(apiQualityScores[apiName] * 0.8 + qualityScore * 0.2);
            }

            TranslationQualityEvaluator.RecordQualityForAPI(
                apiName, qualityScore, "auto", Setting.TargetLanguage);
        }

        public static async Task<string> Translate(string text, CancellationToken token = default)
        {
            return await TranslateWithContext(text, Setting.ApiName, token);
        }

        public static async Task Log(string originalText, string translatedText, 
            bool isOverwrite = false, CancellationToken token = default)
        {
            string targetLanguage, apiName;
            if (Setting != null)
            {
                targetLanguage = Setting.TargetLanguage;
                apiName = Setting.ApiName;
            }
            else
            {
                targetLanguage = "N/A";
                apiName = "N/A";
            }

            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, translatedText, targetLanguage, apiName);
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Logging history failed: {ex.Message}");
            }
        }

        public static async Task LogOnly(string originalText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, "N/A", "N/A", "LogOnly");
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Logging history failed: {ex.Message}");
            }
        }
        
        public static async Task AddLogCard(CancellationToken token = default)
        {
            var lastLog = await SQLiteHistoryLogger.LoadLastTranslation(token);
            if (lastLog == null)
                return;
            if (Caption?.LogCards.Count >= Setting?.MainWindow.CaptionLogMax)
                Caption.LogCards.Dequeue();
            Caption?.LogCards.Enqueue(lastLog);
            Caption?.OnPropertyChanged("DisplayLogCards");
        }
        
        public static async Task<bool> IsOverwrite(string originalText, CancellationToken token = default)
        {
            // If this text is too similar to the last one, rewrite it when logging.
            string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(token);
            if (lastOriginalText == null)
                return false;
            double similarity = TextUtil.Similarity(originalText, lastOriginalText);
            return similarity > 0.66;
        }
    }
}