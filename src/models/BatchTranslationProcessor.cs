using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace LiveCaptionsTranslator.models
{
    public class BatchTranslationProcessor : IAsyncDisposable
    {
        private class TranslationRequest
        {
            public string Text { get; }
            public TaskCompletionSource<string> CompletionSource { get; }
            public DateTime Timestamp { get; }
            public int Priority { get; }

            public TranslationRequest(string text, int priority = 0)
            {
                Text = text;
                CompletionSource = new TaskCompletionSource<string>();
                Timestamp = DateTime.Now;
                Priority = priority;
            }
        }

        private readonly ConcurrentPriorityQueue<TranslationRequest> _queue;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Task _processingTask;
        private readonly Func<string, Task<string>> _translateFunc;
        private readonly int _maxBatchSize = 15;
        private readonly TimeSpan _maxWaitTime = TimeSpan.FromMilliseconds(200);
        private readonly SemaphoreSlim _queueSemaphore = new SemaphoreSlim(10);
        private readonly int _maxConcurrentBatches = 10;
        private volatile bool _isDisposed;

        public BatchTranslationProcessor(
            Func<string, Task<string>> translateFunc,
            int maxBatchSize = 15,
            int maxWaitMilliseconds = 200,
            int maxConcurrentBatches = 10)
        {
            _queue = new ConcurrentPriorityQueue<TranslationRequest>();
            _cancellationTokenSource = new CancellationTokenSource();
            _translateFunc = translateFunc;
            _maxBatchSize = maxBatchSize;
            _maxWaitTime = TimeSpan.FromMilliseconds(maxWaitMilliseconds);
            _maxConcurrentBatches = maxConcurrentBatches;
            _queueSemaphore = new SemaphoreSlim(maxConcurrentBatches);
            _processingTask = ProcessQueueAsync(_cancellationTokenSource.Token);
        }

        public async Task<string> EnqueueTranslationAsync(string text, int priority = 0)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(BatchTranslationProcessor));

            var request = new TranslationRequest(text, priority);
            _queue.Enqueue(request);
            return await request.CompletionSource.Task;
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _queueSemaphore.WaitAsync(cancellationToken);

                    try
                    {
                        var batch = new List<TranslationRequest>();
                        var waitStartTime = DateTime.Now;

                        // 收集批处理项
                        while (batch.Count < _maxBatchSize &&
                               DateTime.Now - waitStartTime < _maxWaitTime &&
                               !cancellationToken.IsCancellationRequested)
                        {
                            if (_queue.TryDequeue(out var request))
                            {
                                batch.Add(request);
                            }
                            else
                            {
                                await Task.Delay(10, cancellationToken);
                            }
                        }

                        if (batch.Count > 0)
                        {
                            // 启动新的任务处理批次
                            _ = ProcessBatchAsync(batch);
                        }
                    }
                    finally
                    {
                        _queueSemaphore.Release();
                    }

                    // 如果队列为空，等待一小段时间
                    if (_queue.IsEmpty)
                    {
                        await Task.Delay(50, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Error] Batch processing error: {ex.Message}");
                }
            }
        }

        private async Task ProcessBatchAsync(List<TranslationRequest> batch)
        {
            try
            {
                // 按优先级和时间戳排序
                batch.Sort((a, b) =>
                {
                    var priorityComparison = b.Priority.CompareTo(a.Priority);
                    return priorityComparison != 0
                        ? priorityComparison
                        : a.Timestamp.CompareTo(b.Timestamp);
                });

                // 并行处理批次中的请求
                var tasks = batch.Select(async request =>
                {
                    try
                    {
                        var result = await _translateFunc(request.Text);
                        request.CompletionSource.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        request.CompletionSource.TrySetException(ex);
                    }
                });

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                foreach (var request in batch)
                {
                    request.CompletionSource.TrySetException(ex);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _cancellationTokenSource.Cancel();

            try
            {
                await _processingTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            _cancellationTokenSource.Dispose();
            _queueSemaphore.Dispose();

            // 处理剩余的请求
            while (_queue.TryDequeue(out var request))
            {
                request.CompletionSource.TrySetCanceled();
            }
        }
    }

    public class ConcurrentPriorityQueue<T>
    {
        private readonly ConcurrentQueue<T> _queue = new();
        private readonly object _lock = new();

        public bool IsEmpty => _queue.IsEmpty;

        public void Enqueue(T item)
        {
            lock (_lock)
            {
                _queue.Enqueue(item);
            }
        }

        public bool TryDequeue(out T item)
        {
            lock (_lock)
            {
                return _queue.TryDequeue(out item);
            }
        }
    }
}
