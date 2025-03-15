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
        private static readonly TranslationTaskQueue translationTaskQueue = new TranslationTaskQueue();
        private static readonly Queue<string> pendingTextQueue = new();
        
        // 错误恢复相关变量
        private static int consecutiveErrors = 0;
        private static readonly int MAX_ERRORS_BEFORE_RESTART = 5;
        private static DateTime lastLiveCaptionsCheck = DateTime.MinValue;
        private static readonly TimeSpan LIVE_CAPTIONS_CHECK_INTERVAL = TimeSpan.FromSeconds(5);
        
        // 自适应字幕捕获间隔
        private static int syncLoopDelay = 25;
        private static int translateLoopDelay = 40;
        private static readonly object delayLock = new object();
        
        private static readonly CancellationTokenSource globalCts = new CancellationTokenSource();

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
            try
            {
                InitializeTranslator();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Fatal Error] Failed to initialize translator: {ex.Message}");
                // 即使初始化失败，仍会继续尝试
            }
        }

        private static void InitializeTranslator()
        {
            window = LiveCaptionsHandler.LaunchLiveCaptions();
            if (window != null)
            {
                LiveCaptionsHandler.FixLiveCaptions(Window);
                LiveCaptionsHandler.HideLiveCaptions(Window);
            }
            else
            {
                Console.WriteLine("[Warning] Failed to launch LiveCaptions, will retry later");
            }
            
            caption = Caption.GetInstance();
            setting = Setting.Load();
        }

        public static void SyncLoop()
        {
            int idleCount = 0;
            int syncCount = 0;
            string previousOriginalCaption = string.Empty;
            
            while (!globalCts.Token.IsCancellationRequested)
            {
                try
                {
                    if (Window == null)
                    {
                        // 尝试重启 LiveCaptions
                        if ((DateTime.Now - lastLiveCaptionsCheck).TotalSeconds >= 3)
                        {
                            lastLiveCaptionsCheck = DateTime.Now;
                            Console.WriteLine("[Info] Attempting to restart LiveCaptions");
                            Window = LiveCaptionsHandler.LaunchLiveCaptions();
                            if (Window != null)
                            {
                                LiveCaptionsHandler.FixLiveCaptions(Window);
                                LiveCaptionsHandler.HideLiveCaptions(Window);
                                Caption.DisplayTranslatedCaption = "[LiveCaptions restored]";
                            }
                        }
                        Thread.Sleep(2000);
                        continue;
                    }

                    // 定期检查 LiveCaptions 是否还在运行
                    if ((DateTime.Now - lastLiveCaptionsCheck) > LIVE_CAPTIONS_CHECK_INTERVAL)
                    {
                        lastLiveCaptionsCheck = DateTime.Now;
                        try
                        {
                            var info = Window.Current;
                            var name = info.Name;
                        }
                        catch (ElementNotAvailableException)
                        {
                            Window = null;
                            Caption.DisplayTranslatedCaption = "[WARNING] LiveCaptions was unexpectedly closed, restarting...";
                            continue;
                        }
                    }

<<<<<<< HEAD
                    // 获取 LiveCaptions 识别的文本
                    string fullText = string.Empty;
                    try
                    {
                        fullText = LiveCaptionsHandler.GetCaptions(Window);
                    }
                    catch (ElementNotAvailableException)
                    {
                        Window = null;
                        Caption.DisplayTranslatedCaption = "[WARNING] LiveCaptions was unexpectedly closed, restarting...";
                        continue;
                    }
=======
                // Note: For certain languages (such as Japanese), LiveCaptions excessively uses `\n`.
                // Preprocess - remove the `.` between 2 uppercase letters.
                fullText = Regex.Replace(fullText, @"(?<=[A-Z])\s*\.\s*(?=[A-Z])", "");
                // Preprocess - Remove redundant `\n` around punctuation.
                fullText = Regex.Replace(fullText, @"\s*([.!?,])\s*", "$1 ");
                fullText = Regex.Replace(fullText, @"\s*([。！？，、])\s*", "$1");
                // Preprocess - Replace redundant `\n` within sentences with comma or period.
                fullText = TextUtil.ReplaceNewlines(fullText, TextUtil.MEDIUM_THRESHOLD);

                // Get the last sentence.
                int lastEOSIndex;
                if (Array.IndexOf(TextUtil.PUNC_EOS, fullText[^1]) != -1)
                    lastEOSIndex = fullText[0..^1].LastIndexOfAny(TextUtil.PUNC_EOS);
                else
                    lastEOSIndex = fullText.LastIndexOfAny(TextUtil.PUNC_EOS);
                string latestCaption = fullText.Substring(lastEOSIndex + 1);
                
                // If the last sentence is too short, extend it by adding the previous sentence.
                // Note: Expand `lastestCaption` instead of `DisplayOriginalCaption`,
                // because LiveCaptions may generate multiple characters including EOS at once.
                if (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < TextUtil.SHORT_THRESHOLD)
                {
                    lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                    latestCaption = fullText.Substring(lastEOSIndex + 1);
                }

                // `DisplayOriginalCaption`: The sentence to be displayed to the user.
                if (Caption.DisplayOriginalCaption.CompareTo(latestCaption) != 0)
                {
                    Caption.DisplayOriginalCaption = latestCaption;
                    // If the last sentence is too long, truncate it when displayed.
                    Caption.DisplayOriginalCaption = 
                        TextUtil.ShortenDisplaySentence(Caption.DisplayOriginalCaption, TextUtil.LONG_THRESHOLD);
                }

                // Prepare for `OriginalCaption`. If Expanded, only retain the complete sentence.
                int lastEOS = latestCaption.LastIndexOfAny(TextUtil.PUNC_EOS);
                if (lastEOS != -1)
                    latestCaption = latestCaption.Substring(0, lastEOS + 1);
                
                // `OriginalCaption`: The sentence to be really translated.
                if (Caption.OriginalCaption.CompareTo(latestCaption) != 0)
                {
                    Caption.OriginalCaption = latestCaption;
>>>>>>> b6661e87da83c8b28c6c1afb387deca63143704e
                    
                    if (string.IsNullOrEmpty(fullText))
                    {
                        Thread.Sleep(syncLoopDelay);
                        continue;
                    }

                    // 使用优化后的文本处理
                    fullText = TextUtil.ProcessTextWithRegex(fullText);
                    fullText = TextUtil.ReplaceNewlines(fullText, TextUtil.MEDIUM_THRESHOLD);

                    // 获取最后一个句子
                    int lastEOSIndex;
                    if (Array.IndexOf(TextUtil.PUNC_EOS, fullText[^1]) != -1)
                        lastEOSIndex = fullText[0..^1].LastIndexOfAny(TextUtil.PUNC_EOS);
                    else
                        lastEOSIndex = fullText.LastIndexOfAny(TextUtil.PUNC_EOS);
                    string latestCaption = fullText.Substring(lastEOSIndex + 1);
                    
                    // 如果最后一个句子太短，扩展它
                    if (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < TextUtil.SHORT_THRESHOLD)
                    {
                        lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                        latestCaption = fullText.Substring(lastEOSIndex + 1);
                    }

                    // 更新显示的原始字幕
                    bool captionChanged = Caption.DisplayOriginalCaption.CompareTo(latestCaption) != 0;
                    if (captionChanged)
                    {
                        Caption.DisplayOriginalCaption = latestCaption;
                        // 如果字幕太长，显示时截断
                        Caption.DisplayOriginalCaption = 
                            TextUtil.ShortenDisplaySentence(Caption.DisplayOriginalCaption, TextUtil.LONG_THRESHOLD);
                            
                        // 调整延迟以提高响应速度
                        lock (delayLock)
                        {
                            syncLoopDelay = Math.Max(syncLoopDelay - 5, 15);
                        }
                    }
                    else
                    {
                        // 无变化时，逐渐增加延迟
                        lock (delayLock)
                        {
                            syncLoopDelay = Math.Min(syncLoopDelay + 2, 100);
                        }
                    }

                    // 准备 OriginalCaption - 要翻译的句子
                    int lastEOS = latestCaption.LastIndexOfAny(TextUtil.PUNC_EOS);
                    if (lastEOS != -1)
                        latestCaption = latestCaption.Substring(0, lastEOS + 1);
                    
                    // 更新 OriginalCaption 并决定是否翻译
                    if (Caption.OriginalCaption.CompareTo(latestCaption) != 0)
                    {
                        Caption.OriginalCaption = latestCaption;
                        previousOriginalCaption = latestCaption;
                        
                        idleCount = 0;
                        if (Array.IndexOf(TextUtil.PUNC_EOS, Caption.OriginalCaption[^1]) != -1)
                        {
                            // 如果是完整句子，立即添加到翻译队列
                            syncCount = 0;
                            pendingTextQueue.Enqueue(Caption.OriginalCaption);
                        }
                        else if (Encoding.UTF8.GetByteCount(Caption.OriginalCaption) >= TextUtil.SHORT_THRESHOLD)
                        {
                            // 如果长度足够但不是完整句子，增加计数器
                            syncCount++;
                        }
                    }
                    else
                    {
                        idleCount++;
                    }

                    // 如果长时间未得到完整句子，或原始字幕长时间未变化，也加入翻译队列
                    if (syncCount > Setting.MaxSyncInterval ||
                        idleCount == Setting.MaxIdleInterval)
                    {
                        syncCount = 0;
                        pendingTextQueue.Enqueue(Caption.OriginalCaption);
                    }
                    
                    // 自适应延迟，减少 CPU 使用
                    Thread.Sleep(syncLoopDelay);
                    
                    // 重置连续错误计数
                    consecutiveErrors = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Error] SyncLoop error: {ex.Message}");
                    consecutiveErrors++;
                    
                    if (consecutiveErrors >= MAX_ERRORS_BEFORE_RESTART)
                    {
                        Console.WriteLine("[Warning] Too many consecutive errors, restarting LiveCaptions");
                        try
                        {
                            if (Window != null)
                            {
                                LiveCaptionsHandler.KillLiveCaptions(Window);
                            }
                            Window = null;
                            consecutiveErrors = 0;
                        }
                        catch (Exception restartEx)
                        {
                            Console.WriteLine($"[Error] Failed to restart LiveCaptions: {restartEx.Message}");
                        }
                    }
                    
                    Thread.Sleep(1000); // 错误后短暂等待
                }
            }
        }

        public static async Task TranslateLoop()
        {
            while (!globalCts.Token.IsCancellationRequested)
            {
                try
                {
                    if (Window == null)
                    {
                        Caption.DisplayTranslatedCaption = "[WARNING] LiveCaptions was unexpectedly closed, restarting...";
                        Window = LiveCaptionsHandler.LaunchLiveCaptions();
                        if (Window != null)
                        {
                            LiveCaptionsHandler.FixLiveCaptions(Window);
                            LiveCaptionsHandler.HideLiveCaptions(Window);
                            Caption.DisplayTranslatedCaption = "";
                        }
                        else
                        {
                            await Task.Delay(2000);
                            continue;
                        }
                    }

                    if (pendingTextQueue.Count > 0)
                    {
                        var originalText = pendingTextQueue.Dequeue();
                        bool hasEndPunctuation = Array.IndexOf(TextUtil.PUNC_EOS, originalText[^1]) != -1;
                        
                        // 决定翻译优先级
                        var priority = hasEndPunctuation ? 
                            TranslationTaskQueue.TaskPriority.High : 
                            TranslationTaskQueue.TaskPriority.Normal;

                        // 处理翻译或记录
                        if (LogOnlyFlag)
                        {
                            bool isOverwrite = await IsOverwrite(originalText);
                            await LogOnly(originalText, isOverwrite);
                            Caption.TranslatedCaption = string.Empty;
                            Caption.DisplayTranslatedCaption = "[Paused]";
                        }
                        else
                        {
                            // 加入翻译队列
                            translationTaskQueue.Enqueue(
                                token => Task.Run(() => Translate(originalText, token), token), 
                                originalText,
                                priority);
                                
                            // 更新界面显示
                            if (!string.IsNullOrEmpty(translationTaskQueue.Output))
                            {
                                Caption.TranslatedCaption = translationTaskQueue.Output;
                                Caption.DisplayTranslatedCaption = 
                                    TextUtil.ShortenDisplaySentence(Caption.TranslatedCaption, TextUtil.VERYLONG_THRESHOLD);
                            }
                        }

                        // 如果是完整句子，暂停以获得更好的视觉体验
                        if (hasEndPunctuation)
                        {
                            // 完整句子使用较短暂停
                            await Task.Delay(300);
                        }
                        
                        // 重置延迟，提高响应速度
                        lock (delayLock)
                        {
                            translateLoopDelay = Math.Max(translateLoopDelay - 5, 20);
                        }
                    }
                    else
                    {
                        // 无任务时，增加延迟
                        lock (delayLock)
                        {
                            translateLoopDelay = Math.Min(translateLoopDelay + 5, 100);
                        }
                    }
<<<<<<< HEAD
                    
                    await Task.Delay(translateLoopDelay);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Error] TranslateLoop error: {ex.Message}");
                    await Task.Delay(1000); // 错误后短暂等待
=======

                    if (LogOnlyFlag)
                    {
                        Caption.TranslatedCaption = string.Empty;
                        Caption.DisplayTranslatedCaption = "[Paused]";
                    }
                    else if (!string.IsNullOrEmpty(translationTaskQueue.Output))
                    {
                        Caption.TranslatedCaption = translationTaskQueue.Output;
                        Caption.DisplayTranslatedCaption = 
                            TextUtil.ShortenDisplaySentence(Caption.TranslatedCaption, TextUtil.VERYLONG_THRESHOLD);
                    }

                    // If the original sentence is a complete sentence, pause for better visual experience.
                    if (Array.IndexOf(TextUtil.PUNC_EOS, originalSnapshot[^1]) != -1)
                        Thread.Sleep(600);
>>>>>>> b6661e87da83c8b28c6c1afb387deca63143704e
                }
            }
        }

        public static async Task<string> Translate(string text, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
                
            try
            {
                Stopwatch? sw = null;
<<<<<<< HEAD
                if (Setting?.MainWindow.LatencyShow == true)
=======
                if (Setting.MainWindow.LatencyShow)
>>>>>>> b6661e87da83c8b28c6c1afb387deca63143704e
                {
                    sw = Stopwatch.StartNew();
                }

<<<<<<< HEAD
                // 调用翻译 API，这里使用了改进后的 API 调用方法
                string translatedText = await TranslateAPI.TranslateFunction(text, token);
=======
                translatedText = await TranslateAPI.TranslateFunction(text, token);
>>>>>>> b6661e87da83c8b28c6c1afb387deca63143704e

                if (sw != null)
                {
                    sw.Stop();
                    translatedText = $"[{sw.ElapsedMilliseconds} ms] " + translatedText;
                }
                
                return translatedText;
            }
            catch (OperationCanceledException ex)
            {
                if (token.IsCancellationRequested)
                    return string.Empty;
                return $"[Translation Cancelled] {ex.Message}";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Translation failed: {ex.Message}");
                return $"[Translation Failed] {ex.Message}";
            }
        }

        public static async Task Log(string originalText, string translatedText, 
            bool isOverwrite = false, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(originalText))
                return;
                
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
            if (string.IsNullOrEmpty(originalText))
                return;
                
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
<<<<<<< HEAD
            try
            {
                var lastLog = await SQLiteHistoryLogger.LoadLastTranslation(token);
                if (lastLog == null)
                    return;
                    
                if (Caption?.LogCards.Count >= Setting?.MainWindow.CaptionLogMax)
                    Caption?.LogCards.Dequeue();
                    
                Caption?.LogCards.Enqueue(lastLog);
                Caption?.OnPropertyChanged("DisplayLogCards");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to add log card: {ex.Message}");
            }
=======
            var lastLog = await SQLiteHistoryLogger.LoadLastTranslation(token);
            if (lastLog == null)
                return;
            if (Caption?.LogCards.Count >= Setting?.MainWindow.CaptionLogMax)
                Caption?.LogCards.Dequeue();
            Caption?.LogCards.Enqueue(lastLog);
            Caption?.OnPropertyChanged("DisplayLogCards");
>>>>>>> b6661e87da83c8b28c6c1afb387deca63143704e
        }
        
        public static async Task<bool> IsOverwrite(string originalText, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(originalText))
                return false;
                
            try
            {
                // 如果这个文本与上一个太相似，重写它
                string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(token);
                if (string.IsNullOrEmpty(lastOriginalText))
                    return false;
                    
                double similarity = TextUtil.Similarity(originalText, lastOriginalText);
                return similarity > 0.66;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to check overwrite: {ex.Message}");
                return false;
            }
        }
        
        // 新增方法：在应用程序退出时清理资源
        public static async Task Cleanup()
        {
            try
            {
                // 取消所有挂起的任务
                globalCts.Cancel();
                
                // 确保所有挂起的日志都被写入
                await SQLiteHistoryLogger.Cleanup();
                
                // 关闭 LiveCaptions
                if (Window != null)
                {
                    LiveCaptionsHandler.RestoreLiveCaptions(Window);
                    LiveCaptionsHandler.KillLiveCaptions(Window);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Cleanup failed: {ex.Message}");
            }
        }
    }
}