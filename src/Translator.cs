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
        // 增加缓存上一次处理的完整文本，用于语义分析和连续性检测
        private static string lastProcessedFullText = string.Empty;

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

                // Get the text recognized by LiveCaptions.
                string fullText = string.Empty;
                try
                {
                    // Check LiveCaptions.exe still alive
                    var info = Window.Current;
                    var name = info.Name;

                    fullText = LiveCaptionsHandler.GetCaptions(Window);     // 10-20ms
                }
                catch (ElementNotAvailableException)
                {
                    Window = null;
                    continue;
                }
                if (string.IsNullOrEmpty(fullText))
                    continue;

                // 检查与上次文本的差异，以确保连续性
                string addedContent = TextUtil.GetAddedContent(lastProcessedFullText, fullText);
                lastProcessedFullText = fullText;

                // Note: For certain languages (such as Japanese), LiveCaptions excessively uses `\n`.
                // Preprocess - remove the `.` between 2 uppercase letters.
                fullText = Regex.Replace(fullText, @"(?<=[A-Z])\s*\.\s*(?=[A-Z])", "");
                // Preprocess - Remove redundant `\n` around punctuation.
                fullText = Regex.Replace(fullText, @"\s*([.!?,])\s*", "$1 ");
                fullText = Regex.Replace(fullText, @"\s*([。！？，、])\s*", "$1");
                // Preprocess - Replace redundant `\n` within sentences with comma or period.
                fullText = TextUtil.ReplaceNewlines(fullText, TextUtil.MEDIUM_THRESHOLD);

                // 改进：使用语义分割，而不仅仅依靠句尾标点
                string latestCaption = TextUtil.ExtractMeaningfulSegment(fullText);
                
                // `DisplayOriginalCaption`: The sentence to be displayed to the user.
                if (Caption.DisplayOriginalCaption.CompareTo(latestCaption) != 0)
                {
                    Caption.DisplayOriginalCaption = latestCaption;
                    // 优化显示截断：使用更智能的方式截断
                    Caption.DisplayOriginalCaption = 
                        TextUtil.ShortenDisplaySentenceSmartly(Caption.DisplayOriginalCaption, TextUtil.LONG_THRESHOLD);
                }

                // `OriginalCaption`: The sentence to be really translated.
                // 确保句子有足够的完整性
                string translationContent = TextUtil.EnsureSentenceCompleteness(latestCaption);
                if (Caption.OriginalCaption.CompareTo(translationContent) != 0)
                {
                    Caption.OriginalCaption = translationContent;
                    
                    idleCount = 0;
                    // 优化翻译触发条件 - 使用语义完整性判断而不仅是根据标点
                    if (TextUtil.IsMeaningfulForTranslation(Caption.OriginalCaption))
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
                        translationTaskQueue.Enqueue(token => Task.Run(
                            () => Translate(originalSnapshot, token), token), originalSnapshot);
                    }

                    if (LogOnlyFlag)
                    {
                        Caption.TranslatedCaption = string.Empty;
                        Caption.DisplayTranslatedCaption = "[Paused]";
                    }
                    else if (!string.IsNullOrEmpty(translationTaskQueue.Output))
                    {
                        Caption.TranslatedCaption = translationTaskQueue.Output;
                        Caption.DisplayTranslatedCaption = 
                            TextUtil.ShortenDisplaySentenceSmartly(Caption.TranslatedCaption, TextUtil.VERYLONG_THRESHOLD);
                    }

                    // If the original sentence is a complete sentence, pause for better visual experience.
                    if (TextUtil.IsMeaningfulForTranslation(originalSnapshot))
                        Thread.Sleep(600);
                }
                Thread.Sleep(40);
            }
        }

        public static async Task<string> Translate(string text, CancellationToken token = default)
        {
            string translatedText = string.Empty;
            int maxRetries = 2; // 添加重试次数
            int currentRetry = 0;
            
            while(currentRetry <= maxRetries)
            {
                try
                {
                    Stopwatch? sw = null;
                    if (Setting.MainWindow.LatencyShow)
                    {
                        sw = Stopwatch.StartNew();
                    }

                    translatedText = await TranslateAPI.TranslateFunction(text, token);

                    if (sw != null)
                    {
                        sw.Stop();
                        translatedText = $"[{sw.ElapsedMilliseconds} ms] " + translatedText;
                    }
                    
                    // 如果翻译成功，返回结果
                    if (!translatedText.StartsWith("[Translation Failed]"))
                        return translatedText;
                    
                    // 如果翻译失败但不是最后一次尝试，进行重试
                    if (currentRetry < maxRetries)
                    {
                        currentRetry++;
                        // 指数退避策略 - 每次等待时间增加
                        await Task.Delay(500 * (int)Math.Pow(2, currentRetry), token);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Error] Translation failed: {ex.Message}");
                    
                    // 如果不是最后一次尝试，进行重试
                    if (currentRetry < maxRetries)
                    {
                        currentRetry++;
                        await Task.Delay(500 * (int)Math.Pow(2, currentRetry), token);
                        continue;
                    }
                    
                    return $"[Translation Failed] {ex.Message}";
                }
                
                // 如果到达这里，说明重试机会已用完
                break;
            }
            
            // 所有重试都失败
            return translatedText ?? $"[Translation Failed] After {maxRetries} retries";
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
                Caption?.LogCards.Dequeue();
            Caption?.LogCards.Enqueue(lastLog);
            Caption?.OnPropertyChanged("DisplayLogCards");
        }
        
        public static async Task<bool> IsOverwrite(string originalText, CancellationToken token = default)
        {
            // 改进相似度判断：提高相似度阈值，添加更严格的内容比较
            string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(token);
            if (lastOriginalText == null)
                return false;
                
            // 只有当相似度非常高且内容长度相近时才判定为覆盖
            double similarity = TextUtil.Similarity(originalText, lastOriginalText);
            double lengthRatio = (double)Math.Max(originalText.Length, lastOriginalText.Length) / 
                                Math.Min(originalText.Length, lastOriginalText.Length);
                                
            // 提高相似度阈值到0.8，并增加长度比例判断条件
            return similarity > 0.8 && lengthRatio < 1.2;
        }
    }
}