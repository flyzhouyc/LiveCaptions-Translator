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
        private const int MAX_QUEUE_SIZE = 10;
        private const int DEFAULT_CONCURRENT_TRANSLATIONS = 3;
        private bool _disposed;

        public class TranslationItem
        {
            public string Text { get; set; }
            public DateTime Timestamp { get; set; }
            public TaskCompletionSource<string> CompletionSource { get; set; }
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
            // Remove old items if queue is too large
            while (_translationQueue.Count >= MAX_QUEUE_SIZE)
            {
                if (_translationQueue.TryDequeue(out var oldItem))
                {
                    oldItem.CompletionSource.TrySetCanceled();
                }
            }

            var translationItem = new TranslationItem
            {
                Text = text,
                Timestamp = DateTime.UtcNow,
                CompletionSource = new TaskCompletionSource<string>()
            };

            _translationQueue.Enqueue(translationItem);
            
            // Set timeout for translation
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCts.Token, _cancellationTokenSource.Token);

            try
            {
                return await translationItem.CompletionSource.Task
                    .WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                return string.Empty;
            }
        }

        private async Task ProcessTranslationQueueAsync()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                if (_translationQueue.TryDequeue(out var translationItem))
                {
                    await _translationSemaphore.WaitAsync(_cancellationTokenSource.Token);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var translation = await _translationController.TranslateAndLogAsync(translationItem.Text);
                            translationItem.CompletionSource.TrySetResult(translation);
                        }
                        catch (Exception ex)
                        {
                            translationItem.CompletionSource.TrySetException(ex);
                        }
                        finally
                        {
                            _translationSemaphore.Release();
                        }
                    }, _cancellationTokenSource.Token);
                }

                await Task.Delay(10, _cancellationTokenSource.Token);
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
