using System.Windows.Automation;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using LiveCaptionsTranslator.controllers;
using LiveCaptionsTranslator.models.CaptionProviders;
using LiveCaptionsTranslator.models.CaptionProcessing;

namespace LiveCaptionsTranslator.models
{
    public class Caption : INotifyPropertyChanged, IDisposable
    {
        // 单例模式
        private static Caption? instance = null;
        private static readonly object _lock = new object();

        public event PropertyChangedEventHandler? PropertyChanged;

        private string original = "";
        private string translated = "";
        private readonly Queue<CaptionHistoryItem> captionHistory = new(5);
        private ICaptionProvider _captionProvider;
        private CancellationTokenSource? _syncCts;
        private readonly SentenceProcessor _sentenceProcessor;

        public class CaptionHistoryItem
        {
            public string Original { get; set; }
            public string Translated { get; set; }
        }

        // 保留原有的公共属性
        public IEnumerable<CaptionHistoryItem> CaptionHistory => captionHistory.Reverse();
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

        private Caption() 
        {
            _sentenceProcessor = new SentenceProcessor();
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }

        public void InitializeProvider(string providerName)
        {
            try
            {
                _captionProvider = CaptionProviderFactory.GetProvider(providerName);
                Console.WriteLine($"Initialized caption provider: {providerName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing provider {providerName}: {ex.Message}");
                throw;
            }
        }

public async Task SyncAsync(CancellationToken externalToken = default)
{
    if (_captionProvider == null)
    {
        throw new InvalidOperationException("Caption provider not initialized");
    }

    // Combine external token with internal token if available
    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
    var token = combinedCts.Token;
    int syncCount = 0;
    int lastTranslationTime = Environment.TickCount;
    string previousText = string.Empty;

    Console.WriteLine($"Starting sync with provider: {_captionProvider.ProviderName}");

    try
    {
        while (!token.IsCancellationRequested)
        {
            if (PauseFlag || App.Window == null)
            {
                await Task.Delay(20, token); // 减少暂停时的延迟
                continue;
            }

            try
            {
                string fullText = (await _captionProvider.GetCaptionsAsync(App.Window, token)).Trim();
                
                // 快速检查文本是否有变化
                if (string.IsNullOrEmpty(fullText) || fullText == previousText)
                {
                    await Task.Delay(10, token); // 最小延迟
                    continue;
                }

                previousText = fullText;
                fullText = CaptionTextProcessor.Instance.ProcessFullText(fullText);
                
                // 使用 ValueTask 优化性能
                ValueTask<int> lastEOSTask = new ValueTask<int>(CaptionTextProcessor.Instance.GetLastEOSIndex(fullText));
                int lastEOSIndex = await lastEOSTask;
                
                string latestCaption = CaptionTextProcessor.Instance.ExtractLatestCaption(fullText, lastEOSIndex);

                if (Original.CompareTo(latestCaption) != 0)
                {
                    syncCount++;
                    Original = latestCaption;
                    int currentTime = Environment.TickCount;
                    int timeSinceLastTranslation = currentTime - lastTranslationTime;

                    // 优化翻译触发逻辑
                    bool shouldTranslate = false;
                    
                    // 1. 检查是否是完整句子
                    if (_sentenceProcessor.IsCompleteSentence(latestCaption))
                    {
                        shouldTranslate = true;
                    }
                    // 2. 检查自然停顿
                    else if (_sentenceProcessor.HasNaturalPause(latestCaption) && 
                             latestCaption.Length >= CalculateDynamicMinTranslationLength(timeSinceLastTranslation))
                    {
                        shouldTranslate = true;
                    }
                    // 3. 检查时间阈值
                    else if (timeSinceLastTranslation >= 2000) // 2秒阈值
                    {
                        shouldTranslate = true;
                    }
                    // 4. 检查同步计数
                    else if (syncCount > App.Settings.MaxSyncInterval)
                    {
                        shouldTranslate = true;
                    }

                    TranslateFlag = shouldTranslate;
                    EOSFlag = _sentenceProcessor.IsCompleteSentence(latestCaption);

                    if (TranslateFlag)
                    {
                        lastTranslationTime = currentTime;
                        syncCount = 0;
                    }
                }

                // 动态调整延迟
                int delay = _captionProvider.SupportsAdaptiveSync ? 15 : 25;
                if (TranslateFlag) delay = 10; // 翻译时使用最小延迟
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Sync error: {ex.Message}");
                await Task.Delay(20, token); // 减少错误恢复延迟
            }
        }
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested)
    {
        Console.WriteLine("Caption sync cancelled");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Critical sync error: {ex.Message}");
        throw;
    }
}

private int CalculateDynamicMinTranslationLength(int timeSinceLastTranslation)
{
    // 更灵活的动态长度调整
    if (timeSinceLastTranslation < 500) // 0.5秒内
    {
        return 50; // 更激进的长度阈值
    }
    else if (timeSinceLastTranslation < 1000) // 1秒内
    {
        return 70;
    }
    else if (timeSinceLastTranslation < 2000) // 2秒内
    {
        return 90;
    }
    else
    {
        return Math.Min(App.Settings.MinTranslationLength, 120); // 限制最大长度
    }
}


        public async Task TranslateAsync(CancellationToken cancellationToken = default)
        {
            var controller = new TranslationController();
            Console.WriteLine("Starting translation task");
            string lastTranslatedText = string.Empty;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (PauseFlag)
                    {
                        int pauseCount = 0;
                        while (PauseFlag && !cancellationToken.IsCancellationRequested)
                        {
                            if (pauseCount > 30 && App.Window != null)
                            {
                                App.Window = null;
                                LiveCaptionsHandler.KillLiveCaptions();
                            }
                            await Task.Delay(500, cancellationToken);
                            pauseCount++;
                        }
                        continue;
                    }

                    try
                    {
                        if (TranslateFlag && Original != lastTranslatedText)
                        {
                            string translatedResult = await controller.TranslateAndLogAsync(Original);
                            if (!string.IsNullOrEmpty(translatedResult))
                            {
                                Translated = translatedResult;
                                lastTranslatedText = Original;
                                TranslateFlag = false;

                                // 更新历史记录
                                if (!string.IsNullOrEmpty(Original))
                                {
                                    var lastHistory = captionHistory.LastOrDefault();
                                    if (lastHistory == null || 
                                        lastHistory.Original != Original || 
                                        lastHistory.Translated != Translated)
                                    {
                                        if (captionHistory.Count >= 5)
                                            captionHistory.Dequeue();
                                        captionHistory.Enqueue(new CaptionHistoryItem 
                                        { 
                                            Original = Original, 
                                            Translated = Translated 
                                        });
                                        OnPropertyChanged(nameof(CaptionHistory));
                                    }
                                }
                            }
                        }

                        await Task.Delay(50, cancellationToken); // 增加延迟以减少CPU使用
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Translation error: {ex.Message}");
                        await Task.Delay(100, cancellationToken); // 错误后增加延迟
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("Translation task cancelled");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Critical translation error: {ex.Message}");
                throw;
            }
        }

        public void ClearHistory()
        {
            captionHistory.Clear();
            OnPropertyChanged(nameof(CaptionHistory));
        }

        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _syncCts?.Cancel();
                _syncCts?.Dispose();
                _syncCts = null;
                instance = null;
            }
        }
    }
}
