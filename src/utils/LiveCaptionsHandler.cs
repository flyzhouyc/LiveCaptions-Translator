using System.Diagnostics;
using System.Windows.Automation;

namespace LiveCaptionsTranslator.utils
{
    public static class LiveCaptionsHandler
    {
        public static readonly string PROCESS_NAME = "LiveCaptions";

        private static AutomationElement? captionsTextBlock = null;
        private static DateTime lastCaptionsCheck = DateTime.MinValue;
        private static int captionsCheckInterval = 50; // 毫秒，动态调整
        private static string lastCaptionContent = string.Empty;
        private static int consecutiveEmptyResults = 0;
        private static int consecutiveSameResults = 0;

        public static AutomationElement LaunchLiveCaptions()
        {
            // Init
            KillAllProcessesByPName(PROCESS_NAME);
            var process = Process.Start(PROCESS_NAME);

            // Search for window
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

            return window;
        }

        public static void KillLiveCaptions(AutomationElement window)
        {
            try
            {
                // Search for process
                nint hWnd = new nint((long)window.Current.NativeWindowHandle);
                WindowsAPI.GetWindowThreadProcessId(hWnd, out int processId);
                var process = Process.GetProcessById(processId);

                // Kill process
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
            // 自适应检查频率
            TimeSpan timeSinceLastCheck = DateTime.Now - lastCaptionsCheck;
            if (timeSinceLastCheck.TotalMilliseconds < captionsCheckInterval)
            {
                return lastCaptionContent; // 返回缓存的内容
            }

            try
            {
                // 检查 LiveCaptions.exe 是否存活
                var info = window.Current;
                var name = info.Name;

                if (captionsTextBlock == null)
                {
                    captionsTextBlock = FindElementByAId(window, "CaptionsTextBlock");
                    if (captionsTextBlock == null)
                    {
                        consecutiveEmptyResults++;
                        return lastCaptionContent;
                    }
                }

                string newContent = captionsTextBlock.Current.Name;
                lastCaptionsCheck = DateTime.Now;

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
                    captionsCheckInterval = Math.Max(captionsCheckInterval - 10, 30);
                    consecutiveSameResults = 0;
                    lastCaptionContent = newContent;
                }

                return lastCaptionContent;
            }
            catch (ElementNotAvailableException)
            {
                captionsTextBlock = null;
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to get captions: {ex.Message}");
                captionsTextBlock = null;
                return lastCaptionContent;
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