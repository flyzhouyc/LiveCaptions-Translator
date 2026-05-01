using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class CaptionPage : Page
    {
        public const int CARD_HEIGHT = 110;

        private static CaptionPage? instance;
        public static CaptionPage? Instance => instance;

        public CaptionPage()
        {
            InitializeComponent();
            DataContext = Translator.Caption;
            instance = this;

            Loaded += (s, e) =>
            {
                AutoHeight();
                if (App.Current.MainWindow is MainWindow mainWindow)
                    mainWindow.CaptionLogButton.Visibility = Visibility.Visible;
                if (Translator.Caption != null)
                    Translator.Caption.PropertyChanged += TranslatedChanged;
            };
            Unloaded += (s, e) =>
            {
                if (App.Current.MainWindow is MainWindow mainWindow)
                    mainWindow.CaptionLogButton.Visibility = Visibility.Collapsed;
                if (Translator.Caption != null)
                    Translator.Caption.PropertyChanged -= TranslatedChanged;
            };

            CollapseTranslatedCaption(Translator.Setting?.MainWindow.CaptionLogEnabled ?? false);
        }

        private async void TextBlock_MouseLeftButtonDown(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                try
                {
                    Clipboard.SetText(textBlock.Text);
                    SnackbarHost.Show("Copied.", textBlock.Text, SnackbarType.Info, 100);
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Failed to copy caption text.", ex);
                    SnackbarHost.Show("Copy Failed.", string.Empty, SnackbarType.Error, 100);
                }
                await Task.Delay(500);
            }
        }

        private void TranslatedChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(models.Caption.DisplayTranslatedCaption))
            {
                string translatedCaption = Translator.Caption?.DisplayTranslatedCaption ?? string.Empty;
                if (Encoding.UTF8.GetByteCount(translatedCaption) >= TextUtil.LONG_THRESHOLD)
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

        public void CollapseTranslatedCaption(bool isCollapsed)
        {
            if (isCollapsed)
            {
                TranslatedCaption_Row.Height = GridLength.Auto;
                LogCards.Visibility = Visibility.Visible;
            }
            else
            {
                TranslatedCaption_Row.Height = new GridLength(1, GridUnitType.Star);
                LogCards.Visibility = Visibility.Collapsed;
            }
        }

        public void AutoHeight()
        {
            var setting = Translator.Setting;
            if (App.Current.MainWindow is not MainWindow mainWindow || setting == null)
                return;

            if (setting.MainWindow.CaptionLogEnabled)
                mainWindow.AutoHeightAdjust(
                    minHeight: CARD_HEIGHT * (setting.DisplaySentences + 1),
                    maxHeight: CARD_HEIGHT * (setting.DisplaySentences + 1));
            else
                mainWindow.AutoHeightAdjust(
                    minHeight: (int)mainWindow.MinHeight,
                    maxHeight: (int)mainWindow.MinHeight);
        }
    }
}
