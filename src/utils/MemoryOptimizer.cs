using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LiveCaptionsTranslator.utils
{
    /// <summary>
    /// 内存优化工具类，用于优化应用程序的内存使用
    /// </summary>
    public static class MemoryOptimizer
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);
        
        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr proc);
        
        // 内存优化状态
        private static bool _isOptimizationActive = false;
        private static long _lastMemoryUsage = 0;
        private static DateTime _lastOptimizationTime = DateTime.MinValue;
        private static readonly TimeSpan _minOptimizationInterval = TimeSpan.FromMinutes(1);
        
        // 内存阈值
        private const int LOW_MEMORY_THRESHOLD_MB = 100;
        private const int MEDIUM_MEMORY_THRESHOLD_MB = 200;
        private const int HIGH_MEMORY_THRESHOLD_MB = 350;
        
        /// <summary>
        /// 启动内存优化监控
        /// </summary>
        public static void StartMonitoring()
        {
            // 启动内存监控任务
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        CheckAndOptimizeMemory();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"内存优化监控出错: {ex.Message}");
                    }
                    
                    // 每30秒检查一次
                    await Task.Delay(30000);
                }
            });
        }
        
        /// <summary>
        /// 检查并优化内存使用
        /// </summary>
        private static void CheckAndOptimizeMemory()
        {
            // 获取当前内存使用
            var process = Process.GetCurrentProcess();
            long currentMemoryMB = process.WorkingSet64 / (1024 * 1024);
            
            // 如果内存使用过高或明显增长，考虑优化
            bool needsOptimization = false;
            
            if (currentMemoryMB > HIGH_MEMORY_THRESHOLD_MB)
            {
                // 内存使用超过高阈值，需要立即优化
                needsOptimization = true;
            }
            else if (currentMemoryMB > MEDIUM_MEMORY_THRESHOLD_MB && 
                     _lastMemoryUsage > 0 && 
                     currentMemoryMB > _lastMemoryUsage * 1.5)
            {
                // 内存使用超过中等阈值且增长超过50%，需要优化
                needsOptimization = true;
            }
            
            // 检查是否应该执行优化
            if (needsOptimization && !_isOptimizationActive && 
                (DateTime.Now - _lastOptimizationTime) > _minOptimizationInterval)
            {
                PerformMemoryOptimization();
            }
            
            // 更新上次内存使用
            _lastMemoryUsage = currentMemoryMB;
        }
        
        /// <summary>
        /// 执行多级内存优化
        /// </summary>
        private static void PerformMemoryOptimization()
        {
            try
            {
                _isOptimizationActive = true;
                
                // 第一级：触发GC
                GC.Collect(1, GCCollectionMode.Optimized);
                
                // 获取优化前内存
                var process = Process.GetCurrentProcess();
                long memoryBeforeMB = process.WorkingSet64 / (1024 * 1024);
                
                // 测量优化效果
                Thread.Sleep(100);
                long memoryAfterGCMB = process.WorkingSet64 / (1024 * 1024);
                
                // 如果GC没有显著效果且内存使用仍然很高，尝试更激进的优化
                if (memoryAfterGCMB > HIGH_MEMORY_THRESHOLD_MB && 
                    (memoryBeforeMB - memoryAfterGCMB) < 20)
                {
                    // 第二级：压缩工作集
                    EmptyWorkingSet(GetCurrentProcess());
                    
                    // 等待一段时间，让系统重新调整
                    Thread.Sleep(100);
                }
                
                // 更新最后优化时间
                _lastOptimizationTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"执行内存优化时出错: {ex.Message}");
            }
            finally
            {
                _isOptimizationActive = false;
            }
        }
        
        /// <summary>
        /// 主动触发内存优化
        /// </summary>
        public static void OptimizeMemoryNow(bool aggressive = false)
        {
            if (_isOptimizationActive)
                return;
                
            try
            {
                _isOptimizationActive = true;
                
                if (aggressive)
                {
                    // 激进模式：完全清理工作集并强制GC
                    GC.Collect(2, GCCollectionMode.Forced);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced);
                    
                    EmptyWorkingSet(GetCurrentProcess());
                }
                else
                {
                    // 标准模式：只进行优化级别的GC
                    GC.Collect(1, GCCollectionMode.Optimized);
                    EmptyWorkingSet(GetCurrentProcess());
                }
                
                // 更新最后优化时间
                _lastOptimizationTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"手动内存优化时出错: {ex.Message}");
            }
            finally
            {
                _isOptimizationActive = false;
            }
        }
    }
}