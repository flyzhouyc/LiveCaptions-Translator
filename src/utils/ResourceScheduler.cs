using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LiveCaptionsTranslator.utils
{
    /// <summary>
    /// 高级资源调度器 - 提供更细粒度的资源监控和任务调度
    /// </summary>
    public static class ResourceScheduler
    {
        // 任务优先级枚举
        public enum TaskPriority
        {
            Critical = 0,   // 关键任务，必须执行
            High = 1,       // 高优先级，应当优先执行
            Normal = 2,     // 正常优先级
            Low = 3,        // 低优先级，资源充足时执行
            Background = 4  // 后台任务，只在系统极度空闲时执行
        }
        
        // 系统资源状态枚举
        public enum ResourceState
        {
            Abundant,   // 资源充足
            Normal,     // 资源正常
            Limited,    // 资源有限
            Scarce,     // 资源稀缺
            Critical    // 资源极度紧张
        }
        
        // 资源类型枚举
        [Flags]
        public enum ResourceType
        {
            None = 0,
            CPU = 1,
            Memory = 2,
            IO = 4,
            Network = 8,
            All = CPU | Memory | IO | Network
        }
        
        // 资源监控数据
        private class ResourceMetrics
        {
            public float CpuUsage { get; set; }
            public long MemoryAvailableMB { get; set; }
            public float DiskIOUsage { get; set; }
            public float NetworkUsage { get; set; }
            public DateTime Timestamp { get; set; }
        }
        
        // 任务信息
        private class ScheduledTask
        {
            public Func<CancellationToken, Task> Action { get; set; }
            public TaskPriority Priority { get; set; }
            public CancellationTokenSource CTS { get; set; }
            public ResourceType RequiredResources { get; set; }
            public DateTime EnqueueTime { get; set; }
            public int Retries { get; set; }
        }
        
        // 资源阈值配置
        private static class ResourceThresholds
        {
            // CPU 阈值 (百分比)
            public const float CpuAbundant = 30.0f;
            public const float CpuNormal = 50.0f;
            public const float CpuLimited = 70.0f;
            public const float CpuScarce = 85.0f;
            
            // 内存阈值 (MB)
            public const long MemoryAbundant = 1000;
            public const long MemoryNormal = 500;
            public const long MemoryLimited = 250;
            public const long MemoryScarce = 100;
            
            // 网络阈值 (KB/s)
            public const float NetworkAbundant = 500.0f;
            public const float NetworkNormal = 1000.0f;
            public const float NetworkLimited = 2000.0f;
            public const float NetworkScarce = 5000.0f;
        }
        
        // 资源监控状态
        private static ResourceState _cpuState = ResourceState.Normal;
        private static ResourceState _memoryState = ResourceState.Normal;
        private static ResourceState _diskState = ResourceState.Normal;
        private static ResourceState _networkState = ResourceState.Normal;
        private static ResourceState _overallState = ResourceState.Normal;
        
        // 历史资源指标
        private static readonly Queue<ResourceMetrics> _resourceHistory = new Queue<ResourceMetrics>();
        private static readonly int _maxHistorySize = 20;
        
        // 任务队列 (按优先级)
        private static readonly ConcurrentDictionary<TaskPriority, ConcurrentQueue<ScheduledTask>> _taskQueues = 
            new ConcurrentDictionary<TaskPriority, ConcurrentQueue<ScheduledTask>>();
        
        // 资源预测模型参数
        private static readonly double[] _cpuTrendCoefficients = new double[3];
        private static readonly double[] _memoryTrendCoefficients = new double[3];
        
        // 监控与调度线程
        private static Thread _monitoringThread;
        private static Thread _schedulingThread;
        private static volatile bool _isRunning = false;
        
        // 性能计数器
        private static PerformanceCounter _cpuCounter;
        private static PerformanceCounter _diskReadCounter;
        private static PerformanceCounter _diskWriteCounter;
        private static readonly Dictionary<string, NetworkAdapter> _networkAdapters = new Dictionary<string, NetworkAdapter>();
        
        // 同步对象
        private static readonly object _monitoringLock = new object();
        
        // 网络适配器信息
        private class NetworkAdapter
        {
            public long LastBytesSent { get; set; }
            public long LastBytesReceived { get; set; }
            public DateTime LastSampleTime { get; set; }
            public float BytesPerSecond { get; set; }
        }
        
        // 事件
        public static event Action<ResourceState> ResourceStateChanged;
        public static event Action<TaskPriority, int> QueueSizeChanged;
        public static event Action<string, float> ResourceUsageUpdated;
        
        // 外部访问属性
        public static ResourceState CurrentResourceState => _overallState;
        public static float CurrentCpuUsage { get; private set; }
        public static long CurrentMemoryUsageMB { get; private set; }
        public static float CurrentNetworkUsageKBps { get; private set; }
        public static float CurrentDiskIOUsageMBps { get; private set; }
        
        // Win32 API
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
        
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }
        
        // 静态构造函数 - 初始化资源调度器
        static ResourceScheduler()
        {
            // 初始化任务队列
            foreach (TaskPriority priority in Enum.GetValues(typeof(TaskPriority)))
            {
                _taskQueues[priority] = new ConcurrentQueue<ScheduledTask>();
            }
            
            // 初始化性能计数器
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                
                // 初始化网络适配器
                InitializeNetworkAdapters();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"初始化性能计数器失败: {ex.Message}");
            }
            
            // 创建监控线程
            _monitoringThread = new Thread(MonitoringLoop)
            {
                IsBackground = true,
                Name = "ResourceMonitoring",
                Priority = ThreadPriority.BelowNormal
            };
            
            // 创建调度线程
            _schedulingThread = new Thread(SchedulingLoop)
            {
                IsBackground = true,
                Name = "TaskScheduling",
                Priority = ThreadPriority.AboveNormal
            };
        }
        
        // 初始化网络适配器
        private static void InitializeNetworkAdapters()
        {
            try
            {
                _networkAdapters.Clear();
                
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Up && 
                        (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || 
                         ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet))
                    {
                        var stats = ni.GetIPStatistics();
                        _networkAdapters[ni.Name] = new NetworkAdapter
                        {
                            LastBytesSent = stats.BytesSent,
                            LastBytesReceived = stats.BytesReceived,
                            LastSampleTime = DateTime.Now,
                            BytesPerSecond = 0
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"初始化网络适配器失败: {ex.Message}");
            }
        }
        
        // 启动资源调度器
        public static void Start()
        {
            if (_isRunning)
                return;
                
            _isRunning = true;
            
            // 启动监控线程
            if (!_monitoringThread.IsAlive)
                _monitoringThread.Start();
                
            // 启动调度线程
            if (!_schedulingThread.IsAlive)
                _schedulingThread.Start();
                
            Console.WriteLine("资源调度器已启动");
        }
        
        // 停止资源调度器
        public static void Stop()
        {
            _isRunning = false;
            
            // 等待线程自然退出
            try
            {
                if (_monitoringThread.IsAlive)
                    _monitoringThread.Join(1000);
                    
                if (_schedulingThread.IsAlive)
                    _schedulingThread.Join(1000);
            }
            catch
            {
                // 忽略线程终止错误
            }
            
            Console.WriteLine("资源调度器已停止");
        }
        
        // 资源监控循环
        private static void MonitoringLoop()
        {
            try
            {
                while (_isRunning)
                {
                    try
                    {
                        // 获取当前资源指标
                        var metrics = CollectResourceMetrics();
                        
                        // 更新资源历史
                        UpdateResourceHistory(metrics);
                        
                        // 更新资源状态
                        UpdateResourceStates(metrics);
                        
                        // 更新资源趋势预测
                        UpdateResourcePrediction();
                        
                        // 适应性休眠
                        int sleepTime = DetermineMonitoringSleepTime();
                        Thread.Sleep(sleepTime);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"资源监控异常: {ex.Message}");
                        Thread.Sleep(5000); // 出错时休眠较长时间
                    }
                }
            }
            catch (ThreadAbortException)
            {
                // 线程被终止，清理资源
            }
            catch (Exception ex)
            {
                Console.WriteLine($"资源监控线程致命错误: {ex.Message}");
            }
        }
        
        // 任务调度循环
        private static void SchedulingLoop()
        {
            try
            {
                while (_isRunning)
                {
                    try
                    {
                        // 基于当前资源状态决定可执行的任务优先级
                        TaskPriority maxExecutablePriority = DetermineExecutablePriority();
                        
                        // 执行符合条件的任务
                        bool taskExecuted = ExecuteQualifiedTasks(maxExecutablePriority);
                        
                        // 适应性休眠
                        int sleepTime = taskExecuted ? 10 : 100; // 如果有任务执行，快速循环；否则等待较长时间
                        Thread.Sleep(sleepTime);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"任务调度异常: {ex.Message}");
                        Thread.Sleep(1000); // 出错时休眠较长时间
                    }
                }
            }
            catch (ThreadAbortException)
            {
                // 线程被终止，清理资源
            }
            catch (Exception ex)
            {
                Console.WriteLine($"任务调度线程致命错误: {ex.Message}");
            }
        }
        
        // 收集资源指标
        private static ResourceMetrics CollectResourceMetrics()
        {
            var metrics = new ResourceMetrics
            {
                Timestamp = DateTime.Now
            };
            
            try
            {
                // 收集CPU使用率
                metrics.CpuUsage = _cpuCounter?.NextValue() ?? 0;
                CurrentCpuUsage = metrics.CpuUsage;
                
                // 收集内存使用情况
                MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref memStatus))
                {
                    metrics.MemoryAvailableMB = (long)(memStatus.ullAvailPhys / (1024 * 1024));
                    CurrentMemoryUsageMB = (long)((memStatus.ullTotalPhys - memStatus.ullAvailPhys) / (1024 * 1024));
                }
                
                // 收集磁盘IO使用情况
                float diskReadBytes = _diskReadCounter?.NextValue() ?? 0;
                float diskWriteBytes = _diskWriteCounter?.NextValue() ?? 0;
                metrics.DiskIOUsage = (diskReadBytes + diskWriteBytes) / (1024 * 1024); // 转换为MB/s
                CurrentDiskIOUsageMBps = metrics.DiskIOUsage;
                
                // 收集网络使用情况
                float totalNetworkUsage = 0;
                try
                {
                    foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (_networkAdapters.TryGetValue(ni.Name, out var adapter))
                        {
                            var stats = ni.GetIPStatistics();
                            long bytesSent = stats.BytesSent;
                            long bytesReceived = stats.BytesReceived;
                            DateTime now = DateTime.Now;
                            
                            long totalBytes = (bytesSent - adapter.LastBytesSent) + (bytesReceived - adapter.LastBytesReceived);
                            double seconds = (now - adapter.LastSampleTime).TotalSeconds;
                            
                            if (seconds > 0)
                            {
                                adapter.BytesPerSecond = (float)(totalBytes / seconds);
                                totalNetworkUsage += adapter.BytesPerSecond;
                            }
                            
                            adapter.LastBytesSent = bytesSent;
                            adapter.LastBytesReceived = bytesReceived;
                            adapter.LastSampleTime = now;
                        }
                    }
                }
                catch
                {
                    // 忽略网络监控错误
                }
                
                metrics.NetworkUsage = totalNetworkUsage / 1024; // 转换为KB/s
                CurrentNetworkUsageKBps = metrics.NetworkUsage;
                
                // 触发资源使用率更新事件
                ResourceUsageUpdated?.Invoke("CPU", metrics.CpuUsage);
                ResourceUsageUpdated?.Invoke("Memory", CurrentMemoryUsageMB);
                ResourceUsageUpdated?.Invoke("Disk", metrics.DiskIOUsage);
                ResourceUsageUpdated?.Invoke("Network", metrics.NetworkUsage);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"收集资源指标失败: {ex.Message}");
            }
            
            return metrics;
        }
        
        // 更新资源历史
        private static void UpdateResourceHistory(ResourceMetrics metrics)
        {
            lock (_monitoringLock)
            {
                _resourceHistory.Enqueue(metrics);
                while (_resourceHistory.Count > _maxHistorySize)
                {
                    _resourceHistory.Dequeue();
                }
            }
        }
        
        // 更新资源状态
        private static void UpdateResourceStates(ResourceMetrics metrics)
        {
            ResourceState oldOverallState = _overallState;
            
            // 更新CPU状态
            if (metrics.CpuUsage < ResourceThresholds.CpuAbundant)
                _cpuState = ResourceState.Abundant;
            else if (metrics.CpuUsage < ResourceThresholds.CpuNormal)
                _cpuState = ResourceState.Normal;
            else if (metrics.CpuUsage < ResourceThresholds.CpuLimited)
                _cpuState = ResourceState.Limited;
            else if (metrics.CpuUsage < ResourceThresholds.CpuScarce)
                _cpuState = ResourceState.Scarce;
            else
                _cpuState = ResourceState.Critical;
                
            // 更新内存状态
            if (metrics.MemoryAvailableMB > ResourceThresholds.MemoryAbundant)
                _memoryState = ResourceState.Abundant;
            else if (metrics.MemoryAvailableMB > ResourceThresholds.MemoryNormal)
                _memoryState = ResourceState.Normal;
            else if (metrics.MemoryAvailableMB > ResourceThresholds.MemoryLimited)
                _memoryState = ResourceState.Limited;
            else if (metrics.MemoryAvailableMB > ResourceThresholds.MemoryScarce)
                _memoryState = ResourceState.Scarce;
            else
                _memoryState = ResourceState.Critical;
                
            // 更新网络状态
            if (metrics.NetworkUsage < ResourceThresholds.NetworkAbundant)
                _networkState = ResourceState.Abundant;
            else if (metrics.NetworkUsage < ResourceThresholds.NetworkNormal)
                _networkState = ResourceState.Normal;
            else if (metrics.NetworkUsage < ResourceThresholds.NetworkLimited)
                _networkState = ResourceState.Limited;
            else if (metrics.NetworkUsage < ResourceThresholds.NetworkScarce)
                _networkState = ResourceState.Scarce;
            else
                _networkState = ResourceState.Critical;
                
            // 更新整体状态 (取最差的状态)
            _overallState = (ResourceState)Math.Max((int)_cpuState, Math.Max((int)_memoryState, (int)_networkState));
            
            // 如果状态有变化，触发事件
            if (_overallState != oldOverallState)
            {
                ResourceStateChanged?.Invoke(_overallState);
            }
        }
        
        // 更新资源趋势预测
        private static void UpdateResourcePrediction()
        {
            lock (_monitoringLock)
            {
                if (_resourceHistory.Count < 5)
                    return;
                    
                // 使用简单线性回归预测资源趋势
                var dataPoints = _resourceHistory.ToArray();
                double[] times = new double[dataPoints.Length];
                double[] cpuValues = new double[dataPoints.Length];
                double[] memoryValues = new double[dataPoints.Length];
                
                // 基准时间点
                DateTime baseTime = dataPoints[0].Timestamp;
                
                for (int i = 0; i < dataPoints.Length; i++)
                {
                    times[i] = (dataPoints[i].Timestamp - baseTime).TotalSeconds;
                    cpuValues[i] = dataPoints[i].CpuUsage;
                    memoryValues[i] = dataPoints[i].MemoryAvailableMB;
                }
                
                // 使用线性回归预测趋势
                LinearRegression(times, cpuValues, out _cpuTrendCoefficients[0], out _cpuTrendCoefficients[1]);
                LinearRegression(times, memoryValues, out _memoryTrendCoefficients[0], out _memoryTrendCoefficients[1]);
            }
        }
        
        // 简单线性回归
        private static void LinearRegression(double[] x, double[] y, out double a, out double b)
        {
            double sumX = 0;
            double sumY = 0;
            double sumXY = 0;
            double sumX2 = 0;
            int n = x.Length;
            
            for (int i = 0; i < n; i++)
            {
                sumX += x[i];
                sumY += y[i];
                sumXY += x[i] * y[i];
                sumX2 += x[i] * x[i];
            }
            
            double xMean = sumX / n;
            double yMean = sumY / n;
            
            double numerator = sumXY - n * xMean * yMean;
            double denominator = sumX2 - n * xMean * xMean;
            
            if (Math.Abs(denominator) < 1e-10)
            {
                a = 0;
                b = yMean;
            }
            else
            {
                b = numerator / denominator;
                a = yMean - b * xMean;
            }
        }
        
        // 确定监控频率
        private static int DetermineMonitoringSleepTime()
        {
            // 基于当前资源状态调整监控频率
            switch (_overallState)
            {
                case ResourceState.Critical:
                    return 500; // 资源极度紧张时，频繁监控 (500ms)
                case ResourceState.Scarce:
                    return 1000; // 资源稀缺时，较频繁监控 (1s)
                case ResourceState.Limited:
                    return 2000; // 资源有限时，正常监控 (2s)
                case ResourceState.Normal:
                    return 3000; // 资源正常时，降低监控频率 (3s)
                case ResourceState.Abundant:
                    return 5000; // 资源充足时，大幅降低监控频率 (5s)
                default:
                    return 3000; // 默认监控频率 (3s)
            }
        }
        
        // 确定可执行的任务优先级
        private static TaskPriority DetermineExecutablePriority()
        {
            // 基于当前资源状态确定可执行的最低优先级
            switch (_overallState)
            {
                case ResourceState.Abundant:
                    return TaskPriority.Background; // 资源充足，所有任务都可以执行
                case ResourceState.Normal:
                    return TaskPriority.Low; // 资源正常，执行低优先级及以上任务
                case ResourceState.Limited:
                    return TaskPriority.Normal; // 资源有限，执行正常优先级及以上任务
                case ResourceState.Scarce:
                    return TaskPriority.High; // 资源稀缺，仅执行高优先级及以上任务
                case ResourceState.Critical:
                    return TaskPriority.Critical; // 资源极度紧张，仅执行关键任务
                default:
                    return TaskPriority.Normal; // 默认执行正常优先级及以上任务
            }
        }
        
        // 执行符合条件的任务
        private static bool ExecuteQualifiedTasks(TaskPriority maxPriority)
        {
            bool anyTaskExecuted = false;
            
            // 从高优先级到可执行的最低优先级遍历任务队列
            for (int i = (int)TaskPriority.Critical; i <= (int)maxPriority; i++)
            {
                TaskPriority priority = (TaskPriority)i;
                var queue = _taskQueues[priority];
                
                if (queue.TryDequeue(out ScheduledTask task))
                {
                    // 检查任务所需资源是否可用
                    if (AreResourcesAvailable(task.RequiredResources))
                    {
                        // 执行任务
                        Task.Run(async () =>
                        {
                            try
                            {
                                await task.Action(task.CTS.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                // 任务被取消，忽略
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"任务执行异常: {ex.Message}");
                                
                                // 重试逻辑
                                if (task.Retries < GetMaxRetries(priority))
                                {
                                    task.Retries++;
                                    ScheduleTask(task.Action, priority, task.RequiredResources, task.Retries);
                                }
                            }
                        });
                        
                        anyTaskExecuted = true;
                        
                        // 触发队列大小变更事件
                        QueueSizeChanged?.Invoke(priority, queue.Count);
                    }
                    else
                    {
                        // 资源不足，重新入队
                        queue.Enqueue(task);
                    }
                }
            }
            
            return anyTaskExecuted;
        }
        
        // 检查资源是否可用
        private static bool AreResourcesAvailable(ResourceType requiredResources)
        {
            if (requiredResources == ResourceType.None)
                return true;
                
            // 检查CPU资源
            if ((requiredResources & ResourceType.CPU) != 0 && _cpuState >= ResourceState.Scarce)
                return false;
                
            // 检查内存资源
            if ((requiredResources & ResourceType.Memory) != 0 && _memoryState >= ResourceState.Scarce)
                return false;
                
            // 检查网络资源
            if ((requiredResources & ResourceType.Network) != 0 && _networkState >= ResourceState.Scarce)
                return false;
                
            return true;
        }
        
        // 获取最大重试次数 (基于优先级)
        private static int GetMaxRetries(TaskPriority priority)
        {
            switch (priority)
            {
                case TaskPriority.Critical:
                    return 5; // 关键任务允许更多重试
                case TaskPriority.High:
                    return 3;
                case TaskPriority.Normal:
                    return 2;
                case TaskPriority.Low:
                case TaskPriority.Background:
                    return 1; // 低优先级任务重试次数少
                default:
                    return 2;
            }
        }
        
        // 预测未来资源状态
        public static ResourceState PredictFutureResourceState(int secondsAhead)
        {
            if (secondsAhead <= 0)
                return _overallState;
                
            // 预测CPU使用率
            double predictedCpu = _cpuTrendCoefficients[0] + _cpuTrendCoefficients[1] * secondsAhead;
            predictedCpu = Math.Max(0, Math.Min(100, predictedCpu)); // 确保在有效范围内
            
            // 预测内存可用量
            double predictedMemory = _memoryTrendCoefficients[0] + _memoryTrendCoefficients[1] * secondsAhead;
            predictedMemory = Math.Max(0, predictedMemory); // 确保非负
            
            // 基于预测值确定未来资源状态
            ResourceState futureCpuState, futureMemoryState;
            
            // 预测CPU状态
            if (predictedCpu < ResourceThresholds.CpuAbundant)
                futureCpuState = ResourceState.Abundant;
            else if (predictedCpu < ResourceThresholds.CpuNormal)
                futureCpuState = ResourceState.Normal;
            else if (predictedCpu < ResourceThresholds.CpuLimited)
                futureCpuState = ResourceState.Limited;
            else if (predictedCpu < ResourceThresholds.CpuScarce)
                futureCpuState = ResourceState.Scarce;
            else
                futureCpuState = ResourceState.Critical;
                
            // 预测内存状态
            if (predictedMemory > ResourceThresholds.MemoryAbundant)
                futureMemoryState = ResourceState.Abundant;
            else if (predictedMemory > ResourceThresholds.MemoryNormal)
                futureMemoryState = ResourceState.Normal;
            else if (predictedMemory > ResourceThresholds.MemoryLimited)
                futureMemoryState = ResourceState.Limited;
            else if (predictedMemory > ResourceThresholds.MemoryScarce)
                futureMemoryState = ResourceState.Scarce;
            else
                futureMemoryState = ResourceState.Critical;
                
            // 返回较差的状态
            return (ResourceState)Math.Max((int)futureCpuState, (int)futureMemoryState);
        }
        
        // 外部API：调度任务
        public static Task<T> ScheduleTask<T>(Func<CancellationToken, Task<T>> action, TaskPriority priority, ResourceType requiredResources = ResourceType.CPU)
        {
            var taskCompletionSource = new TaskCompletionSource<T>();
            var cts = new CancellationTokenSource();
            
            // 创建任务
            var scheduledTask = new ScheduledTask
            {
                Action = async (token) =>
                {
                    try
                    {
                        var result = await action(token); // 保存操作的返回值
                        taskCompletionSource.TrySetResult(result); // 设置结果
                    }
                    catch (OperationCanceledException)
                    {
                        taskCompletionSource.TrySetCanceled();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        taskCompletionSource.TrySetException(ex);
                        throw;
                    }
                },
                Priority = priority,
                CTS = cts,
                RequiredResources = requiredResources,
                EnqueueTime = DateTime.Now,
                Retries = 0
            };
            
            // 将任务加入队列
            _taskQueues[priority].Enqueue(scheduledTask);
            
            // 触发队列大小变更事件
            QueueSizeChanged?.Invoke(priority, _taskQueues[priority].Count);
            
            // 返回任务结果
            return taskCompletionSource.Task;
        }
        
        // 简化版：调度无返回值的任务
        public static Task ScheduleTask(Func<CancellationToken, Task> action, TaskPriority priority, ResourceType requiredResources = ResourceType.CPU, int initialRetries = 0)
        {
            var cts = new CancellationTokenSource();
            
            // 创建任务
            var scheduledTask = new ScheduledTask
            {
                Action = action,
                Priority = priority,
                CTS = cts,
                RequiredResources = requiredResources,
                EnqueueTime = DateTime.Now,
                Retries = initialRetries
            };
            
            // 将任务加入队列
            _taskQueues[priority].Enqueue(scheduledTask);
            
            // 触发队列大小变更事件
            QueueSizeChanged?.Invoke(priority, _taskQueues[priority].Count);
            
            // 创建跟踪任务
            var taskCompletionSource = new TaskCompletionSource<bool>();
            
            // 监控任务完成
            Task.Run(async () =>
            {
                // 最大等待时间
                int maxWaitSeconds = GetMaxWaitTime(priority);
                DateTime startTime = DateTime.Now;
                
                while (_isRunning && DateTime.Now - startTime < TimeSpan.FromSeconds(maxWaitSeconds))
                {
                    // 检查任务是否仍在队列中
                    bool stillInQueue = false;
                    foreach (var queue in _taskQueues.Values)
                    {
                        if (queue.Contains(scheduledTask))
                        {
                            stillInQueue = true;
                            break;
                        }
                    }
                    
                    if (!stillInQueue)
                    {
                        taskCompletionSource.TrySetResult(true);
                        return;
                    }
                    
                    await Task.Delay(500);
                }
                
                // 超时，取消任务
                if (!taskCompletionSource.Task.IsCompleted)
                {
                    cts.Cancel();
                    taskCompletionSource.TrySetCanceled();
                }
            });
            
            return taskCompletionSource.Task;
        }
        
        // 获取最大等待时间 (秒)
        private static int GetMaxWaitTime(TaskPriority priority)
        {
            switch (priority)
            {
                case TaskPriority.Critical:
                    return 10; // 关键任务等待10秒
                case TaskPriority.High:
                    return 30; // 高优先级任务等待30秒
                case TaskPriority.Normal:
                    return 60; // 普通任务等待1分钟
                case TaskPriority.Low:
                    return 120; // 低优先级任务等待2分钟
                case TaskPriority.Background:
                    return 300; // 后台任务等待5分钟
                default:
                    return 60;
            }
        }
        
        // 取消所有任务
        public static void CancelAllTasks()
        {
            foreach (var queue in _taskQueues.Values)
            {
                while (queue.TryDequeue(out ScheduledTask task))
                {
                    task.CTS.Cancel();
                }
            }
        }
        
        // 取消特定优先级的任务
        public static void CancelTasks(TaskPriority priority)
        {
            var queue = _taskQueues[priority];
            while (queue.TryDequeue(out ScheduledTask task))
            {
                task.CTS.Cancel();
            }
            
            // 触发队列大小变更事件
            QueueSizeChanged?.Invoke(priority, 0);
        }
        
        // 获取队列中任务数量
        public static int GetQueueSize(TaskPriority priority)
        {
            return _taskQueues[priority].Count;
        }
        
        // 获取所有队列任务总数
        public static int GetTotalQueueSize()
        {
            int total = 0;
            foreach (var queue in _taskQueues.Values)
            {
                total += queue.Count;
            }
            return total;
        }
        
        // 获取可利用CPU核心数
        public static int GetAvailableCPUCores()
        {
            int totalCores = Environment.ProcessorCount;
            
            // 根据当前CPU使用率计算可用核心数
            int availableCores = Math.Max(1, (int)(totalCores * (1 - CurrentCpuUsage / 100.0)));
            
            return availableCores;
        }
        
        // 获取任务并行度
        public static int GetParallelismDegree()
        {
            // 基于可用CPU核心数确定并行度
            int availableCores = GetAvailableCPUCores();
            
            // 基于当前资源状态进一步调整
            switch (_overallState)
            {
                case ResourceState.Abundant:
                    return Math.Max(1, availableCores);
                case ResourceState.Normal:
                    return Math.Max(1, availableCores - 1);
                case ResourceState.Limited:
                    return Math.Max(1, availableCores / 2);
                case ResourceState.Scarce:
                    return 1;
                case ResourceState.Critical:
                    return 1;
                default:
                    return Math.Max(1, availableCores / 2);
            }
        }
        
        // 获取资源使用率报告
        public static string GetResourceReport()
        {
            return $"CPU: {CurrentCpuUsage:F1}% | Memory: {CurrentMemoryUsageMB} MB | Network: {CurrentNetworkUsageKBps:F1} KB/s | Disk: {CurrentDiskIOUsageMBps:F1} MB/s | State: {_overallState}";
        }
        
        // 释放资源 (清理性能计数器等)
        public static void Cleanup()
        {
            _isRunning = false;
            
            try
            {
                // 取消所有任务
                CancelAllTasks();
                
                // 清理性能计数器
                _cpuCounter?.Dispose();
                _diskReadCounter?.Dispose();
                _diskWriteCounter?.Dispose();
                
                // 清理事件订阅
                ResourceStateChanged = null;
                QueueSizeChanged = null;
                ResourceUsageUpdated = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"资源调度器清理失败: {ex.Message}");
            }
        }
    }
}