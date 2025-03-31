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
                    
                    // 监控获取字幕的性能
                    performanceMonitor.Stop();
                    if (performanceMonitor.ElapsedMilliseconds > 50) // 阈值50毫秒
                    {
                        consecutiveSlowResponses++;
                        if (consecutiveSlowResponses > maxConsecutiveSlowResponses && !isLowResourceMode)
                        {
                            // 连续多次响应缓慢，切换到低资源模式
                            isLowResourceMode = true;
                            Console.WriteLine("检测到LiveCaptions响应缓慢，切换到低资源模式");
                            
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
                            Console.WriteLine("LiveCaptions响应恢复正常，退出低资源模式");
                        }
                    }
                    
                    return captionText;
                }
                catch (ElementNotAvailableException)
                {
                    // 清除缓存的元素并重新抛出异常
                    captionsTextBlock = null;
                    
                    // 检查是否需要尝试自动恢复
                    TryAutoRecovery(window);
                    
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
            }
            catch
            {
                // 忽略恢复过程中的错误
            }
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
    }
}