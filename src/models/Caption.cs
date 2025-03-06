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
        
        // 句子缓冲区集合
        private List<string> sentenceBuffer = new List<string>();
        // 上次批量翻译的时间戳
        private DateTime lastBatchTranslationTime = DateTime.Now;
        
        // 新增：上次句子变化的时间
        private DateTime lastSentenceChangeTime = DateTime.Now;
        // 新增：最小时间稳定阈值(毫秒)
        private const int MIN_TIME_STABILITY = 300;
        // 新增：不完整句子的最小时间稳定阈值(毫秒)
        private const int MIN_INCOMPLETE_STABILITY = 1000;

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
                bool sentenceChanged = false;
                if (currentSentence != currentSentenceBuilder)
                {
                    // 句子有变化
                    sentenceChanged = true;
                    
                    // 更新上次句子变化时间
                    lastSentenceChangeTime = DateTime.Now;
                    
                    // 不完全重置稳定性计数器，而是减少一部分
                    stabilityCounter = Math.Max(0, stabilityCounter - 2);
                    currentSentenceBuilder = currentSentence;
                    
                    // 判断句子是否完整
                    isSentenceComplete = IsSentenceComplete(currentSentence);
                }
                else
                {
                    // 句子保持不变，增加稳定性计数
                    stabilityCounter++;
                }

                // 基于时间的稳定性判断
                TimeSpan timeSinceLastChange = DateTime.Now - lastSentenceChangeTime;
                
                // 触发翻译的简化逻辑
                // 条件1: 如果是完整句子且不是上次翻译的句子，并且满足稳定性条件
                if (isSentenceComplete && currentSentence != lastCompleteSentence && 
                    (stabilityCounter >= App.Settings?.MinStabilityCount || 
                     timeSinceLastChange.TotalMilliseconds >= MIN_TIME_STABILITY))
                {
                    lastCompleteSentence = currentSentence;
                    AddSentenceToBuffer(currentSentence);
                    hasUnprocessedSentence = true;
                    stabilityCounter = 0;
                    CheckAndTriggerBatchTranslation();
                }
                // 条件2: 如果是不完整句子，但已经稳定足够长的时间或稳定计数足够高，且长度足够
                else if (!isSentenceComplete && 
                        (stabilityCounter >= App.Settings.MaxSyncInterval || 
                         timeSinceLastChange.TotalMilliseconds >= MIN_INCOMPLETE_STABILITY) && 
                        Encoding.UTF8.GetByteCount(currentSentence) >= 15)
                {
                    AddSentenceToBuffer(currentSentence);
                    stabilityCounter = 0;
                    CheckAndTriggerBatchTranslation();
                }
                
                // 基于闲置时间的触发条件
                idleCount++;
                // 如果闲置时间过长，也触发翻译
                if (idleCount >= App.Settings.MaxIdleInterval && !string.IsNullOrEmpty(currentSentence) && sentenceBuffer.Count > 0)
                {
                    if (!sentenceBuffer.Contains(currentSentence))
                    {
                        AddSentenceToBuffer(currentSentence);
                    }
                    TriggerBatchTranslation();
                    idleCount = 0;
                }
                
                // 基于绝对时间的触发条件
                TimeSpan timeSinceLastBatch = DateTime.Now - lastBatchTranslationTime;
                if (timeSinceLastBatch.TotalMilliseconds > App.Settings.BatchTranslationInterval && sentenceBuffer.Count > 0)
                {
                    TriggerBatchTranslation();
                }

                Thread.Sleep(15);
            }
        }
        
        // 将句子添加到缓冲区
        private void AddSentenceToBuffer(string sentence)
        {
            // 避免重复添加相同句子
            if (!sentenceBuffer.Contains(sentence))
            {
                sentenceBuffer.Add(sentence);
            }
        }
        
        // 检查是否应该触发批量翻译
        private void CheckAndTriggerBatchTranslation()
        {
            // 如果缓冲区满了，触发批量翻译
            if (sentenceBuffer.Count >= App.Settings.MaxBufferSize)
            {
                TriggerBatchTranslation();
            }
        }
        
        // 触发批量翻译
        private void TriggerBatchTranslation()
        {
            if (sentenceBuffer.Count == 0)
                return;
                
            // 合并缓冲区中的所有句子
            string combinedText = string.Join(" ", sentenceBuffer);
            
            // 设置翻译文本并触发翻译
            OriginalCaption = combinedText;
            TranslateFlag = true;
            
            // 更新上次批量翻译时间
            lastBatchTranslationTime = DateTime.Now;
            
            // 清空缓冲区
            sentenceBuffer.Clear();
            
            // 完整句子翻译后的短暂延迟，提供更好的视觉体验
            if (hasUnprocessedSentence)
            {
                hasUnprocessedSentence = false;
                Thread.Sleep(150); // 完整句子之后短暂暂停以提供更好的阅读体验
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