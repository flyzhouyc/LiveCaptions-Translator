using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Appearance;

using LiveCaptionsTranslator.utils;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator
{
    public partial class SettingPage : Page
    {
        private static SettingWindow? SettingWindow;
        
        public SettingPage()
        {
            InitializeComponent();
            ApplicationThemeManager.ApplySystemTheme();
            DataContext = Translator.Setting;

            TranslateAPIBox.ItemsSource = Translator.Setting?.Configs.Keys;
            TranslateAPIBox.SelectedIndex = 0;

            LoadAPISetting();
            
            // 初始化内容类型选择框
            InitContentTypeBox();
        }
        
        private void InitContentTypeBox()
        {
            // 根据当前设置选择对应的内容类型
            var currentPromptTemplate = Translator.Setting.PromptTemplate;
            
            switch (currentPromptTemplate)
            {
                case PromptTemplate.General:
                    ContentTypeBox.SelectedIndex = 0;
                    break;
                case PromptTemplate.Technical:
                    ContentTypeBox.SelectedIndex = 1;
                    break;
                case PromptTemplate.Conversation:
                    ContentTypeBox.SelectedIndex = 2;
                    break;
                case PromptTemplate.Conference:
                    ContentTypeBox.SelectedIndex = 3;
                    break;
                case PromptTemplate.Media:
                    ContentTypeBox.SelectedIndex = 4;
                    break;
                default:
                    ContentTypeBox.SelectedIndex = 0;
                    break;
            }
        }

        private void LiveCaptionsButton_click(object sender, RoutedEventArgs e)
        {
            if (Translator.Window == null)
                return;

            var button = sender as Wpf.Ui.Controls.Button;
            var text = ButtonText.Text;

            bool isHide = Translator.Window.Current.BoundingRectangle == Rect.Empty;
            if (isHide)
            {
                LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
                ButtonText.Text = "Hide";
            }
            else
            {
                LiveCaptionsHandler.HideLiveCaptions(Translator.Window);
                ButtonText.Text = "Show";
            }
        }

        private void TranslateAPIBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LoadAPISetting();
        }

        private void TargetLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TargetLangBox.SelectedItem != null)
                Translator.Setting.TargetLanguage = TargetLangBox.SelectedItem.ToString();
        }

        private void TargetLangBox_LostFocus(object sender, RoutedEventArgs e)
        {
            Translator.Setting.TargetLanguage = TargetLangBox.Text;
        }
        
        private void APISettingButton_click(object sender, RoutedEventArgs e)
        {
            if (SettingWindow != null && SettingWindow.IsLoaded)
                SettingWindow.Activate();
            else
            {
                SettingWindow = new SettingWindow();
                SettingWindow.Closed += (sender, args) => SettingWindow = null;
                SettingWindow.Show();
            }
        }
        
        private void CaptionLogMax_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.HistoryMax > Translator.Setting.MainWindow.CaptionLogMax)
                Translator.Setting.OverlayWindow.HistoryMax = Translator.Setting.MainWindow.CaptionLogMax;
            
            while (Translator.Caption.LogCards.Count > Translator.Setting.MainWindow.CaptionLogMax)
                Translator.Caption.LogCards.Dequeue();
            Translator.Caption.OnPropertyChanged("DisplayLogCards");
        }
        
        private void OverlayHistoryMax_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.HistoryMax > Translator.Setting.MainWindow.CaptionLogMax)
                Translator.Setting.MainWindow.CaptionLogMax = Translator.Setting.OverlayWindow.HistoryMax;
        }
        
        private void ContentTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ContentTypeBox.SelectedItem == null)
                return;
                
            var item = ContentTypeBox.SelectedItem as ComboBoxItem;
            if (item == null)
                return;
                
            string contentType = item.Tag as string;
            if (string.IsNullOrEmpty(contentType))
                return;
                
            switch (contentType)
            {
                case "General":
                    Translator.Setting.PromptTemplate = PromptTemplate.General;
                    break;
                case "Technical":
                    Translator.Setting.PromptTemplate = PromptTemplate.Technical;
                    break;
                case "Conversation":
                    Translator.Setting.PromptTemplate = PromptTemplate.Conversation;
                    break;
                case "Conference":
                    Translator.Setting.PromptTemplate = PromptTemplate.Conference;
                    break;
                case "Media":
                    Translator.Setting.PromptTemplate = PromptTemplate.Media;
                    break;
                default:
                    Translator.Setting.PromptTemplate = PromptTemplate.General;
                    break;
            }
        }
        
        private void LiveCaptionsInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            LiveCaptionsInfoFlyout.Show();
        }

        private void LiveCaptionsInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            LiveCaptionsInfoFlyout.Hide();
        }
        
        private void FrequencyInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            FrequencyInfoFlyout.Show();
        }

        private void FrequencyInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            FrequencyInfoFlyout.Hide();
        }
        
        private void TranslateAPIInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            TranslateAPIInfoFlyout.Show();
        }

        private void TranslateAPIInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            TranslateAPIInfoFlyout.Hide();
        }

        private void TargetLangInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            TargetLangInfoFlyout.Show();
        }

        private void TargetLangInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            TargetLangInfoFlyout.Hide();
        }

        private void CaptionLogMaxInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            CaptionLogMaxInfoFlyout.Show();
        }

        private void CaptionLogMaxInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            CaptionLogMaxInfoFlyout.Hide();
        }
        
        private void ContentTypeInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            ContentTypeInfoFlyout.Show();
        }

        private void ContentTypeInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            ContentTypeInfoFlyout.Hide();
        }

        public void LoadAPISetting()
        {
            var supportedLanguages = Translator.Setting.CurrentAPIConfig.SupportedLanguages;
            TargetLangBox.ItemsSource = supportedLanguages.Keys;

            string targetLang = Translator.Setting.TargetLanguage;
            if (!supportedLanguages.ContainsKey(targetLang))
                supportedLanguages[targetLang] = targetLang;    // add custom language to supported languages
            TargetLangBox.SelectedItem = targetLang;
        }
    }
}