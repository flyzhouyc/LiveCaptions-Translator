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
        private static readonly Queue<TranslationRequest> pendingTextQueue = new();
        
        // 错误恢复相关变量
        private static int consecutiveErrors = 0;
        private static readonly int MAX_ERRORS_BEFORE_RESTART = 5;
        private static DateTime lastLiveCaptionsCheck = DateTime.MinValue;
        private static readonly TimeSpan LIVE_CAPTIONS_CHECK_INTERVAL = TimeSpan.FromSeconds(5);
        
        // 优化1: 降低初始延迟以提高响应速度
        private static int syncLoopDelay = 15;  // 从25降至15
        private static int translateLoopDelay = 25;  // 从40降至25
        private static readonly object delayLock = new object();
        
        // 优化2: 添加增量翻译支持的变量
        private static string lastTranslatedText = string.Empty;
        private static DateTime lastTranslationTime = DateTime.MinValue;
        private static readonly TimeSpan INCREMENTAL_THRESHOLD = TimeSpan.FromMilliseconds(1500);
        
        // 优化3: 增加翻译状态指示
        private static bool isTranslationInProgress = false;
        
        private static readonly CancellationTokenSource globalCts = new CancellationTokenSource();

        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }
        public static Caption? Caption => caption;
        public static Setting? Setting => setting;
        public static bool LogOnlyFlag { get; set; } = false;
        public static bool IsTranslating => isTranslationInProgress;
        
        public static event Action? TranslationLogged;
        public static event Action? TranslationStarted;
        public static event Action? TranslationCompleted;

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
                            
                        // 优化1: 更激进地减少延迟，提高响应速度
                        lock (delayLock)
                        {
                            syncLoopDelay = Math.Max(syncLoopDelay - 8, 10);
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
                        // 优化2: 检测增量更新场景
                        bool isIncremental = Caption.OriginalCaption.StartsWith(previousOriginalCaption) && 
                                           Caption.OriginalCaption.Length > previousOriginalCaption.Length;
                                           
                        string oldCaption = Caption.OriginalCaption;
                        Caption.OriginalCaption = latestCaption;
                        previousOriginalCaption = latestCaption;
                        
                        idleCount = 0;
                        
                        // 优化3: 改进翻译触发条件
                        bool shouldTranslate = false;
                        TranslationTaskQueue.TaskPriority priority = TranslationTaskQueue.TaskPriority.Normal;
                        
                        // 如果是完整句子，立即高优先级翻译
                        if (Array.IndexOf(TextUtil.PUNC_EOS, Caption.OriginalCaption[^1]) != -1)
                        {
                            shouldTranslate = true;
                            priority = TranslationTaskQueue.TaskPriority.High;
                            syncCount = 0;
                        }
                        // 当文本变化超过一定字符数，触发普通优先级翻译
                        else if (Math.Abs(Caption.OriginalCaption.Length - oldCaption.Length) > 10)
                        {
                            shouldTranslate = true;
                        }
                        // 如果是增量更新且距离上次翻译时间不长，使用增量翻译
                        else if (isIncremental && 
                                (DateTime.Now - lastTranslationTime) < INCREMENTAL_THRESHOLD)
                        {
                            // 提交增量翻译请求
                            pendingTextQueue.Enqueue(new TranslationRequest
                            {
                                Text = Caption.OriginalCaption,
                                PreviousText = previousOriginalCaption,
                                IsIncremental = true,
                                Priority = TranslationTaskQueue.TaskPriority.Normal
                            });
                        }
                        else if (Encoding.UTF8.GetByteCount(Caption.OriginalCaption) >= TextUtil.SHORT_THRESHOLD)
                        {
                            // 如果长度足够但不是完整句子，增加计数器
                            syncCount++;
                        }
                        
                        if (shouldTranslate)
                        {
                            pendingTextQueue.Enqueue(new TranslationRequest
                            {
                                Text = Caption.OriginalCaption,
                                PreviousText = oldCaption,
                                IsIncremental = isIncremental,
                                Priority = priority
                            });
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
                        pendingTextQueue.Enqueue(new TranslationRequest
                        {
                            Text = Caption.OriginalCaption,
                            PreviousText = string.Empty,
                            IsIncremental = false,
                            Priority = TranslationTaskQueue.TaskPriority.Low
                        });
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

                    // 优化4: 智能任务取消，仅保留最新/最高优先级的任务
                    if (pendingTextQueue.Count > 1)
                    {
                        var newerRequests = new List<TranslationRequest>();
                        while (pendingTextQueue.Count > 0)
                        {
                            newerRequests.Add(pendingTextQueue.Dequeue());
                        }
                        
                        // 根据优先级和时间戳排序
                        newerRequests = newerRequests
                            .OrderByDescending(r => (int)r.Priority)
                            .ThenBy(r => r.IsIncremental ? 0 : 1)
                            .ToList();
                            
                        // 只保留最重要的1-2个请求
                        var keptRequests = newerRequests.Take(newerRequests.Count > 3 ? 2 : 1).ToList();
                        
                        // 取消队列中正在进行的较旧任务
                        translationTaskQueue.CancelOlderTasks();
                        
                        // 重新添加保留的请求
                        foreach (var request in keptRequests)
                        {
                            pendingTextQueue.Enqueue(request);
                        }
                    }
                    
                    if (pendingTextQueue.Count > 0)
                    {
                        var request = pendingTextQueue.Dequeue();
                        
                        // 处理翻译或记录
                        if (LogOnlyFlag)
                        {
                            bool isOverwrite = await IsOverwrite(request.Text);
                            await LogOnly(request.Text, isOverwrite);
                            Caption.TranslatedCaption = string.Empty;
                            Caption.DisplayTranslatedCaption = "[Paused]";
                        }
                        else
                        {
                            // 优化5: 使用增量翻译和状态指示
                            isTranslationInProgress = true;
                            TranslationStarted?.Invoke();
                            
                            // 更新UI显示状态
                            if (!string.IsNullOrEmpty(Caption.TranslatedCaption))
                            {
                                Caption.DisplayTranslatedCaption = Caption.TranslatedCaption + " ...";
                            }
                            
                            // 选择合适的翻译方法
                            if (request.IsIncremental && 
                                !string.IsNullOrEmpty(lastTranslatedText) &&
                                (DateTime.Now - lastTranslationTime) < INCREMENTAL_THRESHOLD)
                            {
                                // 使用增量翻译
                                translationTaskQueue.EnqueueIncrementalTranslation(
                                    request.PreviousText, 
                                    request.Text, 
                                    lastTranslatedText,
                                    request.Priority);
                            }
                            else
                            {
                                // 使用完整翻译
                                translationTaskQueue.Enqueue(
                                    token => Task.Run(() => Translate(request.Text, token), token), 
                                    request.Text,
                                    request.Priority);
                            }
                                
                            // 更新界面显示和状态
                            if (!string.IsNullOrEmpty(translationTaskQueue.Output))
                            {
                                lastTranslatedText = translationTaskQueue.Output;
                                lastTranslationTime = DateTime.Now;
                                
                                Caption.TranslatedCaption = translationTaskQueue.Output;
                                Caption.DisplayTranslatedCaption = 
                                    TextUtil.ShortenDisplaySentence(Caption.TranslatedCaption, TextUtil.VERYLONG_THRESHOLD);
                                    
                                isTranslationInProgress = false;
                                TranslationCompleted?.Invoke();
                            }
                        }

                        // 如果是完整句子，暂停以获得更好的视觉体验
                        bool hasEndPunctuation = Array.IndexOf(TextUtil.PUNC_EOS, request.Text[^1]) != -1;
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
                    
                    await Task.Delay(translateLoopDelay);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Error] TranslateLoop error: {ex.Message}");
                    isTranslationInProgress = false;
                    await Task.Delay(1000); // 错误后短暂等待
                }
            }
        }

        /// <summary>
        /// 翻译指定文本
        /// </summary>
        public static async Task<string> Translate(string text, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
                
            try
            {
                Stopwatch? sw = null;
                if (Setting?.MainWindow.LatencyShow == true)
                {
                    sw = Stopwatch.StartNew();
                }

                // 调用翻译 API
                string translatedText = await TranslateAPI.TranslateFunction(text, token);

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
        
        /// <summary>
        /// 增量翻译，处理新增内容
        /// </summary>
        public static async Task<string> TranslateIncremental(string fullText, string incrementalText, 
                                                            string previousTranslation, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(incrementalText))
                return previousTranslation;
                
            try
            {
                // 处理增量部分
                string incrementalTranslation = await TranslateAPI.TranslateFunction(incrementalText, token);
                
                // 简单的拼接策略 - 实际项目中可能需要更复杂的合并逻辑
                return previousTranslation + " " + incrementalTranslation;
            }
            catch (OperationCanceledException ex)
            {
                if (token.IsCancellationRequested)
                    return string.Empty;
                return previousTranslation;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Incremental translation failed: {ex.Message}");
                return previousTranslation;
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
        
        // 在应用程序退出时清理资源
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
    
    /// <summary>
    /// 翻译请求模型
    /// </summary>
    public class TranslationRequest
    {
        public string Text { get; set; } = string.Empty;
        public string PreviousText { get; set; } = string.Empty;
        public bool IsIncremental { get; set; } = false;
        public TranslationTaskQueue.TaskPriority Priority { get; set; } = TranslationTaskQueue.TaskPriority.Normal;
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}