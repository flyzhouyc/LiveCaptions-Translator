using System.Diagnostics;
using System.Windows;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        // 低性能模式标志
        private bool isLowPerformanceMode = false;
        
        // 轮询任务
        private Task syncLoopTask;
        private Task translateLoopTask;
        
        // 取消令牌源
        private CancellationTokenSource cancellationTokenSource;
        
        App()
        {
            // 注册进程退出处理
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            
            // 配置进程优先级
            try
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal;
            }
            catch
            {
                // 忽略优先级设置失败
            }
            
            // 启动性能监控
            PerformanceMonitor.StartMonitoring();
            PerformanceMonitor.SystemLoadChanged += OnSystemLoadChanged;
            
            // 初始化取消令牌源
            cancellationTokenSource = new CancellationTokenSource();
            
            // 启动任务
            syncLoopTask = Task.Run(() => Translator.SyncLoop());
            translateLoopTask = Task.Run(() => Translator.TranslateLoop());
        }
        
        // 系统负载变化处理
        private void OnSystemLoadChanged(PerformanceMonitor.SystemLoadState newState)
        {
            // 根据系统负载调整应用行为
            if (newState == PerformanceMonitor.SystemLoadState.Critical && !isLowPerformanceMode)
            {
                EnterLowPerformanceMode();
            }
            else if (newState == PerformanceMonitor.SystemLoadState.Normal && isLowPerformanceMode)
            {
                ExitLowPerformanceMode();
            }
        }
        
        // 进入低性能模式
        private void EnterLowPerformanceMode()
        {
            Dispatcher.Invoke(() =>
            {
                isLowPerformanceMode = true;
                
                // 显示低性能模式提示
                MessageBox.Show(
                    "应用检测到系统资源紧张，已自动切换至低性能模式以减轻系统负担。\n\n" +
                    "在此模式下，翻译响应可能略有延迟，但能减少字幕丢失的情况。\n\n" +
                    "建议关闭不必要的应用程序以释放系统资源。", 
                    "低性能模式已启用", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Information);
                
                // 其他低性能模式调整...
            });
        }
        
        // 退出低性能模式
        private void ExitLowPerformanceMode()
        {
            Dispatcher.Invoke(() =>
            {
                isLowPerformanceMode = false;
                
                // 其他恢复操作...
            });
        }

        private void OnProcessExit(object sender, EventArgs e)
        {
            try
            {
                // 取消正在进行的任务
                cancellationTokenSource.Cancel();
                
                // 清理LiveCaptions
                if (Translator.Window != null)
                {
                    LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
                    LiveCaptionsHandler.KillLiveCaptions(Translator.Window);
                }
            }
            catch
            {
                // 忽略关闭过程中的错误
            }
        }
        
        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // 确保取消所有任务
                cancellationTokenSource.Cancel();
                
                // 等待任务完成，但设置超时避免卡住
                Task.WaitAll(new[] { syncLoopTask, translateLoopTask }, 1000);
                
                // 清理资源
                if (Translator.Window != null)
                {
                    LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
                    LiveCaptionsHandler.KillLiveCaptions(Translator.Window);
                }
            }
            catch
            {
                // 忽略退出时的错误
            }
            finally
            {
                base.OnExit(e);
            }
        }
    }
}