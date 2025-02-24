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

        private readonly TranslationController _translationController = new TranslationController("path/to/cache/file.json");

public async Task SyncAsync(CancellationToken externalToken = default)
{
    if (_captionProvider == null)
    {
        throw new InvalidOperationException("Caption provider not initialized");
    }

    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
    var token = combinedCts.Token;

    Console.WriteLine($"Starting sync with provider: {_captionProvider.ProviderName}");

    var textProcessor = CaptionTextProcessor.Instance;
    var metrics = _optimizer.GetPerformanceMetrics();
    int consecutiveEmptyCount = 0;
    int consecutiveErrorCount = 0;
    DateTime lastUpdateTime = DateTime.Now;
    int accumulatedLength = 0;
    const int FORCE_UPDATE_INTERVAL_MS = 3000; // 3秒强制更新
    const int MAX_ACCUMULATED_LENGTH = 100; // 最大累积长度

    try
    {
        while (!token.IsCancellationRequested)
        {
            if (PauseFlag || App.Window == null)
            {
                await Task.Delay(Math.Min(50 * consecutiveEmptyCount, 200), token);
                continue;
            }

            try
            {
                string fullText;
                // 使用预测性缓存获取字幕
                fullText = await _optimizer.GetCaptionTextAsync(App.Window);

                // 如果预测性缓存失败，尝试使用CaptionProvider
                if (string.IsNullOrEmpty(fullText))
                {
                    fullText = (await _captionProvider.GetCaptionsAsync(App.Window, token)).Trim();
                }

                var now = DateTime.Now;
                bool shouldForceUpdate = (now - lastUpdateTime).TotalMilliseconds >= FORCE_UPDATE_INTERVAL_MS;

                if (string.IsNullOrEmpty(fullText))
                {
                    consecutiveEmptyCount++;
                    if (shouldForceUpdate && !string.IsNullOrEmpty(Original))
                    {
                        // 强制清空，防止字幕卡住
                        Original = string.Empty;
                        _previousText = string.Empty;
                        accumulatedLength = 0;
                        lastUpdateTime = now;
                    }
                    int delay = _optimizer.GetOptimalDelay(fullText, _previousText);
                    await Task.Delay(delay, token);
                    continue;
                }

                consecutiveEmptyCount = 0;
                consecutiveErrorCount = 0;

                fullText = textProcessor.ProcessFullText(fullText);

                // 检查文本变化
                bool textChanged = !string.Equals(fullText, _previousText, StringComparison.Ordinal);
                bool shouldUpdate = textChanged || shouldForceUpdate;

                if (shouldUpdate)
                {
                    int lastEOSIndex = textProcessor.GetLastEOSIndex(fullText);
                    string latestCaption = textProcessor.ExtractLatestCaption(fullText, lastEOSIndex);

                    // 累积长度检查
                    accumulatedLength += latestCaption.Length;
                    if (accumulatedLength > MAX_ACCUMULATED_LENGTH || shouldForceUpdate)
                    {
                        // 强制更新并重置累积
                        Original = latestCaption;
                        accumulatedLength = latestCaption.Length;
                        lastUpdateTime = now;

                        // 异步触发翻译检查
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                TranslateFlag = await _optimizer.ShouldTranslateAsync(latestCaption);
                                if (TranslateFlag)
                                {
                                    Console.WriteLine($"Translation triggered for: {latestCaption}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Translation check error: {ex.Message}");
                            }
                        });
                    }
                    else if (!string.Equals(Original, latestCaption, StringComparison.Ordinal))
                    {
                        // 正常更新
                        Original = latestCaption;
                        lastUpdateTime = now;
                    }

                    _previousText = fullText;
                }

                metrics = _optimizer.GetPerformanceMetrics();
                int nextDelay = _optimizer.GetOptimalDelay(fullText, _previousText);

                // 动态调整延迟
                if (accumulatedLength > MAX_ACCUMULATED_LENGTH / 2)
                {
                    // 当累积较多时，减少延迟
                    nextDelay = Math.Max(5, nextDelay / 2);
                }
                else if (metrics.AverageDelay < 20 && consecutiveErrorCount == 0)
                {
                    nextDelay = Math.Max(5, nextDelay - 2);
                }

                await Task.Delay(nextDelay, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                consecutiveErrorCount++;
                Console.WriteLine($"Sync error: {ex.Message}");

                if (consecutiveErrorCount > 5)
                {
                    // 连续错误过多时清空状态
                    Original = string.Empty;
                    _previousText = string.Empty;
                    accumulatedLength = 0;
                    lastUpdateTime = DateTime.Now;
                }

                // 使用指数退避策略
                int errorDelay = Math.Min(50 * (1 << Math.Min(consecutiveErrorCount, 4)), 500);
                await Task.Delay(errorDelay, token);
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
    var controller = new TranslationController("path/to/cache/file.json");
    Console.WriteLine("Starting translation task");
    string lastTranslatedText = string.Empty;
    var metrics = _optimizer.GetPerformanceMetrics();
    int consecutiveErrors = 0;
    int pauseCount = 0;

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (PauseFlag)
            {
                if (pauseCount++ > 30 && App.Window != null)
                {
                    App.Window = null;
                    LiveCaptionsHandler.KillLiveCaptions();
                    pauseCount = 0;
                }
                await Task.Delay(100, cancellationToken);
                continue;
            }
            pauseCount = 0;

            try
            {
                if (TranslateFlag && !string.Equals(Original, lastTranslatedText, StringComparison.Ordinal))
                {
                    metrics = _optimizer.GetPerformanceMetrics();

                    // 优化的翻译延迟策略
                    int baseDelay = metrics.LastConfidence switch
                    {
                        var c when c > 0.9f => 3,
                        var c when c > 0.7f => 5,
                        var c when c > 0.5f => 8,
                        _ => 10
                    };

                    // 使用信号量限制并发翻译请求
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                    string translatedResult = null;
                    var translationTask = controller.TranslateAndLogAsync(Original);

                    try
                    {
                        translatedResult = await translationTask.WaitAsync(linkedCts.Token);
                    }
                    catch (TimeoutException)
                    {
                        Console.WriteLine("Translation timeout, will retry");
                        consecutiveErrors++;
                    }

                    if (!string.IsNullOrEmpty(translatedResult))
                    {
                        Translated = translatedResult;
                        lastTranslatedText = Original;
                        TranslateFlag = false;
                        consecutiveErrors = 0;

                        // 高效的历史记录更新
                        if (!string.IsNullOrEmpty(Original))
                        {
                            var lastHistory = captionHistory.LastOrDefault();
                            if (lastHistory == null ||
                                !string.Equals(lastHistory.Original, Original, StringComparison.Ordinal) ||
                                !string.Equals(lastHistory.Translated, Translated, StringComparison.Ordinal))
                            {
                                if (captionHistory.Count >= 5)
                                {
                                    captionHistory.Dequeue();
                                }
                                captionHistory.Enqueue(new CaptionHistoryItem
                                {
                                    Original = Original,
                                    Translated = Translated
                                });
                                OnPropertyChanged(nameof(CaptionHistory));
                            }
                        }

                        // 动态调整下一次延迟
                        baseDelay = Math.Max(3, baseDelay - (consecutiveErrors == 0 ? 1 : 0));
                    }
                    else
                    {
                        Console.WriteLine("Translation failed");
                        consecutiveErrors++;
                        baseDelay *= 2; // 翻译失败时增加延迟
                    }

                    await Task.Delay(baseDelay, cancellationToken);
                }
                else
                {
                    // 优化空闲检查间隔
                    await Task.Delay(
                        metrics.LastConfidence > 0.7f ? 10 :
                        metrics.LastConfidence > 0.4f ? 15 : 20,
                        cancellationToken);
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

                // 优化的错误恢复策略
                int errorDelay = Math.Min(50 * (1 << Math.Min(consecutiveErrors, 4)), 800);
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
