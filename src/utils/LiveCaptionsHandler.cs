﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Collections.Generic;
using System.Linq;


namespace LiveCaptionsTranslator.utils
{
    public static class LiveCaptionsHandler
    {
        public static readonly string PROCESS_NAME = "LiveCaptions";

        // 缓存LiveCaptions的自动化元素和句柄，减少重复查找
        private static AutomationElement? captionsTextBlock = null;
        private static nint? windowHandle = null;
        private static int processId = -1;
        
        // 用于监控LiveCaptions性能
        private static readonly Stopwatch performanceMonitor = new Stopwatch();
        private static int consecutiveSlowResponses = 0;
        private static int maxConsecutiveSlowResponses = 3;
        private static bool isLowResourceMode = false;
        
        // 自动恢复计数器
        private static int autoRecoveryAttempts = 0;
        private static DateTime lastRecoveryTime = DateTime.MinValue;
        private static readonly TimeSpan recoveryThrottleTime = TimeSpan.FromMinutes(2);
        
        // 添加更多控制参数
        private static int consecutiveFailureCount = 0;
        private static readonly int maxRetryAttempts = 3;
        private static readonly TimeSpan recoveryDelay = TimeSpan.FromSeconds(0.5);
        private static readonly TimeSpan memoryCheckInterval = TimeSpan.FromSeconds(30);
        private static DateTime lastMemoryCheckTime = DateTime.MinValue;
        private static long lastMemoryUsage = 0;
        private static int baseSampleInterval = 25; // 默认字幕采样间隔(毫秒)
        private static int currentSampleInterval = 25;
        private static bool isHighLoadMode = false;
        
        // 优化：自动化元素结果缓存
        private static readonly ConcurrentDictionary<string, AutomationElement> _automationElementCache = 
            new ConcurrentDictionary<string, AutomationElement>();
        private static int _automationCacheMisses = 0;
        private static DateTime _lastCacheCleanup = DateTime.MinValue;
        
        // 优化：增强型共享内存通信
        private static readonly string SHARED_MEM_NAME = "LiveCaptionsTranslator_SharedMemory";
        private static readonly string SHARED_EVENT_DATA_READY = "LiveCaptionsTranslator_DataReady";
        private static readonly string SHARED_EVENT_DATA_PROCESSED = "LiveCaptionsTranslator_DataProcessed";
        private static MemoryMappedFile _sharedMemory;
        private static MemoryMappedViewAccessor _sharedMemoryView;
        private static EventWaitHandle _dataReadyEvent;
        private static EventWaitHandle _dataProcessedEvent;
        private static bool _useSharedMemory = false;
        private static bool _sharedMemoryInitialized = false;
        private static readonly object _sharedMemoryLock = new object();
        private static readonly byte[] _memoryBuffer = new byte[8192]; // 增加缓冲区大小
        private static long _lastCaptionHash = 0; // 上次字幕内容的哈希值
        private static readonly Thread _heartbeatThread;
        private static volatile bool _isHeartbeatRunning = false;
        private static DateTime _lastHeartbeatTime = DateTime.MinValue;
        private static int _heartbeatMissCount = 0;
        private static readonly int MAX_HEARTBEAT_MISS = 3;
        
        // 通信协议版本
        private const int PROTOCOL_VERSION = 2;
        
        // 共享内存布局
        // 0-3: 状态标志 (0=未初始化, 1=初始化完成, 2=数据就绪, 3=心跳)
        // 4-7: 数据长度
        // 8-11: 协议版本
        // 12-19: 时间戳
        // 20-27: 窗口句柄
        // 28-31: 检查和
        // 32-8191: 数据区域
        
        // 缓存控制和性能相关参数
        private static readonly TimeSpan _cacheCleanupInterval = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan _lowLoadReassessmentInterval = TimeSpan.FromMinutes(5);
        private static DateTime _lastLowLoadReassessment = DateTime.MinValue;
        
        // 启动单独的字幕收集线程
        private static Thread _captionCollectorThread;
        private static volatile bool _isCaptionCollectorRunning = false;
        private static readonly ConcurrentQueue<string> _captionQueue = new ConcurrentQueue<string>();
        private static readonly AutoResetEvent _captionProcessEvent = new AutoResetEvent(false);
        private static readonly int MAX_QUEUE_SIZE = 20;
        private static string _lastCaptionText = string.Empty;
        private static readonly TimeSpan _maxCaptionAge = TimeSpan.FromSeconds(2); // 字幕最大有效期
        
        // 性能状态枚举
        public enum PerformanceState
        {
            Normal,
            LowResource,
            Critical
        }
        
        // 性能状态变更事件
        public static event Action<PerformanceState>? PerformanceStateChanged;
        
        // 内存问题通知事件
        public static event Action<long>? LiveCaptionsMemoryIssue;
        
        // LiveCaptions恢复尝试事件
        public static event Action<int>? LiveCaptionsRecoveryAttempt;
        
        // LiveCaptions重启请求事件
        public static event Action? LiveCaptionsRestartRequested;
        
        // 字幕更新事件
        public static event Action<string>? CaptionUpdated;
        
        // 静态构造函数 - 初始化共享内存和性能监控
        static LiveCaptionsHandler()
        {
            // 初始化心跳线程
            _heartbeatThread = new Thread(HeartbeatLoop)
            {
                IsBackground = true,
                Name = "LiveCaptions_Heartbeat",
                Priority = ThreadPriority.BelowNormal
            };
            
            // 初始化字幕收集线程
            _captionCollectorThread = new Thread(CaptionCollectorLoop)
            {
                IsBackground = true,
                Name = "LiveCaptions_CaptionCollector",
                Priority = ThreadPriority.AboveNormal
            };
            
            // 尝试初始化共享内存通信
            InitializeSharedMemory();
            
            // 启动性能监控
            performanceMonitor.Start();
        }
        
        // 优化：心跳机制确保共享内存连接稳定
        private static void HeartbeatLoop()
        {
            try
            {
                while (_isHeartbeatRunning)
                {
                    try
                    {
                        if (!_sharedMemoryInitialized)
                        {
                            Thread.Sleep(1000);
                            continue;
                        }
                        
                        // 发送心跳
                        lock (_sharedMemoryLock)
                        {
                            // 写入心跳数据
                            _sharedMemoryView.Write(0, 3); // 状态 = 心跳
                            _sharedMemoryView.Write(12, DateTime.UtcNow.Ticks); // 时间戳
                        }
                        
                        // 尝试触发事件，通知LiveCaptions进程
                        try
                        {
                            _dataReadyEvent?.Set();
                        }
                        catch
                        {
                            // 忽略事件触发错误
                        }
                        
                        // 等待心跳响应
                        bool response = false;
                        try
                        {
                            response = _dataProcessedEvent?.WaitOne(500) ?? false;
                        }
                        catch
                        {
                            response = false;
                        }
                        
                        if (!response)
                        {
                            _heartbeatMissCount++;
                            if (_heartbeatMissCount > MAX_HEARTBEAT_MISS)
                            {
                                // 心跳丢失过多，重新初始化共享内存
                                Console.WriteLine("心跳响应超时，重新初始化共享内存");
                                ResetSharedMemory();
                                _heartbeatMissCount = 0;
                            }
                        }
                        else
                        {
                            // 成功收到心跳响应
                            _heartbeatMissCount = 0;
                            _lastHeartbeatTime = DateTime.Now;
                        }
                        
                        // 心跳间隔
                        Thread.Sleep(2000);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"心跳线程异常: {ex.Message}");
                        Thread.Sleep(5000); // 出错时等待较长时间
                    }
                }
            }
            catch (ThreadAbortException)
            {
                // 线程被终止，清理资源
            }
            catch (Exception ex)
            {
                Console.WriteLine($"心跳线程致命错误: {ex.Message}");
            }
        }
        
        // 优化：字幕收集线程，减少UI线程压力
        private static void CaptionCollectorLoop()
        {
            try
            {
                while (_isCaptionCollectorRunning)
                {
                    try
                    {
                        if (window == null || !_isCaptionCollectorRunning)
                        {
                            Thread.Sleep(500);
                            continue;
                        }
                        
                        string captionText = string.Empty;
                        bool captionReceived = false;
                        
                        // 首先尝试从共享内存获取
                        if (_useSharedMemory && _sharedMemoryInitialized)
                        {
                            captionText = GetCaptionsFromSharedMemory();
                            if (!string.IsNullOrEmpty(captionText))
                            {
                                captionReceived = true;
                            }
                        }
                        
                        // 如果共享内存失败，回退到UI自动化
                        if (!captionReceived)
                        {
                            // 使用UI自动化获取字幕
                            try
                            {
                                captionText = GetCaptionsFromUIAutomation();
                                if (!string.IsNullOrEmpty(captionText))
                                {
                                    captionReceived = true;
                                    
                                    // 将获取到的字幕写入共享内存
                                    if (_useSharedMemory && _sharedMemoryInitialized)
                                    {
                                        WriteToSharedMemory(captionText);
                                    }
                                }
                            }
                            catch (ElementNotAvailableException)
                            {
                                captionsTextBlock = null;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"UI自动化获取字幕失败: {ex.Message}");
                            }
                        }
                        
                        // 如果成功获取到字幕并且与上次不同，加入队列
                        if (captionReceived && !string.IsNullOrEmpty(captionText) && captionText != _lastCaptionText)
                        {
                            // 更新最后获取的字幕
                            _lastCaptionText = captionText;
                            
                            // 限制队列大小
                            if (_captionQueue.Count >= MAX_QUEUE_SIZE)
                            {
                                string dummy;
                                _captionQueue.TryDequeue(out dummy);
                            }
                            
                            // 加入队列
                            _captionQueue.Enqueue(captionText);
                            
                            // 触发处理事件
                            _captionProcessEvent.Set();
                            
                            // 触发字幕更新事件
                            CaptionUpdated?.Invoke(captionText);
                        }
                        
                        // 根据当前系统负载和设置动态调整采样间隔
                        int sleepTime = AdjustSampleIntervalDynamically();
                        Thread.Sleep(sleepTime);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"字幕收集线程异常: {ex.Message}");
                        Thread.Sleep(1000); // 出错时等待较长时间
                    }
                }
            }
            catch (ThreadAbortException)
            {
                // 线程被终止，清理资源
            }
            catch (Exception ex)
            {
                Console.WriteLine($"字幕收集线程致命错误: {ex.Message}");
            }
        }
        
        // 优化：动态调整采样间隔
        private static int AdjustSampleIntervalDynamically()
        {
            // 基础间隔
            int interval = currentSampleInterval;
            
            // 根据系统负载状态调整
            if (isHighLoadMode)
            {
                interval = Math.Min(baseSampleInterval * 2, 100);
            }
            
            if (isLowResourceMode)
            {
                interval = Math.Min(baseSampleInterval * 3, 150);
            }
            
            // 根据队列长度进一步调整
            if (_captionQueue.Count > 10)
            {
                interval += 50; // 队列较长时，延长间隔
            }
            else if (_captionQueue.Count < 2)
            {
                interval = Math.Max(interval - 5, baseSampleInterval); // 队列较短时，缩短间隔
            }
            
            // 根据CPU使用率动态调整
            float cpuUsage = PerformanceMonitor.CpuUsage;
            if (cpuUsage > 70)
            {
                interval += 25; // CPU高负载时，显著增加间隔
            }
            else if (cpuUsage < 30 && interval > baseSampleInterval)
            {
                interval -= 10; // CPU低负载时，减少间隔
            }
            
            // 确保间隔在合理范围内
            return Math.Max(10, Math.Min(interval, 200));
        }
        
        // 初始化共享内存通信
        public static void InitializeSharedMemory()
        {
            if (_sharedMemoryInitialized)
                return;
                
            try
            {
                lock (_sharedMemoryLock)
                {
                    if (_sharedMemoryInitialized)
                        return;
                        
                    // 创建或打开共享内存
                    _sharedMemory = MemoryMappedFile.CreateOrOpen(
                        SHARED_MEM_NAME, 
                        8192, // 8KB 共享内存空间
                        MemoryMappedFileAccess.ReadWrite);
                        
                    // 获取视图访问器
                    _sharedMemoryView = _sharedMemory.CreateViewAccessor(
                        0, 8192, MemoryMappedFileAccess.ReadWrite);
                        
                    // 创建或打开事件信号
                    bool createdNew;
                    _dataReadyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, 
                        SHARED_EVENT_DATA_READY, out createdNew);
                        
                    _dataProcessedEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
                        SHARED_EVENT_DATA_PROCESSED, out createdNew);
                        
                    // 初始化共享内存
                    _sharedMemoryView.Write(0, 0); // 状态 = 未初始化
                    _sharedMemoryView.Write(4, 0); // 数据长度 = 0
                    _sharedMemoryView.Write(8, PROTOCOL_VERSION); // 协议版本
                    _sharedMemoryView.Write(12, DateTime.UtcNow.Ticks); // 时间戳
                    
                    _sharedMemoryInitialized = true;
                    _useSharedMemory = true;
                    
                    Console.WriteLine("共享内存通信已初始化");
                    
                    // 启动心跳线程
                    _isHeartbeatRunning = true;
                    if (!_heartbeatThread.IsAlive)
                        _heartbeatThread.Start();
                    
                    // 启动字幕收集线程
                    _isCaptionCollectorRunning = true;
                    if (!_captionCollectorThread.IsAlive)
                        _captionCollectorThread.Start();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"初始化共享内存失败: {ex.Message}");
                _useSharedMemory = false;
                _sharedMemoryInitialized = false;
            }
        }
        
        // 重置共享内存
        private static void ResetSharedMemory()
        {
            try
            {
                lock (_sharedMemoryLock)
                {
                    // 清理资源
                    CleanupSharedMemory();
                    
                    // 重新初始化
                    Thread.Sleep(100); // 等待资源释放
                    InitializeSharedMemory();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"重置共享内存失败: {ex.Message}");
                _useSharedMemory = false;
                _sharedMemoryInitialized = false;
            }
        }
        
        // 清理共享内存资源
        private static void CleanupSharedMemory()
        {
            if (!_sharedMemoryInitialized)
                return;
                
            lock (_sharedMemoryLock)
            {
                if (!_sharedMemoryInitialized)
                    return;
                    
                try
                {
                    _dataReadyEvent?.Dispose();
                    _dataProcessedEvent?.Dispose();
                    _sharedMemoryView?.Dispose();
                    _sharedMemory?.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"清理共享内存时出错: {ex.Message}");
                }
                finally
                {
                    _sharedMemoryInitialized = false;
                    _useSharedMemory = false;
                }
            }
        }

        private static AutomationElement? window = null;

        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }

        // 增强的LaunchLiveCaptions方法
        public static AutomationElement LaunchLiveCaptions()
        {
            try
            {
                // 在启动新进程前尝试清理
                KillAllProcessesByPName(PROCESS_NAME);
                
                // 清理缓存和状态
                _automationElementCache.Clear();
                captionsTextBlock = null;
                windowHandle = null;
                processId = -1;
                consecutiveSlowResponses = 0;
                consecutiveFailureCount = 0;
                
                // 使用新的方式启动进程，设置低优先级以减少资源竞争
                var startInfo = new ProcessStartInfo
                {
                    FileName = PROCESS_NAME,
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
                
                var process = Process.Start(startInfo);
                if (process == null)
                    throw new Exception("启动LiveCaptions进程失败");
                
                // 调整进程优先级以减轻系统负担
                try
                {
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch
                {
                    // 忽略无法调整优先级的错误
                }
                
                processId = process.Id;
                Console.WriteLine($"LiveCaptions进程已启动，进程ID: {processId}");
                
                // 更健壮的窗口查找逻辑
                AutomationElement? window = null;
                DateTime startTime = DateTime.Now;
                TimeSpan maxWaitTime = TimeSpan.FromSeconds(15); // 增加等待时间
                
                while (window == null)
                {
                    // 有条件的等待，避免无限循环
                    if (DateTime.Now - startTime > maxWaitTime)
                    {
                        Console.WriteLine("等待LiveCaptions窗口超时，请确认Windows LiveCaptions功能是否正常");
                        throw new Exception("等待LiveCaptions窗口超时");
                    }
                    
                    try
                    {
                        // 首先尝试通过进程ID查找
                        window = FindWindowByPId(process.Id);
                        
                        if (window != null)
                        {
                            Console.WriteLine($"通过进程ID找到LiveCaptions窗口: {window.Current.ClassName}");
                        }
                        
                        // 如果找不到，尝试通过窗口名称查找
                        if (window == null)
                        {
                            Console.WriteLine("通过进程ID未找到窗口，尝试通过窗口名称查找...");
                            
                            // 尝试查找任何包含LiveCaptions相关名称的窗口
                            List<string> possibleNames = new List<string> { 
                                "Live Captions", 
                                "实时字幕",
                                "LiveCaptions",
                                "Captions"
                            };
                            
                            foreach (string name in possibleNames)
                            {
                                Condition liveCondition = new PropertyCondition(
                                    AutomationElement.NameProperty, 
                                    name, 
                                    PropertyConditionFlags.None);
                                    
                                window = AutomationElement.RootElement.FindFirst(
                                    TreeScope.Children, 
                                    liveCondition);
                                    
                                if (window != null)
                                {
                                    Console.WriteLine($"通过名称'{name}'找到LiveCaptions窗口");
                                    break;
                                }
                            }
                            
                            // 尝试查找所有顶级窗口，可能帮助诊断问题
                            if (window == null)
                            {
                                Console.WriteLine("尝试列出所有顶级窗口以帮助诊断问题:");
                                
                                var allWindows = AutomationElement.RootElement.FindAll(
                                    TreeScope.Children, 
                                    Condition.TrueCondition);
                                
                                foreach (AutomationElement win in allWindows)
                                {
                                    try
                                    {
                                        Console.WriteLine($"窗口: 名称='{win.Current.Name}', 类名='{win.Current.ClassName}'");
                                    }
                                    catch
                                    {
                                        // 忽略访问错误
                                    }
                                }
                            }
                        }
                        
                        // 如果找不到窗口，尝试通过类名查找
                        if (window == null)
                        {
                            Console.WriteLine("通过名称未找到窗口，尝试通过类名查找...");
                            
                            List<string> possibleClassNames = new List<string> { 
                                "LiveCaptionsDesktopWindow", 
                                "LiveCaptionsWindow",
                                "Windows.UI.Core.CoreWindow"
                            };
                            
                            foreach (string className in possibleClassNames)
                            {
                                Condition classCondition = new PropertyCondition(
                                    AutomationElement.ClassNameProperty, 
                                    className);
                                    
                                window = AutomationElement.RootElement.FindFirst(
                                    TreeScope.Children, 
                                    classCondition);
                                    
                                if (window != null)
                                {
                                    Console.WriteLine($"通过类名'{className}'找到LiveCaptions窗口");
                                    break;
                                }
                            }
                        }
                        
                        if (window != null)
                        {
                            // 初始化窗口句柄缓存
                            windowHandle = new nint((long)window.Current.NativeWindowHandle);
                            
                            // 注册窗口事件监听
                            RegisterWindowEventHandlers(window);
                            
                            // 记录窗口信息以便调试
                            Console.WriteLine($"找到LiveCaptions窗口: Name={window.Current.Name}, " +
                                            $"ClassName={window.Current.ClassName}");
                            
                            // 立即调试UI结构
                            Task.Run(() => DebugLiveCaptionsUIStructure());
                            
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"查找LiveCaptions窗口时出错: {ex.Message}");
                    }
                    
                    // 更友好的等待方式
                    Thread.Sleep(500);
                }
                
                // 初始化内存监控
                if (processId > 0)
                {
                    try
                    {
                        var proc = Process.GetProcessById(processId);
                        lastMemoryUsage = proc.WorkingSet64;
                        lastMemoryCheckTime = DateTime.Now;
                    }
                    catch
                    {
                        // 忽略内存监控初始化错误
                    }
                }
                
                // 增强共享内存连接
                if (_useSharedMemory && window != null)
                {
                    // 尝试将LiveCaptions连接到共享内存
                    ConnectLiveCaptionsToSharedMemory(window);
                }
                
                return window;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"启动LiveCaptions失败: {ex.Message}");
                throw;
            }
        }
        
        // 增强型：将LiveCaptions连接到共享内存
        private static void ConnectLiveCaptionsToSharedMemory(AutomationElement window)
        {
            try
            {
                // 已初始化共享内存
                if (_sharedMemoryInitialized)
                {
                    lock (_sharedMemoryLock)
                    {
                        // 将窗口句柄写入共享内存以便LiveCaptions进程识别
                        if (windowHandle.HasValue)
                        {
                            _sharedMemoryView.Write(20, windowHandle.Value.ToInt64());
                        }
                        
                        // 写入初始化状态
                        _sharedMemoryView.Write(0, 1); // 1表示初始化完成
                        _sharedMemoryView.Write(12, DateTime.UtcNow.Ticks); // 时间戳
                        
                        // 计算校验和
                        int checksum = ComputeChecksum(_memoryBuffer, 0);
                        _sharedMemoryView.Write(28, checksum);
                        
                        // 触发事件通知LiveCaptions
                        _dataReadyEvent?.Set();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"连接LiveCaptions到共享内存失败: {ex.Message}");
                _useSharedMemory = false;
            }
        }
        
        // 计算校验和
        private static int ComputeChecksum(byte[] buffer, int length)
        {
            int sum = 0;
            for (int i = 0; i < Math.Min(length, 32); i++)
            {
                sum += buffer[i];
            }
            return sum;
        }
        
        // 注册窗口事件处理程序
        private static void RegisterWindowEventHandlers(AutomationElement window)
        {
            try
            {
                // 尝试为LiveCaptions窗口注册事件监听
                AutomationEventHandler windowClosedHandler = new AutomationEventHandler(OnWindowClosed);
                
                Automation.AddAutomationEventHandler(
                    WindowPattern.WindowClosedEvent,
                    window,
                    TreeScope.Element,
                    windowClosedHandler);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"注册事件处理程序失败: {ex.Message}");
            }
        }
        
        // 窗口关闭事件处理
        private static void OnWindowClosed(object sender, AutomationEventArgs e)
        {
            try
            {
                // 清理资源
                captionsTextBlock = null;
                windowHandle = null;
                
                // 通知应用LiveCaptions已关闭
                Console.WriteLine("LiveCaptions窗口已关闭");
            }
            catch
            {
                // 忽略事件处理错误
            }
        }

        public static void KillLiveCaptions(AutomationElement window)
        {
            try
            {
                // 停止收集线程
                _isCaptionCollectorRunning = false;
                _isHeartbeatRunning = false;
                
                // 获取窗口句柄和进程ID
                nint hWnd;
                
                // 使用缓存的窗口句柄
                if (windowHandle.HasValue)
                {
                    hWnd = windowHandle.Value;
                }
                else
                {
                    hWnd = new nint((long)window.Current.NativeWindowHandle);
                    windowHandle = hWnd;
                }
                
                // 获取进程ID
                int localProcessId;
                if (processId > 0)
                {
                    localProcessId = processId;
                }
                else
                {
                    WindowsAPI.GetWindowThreadProcessId(hWnd, out localProcessId);
                    processId = localProcessId;
                }
                
                if (localProcessId <= 0)
                {
                    Console.WriteLine("无法获取LiveCaptions进程ID");
                    return;
                }
                
                // 尝试优雅关闭进程
                try
                {
                    var process = Process.GetProcessById(localProcessId);
                    process.CloseMainWindow();
                    
                    // 等待进程自行关闭
                    if (!process.WaitForExit(1000))
                    {
                        process.Kill();
                    }
                    
                    process.WaitForExit();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"关闭LiveCaptions进程失败: {ex.Message}");
                    // 尝试强制终止
                    try
                    {
                        KillAllProcessesByPName(PROCESS_NAME);
                    }
                    catch
                    {
                        // 忽略最后的失败
                    }
                }
                
                // 清理缓存和状态
                ClearResources();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"终止LiveCaptions时发生错误: {ex.Message}");
            }
        }
        
        // 清理资源方法
        private static void ClearResources()
        {
            // 重置缓存
            windowHandle = null;
            captionsTextBlock = null;
            processId = -1;
            consecutiveFailureCount = 0;
            isHighLoadMode = false;
            currentSampleInterval = baseSampleInterval;
            _automationElementCache.Clear();
            _automationCacheMisses = 0;
            
            // 清空字幕队列
            while (_captionQueue.TryDequeue(out _)) { }
            
            // 停止线程
            _isCaptionCollectorRunning = false;
            _isHeartbeatRunning = false;
        }

        public static void HideLiveCaptions(AutomationElement window)
        {
            try
            {
                nint hWnd;
                
                // 使用缓存的窗口句柄
                if (windowHandle.HasValue)
                {
                    hWnd = windowHandle.Value;
                }
                else
                {
                    hWnd = new nint((long)window.Current.NativeWindowHandle);
                    windowHandle = hWnd;
                }
                
                int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);

                WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_MINIMIZE);
                WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle | WindowsAPI.WS_EX_TOOLWINDOW);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"隐藏LiveCaptions窗口失败: {ex.Message}");
            }
        }

        public static void RestoreLiveCaptions(AutomationElement window)
        {
            try
            {
                nint hWnd;
                
                // 使用缓存的窗口句柄
                if (windowHandle.HasValue)
                {
                    hWnd = windowHandle.Value;
                }
                else
                {
                    hWnd = new nint((long)window.Current.NativeWindowHandle);
                    windowHandle = hWnd;
                }
                
                int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);

                WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle & ~WindowsAPI.WS_EX_TOOLWINDOW);
                WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_RESTORE);
                WindowsAPI.SetForegroundWindow(hWnd);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"恢复LiveCaptions窗口失败: {ex.Message}");
            }
        }

        public static void FixLiveCaptions(AutomationElement window)
        {
            try
            {
                nint hWnd;
                
                // 使用缓存的窗口句柄
                if (windowHandle.HasValue)
                {
                    hWnd = windowHandle.Value;
                }
                else
                {
                    hWnd = new nint((long)window.Current.NativeWindowHandle);
                    windowHandle = hWnd;
                }
                
                RECT rect;
                if (!WindowsAPI.GetWindowRect(hWnd, out rect))
                    throw new Exception("无法获取LiveCaptions窗口矩形");
                
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                int x = rect.Left;
                int y = rect.Top;

                bool isSuccess = true;
                if (x < 0 || y < 0 || width < 100 || height < 100)
                    isSuccess = WindowsAPI.MoveWindow(hWnd, 800, 600, 600, 200, true);
                
                if (!isSuccess)
                    throw new Exception("修复LiveCaptions窗口位置失败");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"修复LiveCaptions窗口失败: {ex.Message}");
                throw;
            }
        }
        
        // 增强型UI自动化元素查找方法
        public static AutomationElement? FindElementByMultipleProperties(
            AutomationElement window, string automationId, CancellationToken token = default)
        {
            try
            {
                Console.WriteLine($"尝试通过多种属性查找元素: {automationId}");
                
                // 方法1：通过AutomationId查找
                PropertyCondition idCondition = new PropertyCondition(
                    AutomationElement.AutomationIdProperty, automationId);
                var result = window.FindFirst(TreeScope.Descendants, idCondition);
                if (result != null)
                {
                    Console.WriteLine($"通过AutomationId '{automationId}' 找到元素");
                    return result;
                }
                
                // 方法2：通过ControlType和Name模式查找文本区域元素
                PropertyCondition textCondition = new PropertyCondition(
                    AutomationElement.ControlTypeProperty, ControlType.Text);
                // 查找所有文本控件
                var textElements = window.FindAll(TreeScope.Descendants, textCondition);
                
                Console.WriteLine($"找到 {textElements.Count} 个文本元素，尝试识别字幕元素");
                
                foreach (AutomationElement element in textElements)
                {
                    try {
                        // 记录找到的文本元素信息
                        Console.WriteLine($"文本元素: Name='{element.Current.Name}', " +
                                     $"AutomationId='{element.Current.AutomationId}', " +
                                     $"ClassName='{element.Current.ClassName}'");
                                     
                        // 寻找可能包含字幕内容的文本控件
                        // LiveCaptions的文本框通常有内容且位于窗口中部
                        if (!string.IsNullOrEmpty(element.Current.Name))
                        {
                            // 这可能是包含字幕的元素，特别是如果它有文本内容
                            Console.WriteLine($"找到可能的字幕元素: '{element.Current.Name}'");
                            return element;
                        }
                    } catch {
                        continue;
                    }
                }
                
                // 方法3：尝试通过UI结构查找
                Console.WriteLine("尝试通过UI结构查找文本元素");
                // 从窗口开始向下遍历，查找可能的容器和文本元素
                var contentElement = FindFirstChild(window);
                if (contentElement != null)
                {
                    Console.WriteLine("找到窗口第一个子元素，开始查找文本元素");
                    var candidate = FindTextElementInChildren(contentElement);
                    if (candidate != null)
                    {
                        Console.WriteLine($"通过递归查找找到可能的字幕元素: '{candidate.Current.Name}'");
                        return candidate;
                    }
                }
                
                Console.WriteLine("无法找到任何可能的字幕元素");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"查找UI元素时发生错误: {ex.Message}");
                return null;
            }
        }

        // 查找第一个子元素
        private static AutomationElement FindFirstChild(AutomationElement parent)
        {
            try {
                return TreeWalker.RawViewWalker.GetFirstChild(parent);
            } catch {
                return null;
            }
        }

        // 在子元素中递归查找文本元素
        private static AutomationElement FindTextElementInChildren(AutomationElement element)
        {
            try {
                // 如果当前元素是文本元素并且有内容，返回它
                if (element.Current.ControlType == ControlType.Text && 
                    !string.IsNullOrEmpty(element.Current.Name))
                    return element;
                    
                // 搜索所有子元素
                AutomationElement child = TreeWalker.RawViewWalker.GetFirstChild(element);
                while (child != null)
                {
                    var result = FindTextElementInChildren(child);
                    if (result != null)
                        return result;
                        
                    child = TreeWalker.RawViewWalker.GetNextSibling(child);
                }
            } catch {
                // 忽略错误，继续查找
            }
            return null;
        }
        
        // 调试LiveCaptions的UI结构
        public static void DebugLiveCaptionsUIStructure()
        {
            try
            {
                if (window == null)
                {
                    Console.WriteLine("LiveCaptions窗口未初始化");
                    return;
                }
                
                Console.WriteLine("开始分析LiveCaptions窗口结构...");
                Console.WriteLine($"窗口类名: {window.Current.ClassName}");
                Console.WriteLine($"窗口名称: {window.Current.Name}");
                
                // 记录所有后代元素的信息
                DumpElementTree(window, 0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"分析LiveCaptions UI结构时出错: {ex.Message}");
            }
        }

        private static void DumpElementTree(AutomationElement element, int depth)
        {
            try
            {
                string indent = new string(' ', depth * 2);
                Console.WriteLine($"{indent}元素: {element.Current.ControlType.ProgrammaticName}");
                Console.WriteLine($"{indent}  Name: {element.Current.Name}");
                Console.WriteLine($"{indent}  AutomationId: {element.Current.AutomationId}");
                Console.WriteLine($"{indent}  ClassName: {element.Current.ClassName}");
                
                // 递归处理所有子元素
                AutomationElement child = TreeWalker.RawViewWalker.GetFirstChild(element);
                while (child != null)
                {
                    DumpElementTree(child, depth + 1);
                    child = TreeWalker.RawViewWalker.GetNextSibling(child);
                }
            }
            catch
            {
                // 忽略元素访问错误
            }
        }
        
        // 优化：从共享内存中获取字幕
        private static string GetCaptionsFromSharedMemory()
        {
            if (!_sharedMemoryInitialized)
                return string.Empty;
                
            try
            {
                lock (_sharedMemoryLock)
                {
                    // 读取状态标志
                    int status = _sharedMemoryView.ReadInt32(0);
                    if (status != 2) // 2表示有新的字幕数据
                        return string.Empty;
                        
                    // 读取字幕长度
                    int length = _sharedMemoryView.ReadInt32(4);
                    if (length <= 0 || length > 8000) // 合理的最大长度
                        return string.Empty;
                    
                    // 验证协议版本
                    int protocol = _sharedMemoryView.ReadInt32(8);
                    if (protocol != PROTOCOL_VERSION)
                        return string.Empty;
                        
                    // 验证时间戳，确保数据不是过时的
                    long timestamp = _sharedMemoryView.ReadInt64(12);
                    DateTime dataTime = new DateTime(timestamp, DateTimeKind.Utc);
                    if (DateTime.UtcNow - dataTime > _maxCaptionAge)
                        return string.Empty;
                        
                    // 读取校验和
                    int expectedChecksum = _sharedMemoryView.ReadInt32(28);
                        
                    // 读取字幕内容
                    _sharedMemoryView.ReadArray(32, _memoryBuffer, 0, length);
                    
                    // 验证校验和
                    int actualChecksum = ComputeChecksum(_memoryBuffer, length);
                    if (actualChecksum != expectedChecksum)
                        return string.Empty;
                        
                    // 计算内容哈希值并比较是否有变化
                    long hash = ComputeHash(_memoryBuffer, length);
                    if (hash == _lastCaptionHash)
                        return string.Empty; // 相同内容，没有变化
                        
                    _lastCaptionHash = hash;
                    
                    // 通知LiveCaptions数据已处理
                    _sharedMemoryView.Write(0, 0); // 重置状态
                    _dataProcessedEvent?.Set();
                    
                    // 转换为字符串
                    return Encoding.UTF8.GetString(_memoryBuffer, 0, length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"从共享内存读取字幕失败: {ex.Message}");
                _useSharedMemory = false;
                return string.Empty;
            }
        }
        
        // 增强型：使用UI自动化获取字幕
        private static string GetCaptionsFromUIAutomation()
        {
            try
            {
                // 检测系统负载状态，调整采样间隔
                AdjustSampleIntervalBasedOnSystemLoad();
                
                // 周期性检查LiveCaptions内存使用
                MonitorLiveCaptionsMemory();
                
                performanceMonitor.Restart();
                
                // 优化：尝试用多种方式查找字幕文本元素
                if (captionsTextBlock == null)
                {
                    // 首先尝试缓存查找
                    captionsTextBlock = FindCachedElementByAId(window, "CaptionsTextBlock");
                    
                    // 如果找不到，使用增强的查找方法
                    if (captionsTextBlock == null)
                    {
                        Console.WriteLine("缓存中找不到字幕元素，尝试多属性查找方法");
                        captionsTextBlock = FindElementByMultipleProperties(window, "CaptionsTextBlock");
                        
                        // 如果仍然找不到，记录UI结构以便调试
                        if (captionsTextBlock == null)
                        {
                            Console.WriteLine("无法找到字幕文本元素，正在记录UI结构以便调试...");
                            DebugLiveCaptionsUIStructure();
                            return string.Empty;
                        }
                        else
                        {
                            Console.WriteLine($"找到字幕元素: Name='{captionsTextBlock.Current.Name}', " +
                                         $"AutomationId='{captionsTextBlock.Current.AutomationId}'");
                        }
                    }
                }
                
                try
                {
                    string captionText = captionsTextBlock?.Current.Name ?? string.Empty;
                    
                    // 重置失败计数
                    consecutiveFailureCount = 0;
                    
                    // 监控获取字幕的性能
                    performanceMonitor.Stop();
                    if (performanceMonitor.ElapsedMilliseconds > 50) // 阈值50毫秒
                    {
                        consecutiveSlowResponses++;
                        if (consecutiveSlowResponses > maxConsecutiveSlowResponses && !isLowResourceMode)
                        {
                            // 连续多次响应缓慢，切换到低资源模式
                            isLowResourceMode = true;
                            isHighLoadMode = true;
                            currentSampleInterval = Math.Min(currentSampleInterval * 2, 100); // 增加采样间隔
                            Console.WriteLine("检测到LiveCaptions响应缓慢，切换到低资源模式");
                            
                            // 尝试通知应用主线程
                            NotifyPerformanceStateChanged(PerformanceState.LowResource);
                            
                            // 尝试减轻系统负担，调整LiveCaptions进程优先级
                            try
                            {
                                if (processId > 0)
                                {
                                    var process = Process.GetProcessById(processId);
                                    if (process.PriorityClass != ProcessPriorityClass.BelowNormal)
                                    {
                                        process.PriorityClass = ProcessPriorityClass.BelowNormal;
                                    }
                                }
                            }
                            catch
                            {
                                // 忽略优先级调整失败
                            }
                        }
                    }
                    else
                    {
                        // 恢复正常计数
                        if (consecutiveSlowResponses > 0)
                            consecutiveSlowResponses--;
                            
                        // 如果系统性能恢复，退出低资源模式
                        if (consecutiveSlowResponses == 0 && isLowResourceMode && 
                            (DateTime.Now - _lastLowLoadReassessment) > _lowLoadReassessmentInterval)
                        {
                            isLowResourceMode = false;
                            isHighLoadMode = false;
                            currentSampleInterval = baseSampleInterval;
                            _lastLowLoadReassessment = DateTime.Now;
                            
                            Console.WriteLine("LiveCaptions响应恢复正常，退出低资源模式");
                            
                            // 尝试通知应用主线程
                            NotifyPerformanceStateChanged(PerformanceState.Normal);
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(captionText))
                    {
                        Console.WriteLine($"成功获取字幕: '{captionText.Substring(0, Math.Min(30, captionText.Length))}'...");
                    }
                    
                    return captionText;
                }
                catch (ElementNotAvailableException)
                {
                    // 清除缓存的元素
                    captionsTextBlock = null;
                    
                    // 增加失败计数并应用渐进式回退策略
                    consecutiveFailureCount++;
                    
                    if (consecutiveFailureCount <= maxRetryAttempts)
                    {
                        // 等待时间随失败次数增加而延长
                        Thread.Sleep((int)recoveryDelay.TotalMilliseconds * consecutiveFailureCount);
                        
                        // 尝试恢复LiveCaptions窗口
                        TryRecoverLiveCaptions(window);
                    }
                    
                    // 检查是否需要尝试自动恢复
                    if (consecutiveFailureCount > maxRetryAttempts)
                    {
                        TryAutoRecovery(window);
                    }
                    
                    throw;
                }
            }
            catch (ElementNotAvailableException)
            {
                captionsTextBlock = null;
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取字幕失败: {ex.Message}");
                captionsTextBlock = null;
                return string.Empty;
            }
        }

        // 将字幕写入共享内存
        private static void WriteToSharedMemory(string text)
        {
            if (!_sharedMemoryInitialized || string.IsNullOrEmpty(text))
                return;
                
            try
            {
                lock (_sharedMemoryLock)
                {
                    // 计算哈希值，检查是否有变化
                    byte[] textBytes = Encoding.UTF8.GetBytes(text);
                    long hash = ComputeHash(textBytes, textBytes.Length);
                    
                    if (hash == _lastCaptionHash)
                        return; // 相同内容，跳过写入
                        
                    _lastCaptionHash = hash;
                    
                    // 写入协议版本
                    _sharedMemoryView.Write(8, PROTOCOL_VERSION);
                    
                    // 写入时间戳
                    _sharedMemoryView.Write(12, DateTime.UtcNow.Ticks);
                    
                    // 写入字幕长度
                    _sharedMemoryView.Write(4, textBytes.Length);
                    
                    // 写入字幕内容
                    _sharedMemoryView.WriteArray(32, textBytes, 0, textBytes.Length);
                    
                    // 计算校验和
                    int checksum = ComputeChecksum(textBytes, textBytes.Length);
                    _sharedMemoryView.Write(28, checksum);
                    
                    // 更新状态标志为2，表示有新的字幕数据
                    _sharedMemoryView.Write(0, 2);
                    
                    // 通知等待的进程
                    _dataReadyEvent?.Set();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"写入字幕到共享内存失败: {ex.Message}");
                _useSharedMemory = false;
            }
        }

        // 计算字节数组的哈希值
        private static long ComputeHash(byte[] data, int length)
        {
            const ulong FNV_prime = 1099511628211;
            const ulong FNV_offset_basis = 14695981039346656037;
            
            ulong hash = FNV_offset_basis;
            
            for (int i = 0; i < length; i++)
            {
                hash ^= data[i];
                hash *= FNV_prime;
            }
            
            return (long)hash;
        }
        
        // 返回当前字幕文本，优先从队列获取
        public static string GetCaptions(AutomationElement window)
        {
            try
            {
                // 首先尝试从队列获取最新字幕
                if (_captionQueue.TryDequeue(out string latestCaption))
                {
                    Console.WriteLine($"从队列获取字幕: '{latestCaption.Substring(0, Math.Min(30, latestCaption.Length))}'...");
                    return latestCaption;
                }
                
                // 如果队列为空，返回最后获取的字幕
                return _lastCaptionText;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取字幕文本失败: {ex.Message}");
                return string.Empty;
            }
        }
        
        // 基于系统负载调整采样间隔
        private static void AdjustSampleIntervalBasedOnSystemLoad()
        {
            try
            {
                var systemState = PerformanceMonitor.CurrentSystemState;
                
                switch (systemState)
                {
                    case PerformanceMonitor.SystemLoadState.High:
                        if (!isHighLoadMode)
                        {
                            isHighLoadMode = true;
                            currentSampleInterval = Math.Min(baseSampleInterval * 2, 100);
                            Console.WriteLine("检测到系统负载较高，降低字幕采样频率");
                        }
                        break;
                        
                    case PerformanceMonitor.SystemLoadState.Critical:
                        isHighLoadMode = true;
                        currentSampleInterval = Math.Min(baseSampleInterval * 4, 200);
                        Console.WriteLine("检测到系统负载临界，显著降低字幕采样频率");
                        break;
                        
                    case PerformanceMonitor.SystemLoadState.Normal:
                        if (isHighLoadMode && !isLowResourceMode)
                        {
                            isHighLoadMode = false;
                            currentSampleInterval = baseSampleInterval;
                            Console.WriteLine("系统负载恢复正常，恢复默认字幕采样频率");
                        }
                        break;
                }
            }
            catch
            {
                // 忽略负载检测错误
            }
        }
        
        // 监控LiveCaptions进程内存使用
        private static void MonitorLiveCaptionsMemory()
        {
            try
            {
                if (processId <= 0 || DateTime.Now - lastMemoryCheckTime < memoryCheckInterval)
                    return;
                    
                var process = Process.GetProcessById(processId);
                if (process == null)
                    return;
                    
                long currentMemory = process.WorkingSet64;
                lastMemoryCheckTime = DateTime.Now;
                
                // 检测内存增长率
                long memoryGrowth = currentMemory - lastMemoryUsage;
                lastMemoryUsage = currentMemory;
                
                Console.WriteLine($"LiveCaptions内存使用: {currentMemory / (1024 * 1024)}MB, " +
                             $"增长: {memoryGrowth / (1024 * 1024)}MB");
                
                // 如果内存使用超过200MB或快速增长，尝试回收内存
                if (currentMemory > 200 * 1024 * 1024 || memoryGrowth > 50 * 1024 * 1024)
                {
                    // 可能的内存泄漏，考虑重启LiveCaptions
                    if (currentMemory > 500 * 1024 * 1024)
                    {
                        // 通知主应用存在潜在的内存问题
                        NotifyLiveCaptionsMemoryIssue(currentMemory / (1024 * 1024));
                        
                        // 内存使用过高，考虑重启LiveCaptions
                        if (DateTime.Now - lastRecoveryTime > TimeSpan.FromMinutes(10))
                        {
                            // 仅当距离上次恢复足够长时间后才尝试重启
                            Console.WriteLine($"LiveCaptions内存使用过高 ({currentMemory / (1024 * 1024)}MB)，准备重启");
                            lastRecoveryTime = DateTime.Now;
                            
                            // 在单独线程中请求重启
                            Task.Run(() => RequestLiveCaptionsRestart());
                        }
                    }
                }
            }
            catch
            {
                // 忽略内存监控错误
            }
        }
        
        // 使用缓存查找自动化元素
        private static AutomationElement? FindCachedElementByAId(AutomationElement window, string automationId)
        {
            string cacheKey = $"{window.GetHashCode()}_{automationId}";
            
            // 尝试从缓存获取
            if (_automationElementCache.TryGetValue(cacheKey, out AutomationElement? cachedElement))
            {
                try
                {
                    // 验证缓存的元素是否仍然有效
                    var name = cachedElement.Current.Name;
                    return cachedElement;
                }
                catch
                {
                    // 元素不再有效，从缓存中移除
                    _automationElementCache.TryRemove(cacheKey, out _);
                }
            }
            
            // 缓存未命中，使用传统方法查找
            _automationCacheMisses++;
            
            // 如果缓存未命中次数过多，清理缓存
            if (_automationCacheMisses > 50 || 
                (DateTime.Now - _lastCacheCleanup) > _cacheCleanupInterval)
            {
                _automationElementCache.Clear();
                _automationCacheMisses = 0;
                _lastCacheCleanup = DateTime.Now;
            }
            
            // 查找元素
            var element = FindElementByAId(window, automationId);
            
            // 将找到的元素添加到缓存
            if (element != null)
            {
                _automationElementCache[cacheKey] = element;
            }
            
            return element;
        }
        
        // 尝试恢复LiveCaptions
        private static void TryRecoverLiveCaptions(AutomationElement window)
        {
            try
            {
                Console.WriteLine($"尝试恢复LiveCaptions (尝试 #{consecutiveFailureCount})");
                
                // 尝试显示再隐藏窗口
                RestoreLiveCaptions(window);
                Thread.Sleep(300);
                HideLiveCaptions(window);
                
                // 重新查找字幕元素
                captionsTextBlock = FindElementByAId(window, "CaptionsTextBlock");
                
                // 如果无法通过传统方法找到，尝试多属性查找
                if (captionsTextBlock == null)
                {
                    captionsTextBlock = FindElementByMultipleProperties(window, "CaptionsTextBlock");
                }
            }
            catch
            {
                // 忽略恢复过程中的错误
            }
        }
        
        // 尝试自动恢复LiveCaptions
        private static void TryAutoRecovery(AutomationElement window)
        {
            // 防止过于频繁的恢复尝试
            if (DateTime.Now - lastRecoveryTime < recoveryThrottleTime)
                return;
                
            // 限制恢复尝试次数
            if (autoRecoveryAttempts >= 3)
                return;
                
            try
            {
                Console.WriteLine("尝试自动恢复LiveCaptions...");
                autoRecoveryAttempts++;
                lastRecoveryTime = DateTime.Now;
                
                // 尝试恢复操作
                RestoreLiveCaptions(window);
                Thread.Sleep(500);
                HideLiveCaptions(window);
                
                // 重新查找字幕元素
                captionsTextBlock = FindElementByAId(window, "CaptionsTextBlock");
                
                // 如果无法通过传统方法找到，尝试多属性查找
                if (captionsTextBlock == null)
                {
                    captionsTextBlock = FindElementByMultipleProperties(window, "CaptionsTextBlock");
                }
                
                // 通知应用LiveCaptions已尝试恢复
                NotifyLiveCaptionsRecoveryAttempt(autoRecoveryAttempts);
            }
            catch
            {
                // 忽略恢复过程中的错误
            }
        }
        
        // 通知性能状态改变
        private static void NotifyPerformanceStateChanged(PerformanceState state)
        {
            PerformanceStateChanged?.Invoke(state);
        }
        
        // 通知LiveCaptions内存问题
        private static void NotifyLiveCaptionsMemoryIssue(long memoryMB)
        {
            LiveCaptionsMemoryIssue?.Invoke(memoryMB);
        }
        
        // 通知LiveCaptions恢复尝试
        private static void NotifyLiveCaptionsRecoveryAttempt(int attemptCount)
        {
            LiveCaptionsRecoveryAttempt?.Invoke(attemptCount);
        }
        
        // 请求重启LiveCaptions
        private static void RequestLiveCaptionsRestart()
        {
            LiveCaptionsRestartRequested?.Invoke();
        }

        private static AutomationElement? FindWindowByPId(int processId)
        {
            var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
            return AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
        }

        public static AutomationElement? FindElementByAId(
            AutomationElement window, string automationId, CancellationToken token = default)
        {
            try
            {
                PropertyCondition condition = new PropertyCondition(
                    AutomationElement.AutomationIdProperty, automationId);
                    
                // 优化：使用更高效的查找方式
                var finder = new TreeWalker(new AndCondition(
                    new PropertyCondition(AutomationElement.AutomationIdProperty, automationId),
                    Condition.TrueCondition
                ));
                
                var result = window.FindFirst(TreeScope.Descendants, condition);
                
                // 如果找不到元素，尝试更直接的方式
                if (result == null)
                {
                    var element = finder.GetFirstChild(window);
                    if (element != null)
                        return element;
                }
                
                return result;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (NullReferenceException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"查找UI元素时发生错误: {ex.Message}");
                return null;
            }
        }

        public static void PrintAllElementsAId(AutomationElement window)
        {
            var treeWalker = TreeWalker.RawViewWalker;
            var stack = new Stack<AutomationElement>();
            stack.Push(window);

            while (stack.Count > 0)
            {
                var element = stack.Pop();
                if (!string.IsNullOrEmpty(element.Current.AutomationId))
                    Console.WriteLine(element.Current.AutomationId);

                var child = treeWalker.GetFirstChild(element);
                while (child != null)
                {
                    stack.Push(child);
                    child = treeWalker.GetNextSibling(child);
                }
            }
        }

        public static bool ClickSettingsButton(AutomationElement window)
        {
            try
            {
                var settingsButton = FindElementByAId(window, "SettingsButton");
                if (settingsButton != null)
                {
                    var invokePattern = settingsButton.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                    if (invokePattern != null)
                    {
                        invokePattern.Invoke();
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"点击设置按钮失败: {ex.Message}");
                return false;
            }
        }

        private static void KillAllProcessesByPName(string processName)
        {
            try
            {
                var processes = Process.GetProcessesByName(processName);
                if (processes.Length == 0)
                    return;
                    
                Console.WriteLine($"发现 {processes.Length} 个 {processName} 进程，尝试关闭...");
                    
                foreach (Process process in processes)
                {
                    try
                    {
                        // 尝试优雅关闭
                        if (!process.HasExited)
                        {
                            process.CloseMainWindow();
                            
                            // 给予进程一些时间自行关闭
                            if (!process.WaitForExit(1000))
                            {
                                // 如果优雅关闭失败，强制终止
                                process.Kill();
                            }
                            
                            process.WaitForExit();
                            Console.WriteLine($"已关闭进程 ID: {process.Id}");
                        }
                    }
                    catch
                    {
                        // 如果优雅关闭失败，尝试强制终止
                        try
                        {
                            if (!process.HasExited)
                                process.Kill();
                        }
                        catch
                        {
                            // 忽略终止失败
                        }
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"终止所有{processName}进程失败: {ex.Message}");
            }
        }
        
        // 重置性能计数器
        public static void ResetPerformanceCounters()
        {
            consecutiveSlowResponses = 0;
            consecutiveFailureCount = 0;
            autoRecoveryAttempts = 0;
            isLowResourceMode = false;
            isHighLoadMode = false;
            currentSampleInterval = baseSampleInterval;
        }
        
        // 主动请求优化
        public static void RequestOptimization(bool aggressive = false)
        {
            if (aggressive)
            {
                // 强制进入低资源模式
                isLowResourceMode = true;
                isHighLoadMode = true;
                currentSampleInterval = Math.Min(baseSampleInterval * 4, 200);
                
                // 尝试减少LiveCaptions的资源占用
                if (processId > 0)
                {
                    try
                    {
                        var process = Process.GetProcessById(processId);
                        process.PriorityClass = ProcessPriorityClass.BelowNormal;
                    }
                    catch
                    {
                        // 忽略优先级调整失败
                    }
                }
                
                // 通知已进入低资源模式
                NotifyPerformanceStateChanged(PerformanceState.Critical);
            }
            else
            {
                // 轻度优化
                isHighLoadMode = true;
                currentSampleInterval = Math.Min(baseSampleInterval * 2, 100);
                
                // 通知已进入优化模式
                NotifyPerformanceStateChanged(PerformanceState.LowResource);
            }
            
            Console.WriteLine($"已应用性能优化 (级别: {(aggressive ? "激进" : "轻度")})");
        }
        
        // 在应用退出时释放资源
        public static void Cleanup()
        {
            try
            {
                // 停止字幕收集线程和心跳线程
                _isCaptionCollectorRunning = false;
                _isHeartbeatRunning = false;
                
                // 给线程一些时间自行退出
                Thread.Sleep(100);
                
                // 尝试中断线程
                try
                {
                    if (_captionCollectorThread.IsAlive)
                        _captionCollectorThread.Interrupt();
                        
                    if (_heartbeatThread.IsAlive)
                        _heartbeatThread.Interrupt();
                }
                catch
                {
                    // 忽略中断错误
                }
                
                // 取消事件订阅
                PerformanceStateChanged = null;
                LiveCaptionsMemoryIssue = null;
                LiveCaptionsRecoveryAttempt = null;
                LiveCaptionsRestartRequested = null;
                CaptionUpdated = null;
                
                // 清理共享内存
                CleanupSharedMemory();
                
                // 清理自动化元素缓存
                _automationElementCache.Clear();
                
                // 清空字幕队列
                while (_captionQueue.TryDequeue(out _)) { }
                
                // 重置各种标志
                _useSharedMemory = false;
                isLowResourceMode = false;
                isHighLoadMode = false;
                captionsTextBlock = null;
                windowHandle = null;
                processId = -1;
                
                // 触发垃圾回收
                GC.Collect(1, GCCollectionMode.Optimized);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"清理LiveCaptionsHandler资源时出错: {ex.Message}");
            }
        }
    }
}