using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace LiveCaptionsTranslator.utils
{
    /// <summary>
    /// UI冻结机制，用于在密集型计算期间暂停UI更新，减少资源消耗
    /// </summary>
    public class UISuspender : IDisposable
    {
        private readonly DispatcherFrame _frame;
        private readonly DispatcherOperation _resumeOperation;
        private readonly Dispatcher _dispatcher;
        private bool _disposed = false;

        /// <summary>
        /// 创建UI冻结机制并立即暂停UI更新
        /// </summary>
        public UISuspender(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            
            // 创建新的DispatcherFrame用于冻结UI
            _frame = new DispatcherFrame();
            
            // 安排恢复操作
            _resumeOperation = _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                // 使DispatcherFrame继续执行
                _frame.Continue = false;
            }));
            
            // 开始冻结UI
            Suspend();
        }

        /// <summary>
        /// 暂停UI更新
        /// </summary>
        private void Suspend()
        {
            if (_dispatcher.CheckAccess())
            {
                // 在UI线程上，直接冻结
                Dispatcher.PushFrame(_frame);
            }
            else
            {
                // 在非UI线程上，安排冻结操作
                _dispatcher.Invoke(new Action(() => Dispatcher.PushFrame(_frame)));
            }
        }

        /// <summary>
        /// 恢复UI更新
        /// </summary>
        private void Resume()
        {
            if (!_frame.Continue)
                return;
                
            _frame.Continue = false;
        }

        /// <summary>
        /// 创建UI冻结机制并执行指定操作，完成后自动恢复UI更新
        /// </summary>
        public static async Task ExecuteWithSuspendedUI(Dispatcher dispatcher, Func<Task> action)
        {
            using (var suspender = new UISuspender(dispatcher))
            {
                await action();
            }
        }

        /// <summary>
        /// 创建UI冻结机制并执行指定操作，完成后自动恢复UI更新
        /// </summary>
        public static async Task<T> ExecuteWithSuspendedUI<T>(Dispatcher dispatcher, Func<Task<T>> action)
        {
            using (var suspender = new UISuspender(dispatcher))
            {
                return await action();
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
                
            _disposed = true;
            Resume();
        }
    }
}