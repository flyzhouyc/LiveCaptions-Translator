using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveCaptionsTranslator.utils; // 添加这行引用LiveCaptionsTranslator.utils命名空间
using LiveCaptionsTranslator.models; // 添加这行引用TranslationHistoryEntry类

namespace LiveCaptionsTranslator
{
    public partial class CaptionPage : Page
    {
        public static CaptionPage? Instance { get; set; } = null;

        public CaptionPage()
        {
            InitializeComponent();
            DataContext = App.Caption;
            Instance = this;

            Loaded += (s, e) => {
                App.Caption.PropertyChanged += TranslatedChanged;
                
                // 确保日志显示状态正确
                CollapseTranslatedCaption(App.Setting.MainWindow.CaptionLogEnabled);
                
                // 初始化日志记录 - 使用Task.Run而不是await
                Task.Run(InitializeLogCards);
            };
            Unloaded += (s, e) => App.Caption.PropertyChanged -= TranslatedChanged;
        }

        private async Task InitializeLogCards()
        {
            try
            {
                // 加载最近的记录
                int logCount = App.Setting?.MainWindow.CaptionLogMax ?? 2;
                var recentLogs = await SQLiteHistoryLogger.LoadRecentEntries(logCount);
                
                if (recentLogs.Count > 0) // 修正这里检查集合的Count属性
                {
                    lock (App.Caption._logLock)
                    {
                        // 清空现有记录
                        while (App.Caption.LogCards.Count > 0)
                            App.Caption.LogCards.Dequeue();
                            
                        // 添加从最旧到最新的记录
                        foreach (var log in recentLogs)
                        {
                            App.Caption.LogCards.Enqueue(log);
                        }
                    }
                    
                    // 使用Dispatcher确保在UI线程更新
                    Dispatcher.BeginInvoke(() => {
                        App.Caption.OnPropertyChanged("DisplayLogCards");
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"初始化日志记录失败: {ex.Message}");
            }
        }

        private async void TextBlock_MouseLeftButtonDown(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                try
                {
                    Clipboard.SetText(textBlock.Text);
                    textBlock.ToolTip = "Copied!";
                }
                catch
                {
                    textBlock.ToolTip = "Error to Copy";
                }
                await Task.Delay(500);
                textBlock.ToolTip = "Click to Copy";
            }
        }

        private void TranslatedChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(App.Caption.DisplayTranslatedCaption))
            {
                if (Encoding.UTF8.GetByteCount(App.Caption.DisplayTranslatedCaption) >= 160)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        this.TranslatedCaption.FontSize = 15;
                    }), DispatcherPriority.Background);
                }
                else
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        this.TranslatedCaption.FontSize = 18;
                    }), DispatcherPriority.Background);
                }
            }
        }

        public void CollapseTranslatedCaption(bool collapse)
        {
            // 使用Dispatcher确保UI线程上的操作
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var converter = new GridLengthConverter();

                if (collapse)
                {
                    TranslatedCaption_Row.Height = (GridLength)converter.ConvertFromString("Auto");
                    CaptionLogCard.Visibility = Visibility.Visible;
                }
                else
                {
                    TranslatedCaption_Row.Height = (GridLength)converter.ConvertFromString("*");
                    CaptionLogCard.Visibility = Visibility.Collapsed;
                }
            }));
        }
    }
}