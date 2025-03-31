using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LiveCaptionsTranslator.utils
{
    /// <summary>
    /// 系统性能监控器 - 用于动态调整应用程序行为以适应系统负载
    /// </summary>
    public static class PerformanceMonitor
    {
        // 监控相关设置
        private static bool isMonitoringEnabled = true;
        private static readonly TimeSpan monitoringInterval = TimeSpan.FromSeconds(5);
        private static readonly int sampleHistorySize = 10;
        
        // 性能指标
        private static readonly Queue<float> cpuUsageHistory = new Queue<float>(sampleHistorySize);
        private static readonly Queue<long> memoryUsageHistory = new Queue<long>(sampleHistorySize);
        private static float currentCpuUsage = 0;
        private static long currentMemoryUsageMB = 0;
        private static readonly Dictionary<string, Stopwatch> operationTimers = new Dictionary<string, Stopwatch>();
        
        // 阈值设置
        private const float highCpuThreshold = 70.0f; // 70%
        private const float lowCpuThreshold = 30.0f;  // 30%
        private const long highMemoryThresholdMB = 200; // 200MB
        
        // 系统状态
        public enum SystemLoadState
        {
            Normal,
            High,
            Critical
        }
        
        private static SystemLoadState currentSystemState = SystemLoadState.Normal;
        
        // 性能事件
        public static event Action<SystemLoadState> SystemLoadChanged;
        
        /// <summary>
        /// 当前系统负载状态
        /// </summary>
        public static SystemLoadState CurrentSystemState => currentSystemState;
        
        /// <summary>
        /// 当前CPU使用率 (百分比)
        /// </summary>
        public static float CpuUsage => currentCpuUsage;
        
        /// <summary>
        /// 当前内存使用 (MB)
        /// </summary>
        public static long MemoryUsageMB => currentMemoryUsageMB;
        
        /// <summary>
        /// 启动性能监控
        /// </summary>
        public static void StartMonitoring()
        {
            if (isMonitoringEnabled)
            {
                Task.Run(MonitoringLoop);
            }
        }
        
        /// <summary>
        /// 启动操作计时器
        /// </summary>
        public static void StartTimer(string operationName)
        {
            if (!operationTimers.TryGetValue(operationName, out var timer))
            {
                timer = new Stopwatch();
                operationTimers[operationName] = timer;
            }
            
            timer.Restart();
        }
        
        /// <summary>
        /// 停止操作计时器并返回耗时（毫秒）
        /// </summary>
        public static long StopTimer(string operationName)
        {
            if (operationTimers.TryGetValue(operationName, out var timer))
            {
                timer.Stop();
                return timer.ElapsedMilliseconds;
            }
            
            return 0;
        }
        
        /// <summary>
        /// 获取操作平均耗时
        /// </summary>
        public static Dictionary<string, long> GetOperationTimes()
        {
            var result = new Dictionary<string, long>();
            foreach (var kvp in operationTimers)
            {
                result[kvp.Key] = kvp.Value.ElapsedMilliseconds;
            }
            
            return result;
        }
        
        /// <summary>
        /// 根据当前系统状态获取建议的轮询间隔（毫秒）
        /// </summary>
        public static int GetRecommendedPollingInterval(int baseInterval)
        {
            switch (currentSystemState)
            {
                case SystemLoadState.High:
                    return baseInterval * 2; // 高负载时延长间隔
                case SystemLoadState.Critical:
                    return baseInterval * 4; // 临界负载时显著延长间隔
                default:
                    return baseInterval;
            }
        }
        
        /// <summary>
        /// 获取建议的批处理大小
        /// </summary>
        public static int GetRecommendedBatchSize(int defaultSize)
        {
            switch (currentSystemState)
            {
                case SystemLoadState.High:
                    return Math.Max(1, defaultSize / 2); // 高负载时减小批量
                case SystemLoadState.Critical:
                    return 1; // 临界负载时使用最小批量
                default:
                    return defaultSize;
            }
        }
        
        /// <summary>
        /// 性能监控循环
        /// </summary>
        private static async Task MonitoringLoop()
        {
            Process currentProcess = Process.GetCurrentProcess();
            Stopwatch processorTimeDelta = new Stopwatch();
            TimeSpan lastProcessorTime = currentProcess.TotalProcessorTime;
            
            processorTimeDelta.Start();
            
            while (isMonitoringEnabled)
            {
                try
                {
                    // 更新进程信息
                    currentProcess.Refresh();
                    
                    // 计算CPU使用率
                    TimeSpan currentProcessorTime = currentProcess.TotalProcessorTime;
                    double cpuUsedMs = (currentProcessorTime - lastProcessorTime).TotalMilliseconds;
                    double totalMsElapsed = processorTimeDelta.ElapsedMilliseconds;
                    lastProcessorTime = currentProcessorTime;
                    
                    processorTimeDelta.Restart();
                    
                    float cpuUsagePercent = (float)(cpuUsedMs / (Environment.ProcessorCount * totalMsElapsed) * 100.0);
                    currentCpuUsage = cpuUsagePercent;
                    
                    // 保存历史样本
                    cpuUsageHistory.Enqueue(cpuUsagePercent);
                    if (cpuUsageHistory.Count > sampleHistorySize)
                        cpuUsageHistory.Dequeue();
                    
                    // 计算内存使用
                    long memoryUsageMB = currentProcess.WorkingSet64 / (1024 * 1024);
                    currentMemoryUsageMB = memoryUsageMB;
                    
                    // 保存历史样本
                    memoryUsageHistory.Enqueue(memoryUsageMB);
                    if (memoryUsageHistory.Count > sampleHistorySize)
                        memoryUsageHistory.Dequeue();
                    
                    // 分析系统状态
                    SystemLoadState newState = AnalyzeSystemState();
                    
                    // 检查状态变化
                    if (newState != currentSystemState)
                    {
                        SystemLoadState oldState = currentSystemState;
                        currentSystemState = newState;
                        
                        // 触发事件
                        SystemLoadChanged?.Invoke(newState);
                        
                        // 根据状态执行特定操作
                        HandleStateChange(oldState, newState);
                    }
                    
                    // 定期执行内存优化
                    if (currentMemoryUsageMB > highMemoryThresholdMB)
                    {
                        OptimizeMemory();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"性能监控异常: {ex.Message}");
                }
                
                // 等待下一个监控周期
                await Task.Delay(monitoringInterval);
            }
        }
        
        /// <summary>
        /// 分析系统状态
        /// </summary>
        private static SystemLoadState AnalyzeSystemState()
        {
            // 使用历史样本的平均值进行判断，避免瞬时波动的影响
            float avgCpu = 0;
            foreach (var cpu in cpuUsageHistory)
                avgCpu += cpu;
            
            if (cpuUsageHistory.Count > 0)
                avgCpu /= cpuUsageHistory.Count;
            
            long avgMemory = 0;
            foreach (var mem in memoryUsageHistory)
                avgMemory += mem;
            
            if (memoryUsageHistory.Count > 0)
                avgMemory /= memoryUsageHistory.Count;
            
            // 状态判断逻辑
            if (avgCpu > highCpuThreshold || avgMemory > highMemoryThresholdMB * 1.5)
            {
                return SystemLoadState.Critical;
            }
            else if (avgCpu > lowCpuThreshold || avgMemory > highMemoryThresholdMB)
            {
                return SystemLoadState.High;
            }
            else
            {
                return SystemLoadState.Normal;
            }
        }
        
        /// <summary>
        /// 处理系统状态变化
        /// </summary>
        private static void HandleStateChange(SystemLoadState oldState, SystemLoadState newState)
        {
            Console.WriteLine($"[系统性能] 负载状态由 {oldState} 变更为 {newState}");
            
            if (newState == SystemLoadState.Critical)
            {
                // 执行紧急资源释放
                GC.Collect(2, GCCollectionMode.Aggressive);
                
                // 释放部分缓存等操作
                // ...
            }
        }
        
        /// <summary>
        /// 优化内存使用
        /// </summary>
        private static void OptimizeMemory()
        {
            // 触发垃圾回收
            GC.Collect(1, GCCollectionMode.Optimized);
        }
    }
}