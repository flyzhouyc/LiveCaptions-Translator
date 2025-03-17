using System.Diagnostics;
using System.Windows.Automation;
using System.Collections.Concurrent;

namespace LiveCaptionsTranslator.utils
{
    public static class LiveCaptionsHandler
    {
        public static readonly string PROCESS_NAME = "LiveCaptions";

        private static AutomationElement? captionsTextBlock = null;
        private static DateTime lastCaptionsCheck = DateTime.MinValue;
        private static int captionsCheckInterval = 30; // 毫秒，优化初始值，比原来的50ms更快
        private static string lastCaptionContent = string.Empty;
        private static int consecutiveEmptyResults = 0;
        private static int consecutiveSameResults = 0;
        
        // 优化1: 增加字幕捕获状态跟踪和错误恢复
        private static bool isCapturing = false;
        private static int captureFailCount = 0;
        private static readonly int MAX_FAILURES_BEFORE_RESET = 5;
        
        // 优化2: 增加字幕历史缓存，避免文本抖动
        private static readonly ConcurrentQueue<string> captionHistory = new ConcurrentQueue<string>();
        private static readonly int MAX_HISTORY_SIZE = 5;
        
        // 优化3: 增加性能分析器，自动调整捕获间隔
        private static readonly Stopwatch performanceWatch = new Stopwatch();
        private static int totalCaptures = 0;
        private static int successfulCaptures = 0;

        public static AutomationElement LaunchLiveCaptions()
        {
            // 启动性能分析
            performanceWatch.Start();
            
            // 关闭所有可能存在的LiveCaptions进程
            KillAllProcessesByPName(PROCESS_NAME);
            var process = Process.Start(PROCESS_NAME);

            // 搜索窗口
            AutomationElement? window = null;
            int attemptCount = 0;
            while (window == null || window.Current.ClassName.CompareTo("LiveCaptionsDesktopWindow") != 0)
            {
                window = FindWindowByPId(process.Id);
                attemptCount++;
                
                if (attemptCount % 500 == 0)
                {
                    // 尝试重启进程
                    if (attemptCount >= 1000)
                    {
                        process.Kill();
                        process.WaitForExit();
                        process = Process.Start(PROCESS_NAME);
                        attemptCount = 0;
                    }
                }
                
                if (attemptCount > 10000)
                    throw new Exception("Failed to launch LiveCaptions after multiple attempts!");
                
                Thread.Sleep(5); // 减少 CPU 占用
            }
            
            // 重置捕获状态
            isCapturing = true;
            captureFailCount = 0;
            
            // 清空历史缓存
            while (captionHistory.TryDequeue(out _)) { }

            return window;
        }

        public static void KillLiveCaptions(AutomationElement window)
        {
            try
            {
                // 停止捕获
                isCapturing = false;
                
                // 搜索进程
                nint hWnd = new nint((long)window.Current.NativeWindowHandle);
                WindowsAPI.GetWindowThreadProcessId(hWnd, out int processId);
                var process = Process.GetProcessById(processId);

                // 杀死进程
                process.Kill();
                process.WaitForExit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to kill LiveCaptions: {ex.Message}");
                // 尝试使用进程名称杀死所有相关进程
                KillAllProcessesByPName(PROCESS_NAME);
            }
        }

        public static void HideLiveCaptions(AutomationElement window)
        {
            try
            {
                nint hWnd = new nint((long)window.Current.NativeWindowHandle);
                int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);

                WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_MINIMIZE);
                WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle | WindowsAPI.WS_EX_TOOLWINDOW);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to hide LiveCaptions: {ex.Message}");
            }
        }

        public static void RestoreLiveCaptions(AutomationElement window)
        {
            try
            {
                nint hWnd = new nint((long)window.Current.NativeWindowHandle);
                int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);

                WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle & ~WindowsAPI.WS_EX_TOOLWINDOW);
                WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_RESTORE);
                WindowsAPI.SetForegroundWindow(hWnd);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to restore LiveCaptions: {ex.Message}");
            }
        }

        public static void FixLiveCaptions(AutomationElement window)
        {
            try
            {
                nint hWnd = new nint((long)window.Current.NativeWindowHandle);

                RECT rect;
                if (!WindowsAPI.GetWindowRect(hWnd, out rect))
                    throw new Exception("Unable to get the window rectangle of LiveCaptions!");
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                int x = rect.Left;
                int y = rect.Top;

                bool isSuccess = true;
                if (x < 0 || y < 0 || width < 100 || height < 100)
                    isSuccess = WindowsAPI.MoveWindow(hWnd, 800, 600, 600, 200, true);
                if (!isSuccess)
                    throw new Exception("Failed to fix LiveCaptions!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to fix LiveCaptions: {ex.Message}");
                // 非关键操作，即使失败也继续执行
            }
        }

        public static string GetCaptions(AutomationElement window)
        {
            // 如果捕获被禁用，直接返回上次内容
            if (!isCapturing)
                return lastCaptionContent;
                
            // 自适应检查频率
            TimeSpan timeSinceLastCheck = DateTime.Now - lastCaptionsCheck;
            if (timeSinceLastCheck.TotalMilliseconds < captionsCheckInterval)
            {
                return lastCaptionContent; // 返回缓存的内容
            }
            
            totalCaptures++; // 增加总捕获尝试计数

            try
            {
                // 检查 LiveCaptions.exe 是否存活
                var info = window.Current;
                var name = info.Name;

                if (captionsTextBlock == null)
                {
                    // 优化：使用更高效的查找算法
                    captionsTextBlock = FastFindCaptionsElement(window);
                    
                    if (captionsTextBlock == null)
                    {
                        consecutiveEmptyResults++;
                        RecordCaptureFailure();
                        return lastCaptionContent;
                    }
                }

                string newContent = captionsTextBlock.Current.Name;
                lastCaptionsCheck = DateTime.Now;
                
                successfulCaptures++; // 增加成功捕获计数

                if (string.IsNullOrEmpty(newContent))
                {
                    consecutiveEmptyResults++;
                    // 如果连续多次获取到空内容，增加检查间隔
                    if (consecutiveEmptyResults > 5)
                    {
                        captionsCheckInterval = Math.Min(captionsCheckInterval + 10, 200);
                        consecutiveEmptyResults = 0;
                    }
                }
                else
                {
                    consecutiveEmptyResults = 0;
                    RecordCaptureSuccess();
                }

                if (newContent == lastCaptionContent)
                {
                    consecutiveSameResults++;
                    // 如果内容长时间没变化，逐渐增加检查间隔
                    if (consecutiveSameResults > 10)
                    {
                        captionsCheckInterval = Math.Min(captionsCheckInterval + 5, 200);
                    }
                }
                else
                {
                    // 内容变化，减少检查间隔，提高响应速度
                    captionsCheckInterval = Math.Max(captionsCheckInterval - 10, 20);
                    consecutiveSameResults = 0;
                    
                    // 添加到历史缓存
                    AddToHistory(newContent);
                    
                    // 应用去抖动处理
                    lastCaptionContent = ApplyStabilization(newContent);
                }

                return lastCaptionContent;
            }
            catch (ElementNotAvailableException)
            {
                captionsTextBlock = null;
                RecordCaptureFailure();
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to get captions: {ex.Message}");
                captionsTextBlock = null;
                RecordCaptureFailure();
                return lastCaptionContent;
            }
        }
        
        /// <summary>
        /// 优化的字幕元素快速查找方法
        /// </summary>
        private static AutomationElement? FastFindCaptionsElement(AutomationElement window)
        {
            try
            {
                // 首先尝试直接按ID查找
                var element = FindElementByAId(window, "CaptionsTextBlock");
                if (element != null)
                    return element;
                    
                // 备选策略：按类型模式查找
                var condition = new PropertyCondition(
                    AutomationElement.ControlTypeProperty, ControlType.Text);
                    
                // 首先限定在LiveCaptions窗口中查找
                var elements = window.FindAll(TreeScope.Descendants, condition);
                
                foreach (AutomationElement el in elements)
                {
                    if (!string.IsNullOrEmpty(el.Current.Name))
                    {
                        return el; // 找到第一个有文本的元素
                    }
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Fast find captions failed: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// 记录捕获成功，用于自适应调整
        /// </summary>
        private static void RecordCaptureSuccess()
        {
            captureFailCount = 0;
            
            // 每100次成功后分析性能并调整策略
            if (successfulCaptures % 100 == 0)
            {
                AnalyzePerformance();
            }
        }
        
        /// <summary>
        /// 记录捕获失败，用于错误恢复
        /// </summary>
        private static void RecordCaptureFailure()
        {
            captureFailCount++;
            
            // 连续失败超过阈值，重置元素引用
            if (captureFailCount >= MAX_FAILURES_BEFORE_RESET)
            {
                captionsTextBlock = null;
                captureFailCount = 0;
                
                // 暂时增加检查间隔，减少系统负担
                captionsCheckInterval = Math.Min(captionsCheckInterval + 20, 200);
            }
        }
        
        /// <summary>
        /// 将字幕添加到历史缓存
        /// </summary>
        private static void AddToHistory(string caption)
        {
            captionHistory.Enqueue(caption);
            
            // 限制队列大小
            while (captionHistory.Count > MAX_HISTORY_SIZE)
            {
                captionHistory.TryDequeue(out _);
            }
        }
        
        /// <summary>
        /// 应用字幕稳定化处理，去除文本抖动
        /// </summary>
        private static string ApplyStabilization(string newContent)
        {
            // 如果历史为空，直接返回新内容
            if (captionHistory.Count < 2)
                return newContent;
                
            // 从队列中获取最近的几个字幕
            var recentCaptions = captionHistory.ToArray();
            
            // 检查是否存在文本抖动（快速变化后又恢复）
            if (recentCaptions.Length >= 3)
            {
                // 检查最新内容是否与倒数第三个内容相同，但与倒数第二个不同
                // 这表示可能是暂时的错误识别后恢复
                string thirdLast = recentCaptions[recentCaptions.Length - 3];
                string secondLast = recentCaptions[recentCaptions.Length - 2];
                string latest = recentCaptions[recentCaptions.Length - 1];
                
                if (latest == thirdLast && latest != secondLast && 
                    Math.Abs(latest.Length - secondLast.Length) < 5)
                {
                    // 这是抖动现象，忽略中间的错误识别
                    Console.WriteLine("[Info] Caption stabilization: Detected text jitter, ignoring temporary change");
                    return latest;
                }
            }
            
            // 检查是否有长度突然减少的情况（可能是误识别或临时错误）
            if (recentCaptions.Length >= 2)
            {
                string previous = recentCaptions[recentCaptions.Length - 2];
                string latest = recentCaptions[recentCaptions.Length - 1];
                
                // 如果新内容比前一个短很多，可能是错误
                if (latest.Length < previous.Length * 0.7 && previous.Length > 10)
                {
                    // 长度突然减少超过30%，可能是丢失内容
                    Console.WriteLine("[Info] Caption stabilization: Text length suddenly decreased, using previous content");
                    return previous;
                }
                
                // 如果新内容是前一个内容的开头部分，可能是临时更新
                if (previous.StartsWith(latest) && latest.Length > 5)
                {
                    // 保持使用更完整的版本
                    Console.WriteLine("[Info] Caption stabilization: Using more complete previous text");
                    return previous;
                }
            }
            
            // 默认返回最新内容
            return newContent;
        }
        
        /// <summary>
        /// 分析捕获性能并调整策略
        /// </summary>
        private static void AnalyzePerformance()
        {
            if (totalCaptures == 0)
                return;
                
            // 计算成功率
            double successRate = (double)successfulCaptures / totalCaptures;
            long elapsedMs = performanceWatch.ElapsedMilliseconds;
            
            Console.WriteLine($"[Performance] Caption capture stats: Success rate: {successRate:P2}, " +
                             $"Average interval: {captionsCheckInterval}ms, Total captures: {totalCaptures}");
            
            // 根据成功率调整策略
            if (successRate > 0.95)
            {
                // 非常高的成功率，可以稍微降低检查频率节省资源
                captionsCheckInterval = Math.Min(captionsCheckInterval + 2, 40);
            }
            else if (successRate < 0.8)
            {
                // 较低的成功率，增加检查间隔给系统更多恢复时间
                captionsCheckInterval = Math.Min(captionsCheckInterval + 5, 60);
            }
            else
            {
                // 保持当前设置
            }
            
            // 重置计数器
            if (totalCaptures > 10000)
            {
                totalCaptures = 0;
                successfulCaptures = 0;
                performanceWatch.Restart();
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
                return window.FindFirst(TreeScope.Descendants, condition);
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
                Console.WriteLine($"[Error] Failed to find element by AutomationId: {ex.Message}");
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
                        process.Kill();
                        process.WaitForExit();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Error] Failed to kill process {process.Id}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to kill processes by name: {ex.Message}");
            }
        }
    }
}