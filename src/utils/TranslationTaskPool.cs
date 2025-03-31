using System;
using System.Threading;
using System.Threading.Tasks;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    /// <summary>
    /// 翻译任务对象池，用于减少创建TranslationTask对象的GC压力
    /// </summary>
    public static class TranslationTaskPool
    {
        // 创建对象池实例
        private static readonly ObjectPool<TranslationTask> _pool = new ObjectPool<TranslationTask>(
            // 创建新对象
            () => new TranslationTask(),
            // 重置对象状态
            (task) => task.Reset(),
            // 最大池大小
            20
        );

        /// <summary>
        /// 从池中获取翻译任务
        /// </summary>
        public static TranslationTask Obtain()
        {
            return _pool.Get();
        }

        /// <summary>
        /// 将翻译任务返回到池中
        /// </summary>
        public static void Return(TranslationTask task)
        {
            _pool.Return(task);
        }

        /// <summary>
        /// 重置池
        /// </summary>
        public static void Reset()
        {
            _pool.Clear();
        }
    }

    /// <summary>
    /// 修改的翻译任务类，以支持对象池
    /// </summary>
    public class TranslationTask
    {
        public Task<string>? Task { get; private set; }
        public string OriginalText { get; private set; } = string.Empty;
        public CancellationTokenSource? CTS { get; private set; }
        public DateTime StartTime { get; set; } = DateTime.MinValue;

        // 默认构造函数，用于对象池
        public TranslationTask() { }

        /// <summary>
        /// 初始化翻译任务
        /// </summary>
        public void Initialize(Func<CancellationToken, Task<string>> worker, string originalText)
        {
            OriginalText = originalText;
            CTS = new CancellationTokenSource();
            Task = worker(CTS.Token);
            StartTime = DateTime.Now;
        }

        /// <summary>
        /// 重置任务状态，用于对象池重用
        /// </summary>
        public void Reset()
        {
            // 取消现有任务
            CTS?.Cancel();
            CTS?.Dispose();
            CTS = null;
            
            // 重置状态
            Task = null;
            OriginalText = string.Empty;
            StartTime = DateTime.MinValue;
        }
    }
}