using System.Diagnostics;
using System.Windows;
using System.Windows.Controls; // 添加此行
using System.Windows.Media;    // 添加此行
using System.Windows.Threading;

using LiveCaptionsTranslator.utils;
using Wpf.Ui.Controls;

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
        
        // 通知队列
        private readonly Queue<NotificationItem> notificationQueue = new Queue<NotificationItem>();
        private bool isProcessingNotifications = false;
        
        // 性能指示器
        private PerformanceIndicator performanceIndicator;
        private bool isIndicatorVisible = false;
        
        // 硬件渲染标志
        private bool isHardwareAccelerationDisabled = false;
        
        // 推迟加载任务
        private List<Task> deferredTasks = new List<Task>();
        
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
            
            // 注册LiveCaptions事件
            LiveCaptionsHandler.PerformanceStateChanged += OnLiveCaptionsPerformanceChanged;
            LiveCaptionsHandler.LiveCaptionsMemoryIssue += OnLiveCaptionsMemoryIssue;
            LiveCaptionsHandler.LiveCaptionsRecoveryAttempt += OnLiveCaptionsRecoveryAttempt;
            LiveCaptionsHandler.LiveCaptionsRestartRequested += OnLiveCaptionsRestartRequested;
            
            // 初始化取消令牌源
            cancellationTokenSource = new CancellationTokenSource();
            
            // 根据系统配置优化渲染
            OptimizeRendering();
            
            // 启动任务
            syncLoopTask = Task.Run(() => Translator.SyncLoop());
            translateLoopTask = Task.Run(() => Translator.TranslateLoop());
            
            // 延迟初始化非关键任务
            Dispatcher.BeginInvoke(new Action(() => 
            {
                InitializeDeferredTasks();
            }), DispatcherPriority.Background);
        }
        
        // 初始化延迟加载任务
        private void InitializeDeferredTasks()
        {
            try
            {
                // 预加载常用资源
                deferredTasks.Add(Task.Run(() => 
                {
                    // 预热翻译API
                    try
                    {
                        TranslateAPI.TRANSLATE_FUNCTIONS["Google"]("hello", CancellationToken.None);
                    }
                    catch
                    {
                        // 忽略预热错误
                    }
                }));
                
                // 初始化数据库连接
                deferredTasks.Add(Task.Run(() => 
                {
                    try
                    {
                        SQLiteHistoryLogger.EnsureDatabaseInitialized();
                    }
                    catch
                    {
                        // 忽略数据库初始化错误
                    }
                }));
            }
            catch
            {
                // 忽略延迟初始化错误
            }
        }
        
        // 优化渲染方式
        private void OptimizeRendering()
        {
            try
            {
                // 检测系统GPU能力
                if (RenderCapability.Tier > 0)
                {
                    // 启用硬件加速
                    RenderOptions.ProcessRenderMode = RenderMode.Default;
                }
                else
                {
                    // 系统GPU能力有限，使用软件渲染
                    isHardwareAccelerationDisabled = true;
                    RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                }
                
                // 设置更好的缓存模式
                if (isHardwareAccelerationDisabled)
                {
                    // 软件渲染时使用最小缓存
                    RenderOptions.SetCachingHint(this, CachingHint.Unspecified);
                }
                else
                {
                    // 硬件加速时缓存静态内容
                    RenderOptions.SetCachingHint(this, CachingHint.Cache);
                }
                
                // 配置透明窗口优化
                if (!isHardwareAccelerationDisabled)
                {
                    // 启用位图缓存以提高透明窗口性能
                    RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
                }
            }
            catch
            {
                // 忽略渲染优化错误
            }
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
        
        // LiveCaptions性能状态变化处理
        private void OnLiveCaptionsPerformanceChanged(LiveCaptionsHandler.PerformanceState state)
        {
            switch (state)
            {
                case LiveCaptionsHandler.PerformanceState.LowResource:
                    if (!isLowPerformanceMode)
                    {
                        EnterLowPerformanceMode();
                    }
                    break;
                    
                case LiveCaptionsHandler.PerformanceState.Critical:
                    EnterLowPerformanceMode(true);
                    break;
                    
                case LiveCaptionsHandler.PerformanceState.Normal:
                    if (isLowPerformanceMode)
                    {
                        ExitLowPerformanceMode();
                    }
                    break;
            }
        }
        
        // LiveCaptions内存问题处理
        private void OnLiveCaptionsMemoryIssue(long memoryMB)
        {
            ShowNotification($"检测到LiveCaptions内存使用异常 ({memoryMB} MB)，可能影响性能", NotificationType.Warning);
        }
        
        // LiveCaptions恢复尝试处理
        private void OnLiveCaptionsRecoveryAttempt(int attemptCount)
        {
            ShowNotification($"正在尝试恢复LiveCaptions (第{attemptCount}次)", NotificationType.Info);
        }
        
        // LiveCaptions重启请求处理
        private void OnLiveCaptionsRestartRequested()
        {
            ShowNotification("准备重启LiveCaptions以提高性能", NotificationType.Warning);
            
            // 在UI线程上重启LiveCaptions
            Dispatcher.BeginInvoke(new Action(() => 
            {
                try
                {
                    // 保存当前翻译状态
                    bool wasLogOnly = Translator.LogOnlyFlag;
                    
                    // 重启LiveCaptions
                    if (Translator.Window != null)
                    {
                        LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
                        LiveCaptionsHandler.KillLiveCaptions(Translator.Window);
                    }
                    
                    // 启动新的LiveCaptions
                    Translator.Window = LiveCaptionsHandler.LaunchLiveCaptions();
                    LiveCaptionsHandler.FixLiveCaptions(Translator.Window);
                    LiveCaptionsHandler.HideLiveCaptions(Translator.Window);
                    
                    // 恢复翻译状态
                    Translator.LogOnlyFlag = wasLogOnly;
                    
                    // 通知用户
                    ShowNotification("LiveCaptions已重启", NotificationType.Success);
                }
                catch (Exception ex)
                {
                    // 通知重启失败
                    ShowNotification($"LiveCaptions重启失败: {ex.Message}", NotificationType.Error);
                }
            }));
        }
        
        // 进入低性能模式
        private void EnterLowPerformanceMode(bool critical = false)
        {
            Dispatcher.Invoke(() =>
            {
                isLowPerformanceMode = true;
                
                // 显示低性能模式非阻塞通知
                if (critical)
                {
                    ShowNotification(
                        "系统资源严重不足，已自动切换至极限低性能模式。\n建议关闭其他占用资源的应用程序。", 
                        NotificationType.Warning);
                }
                else
                {
                    ShowNotification(
                        "系统资源紧张，已自动切换至低性能模式。\n在此模式下，翻译响应可能略有延迟。", 
                        NotificationType.Info);
                }
                
                // 显示性能指示器
                ShowPerformanceIndicator();
                
                // 其他低性能模式调整...
                if (critical)
                {
                    // 更激进的优化
                    LiveCaptionsHandler.RequestOptimization(true);
                    
                    // 调整透明窗口和渲染选项
                    if (!isHardwareAccelerationDisabled)
                    {
                        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
                    }
                }
                else
                {
                    // 标准优化
                    LiveCaptionsHandler.RequestOptimization(false);
                }
                
                // 触发垃圾回收
                GC.Collect(1, GCCollectionMode.Optimized);
            });
        }
        
        // 退出低性能模式
        private void ExitLowPerformanceMode()
        {
            Dispatcher.Invoke(() =>
            {
                if (isLowPerformanceMode)
                {
                    isLowPerformanceMode = false;
                    
                    // 显示退出低性能模式通知
                    ShowNotification("系统资源已恢复，退出低性能模式", NotificationType.Success);
                    
                    // 隐藏性能指示器
                    HidePerformanceIndicator();
                    
                    // 重置性能计数器
                    LiveCaptionsHandler.ResetPerformanceCounters();
                    
                    // 恢复渲染选项
                    if (!isHardwareAccelerationDisabled)
                    {
                        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
                    }
                }
            });
        }
        
        // 显示性能指示器
        private void ShowPerformanceIndicator()
        {
            try
            {
                if (isIndicatorVisible || Dispatcher == null)
                    return;
                    
                // 在UI线程创建性能指示器
                Dispatcher.Invoke(() => 
                {
                    // 查找主窗口
                    var mainWindow = Current.Windows.OfType<MainWindow>().FirstOrDefault();
                    if (mainWindow == null)
                        return;
                        
                    // 创建并显示性能指示器
                    performanceIndicator = new PerformanceIndicator();
                    performanceIndicator.Owner = mainWindow;
                    performanceIndicator.Show();
                    isIndicatorVisible = true;
                    
                    // 启动定时更新
                    StartPerformanceIndicatorUpdates();
                });
            }
            catch
            {
                // 忽略指示器创建错误
            }
        }
        
        // 隐藏性能指示器
        private void HidePerformanceIndicator()
        {
            try
            {
                if (!isIndicatorVisible || performanceIndicator == null || Dispatcher == null)
                    return;
                    
                // 在UI线程关闭性能指示器
                Dispatcher.Invoke(() => 
                {
                    performanceIndicator.Close();
                    performanceIndicator = null;
                    isIndicatorVisible = false;
                });
            }
            catch
            {
                // 忽略指示器关闭错误
            }
        }
        
        // 启动性能指示器更新
        private void StartPerformanceIndicatorUpdates()
        {
            // 启动后台任务定期更新指示器
            Task.Run(async () =>
            {
                while (isIndicatorVisible && performanceIndicator != null)
                {
                    try
                    {
                        // 收集性能数据
                        float cpuUsage = PerformanceMonitor.CpuUsage;
                        long memoryMB = PerformanceMonitor.MemoryUsageMB;
                        
                        // 更新UI
                        await Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                if (performanceIndicator != null)
                                {
                                    performanceIndicator.UpdateValues(cpuUsage, memoryMB);
                                }
                            }
                            catch
                            {
                                // 忽略UI更新错误
                            }
                        });
                    }
                    catch
                    {
                        // 忽略性能数据收集错误
                    }
                    
                    // 等待下一个更新周期
                    await Task.Delay(1000);
                }
            });
        }
        
        // 通知类型
        public enum NotificationType
        {
            Info,
            Success,
            Warning,
            Error
        }
        
        // 通知项类
        private class NotificationItem
        {
            public string Message { get; set; }
            public NotificationType Type { get; set; }
            public TimeSpan Duration { get; set; }
            
            public NotificationItem(string message, NotificationType type, TimeSpan? duration = null)
            {
                Message = message;
                Type = type;
                Duration = duration ?? TimeSpan.FromSeconds(type == NotificationType.Error ? 5 : 3);
            }
        }
        
        // 显示非阻塞通知
        private void ShowNotification(string message, NotificationType type = NotificationType.Info)
        {
            notificationQueue.Enqueue(new NotificationItem(message, type));
            
            if (!isProcessingNotifications)
            {
                isProcessingNotifications = true;
                
                // 开始处理通知队列
                _ = ProcessNotificationQueue();
            }
        }
        
        // 处理通知队列
        private async Task ProcessNotificationQueue()
        {
            while (notificationQueue.Count > 0)
            {
                try
                {
                    NotificationItem notification = notificationQueue.Dequeue();
                
                    // 查找主窗口
                    await Dispatcher.InvokeAsync(() => 
                    {
                        try
                        {
                            var mainWindow = Current.Windows.OfType<MainWindow>().FirstOrDefault();
                            if (mainWindow != null && mainWindow.IsLoaded)
                            {
                                // 创建并显示通知
                                var snackbar = new Snackbar()
                                {
                                    Title = "LiveCaptions Translator",
                                    Content = notification.Message,
                                    Timeout = notification.Duration
                                };
                                
                                // 根据类型设置样式
                                switch (notification.Type)
                                {
                                    case NotificationType.Success:
                                        snackbar.Appearance = Wpf.Ui.Common.ControlAppearance.Success;
                                        break;
                                    case NotificationType.Warning:
                                        snackbar.Appearance = Wpf.Ui.Common.ControlAppearance.Caution;
                                        break;
                                    case NotificationType.Error:
                                        snackbar.Appearance = Wpf.Ui.Common.ControlAppearance.Danger;
                                        break;
                                    default:
                                        snackbar.Appearance = Wpf.Ui.Common.ControlAppearance.Secondary;
                                        break;
                                }
                                
                                // 显示通知
                                if (mainWindow.SnackbarHost != null)
                                {
                                    snackbar.Show(mainWindow.SnackbarHost);
                                }
                                else
                                {
                                    snackbar.Show();
                                }
                            }
                        }
                        catch
                        {
                            // 忽略通知显示错误
                        }
                    });
                    
                    // 等待通知显示完成再显示下一个
                    // 不同通知类型的间隔不同
                    int delayMs = (int)notification.Duration.TotalMilliseconds + 300;
                    await Task.Delay(delayMs);
                }
                catch
                {
                    // 忽略通知处理错误，继续处理队列
                    await Task.Delay(500);
                }
            }
            
            isProcessingNotifications = false;
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
                
                // 取消事件订阅
                PerformanceMonitor.SystemLoadChanged -= OnSystemLoadChanged;
                LiveCaptionsHandler.PerformanceStateChanged -= OnLiveCaptionsPerformanceChanged;
                LiveCaptionsHandler.LiveCaptionsMemoryIssue -= OnLiveCaptionsMemoryIssue;
                LiveCaptionsHandler.LiveCaptionsRecoveryAttempt -= OnLiveCaptionsRecoveryAttempt;
                LiveCaptionsHandler.LiveCaptionsRestartRequested -= OnLiveCaptionsRestartRequested;
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
                
                // 关闭性能指示器
                if (isIndicatorVisible && performanceIndicator != null)
                {
                    try
                    {
                        performanceIndicator.Close();
                    }
                    catch
                    {
                        // 忽略关闭错误
                    }
                }
                
                // 清理延迟任务
                foreach (var task in deferredTasks)
                {
                    try
                    {
                        if (!task.IsCompleted)
                        {
                            // 尝试取消任务
                            // 这里假设任务支持取消
                        }
                    }
                    catch
                    {
                        // 忽略任务清理错误
                    }
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
    
    // 性能指示器窗口类
    public class PerformanceIndicator : Window
    {
        private TextBlock cpuTextBlock;
        private TextBlock memoryTextBlock;
        private ProgressBar cpuProgressBar;
        private ProgressBar memoryProgressBar;
        
        public PerformanceIndicator()
        {
            // 配置窗口属性
            Title = "性能监视器";
            Width = 200;
            Height = 100;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            
            // 创建UI布局
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            
            // CPU标签
            var cpuLabel = new TextBlock
            {
                Text = "CPU使用率:",
                Margin = new Thickness(5, 5, 5, 2),
                FontSize = 12
            };
            Grid.SetRow(cpuLabel, 0);
            
            // CPU进度条
            cpuProgressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Height = 15,
                Margin = new Thickness(5, 0, 5, 2)
            };
            Grid.SetRow(cpuProgressBar, 1);
            
            // CPU文本
            cpuTextBlock = new TextBlock
            {
                Text = "0%",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, -17, 10, 0),
                FontSize = 10,
                Foreground = Brushes.Black
            };
            Grid.SetRow(cpuTextBlock, 1);
            
            // 内存标签
            var memoryLabel = new TextBlock
            {
                Text = "内存使用:",
                Margin = new Thickness(5, 5, 5, 2),
                FontSize = 12
            };
            Grid.SetRow(memoryLabel, 2);
            
            // 内存进度条
            memoryProgressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Height = 15,
                Margin = new Thickness(5, 0, 5, 5)
            };
            Grid.SetRow(memoryProgressBar, 3);
            
            // 内存文本
            memoryTextBlock = new TextBlock
            {
                Text = "0 MB",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, -17, 10, 0),
                FontSize = 10,
                Foreground = Brushes.Black
            };
            Grid.SetRow(memoryTextBlock, 3);
            
            // 添加所有元素到网格
            grid.Children.Add(cpuLabel);
            grid.Children.Add(cpuProgressBar);
            grid.Children.Add(cpuTextBlock);
            grid.Children.Add(memoryLabel);
            grid.Children.Add(memoryProgressBar);
            grid.Children.Add(memoryTextBlock);
            
            // 设置窗口内容
            Content = grid;
            
            // 设置窗口位置
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.WorkArea.Width - Width - 10;
            Top = SystemParameters.WorkArea.Height - Height - 10;
        }
        
        // 更新显示值
        public void UpdateValues(float cpuPercent, long memoryMB)
        {
            // 更新CPU
            cpuProgressBar.Value = cpuPercent;
            cpuTextBlock.Text = $"{cpuPercent:0.0}%";
            
            // 更新内存
            memoryProgressBar.Value = Math.Min(memoryMB / 5, 100); // 5MB = 1%，最大100%
            memoryTextBlock.Text = $"{memoryMB} MB";
            
            // 设置进度条颜色
            if (cpuPercent > 80)
            {
                cpuProgressBar.Foreground = Brushes.Red;
            }
            else if (cpuPercent > 50)
            {
                cpuProgressBar.Foreground = Brushes.Orange;
            }
            else
            {
                cpuProgressBar.Foreground = Brushes.Green;
            }
            
            // 设置内存进度条颜色
            if (memoryMB > 400)
            {
                memoryProgressBar.Foreground = Brushes.Red;
            }
            else if (memoryMB > 200)
            {
                memoryProgressBar.Foreground = Brushes.Orange;
            }
            else
            {
                memoryProgressBar.Foreground = Brushes.Green;
            }
        }
    }
}