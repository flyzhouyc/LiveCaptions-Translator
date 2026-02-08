using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        
        protected override void OnStartup(StartupEventArgs e)
        {
            // 配置进程优先级
            try
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal;
            }
            catch
            {
                // 忽略优先级设置失败
            }
            
            // 优化内存设置
            Environment.SetEnvironmentVariable("COMPlus_gcConcurrent", "1"); // 启用并发GC
            Environment.SetEnvironmentVariable("COMPlus_gcServer", "0"); // 禁用服务器GC，对WPF更友好
            Environment.SetEnvironmentVariable("COMPlus_Thread_UseAllCpuGroups", "1"); // 使用所有CPU组
            
            // 预热应用程序
            Task.Run(() => 
            {
                // 预热Translator类
                try
                {
                    TranslateAPI.TRANSLATE_FUNCTIONS["Google"]("hello", CancellationToken.None).Wait(1000);
                }
                catch
                {
                    // 忽略预热错误
                }
                
                // 预热对象池
                for (int i = 0; i < 5; i++)
                {
                    var task = TranslationTaskPool.Obtain();
                    TranslationTaskPool.Return(task);
                }
            });
            
            // 初始化延迟加载任务 - 添加调用
            InitializeDeferredTasks();
            
            // 注册退出事件处理器
            this.Exit += OnProcessExit;
            
            // 订阅LiveCaptions事件 - 添加订阅
            PerformanceMonitor.SystemLoadChanged += OnSystemLoadChanged;
            LiveCaptionsHandler.PerformanceStateChanged += OnLiveCaptionsPerformanceChanged;
            LiveCaptionsHandler.LiveCaptionsMemoryIssue += OnLiveCaptionsMemoryIssue;
            LiveCaptionsHandler.LiveCaptionsRecoveryAttempt += OnLiveCaptionsRecoveryAttempt;
            LiveCaptionsHandler.LiveCaptionsRestartRequested += OnLiveCaptionsRestartRequested;
            
            // 初始化取消令牌源 - 添加初始化
            cancellationTokenSource = new CancellationTokenSource();
            
            // 启动性能监控
            PerformanceMonitor.StartMonitoring();
            
            // 启动核心循环任务 - 添加任务启动
            syncLoopTask = Task.Run(() => Translator.SyncLoop(cancellationTokenSource.Token));
            translateLoopTask = Task.Run(() => Translator.TranslateLoop(cancellationTokenSource.Token));
            
            base.OnStartup(e);
        }
        
        // 初始化延迟加载任务
        private void InitializeDeferredTasks()
        {
            try
            {
                // 预加载常用资源
                Task.Run(() => 
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
                });
                
                // 初始化数据库连接
                Task.Run(() => 
                {
                    try
                    {
                        // 预热数据库连接
                        SQLiteHistoryLogger.LoadLastSourceText().Wait();
                    }
                    catch
                    {
                        // 忽略数据库初始化错误
                    }
                });
            }
            catch
            {
                // 忽略延迟初始化错误
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
                    var mainWindow = Current.Windows.OfType<MainWindow>().FirstOrDefault();
                    if (mainWindow != null && mainWindow.IsLoaded)
                    {
                        await Dispatcher.InvokeAsync(() => 
                        {
                            try
                            {
                                // 使用System.Windows.MessageBox，明确指定命名空间避免冲突
                                System.Windows.MessageBox.Show(
                                    mainWindow,
                                    notification.Message,
                                    "LiveCaptions Translator",
                                    System.Windows.MessageBoxButton.OK,
                                    notification.Type == NotificationType.Error ? System.Windows.MessageBoxImage.Error :
                                    notification.Type == NotificationType.Warning ? System.Windows.MessageBoxImage.Warning :
                                    notification.Type == NotificationType.Success ? System.Windows.MessageBoxImage.Information :
                                    System.Windows.MessageBoxImage.Information);
                            }
                            catch
                            {
                                // 忽略通知显示错误
                            }
                        });
                    }
                    
                    // 等待通知显示完成再显示下一个
                    await Task.Delay(100); // 短暂延迟防止消息框堆叠
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
                // 取消正在进行的任务并等待循环退出
                StopCoreLoops();
                
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

        private void StopCoreLoops()
        {
            try
            {
                cancellationTokenSource?.Cancel();
            }
            catch
            {
                // 忽略取消异常
            }

            try
            {
                syncLoopTask?.Wait(1000);
            }
            catch
            {
                // 忽略等待异常
            }

            try
            {
                translateLoopTask?.Wait(1000);
            }
            catch
            {
                // 忽略等待异常
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // 确保在应用退出时所有资源都被正确释放
                
                // 取消所有任务并等待循环退出
                if (cancellationTokenSource != null)
                {
                    StopCoreLoops();
                    cancellationTokenSource.Dispose();
                }
                
                if (Translator.Setting != null)
                {
                    // 确保所有待保存的设置都被保存
                    BatchSettingsSave.CommitAllPendingChangesAsync().Wait(1000);
                }
                
                // 清理LiveCaptions
                if (Translator.Window != null)
                {
                    LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
                    LiveCaptionsHandler.KillLiveCaptions(Translator.Window);
                }
                
                // 清理LiveCaptionsHandler资源
                LiveCaptionsHandler.Cleanup();
                
                // 清理对象池
                TranslationTaskPool.Reset();
                
                // 最后尝试强制GC
                GC.Collect(2, GCCollectionMode.Forced);
                GC.WaitForPendingFinalizers();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"应用退出时清理资源失败: {ex.Message}");
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
        private System.Windows.Controls.TextBlock cpuTextBlock;
        private System.Windows.Controls.TextBlock memoryTextBlock;
        private System.Windows.Controls.ProgressBar cpuProgressBar;
        private System.Windows.Controls.ProgressBar memoryProgressBar;
        
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
            var cpuLabel = new System.Windows.Controls.TextBlock
            {
                Text = "CPU使用率:",
                Margin = new Thickness(5, 5, 5, 2),
                FontSize = 12
            };
            Grid.SetRow(cpuLabel, 0);
            
            // CPU进度条
            cpuProgressBar = new System.Windows.Controls.ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Height = 15,
                Margin = new Thickness(5, 0, 5, 2)
            };
            Grid.SetRow(cpuProgressBar, 1);
            
            // CPU文本
            cpuTextBlock = new System.Windows.Controls.TextBlock
            {
                Text = "0%",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, -17, 10, 0),
                FontSize = 10,
                Foreground = Brushes.Black
            };
            Grid.SetRow(cpuTextBlock, 1);
            
            // 内存标签
            var memoryLabel = new System.Windows.Controls.TextBlock
            {
                Text = "内存使用:",
                Margin = new Thickness(5, 5, 5, 2),
                FontSize = 12
            };
            Grid.SetRow(memoryLabel, 2);
            
            // 内存进度条
            memoryProgressBar = new System.Windows.Controls.ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Height = 15,
                Margin = new Thickness(5, 0, 5, 5)
            };
            Grid.SetRow(memoryProgressBar, 3);
            
            // 内存文本
            memoryTextBlock = new System.Windows.Controls.TextBlock
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
