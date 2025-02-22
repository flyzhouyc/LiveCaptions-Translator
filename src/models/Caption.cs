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
        private readonly RealtimeOptimizer _optimizer;
        private string _previousText = string.Empty;

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
            _optimizer = new RealtimeOptimizer();
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

    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
    var token = combinedCts.Token;

    Console.WriteLine($"Starting sync with provider: {_captionProvider.ProviderName}");

    try
    {
        while (!token.IsCancellationRequested)
        {
            if (PauseFlag || App.Window == null)
            {
                await Task.Delay(50, token); // 增加暂停时的延迟
                continue;
            }

            try
            {
                // 首先尝试使用CaptionProvider获取字幕
                string fullText = (await _captionProvider.GetCaptionsAsync(App.Window, token)).Trim();
                
                // 如果CaptionProvider失败，使用优化器的备用方案
                if (string.IsNullOrEmpty(fullText))
                {
                    fullText = await _optimizer.GetCaptionTextAsync(App.Window);
                }

                if (string.IsNullOrEmpty(fullText))
                {
                    int delay = _optimizer.GetOptimalDelay(fullText, _previousText);
                    Console.WriteLine($"Empty text, waiting {delay}ms");
                    await Task.Delay(delay, token);
                    continue;
                }

                fullText = CaptionTextProcessor.Instance.ProcessFullText(fullText);
                
                // 只有当文本真正变化时才进行处理
                if (fullText != _previousText)
                {
                    int lastEOSIndex = CaptionTextProcessor.Instance.GetLastEOSIndex(fullText);
                    string latestCaption = CaptionTextProcessor.Instance.ExtractLatestCaption(fullText, lastEOSIndex);

                    if (Original.CompareTo(latestCaption) != 0)
                    {
                        Original = latestCaption;
                        Console.WriteLine($"New caption: {latestCaption}");
                        
                        // 使用优化器决定是否翻译
                        TranslateFlag = await _optimizer.ShouldTranslateAsync(latestCaption);
                        if (TranslateFlag)
                        {
                            Console.WriteLine("Translation triggered");
                        }
                    }

                    _previousText = fullText;
                }

                int nextDelay = _optimizer.GetOptimalDelay(fullText, _previousText);
                await Task.Delay(nextDelay, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Sync error: {ex.Message}");
                await Task.Delay(100, token); // 增加错误恢复延迟
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



        public async Task TranslateAsync(CancellationToken cancellationToken = default)
        {
            var controller = new TranslationController();
            Console.WriteLine("Starting translation task");
            string lastTranslatedText = string.Empty;
            var metrics = _optimizer.GetPerformanceMetrics();
            int consecutiveErrors = 0;

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
                            await Task.Delay(200, cancellationToken); // 增加暂停检查间隔
                            pauseCount++;
                        }
                        continue;
                    }

                    try
                    {
                        if (TranslateFlag && Original != lastTranslatedText)
                        {
                            Console.WriteLine($"Translating: {Original}");
                            
                            // 使用优化器的置信度来决定翻译优先级
                            metrics = _optimizer.GetPerformanceMetrics();
                            int delay = metrics.LastConfidence > 0.8f ? 10 : 
                                      metrics.LastConfidence > 0.5f ? 20 : 30;

                            string translatedResult = await controller.TranslateAndLogAsync(Original);
                            if (!string.IsNullOrEmpty(translatedResult))
                            {
                                Translated = translatedResult;
                                lastTranslatedText = Original;
                                TranslateFlag = false;
                                consecutiveErrors = 0;

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

                                Console.WriteLine($"Translation success: {translatedResult}");
                            }

                            await Task.Delay(delay, cancellationToken);
                        }
                        else
                        {
                            // 动态调整检查间隔
                            await Task.Delay(metrics.LastConfidence > 0.5f ? 30 : 50, cancellationToken);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Translation error: {ex.Message}");
                        consecutiveErrors++;
                        
                        // 如果连续错误过多，增加等待时间
                        int errorDelay = Math.Min(100 * consecutiveErrors, 1000);
                        await Task.Delay(errorDelay, cancellationToken);
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
