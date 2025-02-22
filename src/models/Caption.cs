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
                await Task.Delay(5, token); // 减少暂停时的延迟
                continue;
            }

            try
            {
                // 使用优化器获取字幕
                string fullText = await _optimizer.GetCaptionTextAsync(App.Window);
                if (string.IsNullOrEmpty(fullText))
                {
                    await Task.Delay(_optimizer.GetOptimalDelay(fullText, _previousText), token);
                    continue;
                }

                fullText = CaptionTextProcessor.Instance.ProcessFullText(fullText);
                int lastEOSIndex = CaptionTextProcessor.Instance.GetLastEOSIndex(fullText);
                string latestCaption = CaptionTextProcessor.Instance.ExtractLatestCaption(fullText, lastEOSIndex);

                if (Original.CompareTo(latestCaption) != 0)
                {
                    Original = latestCaption;
                    
                    // 使用优化器决定是否翻译
                    TranslateFlag = await _optimizer.ShouldTranslateAsync(latestCaption);
                }

                _previousText = fullText;
                await Task.Delay(_optimizer.GetOptimalDelay(fullText, _previousText), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Sync error: {ex.Message}");
                await Task.Delay(5, token); // 减少错误恢复延迟
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
                            await Task.Delay(100, cancellationToken); // 减少暂停检查间隔
                            pauseCount++;
                        }
                        continue;
                    }

                    try
                    {
                        if (TranslateFlag && Original != lastTranslatedText)
                        {
                            // 使用优化器的置信度来决定翻译优先级
                            metrics = _optimizer.GetPerformanceMetrics();
                            int delay = metrics.LastConfidence > 0.8f ? 1 : 
                                      metrics.LastConfidence > 0.5f ? 3 : 5;

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

                            await Task.Delay(delay, cancellationToken);
                        }
                        else
                        {
                            // 动态调整检查间隔
                            await Task.Delay(metrics.LastConfidence > 0.5f ? 10 : 20, cancellationToken);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Translation error: {ex.Message}");
                        await Task.Delay(50, cancellationToken); // 减少错误恢复延迟
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
