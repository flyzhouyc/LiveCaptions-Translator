using System.Windows.Automation;
using System.Text;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Diagnostics;

using LiveCaptionsTranslator.controllers;
using System.Security.AccessControl;
using static System.Net.Mime.MediaTypeNames;


namespace LiveCaptionsTranslator.models
{
    public class Caption : INotifyPropertyChanged
    {
        // 单例模式
        private static Caption? instance = null;
        private static readonly object _lock = new object();

        public event PropertyChangedEventHandler? PropertyChanged;

        private static readonly char[] PUNC_EOS = ".?!。？！".ToCharArray();
        private static readonly char[] PUNC_COMMA = ",，、—\n".ToCharArray();

        private string original = "";
        private string presentedCaption_Last = "";
        private string translated = "";
        private string translatedCaption_Last = "";
        private readonly Queue<CaptionHistoryItem> captionHistory = new(5);

        public class CaptionHistoryItem
        {
            public string Original { get; set; }
            public string Translated { get; set; }
        }

        // 保留原有的公共属性
        public IEnumerable<CaptionHistoryItem> CaptionHistory => captionHistory.Reverse();
        public static event Action? TranslationLogged;
        public bool PauseFlag { get; set; } = false;
        public bool TranslateFlag { get; set; } = false;
        private bool EOSFlag { get; set; } = false;

        public string Original
        {
            get => original;
            set
            {
                original = value;
                OnPropertyChanged(nameof(Original));
            }
        }

        public string Translated
        {
            get => translated;
            set
            {
                translated = value;
                OnPropertyChanged(nameof(Translated));
            }
        }

        // 单例获取方法
        public static Caption GetInstance()
        {
            if (instance == null)
            {
                lock (_lock)
                {
                    if (instance == null)
                    {
                        instance = new Caption();
                    }
                }
            }
            return instance;
        }

        private Caption() { }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }

        // 性能监控字段
        private long _totalSyncTime = 0;
        private int _syncCount = 0;
        private long _totalTranslateTime = 0;
        private int _translateCount = 0;

        public void Sync()
        {
            int idleCount = 0;
            int syncCount = 0;

            while (true)
            {
                var syncStartTime = Stopwatch.GetTimestamp();

                if (PauseFlag || App.Window == null)
                {
                    Thread.Sleep(50);
                    continue;
                }

                try
                {
                    string fullText = GetCaptions(App.Window).Trim();
                    if (string.IsNullOrEmpty(fullText))
                        continue;

                    fullText = ProcessFullText(fullText);

                    int lastEOSIndex = GetLastEOSIndex(fullText);
                    var data = ExtractLatestCaption(fullText, lastEOSIndex);
                    string latestCaption = data.Item1;
                    bool HistoryCap = data.Item2;

                    if (Original.CompareTo(latestCaption) != 0)
                    {

                        if (HistoryCap)
                        {
                            var lastHistory = captionHistory.LastOrDefault();
                            if (lastHistory == null ||
                                lastHistory.Original != presentedCaption_Last ||
                                lastHistory.Translated != translatedCaption_Last)
                            {
                                if (captionHistory.Count >= 5)
                                    captionHistory.Dequeue();
                                captionHistory.Enqueue(new CaptionHistoryItem
                                {
                                    Original = presentedCaption_Last,
                                    Translated = translatedCaption_Last
                                });
                                OnPropertyChanged(nameof(CaptionHistory));

                                // Insert history log
                                if (!string.IsNullOrEmpty(translatedCaption_Last))
                                {
                                    string targetLanguage = App.Settings.TargetLanguage;
                                    string apiName = App.Settings.ApiName;

                                    try
                                    {
                                        SQLiteHistoryLogger.LogTranslationAsync(presentedCaption_Last, translatedCaption_Last, targetLanguage, apiName);
                                        TranslationLogged?.Invoke();
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[Error] Logging history failed: {ex.Message}");
                                    }
                                }
                            }
                        }

                        idleCount = 0;
                        syncCount++;
                        Original = latestCaption;
                        UpdateTranslationFlags(latestCaption, ref syncCount);

                        if (!HistoryCap)
                        {
                            presentedCaption_Last = original;
                            translatedCaption_Last = translated;
                        }

                         HistoryCap = false;
                    }
                    else
                    {
                        idleCount++;
                    }

                    // 性能监控
                    UpdateSyncPerformance(syncStartTime);
                }
                catch (Exception ex)
                {
                    // 简单的错误处理
                    Console.WriteLine($"Sync error: {ex.Message}");
                }

                Thread.Sleep(50);
            }
        }

        private string ProcessFullText(string fullText)
        {
            foreach (char eos in PUNC_EOS)
                fullText = fullText.Replace($"{eos}\n", $"{eos}");
            return fullText;
        }

        private int GetLastEOSIndex(string fullText)
        {
            return Array.IndexOf(PUNC_EOS, fullText[^1]) != -1
                ? fullText[0..^1].LastIndexOfAny(PUNC_EOS)
                : fullText.LastIndexOfAny(PUNC_EOS);
        }

        private (string, bool) ExtractLatestCaption(string fullText, int lastEOSIndex)
        {
            bool clear = false;
            string latestCaption = fullText.Substring(lastEOSIndex + 1);
            // 确保字幕长度合适
            while (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < 10)
            {
                lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(PUNC_EOS);
                latestCaption = fullText.Substring(lastEOSIndex + 1);
                clear = true;
            }

            while (Encoding.UTF8.GetByteCount(latestCaption) > 200)
            {
                int commaIndex = latestCaption.IndexOfAny(PUNC_COMMA);
                if (commaIndex < 0 || commaIndex + 1 == latestCaption.Length)
                    break;

                latestCaption = latestCaption.Substring(commaIndex + 1);
                clear = true;
            }

            return (latestCaption, clear);  // 保留原始换行符
        }

        private void UpdateTranslationFlags(string caption, ref int syncCount)
        {
            if (Array.IndexOf(PUNC_EOS, caption[^1]) != -1 ||
                Array.IndexOf(PUNC_COMMA, caption[^1]) != -1)
            {
                syncCount = 0;
                TranslateFlag = true;
                EOSFlag = true;
            }
            else
            {
                EOSFlag = false;
            }

            if (syncCount > App.Settings.MaxSyncInterval)
            {
                syncCount = 0;
                TranslateFlag = true;
            }
        }

        private void UpdateSyncPerformance(long startTime)
        {
            long elapsedTime = Stopwatch.GetTimestamp() - startTime;
            Interlocked.Add(ref _totalSyncTime, elapsedTime);
            Interlocked.Increment(ref _syncCount);
        }

        public async Task Translate()
        {
            var controller = new TranslationController();

            while (true)
            {
                var translateStartTime = Stopwatch.GetTimestamp();

                for (int pauseCount = 0; PauseFlag; pauseCount++)
                {
                    if (pauseCount > 60 && App.Window != null)
                    {
                        App.Window = null;
                        LiveCaptionsHandler.KillLiveCaptions();
                    }
                    Thread.Sleep(1000);
                }

                try
                {
                    if (TranslateFlag)
                    {
                        Translated = await controller.TranslateAsync(Original);
                        TranslateFlag = false;

                        // 性能监控
                        UpdateTranslatePerformance(translateStartTime);

                        if (EOSFlag)
                            Thread.Sleep(1000);
                    }
                }
                catch (Exception ex)
                {
                    // 简单的错误处理
                    Console.WriteLine($"Translate error: {ex.Message}");
                }

                Thread.Sleep(50);
            }
        }

        private void UpdateTranslatePerformance(long startTime)
        {
            long elapsedTime = Stopwatch.GetTimestamp() - startTime;
            Interlocked.Add(ref _totalTranslateTime, elapsedTime);
            Interlocked.Increment(ref _translateCount);
        }

        public static string GetCaptions(AutomationElement window)
        {
            var captionsTextBlock = LiveCaptionsHandler.FindElementByAId(window, "CaptionsTextBlock");
            if (captionsTextBlock == null)
                return string.Empty;
            return captionsTextBlock.Current.Name;
        }

        // 性能分析方法
        public void ClearHistory()
        {
            captionHistory.Clear();
            OnPropertyChanged(nameof(CaptionHistory));
        }

        public (double avgSyncTime, double avgTranslateTime) GetPerformanceMetrics()
        {
            double avgSyncTime = _syncCount > 0 
                ? TimeSpan.FromTicks(_totalSyncTime / _syncCount).TotalMilliseconds 
                : 0;
            
            double avgTranslateTime = _translateCount > 0 
                ? TimeSpan.FromTicks(_totalTranslateTime / _translateCount).TotalMilliseconds 
                : 0;

            return (avgSyncTime, avgTranslateTime);
        }
    }
}
