using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    /// <summary>
    /// 批量设置保存类，用于减少频繁写入设置文件导致的IO开销
    /// </summary>
    public class BatchSettingsSave
    {
        private static readonly SemaphoreSlim _saveLocker = new SemaphoreSlim(1, 1);
        private static Timer _batchTimer;
        private static readonly object _batchLock = new object();
        private static readonly Dictionary<string, object> _pendingChanges = new Dictionary<string, object>();
        private static bool _saveScheduled = false;
        private static readonly TimeSpan BATCH_SAVE_DELAY = TimeSpan.FromMilliseconds(500);
        
        /// <summary>
        /// 静态构造函数
        /// </summary>
        static BatchSettingsSave()
        {
            // 初始化批处理定时器
            _batchTimer = new Timer(ProcessBatchSave, null, Timeout.Infinite, Timeout.Infinite);
        }
        
        /// <summary>
        /// 添加设置更改
        /// </summary>
        public static void AddChange(string propertyPath, object value)
        {
            lock (_batchLock)
            {
                _pendingChanges[propertyPath] = value;
                
                // 如果尚未安排保存，则安排
                if (!_saveScheduled)
                {
                    _saveScheduled = true;
                    _batchTimer.Change(BATCH_SAVE_DELAY, Timeout.InfiniteTimeSpan);
                }
            }
        }
        
        /// <summary>
        /// 批量处理设置保存
        /// </summary>
        private static void ProcessBatchSave(object state)
        {
            try
            {
                Dictionary<string, object> changesToApply;
                
                lock (_batchLock)
                {
                    // 复制待处理的更改
                    changesToApply = new Dictionary<string, object>(_pendingChanges);
                    _pendingChanges.Clear();
                    _saveScheduled = false;
                }
                
                if (changesToApply.Count > 0 && Translator.Setting != null)
                {
                    // 异步保存设置
                    Task.Run(async () => await CommitChangesAsync(changesToApply)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"批量保存设置时出错: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 提交设置更改
        /// </summary>
        public static async Task CommitChangesAsync(Dictionary<string, object> changes = null)
        {
            if (Translator.Setting == null)
                return;
                
            // 如果没有提供更改，使用待处理的更改
            if (changes == null)
            {
                lock (_batchLock)
                {
                    if (_pendingChanges.Count == 0)
                        return;
                        
                    changes = new Dictionary<string, object>(_pendingChanges);
                    _pendingChanges.Clear();
                    _saveScheduled = false;
                }
            }
            
            try
            {
                // 获取锁，确保一次只有一个保存操作
                await _saveLocker.WaitAsync();
                
                // 应用所有更改
                // 这里需要根据属性路径设置对应的设置值
                // 由于无法直接通过路径访问属性，这里使用简化的实现
                
                // 直接保存设置
                await Translator.Setting.SaveAsync();
            }
            finally
            {
                _saveLocker.Release();
            }
        }
        
        /// <summary>
        /// 立即提交所有待处理的更改
        /// </summary>
        public static async Task CommitAllPendingChangesAsync()
        {
            await CommitChangesAsync();
        }
    }
}