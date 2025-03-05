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

        private static readonly char[] PUNC_EOS = ".?!。？！".ToCharArray();
        private static readonly char[] PUNC_COMMA = ",，、—\n".ToCharArray();

        // 事件监听相关
        private AutomationPropertyChangedEventHandler? textChangeHandler;
        private AutomationElement? captionsTextBlock;
        
        // 句子处理相关变量
        private string displayOriginalCaption = "";
        private string displayTranslatedCaption = "";
        private string currentSentenceBuilder = "";  // 存储正在构建的句子
        private string lastCompleteSentence = "";    // 上次翻译的完整句子
        private bool isSentenceComplete = false;     // 句子是否已完成的标志
        private bool hasUnprocessedSentence = false; // 是否存在未翻译的完整句子
        private int stabilityCounter = 0;            // 稳定性计数器(句子保持不变的次数)
        
        // 用于备份检查的字段
        private string lastProcessedText = "";
        private System.Timers.Timer backupTimer;

        public bool TranslateFlag { get; set; } = false;
        public bool LogOnlyFlag { get; set; } = false;

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
            get => displayTranslatedCaption;
            set
            {
                displayTranslatedCaption = value;
                OnPropertyChanged("DisplayTranslatedCaption");
            }
        }

        private Caption() 
        {
            // 创建备份检查计时器，以防事件监听失效
            backupTimer = new System.Timers.Timer(1000); // 每秒检查一次
            backupTimer.Elapsed += (s, e) => CheckForMissedUpdates();
            backupTimer.AutoReset = true;
            backupTimer.Start();
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

        /// <summary>
        /// 设置UI自动化事件监听，替代原有的轮询方式
        /// </summary>
        public void StartListening()
        {
            if (App.Window == null)
            {
                Task.Run(() => TrySetupListener());
                return;
            }

            SetupAutomationEventListener(App.Window);
        }

        /// <summary>
        /// 尝试设置监听器，直到LiveCaptions窗口可用
        /// </summary>
        private async Task TrySetupListener()
        {
            while (true)
            {
                if (App.Window != null)
                {
                    SetupAutomationEventListener(App.Window);
                    break;
                }
                await Task.Delay(1000);
            }
        }

        /// <summary>
        /// 设置UI自动化事件监听器
        /// </summary>
        private void SetupAutomationEventListener(AutomationElement window)
        {
            try
            {
                // 清除现有的事件处理器
                CleanupEventListener();

                // 查找字幕文本块
                captionsTextBlock = LiveCaptionsHandler.FindElementByAId(window, "CaptionsTextBlock");
                if (captionsTextBlock == null)
                {
                    Console.WriteLine("无法找到字幕文本块，将退回到备份检查机制");
                    return;
                }

                // 创建并注册事件处理器
                textChangeHandler = new AutomationPropertyChangedEventHandler(OnCaptionTextChanged);
                Automation.AddAutomationPropertyChangedEventHandler(
                    captionsTextBlock,
                    TreeScope.Element,
                    textChangeHandler,
                    AutomationElement.NameProperty);

                Console.WriteLine("成功设置字幕变化事件监听器");
                
                // 初始处理现有文本
                string initialText = captionsTextBlock.Current.Name;
                if (!string.IsNullOrEmpty(initialText))
                {
                    ProcessCaptionText(initialText);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"设置事件监听器时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 移除事件监听器，避免内存泄漏
        /// </summary>
        private void CleanupEventListener()
        {
            if (captionsTextBlock != null && textChangeHandler != null)
            {
                try
                {
                    Automation.RemoveAutomationPropertyChangedEventHandler(
                        captionsTextBlock,
                        textChangeHandler);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"移除事件监听器时出错: {ex.Message}");
                }
                textChangeHandler = null;
            }
        }

        /// <summary>
        /// 字幕文本改变事件处理函数
        /// </summary>
        private void OnCaptionTextChanged(object sender, AutomationPropertyChangedEventArgs e)
        {
            try
            {
                var element = sender as AutomationElement;
                if (element == null) return;

                string newText = element.Current.Name;
                if (string.IsNullOrEmpty(newText)) return;

                // 更新上次处理的文本，用于备份检查
                lastProcessedText = newText;
                
                // 处理捕获到的文本
                ProcessCaptionText(newText);
            }
            catch (ElementNotAvailableException)
            {
                // 元素不可用，可能需要重新设置监听器
                App.Window = null;
                Task.Run(() => TrySetupListener());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"处理字幕文本变化事件时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 备份检查，防止事件监听失败或丢失
        /// </summary>
        private void CheckForMissedUpdates()
        {
            try
            {
                if (App.Window == null || captionsTextBlock == null)
                {
                    Task.Run(() => TrySetupListener());
                    return;
                }

                string currentText = captionsTextBlock.Current.Name;
                
                // 如果当前文本与上次处理的不同，说明可能漏掉了事件
                if (currentText != lastProcessedText)
                {
                    lastProcessedText = currentText;
                    ProcessCaptionText(currentText);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"备份检查时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理字幕文本，替代原轮询中的处理逻辑
        /// </summary>
        private void ProcessCaptionText(string fullText)
        {
            // 文本预处理
            fullText = PreprocessText(fullText);

            // 提取最后一个完整句子或正在进行的句子
            string currentSentence = ExtractCurrentSentence(fullText);

            // 更新显示的原始字幕
            UpdateDisplayCaption(currentSentence, fullText);

            // 判断句子是否发生变化
            if (currentSentence != currentSentenceBuilder)
            {
                // 句子有变化，重置稳定性计数器
                stabilityCounter = 0;
                currentSentenceBuilder = currentSentence;
                
                // 判断句子是否完整
                isSentenceComplete = IsSentenceComplete(currentSentence);
            }
            else
            {
                // 句子保持不变，增加稳定性计数
                stabilityCounter++;
                
                // 检查句子是否已经稳定了足够的时间
                if (stabilityCounter >= App.Settings?.MinStabilityCount)
                {
                    // 判断是否有新的完整句子需要翻译
                    if (isSentenceComplete && currentSentence != lastCompleteSentence)
                    {
                        // 如果有一个新的完整句子，标记为未处理并准备翻译
                        OriginalCaption = currentSentence;
                        lastCompleteSentence = currentSentence;
                        hasUnprocessedSentence = true;
                        TranslateFlag = true;
                        stabilityCounter = 0; // 重置稳定性计数器
                    }
                    // 如果句子稳定但不完整，且已经稳定足够长时间，也触发翻译
                    else if (!isSentenceComplete && stabilityCounter >= App.Settings.MaxSyncInterval && 
                             Encoding.UTF8.GetByteCount(currentSentence) >= 15)
                    {
                        OriginalCaption = currentSentence;
                        TranslateFlag = true;
                        stabilityCounter = 0; // 重置稳定性计数器
                    }
                }
            }
        }

        // 以下方法与原代码相同，只是移除了轮询循环
        
        // 文本预处理
        private string PreprocessText(string text)
        {
            // 移除大写字母间的点号
            text = Regex.Replace(text, @"(?<=[A-Z])\s*\.\s*(?=[A-Z])", "");
            // 处理标点周围的空白
            text = Regex.Replace(text, @"\s*([.!?,])\s*", "$1 ");
            text = Regex.Replace(text, @"\s*([。！？，、])\s*", "$1");
            // 替换句子内的换行符
            text = ReplaceNewlines(text, 32);
            return text;
        }

        // 从全文中提取当前句子
        private string ExtractCurrentSentence(string fullText)
        {
            // 查找最后一个句子结束标点
            int lastEOSIndex;
            if (Array.IndexOf(PUNC_EOS, fullText[^1]) != -1)
                lastEOSIndex = fullText[0..^1].LastIndexOfAny(PUNC_EOS);
            else
                lastEOSIndex = fullText.LastIndexOfAny(PUNC_EOS);

            // 如果找到了句末标点，提取最后一个句子
            if (lastEOSIndex >= 0)
                return fullText.Substring(lastEOSIndex + 1).Trim();
            else
                return fullText.Trim(); // 没有找到句末标点，返回全文
        }

        // 判断句子是否完整
        private bool IsSentenceComplete(string sentence)
        {
            if (string.IsNullOrEmpty(sentence))
                return false;
                
            // 以句末标点结尾的句子视为完整
            return Array.IndexOf(PUNC_EOS, sentence[^1]) != -1 || 
                   // 或者长度超过一定阈值且已稳定一段时间
                   (Encoding.UTF8.GetByteCount(sentence) >= 50 && stabilityCounter >= 10);
        }

        // 更新显示的原始字幕
        private void UpdateDisplayCaption(string currentSentence, string fullText)
        {
            // 更新显示的原始字幕
            DisplayOriginalCaption = currentSentence;

            // 如果当前句子过短，尝试显示更多上下文
            if (Encoding.UTF8.GetByteCount(currentSentence) < 12)
            {
                int lastEOSIndex = fullText.LastIndexOfAny(PUNC_EOS);
                if (lastEOSIndex > 0)
                {
                    int previousEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(PUNC_EOS);
                    if (previousEOSIndex >= 0)
                        DisplayOriginalCaption = fullText.Substring(previousEOSIndex + 1).Trim();
                }
            }

            // 如果句子过长，裁剪显示内容
            DisplayOriginalCaption = ShortenDisplaySentence(DisplayOriginalCaption, 160);
        }

        public async Task Translate()
        {
            var translationTaskQueue = new TranslationTaskQueue();
            while (true)
            {
                if (App.Window == null)
                {
                    DisplayTranslatedCaption = "[WARNING] LiveCaptions was unexpectedly closed, restarting...";
                    App.Window = LiveCaptionsHandler.LaunchLiveCaptions();
                    StartListening(); // 重新设置事件监听
                    DisplayTranslatedCaption = "";
                } 
                else if (LogOnlyFlag)
                {
                    TranslatedCaption = string.Empty;
                    DisplayTranslatedCaption = "[Paused]";
                } 
                else if (!string.IsNullOrEmpty(translationTaskQueue.Output))
                {
                    // 获取主要翻译结果
                    TranslatedCaption = translationTaskQueue.Output;
                    DisplayTranslatedCaption = ShortenDisplaySentence(TranslatedCaption, 200);
                }
                else
                {
                    // 如果当前没有可用翻译，尝试获取备选翻译结果
                    string backupResult = translationTaskQueue.GetLatestAvailableResult();
                    if (!string.IsNullOrEmpty(backupResult))
                    {
                        TranslatedCaption = backupResult;
                        DisplayTranslatedCaption = ShortenDisplaySentence(TranslatedCaption, 200);
                    }
                }

                if (TranslateFlag)
                {
                    var originalSnapshot = OriginalCaption;

                    // 如果旧句子是新句子的前缀，记录日志时覆盖之前的条目
                    string lastLoggedOriginal = await SQLiteHistoryLogger.LoadLatestSourceText();
                    bool isOverWrite = !string.IsNullOrEmpty(lastLoggedOriginal)
                        && originalSnapshot.StartsWith(lastLoggedOriginal);

                    if (LogOnlyFlag)
                    {
                        var LogOnlyTask = Task.Run(
                            () => Translator.LogOnly(originalSnapshot, isOverWrite)
                        );
                    }
                    else
                    {
                        translationTaskQueue.Enqueue(token => Task.Run(() =>
                        {
                            var TranslateTask = Translator.Translate(OriginalCaption, token);
                            var LogTask = Translator.Log(
                                originalSnapshot, TranslateTask.Result, App.Settings, isOverWrite, token);
                            return TranslateTask;
                        }));
                    }

                    TranslateFlag = false;
                    
                    // 完整句子翻译后的短暂延迟，提供更好的视觉体验
                    if (isSentenceComplete && hasUnprocessedSentence)
                    {
                        hasUnprocessedSentence = false;
                        await Task.Delay(150); // 完整句子之后短暂暂停以提供更好的阅读体验
                    }
                }
                
                await Task.Delay(25); // 保持适当的循环间隔
            }
        }

        public static string GetCaptions(AutomationElement window)
        {
            var captionsTextBlock = LiveCaptionsHandler.FindElementByAId(window, "CaptionsTextBlock");
            if (captionsTextBlock == null)
                return string.Empty;
            return captionsTextBlock.Current.Name;
        }

        private static string ShortenDisplaySentence(string displaySentence, int maxByteLength)
        {
            while (Encoding.UTF8.GetByteCount(displaySentence) >= maxByteLength)
            {
                int commaIndex = displaySentence.IndexOfAny(PUNC_COMMA);
                if (commaIndex < 0 || commaIndex + 1 >= displaySentence.Length)
                    break;
                displaySentence = displaySentence.Substring(commaIndex + 1);
            }
            return displaySentence;
        }

        private static string ReplaceNewlines(string text, int byteThreshold)
        {
            string[] splits = text.Split('\n');
            for (int i = 0; i < splits.Length; i++)
            {
                splits[i] = splits[i].Trim();
                if (i == splits.Length - 1)
                    continue;

                char lastChar = splits[i][^1];
                bool isCJ = (lastChar >= '\u4E00' && lastChar <= '\u9FFF') ||
                            (lastChar >= '\u3400' && lastChar <= '\u4DBF') ||
                            (lastChar >= '\u3040' && lastChar <= '\u30FF');
                bool isKorean = (lastChar >= '\uAC00' && lastChar <= '\uD7AF');

                if (Encoding.UTF8.GetByteCount(splits[i]) >= byteThreshold)
                    splits[i] += isCJ && !isKorean ? "。" : ". ";
                else
                    splits[i] += isCJ && !isKorean ? "——" : "—";
            }
            return string.Join("", splits);
        }
    }
}