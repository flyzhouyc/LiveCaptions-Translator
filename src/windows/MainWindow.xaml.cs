using System;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class MainWindow : FluentWindow
    {
        public OverlayWindow? OverlayWindow { get; set; } = null;
        
        // 窗口调整节流控制
        private DateTime _lastResizeTime = DateTime.MinValue;
        private DateTime _lastMoveTime = DateTime.MinValue;
        private const int ThrottleInterval = 300; // 毫秒

        public MainWindow()
        {
            InitializeComponent();
            ApplicationThemeManager.ApplySystemTheme();

            Loaded += (sender, args) =>
            {
                SystemThemeWatcher.Watch(
                    this,
                    WindowBackdropType.Mica,
                    true
                );
            };
            Loaded += (sender, args) => RootNavigation.Navigate(typeof(CaptionPage));

            var windowState = WindowHandler.LoadState(this, Translator.Setting);
            WindowHandler.RestoreState(this, windowState);
            
            ToggleTopmost(Translator.Setting.MainWindow.Topmost);
            ShowLogCard(Translator.Setting.MainWindow.CaptionLogEnabled);
        }

        private void TopmostButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleTopmost(!this.Topmost);
        }

        private void OverlayModeButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var symbolIcon = button?.Icon as SymbolIcon;

            if (OverlayWindow == null)
            {
                // Caption + Translation
                symbolIcon.Symbol = SymbolRegular.TextUnderlineDouble20;

                OverlayWindow = new OverlayWindow();
                
                // 使用节流技术处理窗口大小和位置变化事件
                OverlayWindow.SizeChanged += WindowResizeThrottler;
                OverlayWindow.LocationChanged += WindowMoveThrottler;

                var windowState = WindowHandler.LoadState(OverlayWindow, Translator.Setting);
                WindowHandler.RestoreState(OverlayWindow, windowState);
                OverlayWindow.Show();
            }
            else if (!OverlayWindow.IsTranslationOnly)
            {
                // Translation Only
                symbolIcon.Symbol = SymbolRegular.TextAddSpaceBefore24;

                OverlayWindow.IsTranslationOnly = true;
                OverlayWindow.Focus();
            }
            else
            {
                // Closed
                symbolIcon.Symbol = SymbolRegular.WindowNew20;

                OverlayWindow.IsTranslationOnly = false;
                OverlayWindow.Close();
                OverlayWindow = null;
            }
        }

        private void LogOnly_OnClickButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var symbolIcon = button?.Icon as SymbolIcon;

            if (Translator.LogOnlyFlag)
            {
                Translator.LogOnlyFlag = false;
                symbolIcon.Filled = false;
            }
            else
            {
                Translator.LogOnlyFlag = true;
                symbolIcon.Filled = true;
            }
        }

        private void CaptionLog_OnClickButton_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.MainWindow.CaptionLogEnabled = !Translator.Setting.MainWindow.CaptionLogEnabled;
            ShowLogCard(Translator.Setting.MainWindow.CaptionLogEnabled);
        }

        // 使用节流处理窗口边界变化事件
        private async void MainWindow_BoundsChanged(object sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            // 如果上次保存时间太近，则跳过
            if ((now - _lastResizeTime).TotalMilliseconds < ThrottleInterval)
                return;
                
            _lastResizeTime = now;
            await WindowHandler.SaveStateAsync(sender as Window, Translator.Setting);
        }
        
        // 窗口大小变更节流处理器
        private async void WindowResizeThrottler(object sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            // 至少300ms间隔才保存状态
            if ((now - _lastResizeTime).TotalMilliseconds < ThrottleInterval)
                return;
                
            _lastResizeTime = now;
            await WindowHandler.SaveStateAsync(sender as Window, Translator.Setting);
        }

        // 窗口位置变更节流处理器
        private async void WindowMoveThrottler(object sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            // 至少300ms间隔才保存状态
            if ((now - _lastMoveTime).TotalMilliseconds < ThrottleInterval)
                return;
                
            _lastMoveTime = now;
            await WindowHandler.SaveStateAsync(sender as Window, Translator.Setting);
        }

        public void ToggleTopmost(bool enabled)
        {
            var button = topmost as Button;
            var symbolIcon = button?.Icon as SymbolIcon;
            symbolIcon.Filled = enabled;
            this.Topmost = enabled;
            Translator.Setting.MainWindow.Topmost = enabled;
        }

        public void ShowLogCard(bool enabled)
        {
            if (captionLog.Icon is SymbolIcon icon)
            {
                if (enabled)
                    icon.Symbol = SymbolRegular.History24;
                else
                    icon.Symbol = SymbolRegular.HistoryDismiss24;
                CaptionPage.Instance?.CollapseTranslatedCaption(enabled);
            }
        }
    }
}