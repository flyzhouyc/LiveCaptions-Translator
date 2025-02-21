using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LiveCaptionsTranslator.controllers;

namespace LiveCaptionsTranslator.models.CaptionProcessing
{
    public class TranslationQueueManager : IDisposable
    {
        private readonly ConcurrentQueue<TranslationItem> _translationQueue;
        private readonly SemaphoreSlim _translationSemaphore;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly TranslationController _translationController;
        private const int MAX_QUEUE_SIZE = 20;  // 增加队列大小
        private const int DEFAULT_CONCURRENT_TRANSLATIONS = 2;  // 减少并发数以确保稳定性
        private const int TRANSLATION_TIMEOUT_MS = 2000;  // 增加超时时间
        private bool _disposed;

        public class TranslationItem
        {
            public string Text { get; set; }
            public DateTime Timestamp { get; set; }
            public TaskCompletionSource<string> CompletionSource { get; set; }
            public bool IsProcessing { get; set; }
        }

        public TranslationQueueManager(TranslationController controller)
        {
            _translationQueue = new ConcurrentQueue<TranslationItem>();
            _translationSemaphore = new SemaphoreSlim(DEFAULT_CONCURRENT_TRANSLATIONS);
            _cancellationTokenSource = new CancellationTokenSource();
            _translationController = controller;

            // Start the translation processing task
            Task.Run(ProcessTranslationQueueAsync);
        }

        public async Task<string> EnqueueTranslationAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            // 只在队列已满时移除最旧的未处理项
            while (_translationQueue.Count >= MAX_QUEUE_SIZE)
            {
                if (_translationQueue.TryDequeue(out var oldItem) && !oldItem.IsProcessing)
                {
                    oldItem.CompletionSource.TrySetCanceled();
                }
            }

            var translationItem = new TranslationItem
            {
                Text = text,
                Timestamp = DateTime.UtcNow,
                CompletionSource = new TaskCompletionSource<string>(),
                IsProcessing = false
            };

            _translationQueue.Enqueue(translationItem);

            try
            {
                // 使用更长的超时时间
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(TRANSLATION_TIMEOUT_MS));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    timeoutCts.Token, _cancellationTokenSource.Token);

                var translationTask = translationItem.CompletionSource.Task;
                var timeoutTask = Task.Delay(TRANSLATION_TIMEOUT_MS, linkedCts.Token);

                var completedTask = await Task.WhenAny(translationTask, timeoutTask);
                if (completedTask == translationTask)
                {
                    return await translationTask;
                }

                // 超时但保留原文
                return text;
            }
            catch (OperationCanceledException)
            {
                // 取消时返回原文而不是空字符串
                return text;
            }
        }

        private async Task ProcessTranslationQueueAsync()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                TranslationItem? translationItem = null;

                try
                {
                    if (_translationQueue.TryDequeue(out translationItem))
                    {
                        translationItem.IsProcessing = true;
                        await _translationSemaphore.WaitAsync(_cancellationTokenSource.Token);

                        try
                        {
                            var translation = await _translationController.TranslateAndLogAsync(translationItem.Text);
                            if (!string.IsNullOrEmpty(translation))
                            {
                                translationItem.CompletionSource.TrySetResult(translation);
                            }
                            else
                            {
                                // 如果翻译为空，返回原文
                                translationItem.CompletionSource.TrySetResult(translationItem.Text);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Translation error: {ex.Message}");
                            // 发生错误时返回原文
                            translationItem.CompletionSource.TrySetResult(translationItem.Text);
                        }
                        finally
                        {
                            _translationSemaphore.Release();
                        }
                    }
                    else
                    {
                        await Task.Delay(50, _cancellationTokenSource.Token);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Queue processing error: {ex.Message}");
                    if (translationItem != null)
                    {
                        translationItem.CompletionSource.TrySetResult(translationItem.Text);
                    }
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                _translationSemaphore.Dispose();
            }
        }
    }
}