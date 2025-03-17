using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class CaptionPage : Page
    {
        public static CaptionPage? Instance { get; set; } = null;
        
        // 用于翻译状态动画
        private Storyboard? translationInProgressStoryboard;
        private Storyboard? translationCompletedStoryboard;
        private SolidColorBrush originalTextBrush;
        private SolidColorBrush translatedTextBrush;
        
        // 最近翻译的文本长度，用于自适应字体大小
        private int lastTranslatedTextLength = 0;

        public CaptionPage()
        {
            InitializeComponent();
            DataContext = Translator.Caption;
            Instance = this;

            // 初始化事件处理
            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;

            // 初始化文本刷子
            originalTextBrush = new SolidColorBrush(Colors.Black);
            translatedTextBrush = new SolidColorBrush(Colors.Black);

            // 设置翻译状态视觉指示
            InitializeTranslationStatusAnimations();
            
            // 设置日志卡片显示
            CollapseTranslatedCaption(Translator.Setting.MainWindow.CaptionLogEnabled);
        }
        
        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            // 订阅事件
            Translator.Caption.PropertyChanged += TranslatedChanged;
            Translator.TranslationStarted += OnTranslationStarted;
            Translator.TranslationCompleted += OnTranslationCompleted;
            
            // 初始化状态
            UpdateTranslationStatus(Translator.IsTranslating);
        }
        
        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            // 取消订阅事件
            Translator.Caption.PropertyChanged -= TranslatedChanged;
            Translator.TranslationStarted -= OnTranslationStarted;
            Translator.TranslationCompleted -= OnTranslationCompleted;
        }

        private async void TextBlock_MouseLeftButtonDown(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                try
                {
                    Clipboard.SetText(textBlock.Text);
                    textBlock.ToolTip = "已复制!";
                    
                    // 为复制操作添加视觉反馈
                    var originalBrush = textBlock.Foreground;
                    textBlock.Foreground = new SolidColorBrush(Colors.Green);
                    await Task.Delay(200);
                    textBlock.Foreground = originalBrush;
                }
                catch
                {
                    textBlock.ToolTip = "复制失败";
                }
                await Task.Delay(500);
                textBlock.ToolTip = "点击复制";
            }
        }

        private void TranslatedChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Translator.Caption.DisplayTranslatedCaption))
            {
                AdaptTextSizeBasedOnContent();
            }
        }
        
        private void AdaptTextSizeBasedOnContent()
        {
            string currentText = Translator.Caption.DisplayTranslatedCaption;
            int currentLength = currentText.Length;
            
            // 根据文本长度智能调整字体大小
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (currentLength > TextUtil.LONG_THRESHOLD)
                {
                    // 长文本使用更小字体
                    this.TranslatedCaption.FontSize = 15;
                }
                else if (currentLength > TextUtil.MEDIUM_THRESHOLD)
                {
                    // 中等长度文本
                    this.TranslatedCaption.FontSize = 16;
                }
                else
                {
                    // 短文本使用标准字体
                    this.TranslatedCaption.FontSize = 18;
                }
                
                // 记录长度，用于检测大变化
                lastTranslatedTextLength = currentLength;
            }), DispatcherPriority.Background);
        }
        
        // 翻译开始事件处理
        private void OnTranslationStarted()
        {
            Dispatcher.BeginInvoke(new Action(() => 
            {
                UpdateTranslationStatus(true);
            }));
        }
        
        // 翻译完成事件处理
        private void OnTranslationCompleted()
        {
            Dispatcher.BeginInvoke(new Action(() => 
            {
                UpdateTranslationStatus(false);
            }));
        }
        
        // 更新翻译状态视觉指示
        private void UpdateTranslationStatus(bool isTranslating)
        {
            if (isTranslating)
            {
                // 开始翻译中动画
                translationInProgressStoryboard?.Begin();
            }
            else
            {
                // 停止翻译中动画
                translationInProgressStoryboard?.Stop();
                
                // 开始翻译完成动画
                translationCompletedStoryboard?.Begin();
            }
        }
        
        // 初始化翻译状态动画
        private void InitializeTranslationStatusAnimations()
        {
            // 翻译进行中动画 - 轻微闪烁效果
            translationInProgressStoryboard = new Storyboard();
            
            var opacityAnimation = new DoubleAnimation
            {
                From = 1.0,
                To = 0.7,
                Duration = new Duration(TimeSpan.FromMilliseconds(500)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            
            Storyboard.SetTarget(opacityAnimation, TranslatedCaption);
            Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath("Opacity"));
            translationInProgressStoryboard.Children.Add(opacityAnimation);
            
            // 翻译完成动画 - 闪光效果
            translationCompletedStoryboard = new Storyboard();
            
            var colorAnimation = new ColorAnimation
            {
                From = Colors.Green,
                To = Colors.Black,
                Duration = new Duration(TimeSpan.FromMilliseconds(800))
            };
            
            translatedTextBrush = new SolidColorBrush(Colors.Black);
            TranslatedCaption.Foreground = translatedTextBrush;
            
            Storyboard.SetTarget(colorAnimation, translatedTextBrush);
            Storyboard.SetTargetProperty(colorAnimation, new PropertyPath("Color"));
            translationCompletedStoryboard.Children.Add(colorAnimation);
        }

        public void CollapseTranslatedCaption(bool isCollapsed)
        {
            var converter = new GridLengthConverter();

            if (isCollapsed)
            {
                TranslatedCaption_Row.Height = (GridLength)converter.ConvertFromString("Auto");
                CaptionLogCard.Visibility = Visibility.Visible;
            }
            else
            {
                TranslatedCaption_Row.Height = (GridLength)converter.ConvertFromString("*");
                CaptionLogCard.Visibility = Visibility.Collapsed;
            }
        }
    }
}