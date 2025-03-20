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
            
            // 更新温度设置面板可见性
            UpdateTemperatureSettingsVisibility();
        }
        
        private void InitContentTypeBox()
        {
            // 根据当前设置选择对应的内容类型
            var currentPromptTemplate = Translator.Setting.PromptTemplate;
            
            switch (currentPromptTemplate)
            {
                case PromptTemplate.AutoDetection:
                    ContentTypeBox.SelectedIndex = 0;
                    break;
                case PromptTemplate.General:
                    ContentTypeBox.SelectedIndex = 1;
                    break;
                case PromptTemplate.Technical:
                    ContentTypeBox.SelectedIndex = 2;
                    break;
                case PromptTemplate.Conversation:
                    ContentTypeBox.SelectedIndex = 3;
                    break;
                case PromptTemplate.Conference:
                    ContentTypeBox.SelectedIndex = 4;
                    break;
                case PromptTemplate.Media:
                    ContentTypeBox.SelectedIndex = 5;
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
                case "AutoDetection":
                    Translator.Setting.PromptTemplate = PromptTemplate.AutoDetection;
                    break;
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
            
            // 如果启用了内容自适应模式，更新API参数
            if (Translator.Setting.UseContentAdaptiveMode)
            {
                Translator.Setting.UpdateAPIParametersForCurrentTemplate();
            }
        }
        
        // 内容自适应模式开关处理
        private void ContentAdaptiveModeToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is Wpf.Ui.Controls.ToggleSwitch toggle)
            {
                Translator.Setting.UseContentAdaptiveMode = toggle.IsChecked ?? false;
                
                // 根据当前设置更新UI状态
                UpdateTemperatureSettingsVisibility();
                
                // 如果开启了自适应模式，立即应用当前模板的参数
                if (Translator.Setting.UseContentAdaptiveMode)
                {
                    Translator.Setting.UpdateAPIParametersForCurrentTemplate();
                }
            }
        }
        
        // 更新温度设置面板可见性
        private void UpdateTemperatureSettingsVisibility()
        {
            // 因为我们使用了绑定和转换器，此方法现在只是确保UI状态正确
            // 如果有特殊的UI逻辑，可以在这里添加
        }
        
        // 处理模板温度滑块值变化
        private void TemplateTemperatureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender is Slider slider && slider.Tag is string templateName)
            {
                if (Enum.TryParse<PromptTemplate>(templateName, out var template))
                {
                    // 检查字典是否包含此键，如果不包含则添加
                    if (!Translator.Setting.TemplateTemperatures.ContainsKey(template))
                    {
                        Translator.Setting.TemplateTemperatures[template] = slider.Value;
                    }
                    
                    // 如果当前正在使用这个模板，立即应用新温度
                    if (Translator.Setting.PromptTemplate == template && Translator.Setting.UseContentAdaptiveMode)
                    {
                        Translator.Setting.UpdateAPIParametersForCurrentTemplate();
                    }
                }
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
        
        private void ContentAdaptiveInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            ContentAdaptiveInfoFlyout.Show();
        }

        private void ContentAdaptiveInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            ContentAdaptiveInfoFlyout.Hide();
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