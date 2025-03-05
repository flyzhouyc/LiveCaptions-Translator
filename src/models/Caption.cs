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

        private string displayOriginalCaption = "";
        private string displayTranslatedCaption = "";

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

        private Caption() { }

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

        public void Sync()
        {
            int idleCount = 0;
            int syncCount = 0;

            while (true)
            {
                if (App.Window == null)
                {
                    Thread.Sleep(2000);
                    continue;
                }

                // Get the text recognized by LiveCaptions.
                string fullText = string.Empty;
                try
                {
                    fullText = GetCaptions(App.Window);         // about 10-20ms
                }
                catch (ElementNotAvailableException ex)
                {
                    App.Window = null;
                    continue;
                }
                if (string.IsNullOrEmpty(fullText))
                    continue;

                // Note: For certain languages (such as Japanese), LiveCaptions excessively uses `\n`.
                // Preprocess - remove the `.` between 2 uppercase letters.
                fullText = Regex.Replace(fullText, @"(?<=[A-Z])\s*\.\s*(?=[A-Z])", "");
                // Preprocess - Remove redundant `\n` around punctuation.
                fullText = Regex.Replace(fullText, @"\s*([.!?,])\s*", "$1 ");
                fullText = Regex.Replace(fullText, @"\s*([。！？，、])\s*", "$1");
                // Preprocess - Replace redundant `\n` within sentences with comma or period.
                fullText = ReplaceNewlines(fullText, 32);

                // Get the last sentence.
                int lastEOSIndex;
                if (Array.IndexOf(PUNC_EOS, fullText[^1]) != -1)
                    lastEOSIndex = fullText[0..^1].LastIndexOfAny(PUNC_EOS);
                else
                    lastEOSIndex = fullText.LastIndexOfAny(PUNC_EOS);
                string latestCaption = fullText.Substring(lastEOSIndex + 1);

                // DisplayOriginalCaption: The sentence to be displayed to the user.
                if (DisplayOriginalCaption.CompareTo(latestCaption) != 0)
                {
                    DisplayOriginalCaption = latestCaption;
                    // If the last sentence is too short, extend it by adding the previous sentence when displayed.
                    if (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < 12)
                    {
                        lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(PUNC_EOS);
                        DisplayOriginalCaption = fullText.Substring(lastEOSIndex + 1);
                    }
                    // If the last sentence is too long, truncate it when displayed.
                    DisplayOriginalCaption = ShortenDisplaySentence(DisplayOriginalCaption, 160);
                }

                // OriginalCaption: The sentence to be really translated.
                if (OriginalCaption.CompareTo(latestCaption) != 0)
                {
                    OriginalCaption = latestCaption;

                    idleCount = 0;
                    if (Encoding.UTF8.GetByteCount(latestCaption) >= 10)
                        syncCount++;
                    if (Array.IndexOf(PUNC_EOS, OriginalCaption[^1]) != -1)
                    {
                        syncCount = 0;
                        TranslateFlag = true;
                    }
                }
                else
                    idleCount++;

                // `TranslateFlag` determines whether this sentence should be translated.
                // When `OriginalCaption` remains unchanged, `idleCount` +1; when `OriginalCaption` changes, `MaxSyncInterval` +1.
                if (syncCount > App.Settings.MaxSyncInterval ||
                    idleCount == App.Settings.MaxIdleInterval)
                {
                    syncCount = 0;
                    TranslateFlag = true;
                }
                Thread.Sleep(25);
            }
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

            // If the old sentence is the prefix of the new sentence,
            // overwrite the previous entry when logging.
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
            // 减少完整句子的等待时间，从600ms降低到300ms，保持一定的视觉体验
            if (Array.IndexOf(PUNC_EOS, originalSnapshot[^1]) != -1)
                Thread.Sleep(200); // 原来是600ms
        }
        
        // 减少循环间隔，更快地响应新的字幕变化
        Thread.Sleep(25); // 原来是40ms
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

        private string displayOriginalCaption = "";
        private string displayTranslatedCaption = "";

        // 存储正在构建的句子
        private string currentSentenceBuilder = "";
        // 上次翻译的完整句子
        private string lastCompleteSentence = "";
        // 句子是否已完成的标志
        private bool isSentenceComplete = false;
        // 是否存在未翻译的完整句子
        private bool hasUnprocessedSentence = false;
        // 稳定性计数器(句子保持不变的次数)
        private int stabilityCounter = 0;
        // 句子需要保持稳定的最小计数
        private const int MIN_STABILITY_COUNT = 3;

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

        private Caption() { }

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

        public void Sync()
        {
            int idleCount = 0;

            while (true)
            {
                if (App.Window == null)
                {
                    Thread.Sleep(2000);
                    continue;
                }

                // 获取 LiveCaptions 识别的文本
                string fullText = string.Empty;
                try
                {
                    fullText = GetCaptions(App.Window);
                }
                catch (ElementNotAvailableException ex)
                {
                    App.Window = null;
                    continue;
                }
                if (string.IsNullOrEmpty(fullText))
                    continue;

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
                    if (stabilityCounter >= MIN_STABILITY_COUNT)
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

                // 如果闲置时间过长，也触发翻译
                idleCount++;
                if (idleCount >= App.Settings.MaxIdleInterval && !string.IsNullOrEmpty(currentSentence))
                {
                    OriginalCaption = currentSentence;
                    TranslateFlag = true;
                    idleCount = 0;
                }

                Thread.Sleep(25);
            }
        }

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
                        Thread.Sleep(150); // 完整句子之后短暂暂停以提供更好的阅读体验
                    }
                }
                
                Thread.Sleep(25);
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