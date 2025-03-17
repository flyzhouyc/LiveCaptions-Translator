using System.Windows.Automation;
using System.Text;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class Caption : INotifyPropertyChanged
    {
        private static Caption? instance = null;
        public event PropertyChangedEventHandler? PropertyChanged;

        private string displayOriginalCaption = "";
        private string displayTranslatedCaption = "";
        private bool isTranslating = false;
        private string animatedDots = "";
        private System.Timers.Timer dotsTimer;

        public string OriginalCaption { get; set; } = "";
        public string TranslatedCaption { get; set; } = "";
        public string DisplayOriginalCaption
        {
            get => displayOriginalCaption;
            set
            {
                displayOriginalCaption = value;
                OnPropertyChanged("DisplayOriginalCaption");
            }
        }
        public string DisplayTranslatedCaption
        {
            get 
            {
                // 添加翻译中的动画指示
                if (isTranslating && !string.IsNullOrEmpty(displayTranslatedCaption))
                    return displayTranslatedCaption + animatedDots;
                return displayTranslatedCaption;
            }
            set
            {
                displayTranslatedCaption = value;
                OnPropertyChanged("DisplayTranslatedCaption");
            }
        }
        
        // 翻译状态属性
        public bool IsTranslating
        {
            get => isTranslating;
            set
            {
                if (isTranslating != value)
                {
                    isTranslating = value;
                    OnPropertyChanged("IsTranslating");
                    OnPropertyChanged("DisplayTranslatedCaption");
                    
                    // 更新动画定时器状态
                    if (isTranslating)
                        dotsTimer.Start();
                    else
                    {
                        dotsTimer.Stop();
                        animatedDots = "";
                    }
                }
            }
        }

        public Queue<TranslationHistoryEntry> LogCards { get; } = new(6);
        public IEnumerable<TranslationHistoryEntry> DisplayLogCards => LogCards.Reverse();

        private Caption() 
        {
            // 初始化动画定时器
            dotsTimer = new System.Timers.Timer(300);
            dotsTimer.Elapsed += (s, e) =>
            {
                // 更新动画点数
                animatedDots = animatedDots + ".";
                if (animatedDots.Length > 3)
                    animatedDots = "";
                    
                // 触发UI更新
                OnPropertyChanged("DisplayTranslatedCaption");
            };
            
            // 设置自动订阅翻译状态事件
            Translator.TranslationStarted += () => IsTranslating = true;
            Translator.TranslationCompleted += () => IsTranslating = false;
        }

        public static Caption GetInstance()
        {
            if (instance != null)
                return instance;
            instance = new Caption();
            return instance;
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}