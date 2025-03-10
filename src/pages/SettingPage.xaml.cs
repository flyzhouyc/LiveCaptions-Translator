﻿using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Appearance;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class SettingPage : Page
    {
        public SettingPage()
        {
            InitializeComponent();
            ApplicationThemeManager.ApplySystemTheme();
            DataContext = App.Settings;

            translateAPIBox.ItemsSource = App.Settings.Configs.Keys;
            translateAPIBox.SelectedIndex = 0;
            LoadAPISetting();

            targetLangBox.SelectionChanged += targetLangBox_SelectionChanged;
            targetLangBox.LostFocus += targetLangBox_LostFocus;
            // 初始化缓冲区大小下拉框
            InitializeBufferSizeBox();
        }
        // 新增方法：初始化缓冲区大小下拉框
        private void InitializeBufferSizeBox()
        {
            int maxBufferSize = App.Settings.MaxBufferSize;
            foreach (ComboBoxItem item in bufferSizeBox.Items)
            {
                if (item.Tag != null && int.Parse(item.Tag.ToString()) == maxBufferSize)
                {
                    bufferSizeBox.SelectedItem = item;
                    break;
                }
            }
        }
        private void Button_LiveCaptions(object sender, RoutedEventArgs e)
        {
            if (App.Window == null)
                return;

            var button = sender as Wpf.Ui.Controls.Button;
            var text = ButtonText.Text;

            bool isHide = App.Window.Current.BoundingRectangle == Rect.Empty;
            if (isHide)
            {
                LiveCaptionsHandler.RestoreLiveCaptions(App.Window);
                ButtonText.Text = "Hide";
            }
            else
            {
                LiveCaptionsHandler.HideLiveCaptions(App.Window);
                ButtonText.Text = "Show";
            }
        }

        private void translateAPIBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LoadAPISetting();
        }

        private void targetLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (targetLangBox.SelectedItem != null)
            {
                App.Settings.TargetLanguage = targetLangBox.SelectedItem.ToString();
            }
        }

        private void targetLangBox_LostFocus(object sender, RoutedEventArgs e)
        {
            App.Settings.TargetLanguage = targetLangBox.Text;
        }
        // 新增事件处理方法
        private void BufferSizeButton_MouseEnter(object sender, MouseEventArgs e)
        {
            BufferSizeInfoFlyout.Show();
        }

        private void BufferSizeButton_MouseLeave(object sender, MouseEventArgs e)
        {
            BufferSizeInfoFlyout.Hide();
        }

        private void BatchIntervalButton_MouseEnter(object sender, MouseEventArgs e)
        {
            BatchIntervalInfoFlyout.Show();
        }

        private void BatchIntervalButton_MouseLeave(object sender, MouseEventArgs e)
        {
            BatchIntervalInfoFlyout.Hide();
        }

        private void bufferSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is ComboBoxItem item && item.Tag != null)
            {
                string tag = item.Tag.ToString();
                if (int.TryParse(tag, out int value))
                {
                    App.Settings.MaxBufferSize = value;
                }
            }
        }

        private void TargetLangButton_MouseEnter(object sender, MouseEventArgs e)
        {
            TargetLangInfoFlyout.Show();
        }

        private void TargetLangButton_MouseLeave(object sender, MouseEventArgs e)
        {
            TargetLangInfoFlyout.Hide();
        }

        private void LiveCaptionsButton_MouseEnter(object sender, MouseEventArgs e)
        {
            LiveCaptionsInfoFlyout.Show();
        }

        private void LiveCaptionsButton_MouseLeave(object sender, MouseEventArgs e)
        {
            LiveCaptionsInfoFlyout.Hide();
        }

        private void FrequencyButton_MouseEnter(object sender, MouseEventArgs e)
        {
            FrequencyInfoFlyout.Show();
        }

        private void FrequencyButton_MouseLeave(object sender, MouseEventArgs e)
        {
            FrequencyInfoFlyout.Hide();
        }
        
        private void StabilityButton_MouseEnter(object sender, MouseEventArgs e)
        {
            StabilityInfoFlyout.Show();
        }

        private void StabilityButton_MouseLeave(object sender, MouseEventArgs e)
        {
            StabilityInfoFlyout.Hide();
        }

        private void LoadAPISetting()
        {
            string targetLang = App.Settings.TargetLanguage;
            var supportedLanguages = App.Settings.CurrentAPIConfig.SupportedLanguages;
            targetLangBox.ItemsSource = supportedLanguages.Keys;

            // Add custom target language to ComboBox
            if (!supportedLanguages.ContainsKey(targetLang))
            {
                supportedLanguages[targetLang] = targetLang;
            }
            targetLangBox.SelectedItem = targetLang;

            foreach (UIElement element in PageGrid.Children)
            {
                if (element is Grid childGrid)
                    childGrid.Visibility = Visibility.Collapsed;
            }
            var settingGrid = FindName($"{App.Settings.ApiName}Grid") as Grid ?? FindName($"NoSettingGrid") as Grid;
            settingGrid.Visibility = Visibility.Visible;
        }
    }
}