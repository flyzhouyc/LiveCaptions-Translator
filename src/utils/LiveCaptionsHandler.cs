using System.Diagnostics;
using System.Windows.Automation;

namespace LiveCaptionsTranslator.utils
{
    public static class LiveCaptionsHandler
    {
        public static readonly string PROCESS_NAME = "LiveCaptions";

        public static AutomationElement LaunchLiveCaptions()
        {
            // Init
            KillAllProcessesByPName(PROCESS_NAME);
            var process = Process.Start(PROCESS_NAME);

            // Search for window
            AutomationElement? window = null;
            for (int attemptCount = 0;
                 window == null || window.Current.ClassName.CompareTo("LiveCaptionsDesktopWindow") != 0;
                 attemptCount++)
            {
                window = FindWindowByPId(process.Id);
                if (attemptCount > 10000)
                    throw new Exception("Failed to launch!");
            }
            return window;
        }

        public static void KillLiveCaptions(AutomationElement window)
        {
            // Search for process
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            WindowsAPI.GetWindowThreadProcessId(hWnd, out int processId);
            var process = Process.GetProcessById(processId);

            // Kill process
            process.Kill();
            process.WaitForExit();
        }

        public static void HideLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);

            WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_MINIMIZE);
            WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle | WindowsAPI.WS_EX_TOOLWINDOW);
        }

        public static void RestoreLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);

            WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle & ~WindowsAPI.WS_EX_TOOLWINDOW);
            WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_RESTORE);
            WindowsAPI.SetForegroundWindow(hWnd);
        }

        public static void FixLiveCaptions(AutomationElement window)
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

        public static string GetCaptions(AutomationElement window)
        {
            int retryCount = 0;
            int maxRetries = 3;
            
            while (retryCount < maxRetries)
            {
                try
                {
                    var captionsTextBlock = FindElementByAId(window, "CaptionsTextBlock");
                    if (captionsTextBlock == null)
                    {
                        retryCount++;
                        Thread.Sleep(50); // 短暂暂停后重试
                        continue;
                    }
                    return captionsTextBlock.Current.Name;
                }
                catch (Exception)
                {
                    retryCount++;
                    if (retryCount >= maxRetries)
                        return string.Empty;
                    Thread.Sleep(50);
                }
            }
            return string.Empty;
        }
        // 添加一个方法检查LiveCaptions窗口状态
        // 添加这个方法到 LiveCaptionsHandler 类中的任何公共方法位置
        public static bool IsLiveCaptionsActive(AutomationElement window)
        {
            if (window == null) return false;
            
            try
            {
                nint hWnd = new nint((long)window.Current.NativeWindowHandle);
                return WindowsAPI.IsWindow(hWnd);
            }
            catch
            {
                return false;
            }
        }
        // 添加尝试恢复LiveCaptions的方法
        // 修改 TryRestoreLiveCaptions 方法避免使用 ref 参数
        public static AutomationElement TryRestoreLiveCaptions(AutomationElement currentWindow)
        {
            try
            {
                if (currentWindow != null)
                {
                    try { KillLiveCaptions(currentWindow); } 
                    catch { /* 忽略错误 */ }
                }
                
                var newWindow = LaunchLiveCaptions();
                FixLiveCaptions(newWindow);
                HideLiveCaptions(newWindow);
                return newWindow;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"恢复LiveCaptions失败: {ex.Message}");
                return null;
            }
        }

        private static AutomationElement FindWindowByPId(int processId)
        {
            var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
            return AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
        }

        public static AutomationElement? FindElementByAId(
            AutomationElement window, 
            string automationId,
            CancellationToken cancellationToken = default)
        {
            if (window == null) return null;

            try
            {
                PropertyCondition condition = new PropertyCondition(
                    AutomationElement.AutomationIdProperty,
                    automationId);
                return window.FindFirst(TreeScope.Descendants, condition);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception)
            {
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
            var processes = Process.GetProcessesByName(processName);
            if (processes.Length == 0)
                return;
            foreach (Process process in processes)
            {
                process.Kill();
                process.WaitForExit();
            }
        }
    }
}
