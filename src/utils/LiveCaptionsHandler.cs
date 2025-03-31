using System.Diagnostics;
using System.Windows.Automation;

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
        private static Stopwatch performanceMonitor = new Stopwatch();
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

        public static AutomationElement LaunchLiveCaptions()
        {
            try
            {
                // 在启动新进程前尝试清理
                KillAllProcessesByPName(PROCESS_NAME);
                
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
                
                // 更健壮的窗口查找逻辑
                AutomationElement? window = null;
                DateTime startTime = DateTime.Now;
                TimeSpan maxWaitTime = TimeSpan.FromSeconds(10);
                
                while (window == null || window.Current.ClassName.CompareTo("LiveCaptionsDesktopWindow") != 0)
                {
                    // 有条件的等待，避免无限循环
                    if (DateTime.Now - startTime > maxWaitTime)
                        throw new Exception("等待LiveCaptions窗口超时");
                    
                    try
                    {
                        window = FindWindowByPId(process.Id);
                        if (window != null)
                        {
                            // 初始化窗口句柄缓存
                            windowHandle = new nint((long)window.Current.NativeWindowHandle);
                        }
                    }
                    catch
                    {
                        // 忽略查找过程中的异常，继续尝试
                    }
                    
                    // 更友好的等待方式
                    Thread.Sleep(100);
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
                
                return window;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"启动LiveCaptions失败: {ex.Message}");
                throw;
            }
        }

        public static void KillLiveCaptions(AutomationElement window)
        {
            try
            {
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
                
                // 重置缓存
                windowHandle = null;
                captionsTextBlock = null;
                processId = -1;
                consecutiveFailureCount = 0;
                isHighLoadMode = false;
                currentSampleInterval = baseSampleInterval;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"终止LiveCaptions时发生错误: {ex.Message}");
            }
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

        public static string GetCaptions(AutomationElement window)
        {
            try
            {
                // 检查系统负载状态，调整采样间隔
                AdjustSampleIntervalBasedOnSystemLoad();
                
                // 周期性检查LiveCaptions内存使用
                MonitorLiveCaptionsMemory();
                
                performanceMonitor.Restart();
                
                // 优化：缓存字幕文本元素以减少UI自动化操作的开销
                if (captionsTextBlock == null)
                {
                    captionsTextBlock = FindElementByAId(window, "CaptionsTextBlock");
                    if (captionsTextBlock == null)
                    {
                        return string.Empty;
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
                        if (consecutiveSlowResponses == 0 && isLowResourceMode)
                        {
                            isLowResourceMode = false;
                            isHighLoadMode = false;
                            currentSampleInterval = baseSampleInterval;
                            Console.WriteLine("LiveCaptions响应恢复正常，退出低资源模式");
                            
                            // 尝试通知应用主线程
                            NotifyPerformanceStateChanged(PerformanceState.Normal);
                        }
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
                        if (isHighLoadMode)
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
                
                // 通知应用LiveCaptions已尝试恢复
                NotifyLiveCaptionsRecoveryAttempt(autoRecoveryAttempts);
            }
            catch
            {
                // 忽略恢复过程中的错误
            }
        }
        
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

        private static AutomationElement FindWindowByPId(int processId)
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
    }
}