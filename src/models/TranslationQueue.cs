using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace LiveCaptionsTranslator.models
{
    public class TranslationQueue
    {
        private static readonly TranslationQueue _instance = new TranslationQueue();
        private readonly ConcurrentQueue<TranslationRequest> _queue = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();
        private readonly SemaphoreSlim _processingLock = new(1);
        private readonly Timer _batchTimer;
        private const int BATCH_DELAY_MS = 100; // 批处理延迟
        private const int MAX_BATCH_SIZE = 5; // 最大批量大小
        private bool _isProcessing = false;

        public static TranslationQueue Instance => _instance;

        private TranslationQueue()
        {
            _batchTimer = new Timer(ProcessBatchAsync, null, Timeout.Infinite, Timeout.Infinite);
        }

        private class TranslationRequest
        {
            public string Text { get; set; }
            public TaskCompletionSource<string> CompletionSource { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public async Task<string> EnqueueAsync(string text)
        {
            // 检查是否有相同文本的待处理请求
            if (_pendingRequests.TryGetValue(text, out var existingTcs))
            {
                return await existingTcs.Task;
            }

            var tcs = new TaskCompletionSource<string>();
            _pendingRequests.TryAdd(text, tcs);

            _queue.Enqueue(new TranslationRequest
            {
                Text = text,
                CompletionSource = tcs,
                Timestamp = DateTime.UtcNow
            });

            // 重置批处理定时器
            _batchTimer.Change(BATCH_DELAY_MS, Timeout.Infinite);

            return await tcs.Task;
        }

        private async void ProcessBatchAsync(object state)
        {
            if (_isProcessing) return;

            await _processingLock.WaitAsync();
            try
            {
                _isProcessing = true;
                var batch = new List<TranslationRequest>();

                // 收集批量请求
                while (batch.Count < MAX_BATCH_SIZE && _queue.TryDequeue(out var request))
                {
                    batch.Add(request);
                }

                if (batch.Count == 0) return;

                // 按时间戳排序
                batch.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

                // 批量处理请求
                foreach (var request in batch)
                {
                    try
                    {
                        string result = await TranslateAPI.TranslateFunc(request.Text);
                        request.CompletionSource.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        request.CompletionSource.TrySetException(ex);
                    }
                    finally
                    {
                        _pendingRequests.TryRemove(request.Text, out _);
                    }
                }

                // 如果队列中还有请求，继续处理
                if (!_queue.IsEmpty)
                {
                    _batchTimer.Change(BATCH_DELAY_MS, Timeout.Infinite);
                }
            }
            finally
            {
                _isProcessing = false;
                _processingLock.Release();
            }
        }

        public void Dispose()
        {
            _batchTimer?.Dispose();
            _processingLock?.Dispose();
        }
    }
}
