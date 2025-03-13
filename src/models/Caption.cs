using System.Windows.Automation;
using System.Text;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class Caption : INotifyPropertyChanged
    {
        private static Caption? instance = null;
        public event PropertyChangedEventHandler? PropertyChanged;

        private string displayOriginalCaption = "";
        private string displayTranslatedCaption = "";
        // 添加锁对象
        private readonly object _syncLock = new object();
        private readonly object _translateLock = new object();

        public string OriginalCaption { get; set; } = "";
        public string TranslatedCaption { get; set; } = "";
        public string DisplayOriginalCaption
        {
            get => displayOriginalCaption;
            set
            {
                displayOriginalCaption = value;
                OnPropertyChanged("DisplayOriginalCaption");
            }
        }
        public string DisplayTranslatedCaption
        {
            get => displayTranslatedCaption;
            set
            {
                displayTranslatedCaption = value;
                OnPropertyChanged("DisplayTranslatedCaption");
            }
        }

        // 修改TranslateFlag为线程安全
        private bool translateFlag = false;
        public bool TranslateFlag
        {
            get
            {
                lock (_syncLock)
                {
                    return translateFlag;
                }
            }
            set
            {
                lock (_syncLock)
                {
                    translateFlag = value;
                }
            }
        }
        public bool LogOnlyFlag { get; set; } = false;

        public Queue<TranslationHistoryEntry> LogCards { get; } = new(6);
        public IEnumerable<TranslationHistoryEntry> DisplayLogCards => LogCards.Reverse();

        private Caption() { }

        public static Caption GetInstance()
        {
            if (instance != null)
                return instance;
            instance = new Caption();
            return instance;
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }

        public void Sync()
        {
            int idleCount = 0;
            int syncCount = 0;
            int errorCount = 0;
            const int MAX_ERROR_COUNT = 5;
            DateTime lastRecoveryAttempt = DateTime.MinValue;

            while (true)
            {
                // 检查LiveCaptions窗口状态
                if (App.Window == null || !LiveCaptionsHandler.IsLiveCaptionsActive(App.Window))
                {
                    Thread.Sleep(1000);
                    errorCount++;
                    
                    // 如果连续错误超过阈值，尝试重启LiveCaptions
                    if (errorCount > MAX_ERROR_COUNT && 
                        (DateTime.Now - lastRecoveryAttempt).TotalSeconds > 10) // 至少间隔10秒再尝试恢复
                    {
                        lastRecoveryAttempt = DateTime.Now;
                        DisplayTranslatedCaption = "[WARNING] LiveCaptions connection lost, restarting...";
                        
                        var newWindow = LiveCaptionsHandler.TryRestoreLiveCaptions(App.Window);
                        if (newWindow != null)
                        {
                            App.Window = newWindow;
                            errorCount = 0;
                            DisplayTranslatedCaption = "[INFO] LiveCaptions restored successfully";
                        }
                        else
                        {
                            DisplayTranslatedCaption = "[ERROR] Failed to restart LiveCaptions, will retry...";
                        }
                    }
                    continue;
                }
                
                // 重置错误计数
                errorCount = 0;

                // 获取文本
                string fullText = string.Empty;
                try
                {
                    fullText = LiveCaptionsHandler.GetCaptions(App.Window);
                }
                catch (ElementNotAvailableException)
                {
                    Thread.Sleep(100);
                    continue;
                }
                catch (Exception ex)
                {
                    DisplayTranslatedCaption = $"[ERROR] Failed to get captions: {ex.Message}";
                    Thread.Sleep(100);
                    continue;
                }
                
                if (string.IsNullOrEmpty(fullText))
                {
                    Thread.Sleep(25);
                    continue;
                }

                // 原有的文本处理逻辑保持不变
                fullText = Regex.Replace(fullText, @"(?<=[A-Z])\s*\.\s*(?=[A-Z])", "");
                fullText = Regex.Replace(fullText, @"\s*([.!?,])\s*", "$1 ");
                fullText = Regex.Replace(fullText, @"\s*([。！？，、])\s*", "$1");
                fullText = TextUtil.ReplaceNewlines(fullText, 32);

                // 获取最后一个句子
                int lastEOSIndex;
                if (Array.IndexOf(TextUtil.PUNC_EOS, fullText[^1]) != -1)
                    lastEOSIndex = fullText[0..^1].LastIndexOfAny(TextUtil.PUNC_EOS);
                else
                    lastEOSIndex = fullText.LastIndexOfAny(TextUtil.PUNC_EOS);
                string latestCaption = fullText.Substring(lastEOSIndex + 1);

                // DisplayOriginalCaption: 要显示给用户的句子
                if (DisplayOriginalCaption.CompareTo(latestCaption) != 0)
                {
                    DisplayOriginalCaption = latestCaption;
                    // 如果最后一个句子太短，通过添加前一个句子来扩展显示内容
                    if (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < 12)
                    {
                        lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                        DisplayOriginalCaption = fullText.Substring(lastEOSIndex + 1);
                    }
                    // 如果最后一个句子太长，截断显示内容
                    DisplayOriginalCaption = TextUtil.ShortenDisplaySentence(DisplayOriginalCaption, 160);
                }

                // OriginalCaption: 实际要翻译的句子
                lock (_syncLock) 
                {
                    if (OriginalCaption.CompareTo(latestCaption) != 0)
                    {
                        OriginalCaption = latestCaption;

                        idleCount = 0;
                        // 降低字节长度阈值，让更短的句子也能增加syncCount
                        if (Encoding.UTF8.GetByteCount(latestCaption) >= 5) // 从10降到5
                            syncCount++;
                        
                        // 句子结束标点触发立即翻译
                        if (Array.IndexOf(TextUtil.PUNC_EOS, OriginalCaption[^1]) != -1)
                        {
                            syncCount = 0;
                            translateFlag = true; // 使用字段而不是属性，避免死锁
                        }
                    }
                    else
                        idleCount++;

                    // 触发翻译的条件 - 使用默认阈值
                    if (syncCount > App.Setting.MaxSyncInterval ||
                        idleCount == App.Setting.MaxIdleInterval)
                    {
                        syncCount = 0;
                        translateFlag = true;
                    }
                }
                
                Thread.Sleep(25);
            }
        }

        public async Task Translate()
        {
            var translationTaskQueue = new TranslationTaskQueue();
            while (true)
            {
                // 检查LiveCaptions窗口
                bool windowIssue = false;
                if (App.Window == null || !LiveCaptionsHandler.IsLiveCaptionsActive(App.Window))
                {
                    DisplayTranslatedCaption = "[WARNING] LiveCaptions was unexpectedly closed, restarting...";
                    windowIssue = true;
                }
                else if (LogOnlyFlag)
                {
                    // 只记录模式
                    TranslatedCaption = string.Empty;
                    DisplayTranslatedCaption = "[Paused]";
                }
                else if (!string.IsNullOrEmpty(translationTaskQueue.Output))
                {
                    // 显示翻译结果
                    TranslatedCaption = translationTaskQueue.Output;
                    
                    // 确保译文不为空再显示
                    if (!string.IsNullOrEmpty(TranslatedCaption))
                    {
                        DisplayTranslatedCaption = TextUtil.ShortenDisplaySentence(TranslatedCaption, 200);
                    }
                }

                // 检查是否需要开始新的翻译
                bool shouldTranslate = false;
                string originalSnapshot = string.Empty;
                
                lock (_syncLock)
                {
                    if (translateFlag && !windowIssue)
                    {
                        shouldTranslate = true;
                        originalSnapshot = OriginalCaption;
                        translateFlag = false; // 直接修改字段避免死锁
                    }
                }

                if (shouldTranslate)
                {
                    if (LogOnlyFlag)
                    {
                        // 只记录模式
                        bool isOverwrite = await Translator.IsOverwrite(originalSnapshot);
                        await Translator.LogOnly(originalSnapshot, isOverwrite);
                    }
                    else
                    {
                        // 向队列添加新的翻译任务
                        translationTaskQueue.Enqueue(token => Task.Run(
                            () => Translator.Translate(originalSnapshot, token), token)
                        , originalSnapshot);
                    }

                    // 如果原始句子是完整句子，暂停以获得更好的视觉体验
                    if (!string.IsNullOrEmpty(originalSnapshot) && 
                        originalSnapshot.Length > 0 && 
                        Array.IndexOf(TextUtil.PUNC_EOS, originalSnapshot[^1]) != -1)
                    {
                        Thread.Sleep(600);
                    }
                }
                
                Thread.Sleep(40);
            }
        }

        public async Task AddLogCard(CancellationToken token = default)
        {
            var lastLog = await SQLiteHistoryLogger.LoadLastTranslation(token);
            if (lastLog == null)
                return;
            if (LogCards.Count >= App.Setting?.MainWindow.CaptionLogMax)
                LogCards.Dequeue();
            LogCards.Enqueue(lastLog);
            OnPropertyChanged("DisplayLogCards");
        }
    }
}