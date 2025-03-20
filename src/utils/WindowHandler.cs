using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public static class WindowHandler
    {
        // 通过时间戳记录最后保存的时间
        private static DateTime _lastSaveTime = DateTime.MinValue;
        // 最小保存间隔(毫秒)
        private const int SaveThrottleInterval = 1000;
        // 保存锁，防止并发保存
        private static readonly SemaphoreSlim _saveLock = new SemaphoreSlim(1, 1);
        // 保存队列标志，如果为true表示已有待处理的保存操作
        private static bool _pendingSave = false;

        /// <summary>
        /// 保存窗口状态(优化版)
        /// </summary>
        public static async Task<Rect> SaveStateAsync(Window? window, Setting? setting)
        {
            if (window == null || setting == null)
                return Rect.Empty;

            string windowName = window.GetType().Name;
            string newBounds = Regex.Replace(
                window.RestoreBounds.ToString(), @"(\d+\.\d{1})\d+", "$1");

            // 仅当边界真正改变时才保存
            if (setting.WindowBounds.TryGetValue(windowName, out string? existingBounds) && 
                existingBounds == newBounds)
            {
                return window.RestoreBounds;
            }

            // 更新内存中的设置
            setting.WindowBounds[windowName] = newBounds;

            // 使用节流技术来减少保存频率
            DateTime now = DateTime.Now;
            if ((now - _lastSaveTime).TotalMilliseconds < SaveThrottleInterval)
            {
                // 如果已经有一个待处理的保存，就不再添加新的
                if (!_pendingSave)
                {
                    _pendingSave = true;
                    // 安排延迟保存
                    await Task.Delay(SaveThrottleInterval);
                    await SaveSettingsAsync(setting);
                }
                return window.RestoreBounds;
            }

            // 更新最后保存时间并执行保存
            _lastSaveTime = now;
            await SaveSettingsAsync(setting);
            
            return window.RestoreBounds;
        }

        /// <summary>
        /// 异步保存设置
        /// </summary>
        private static async Task SaveSettingsAsync(Setting setting)
        {
            // 使用信号量确保一次只有一个保存操作
            bool lockTaken = false;
            try
            {
                // 尝试获取锁，但不阻塞UI线程
                lockTaken = await _saveLock.WaitAsync(0);
                if (!lockTaken) return;
                
                _pendingSave = false;
                
                // 将保存操作移至后台线程
                await Task.Run(() => 
                {
                    try
                    {
                        setting.Save();
                    }
                    catch (Exception ex)
                    {
                        // 记录错误但不中断应用程序
                        Console.WriteLine($"保存设置时出错: {ex.Message}");
                    }
                });
            }
            finally
            {
                if (lockTaken)
                {
                    _saveLock.Release();
                }
            }
        }

        // 保持兼容性的同步方法
        public static Rect SaveState(Window? window, Setting? setting)
        {
            if (window == null || setting == null)
                return Rect.Empty;
                
            // 在UI线程上启动异步操作，但不等待它
            Task.Run(async () => await SaveStateAsync(window, setting));
            
            string windowName = window.GetType().Name;
            setting.WindowBounds[windowName] = Regex.Replace(
                window.RestoreBounds.ToString(), @"(\d+\.\d{1})\d+", "$1");
                
            return window.RestoreBounds;
        }

        public static Rect LoadState(Window? window, Setting? setting)
        {
            if (window == null || setting == null)
                return Rect.Empty;
            string windowName = window.GetType().Name;
            if (!setting.WindowBounds.ContainsKey(windowName))
                return Rect.Empty;
                
            Rect bound = Rect.Parse(setting.WindowBounds[windowName]);
            return bound;
        }

        public static void RestoreState(Window? window, Rect bound)
        {
            if (window == null || bound.IsEmpty)
                return;
            window.Top = bound.Top;
            window.Left = bound.Left;

            // Restore the size only for a manually sized
            if (window.SizeToContent == SizeToContent.Manual)
            {
                window.Width = bound.Width;
                window.Height = bound.Height;
            }
        }
    }
}