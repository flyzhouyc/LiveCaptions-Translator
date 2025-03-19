using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public static class Translator
    {
        private static AutomationElement? window = null;
        private static Caption? caption = null;
        private static Setting? setting = null;
        private static readonly Queue<string> pendingTextQueue = new();
        
        // 优化上下文管理 - 使用循环缓冲区而不是普通队列
        private static readonly CircularBuffer<string> contextHistory = new CircularBuffer<string>(5);
        
        // 缓存转换后的上下文字符串，避免重复构建
        private static string? cachedContextString = null;
        private static int cachedContextVersion = 0;
        private static int currentContextVersion = 0;
        
        private static readonly Dictionary<string, int> apiQualityScores = new();
        private static string lastRecommendedApi = string.Empty;
        private static readonly Random random = new Random();
        
        // 性能优化 - 预编译正则表达式
        private static readonly Regex rxAcronymFix = new Regex(@"([A-Z])\s*\.\s*([A-Z])(?![A-Za-z]+)", RegexOptions.Compiled);
        private static readonly Regex rxAcronymFix2 = new Regex(@"([A-Z])\s*\.\s*([A-Z])(?=[A-Za-z]+)", RegexOptions.Compiled);
        private static readonly Regex rxPunctuationFix = new Regex(@"\s*([.!?,])\s*", RegexOptions.Compiled);
        private static readonly Regex rxAsianPunctuationFix = new Regex(@"\s*([。！？，、])\s*", RegexOptions.Compiled);
        
        // 识别内容类型的正则表达式
        private static readonly Regex rxTechnicalContent = new Regex(@"(function|class|method|API|algorithm|code|software|hardware|\bSQL\b|\bJSON\b|\bHTML\b|\bCSS\b|\bAPI\b|\bC\+\+\b|\bJava\b|\bPython\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex rxConversationalContent = new Regex(@"(\bhey\b|\bhi\b|\bhello\b|\bwhat's up\b|\bhow are you\b|\bnice to meet\b|\btalk to you|\bchit chat\b|\bbye\b|\bsee you\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex rxConferenceContent = new Regex(@"(\bpresent\b|\bconference\b|\bmeeting\b|\bstatement\b|\bannounce\b|\binvestor\b|\bstakeholder\b|\bcolleagues\b|\banalyst\b|\breport\b|\bresearch\b|\bprofessor\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex rxNewsContent = new Regex(@"(\breport\b|\bnews\b|\bheadline\b|\btoday\b|\bbreaking\b|\banalysis\b|\bstudy finds\b|\baccording to\b|\binvestigation\b|\bofficial\b|\bstatement\b|\bpress\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        
        // 上下文重要词检测
        private static readonly HashSet<string> contextKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "this", "that", "these", "those", "it", "they", "he", "she", "him", "her", "his", "hers", "their", "them",
            "the", "a", "an", "and", "but", "or", "so", "because", "if", "when", "where", "how", "why", "what", "who",
            "which", "whose", "我", "你", "他", "她", "它", "我们", "你们", "他们", "她们", "这", "那", "这些", "那些", "因为", "所以"
        };

        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }
        public static Caption? Caption => caption;
        public static Setting? Setting => setting;
        public static bool LogOnlyFlag { get; set; } = false;
        
        public static event Action? TranslationLogged;

        static Translator()
        {
            window = LiveCaptionsHandler.LaunchLiveCaptions();
            LiveCaptionsHandler.FixLiveCaptions(Window);
            LiveCaptionsHandler.HideLiveCaptions(Window);
            
            caption = Caption.GetInstance();
            setting = Setting.Load();
        }

        public static void SyncLoop()
        {
            int idleCount = 0;
            int syncCount = 0;
            
            // 性能优化 - 重用StringBuilder以减少内存分配
            StringBuilder textProcessor = new StringBuilder(1024);

            while (true)
            {
                if (Window == null)
                {
                    Thread.Sleep(2000);
                    continue;
                }

                string fullText = string.Empty;
                try
                {
                    // Check LiveCaptions.exe still alive
                    var info = Window.Current;
                    var name = info.Name;
                    // Get the text recognized by LiveCaptions (10-20ms)
                    fullText = LiveCaptionsHandler.GetCaptions(Window);     
                }
                catch (ElementNotAvailableException)
                {
                    Window = null;
                    continue;
                }
                if (string.IsNullOrEmpty(fullText))
                    continue;

                // 性能优化 - 使用StringBuilder进行文本处理，减少字符串分配
                textProcessor.Clear();
                textProcessor.Append(fullText);
                
                // Note: For certain languages (such as Japanese), LiveCaptions excessively uses `\n`.
                // Preprocess - remove the `.` between two uppercase letters. (Cope with acronym)
                string processedText = rxAcronymFix.Replace(fullText, "$1$2");
                processedText = rxAcronymFix2.Replace(processedText, "$1 $2");
                
                // Preprocess - Remove redundant `\n` around punctuation.
                processedText = rxPunctuationFix.Replace(processedText, "$1 ");
                processedText = rxAsianPunctuationFix.Replace(processedText, "$1");
                
                // Preprocess - Replace redundant `\n` within sentences with comma or period.
                processedText = TextUtil.ReplaceNewlines(processedText, TextUtil.MEDIUM_THRESHOLD);
                
                // 性能优化 - 内容类型检测和提示词模板选择
                DetectContentTypeAndUpdatePrompt(processedText);
                
                // Prevent adding the last sentence from previous running to log cards
                // before the first sentence is completed.
                if (processedText.IndexOfAny(TextUtil.PUNC_EOS) == -1 && Caption.LogCards.Count > 0)
                {
                    Caption.LogCards.Clear();
                    Caption.OnPropertyChanged("DisplayLogCards");
                }

                // Get the last sentence.
                int lastEOSIndex;
                if (Array.IndexOf(TextUtil.PUNC_EOS, processedText[^1]) != -1)
                    lastEOSIndex = processedText[0..^1].LastIndexOfAny(TextUtil.PUNC_EOS);
                else
                    lastEOSIndex = processedText.LastIndexOfAny(TextUtil.PUNC_EOS);
                string latestCaption = processedText.Substring(lastEOSIndex + 1);
                
                // If the last sentence is too short, extend it by adding the previous sentence.
                // Note: LiveCaptions may generate multiple characters including EOS at once.
                if (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < TextUtil.SHORT_THRESHOLD)
                {
                    lastEOSIndex = processedText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                    latestCaption = processedText.Substring(lastEOSIndex + 1);
                }
                
                // `OverlayOriginalCaption`: The sentence to be displayed on Overlay Window.
                Caption.OverlayOriginalCaption = latestCaption;
                for (int historyCount = Math.Min(Setting.OverlayWindow.HistoryMax, Caption.LogCards.Count);
                     historyCount > 0 && lastEOSIndex > 0; 
                     historyCount--)
                {
                    lastEOSIndex = processedText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                    Caption.OverlayOriginalCaption = processedText.Substring(lastEOSIndex + 1);
                }

                // `DisplayOriginalCaption`: The sentence to be displayed on Main Window.
                if (Caption.DisplayOriginalCaption.CompareTo(latestCaption) != 0)
                {
                    Caption.DisplayOriginalCaption = latestCaption;
                    // If the last sentence is too long, truncate it when displayed.
                    Caption.DisplayOriginalCaption = 
                        TextUtil.ShortenDisplaySentence(Caption.DisplayOriginalCaption, TextUtil.VERYLONG_THRESHOLD);
                }

                // Prepare for `OriginalCaption`. If Expanded, only retain the complete sentence.
                int lastEOS = latestCaption.LastIndexOfAny(TextUtil.PUNC_EOS);
                if (lastEOS != -1)
                    latestCaption = latestCaption.Substring(0, lastEOS + 1);
                // `OriginalCaption`: The sentence to be really translated.
                if (Caption.OriginalCaption.CompareTo(latestCaption) != 0)
                {
                    Caption.OriginalCaption = latestCaption;
                    
                    idleCount = 0;
                    if (Array.IndexOf(TextUtil.PUNC_EOS, Caption.OriginalCaption[^1]) != -1)
                    {
                        syncCount = 0;
                        pendingTextQueue.Enqueue(Caption.OriginalCaption);
                    }
                    else if (Encoding.UTF8.GetByteCount(Caption.OriginalCaption) >= TextUtil.SHORT_THRESHOLD)
                        syncCount++;
                }
                else
                    idleCount++;

                // `TranslateFlag` determines whether this sentence should be translated.
                // When `OriginalCaption` remains unchanged, `idleCount` +1; when `OriginalCaption` changes, `MaxSyncInterval` +1.
                if (syncCount > Setting.MaxSyncInterval ||
                    idleCount == Setting.MaxIdleInterval)
                {
                    syncCount = 0;
                    pendingTextQueue.Enqueue(Caption.OriginalCaption);
                }
                Thread.Sleep(25);
            }
        }

        // 性能优化 - 检测内容类型并更新提示词模板
        private static void DetectContentTypeAndUpdatePrompt(string text)
        {
            // 仅当使用LLM类API时才进行内容类型检测
            if (!IsLLMBasedAPI(Setting.ApiName))
                return;
                
            PromptTemplate detectedTemplate = PromptTemplate.General;
            
            // 技术内容检测
            if (rxTechnicalContent.IsMatch(text))
            {
                detectedTemplate = PromptTemplate.Technical;
            }
            // 会议/演讲内容检测
            else if (rxConferenceContent.IsMatch(text))
            {
                detectedTemplate = PromptTemplate.Conference;
            }
            // 新闻内容检测
            else if (rxNewsContent.IsMatch(text))
            {
                detectedTemplate = PromptTemplate.Media;
            }
            // 口语对话内容检测
            else if (rxConversationalContent.IsMatch(text))
            {
                detectedTemplate = PromptTemplate.Conversation;
            }
            
            // 如果内容类型与当前模板不同，更新模板
            if (detectedTemplate != Setting.PromptTemplate)
            {
                Setting.PromptTemplate = detectedTemplate;
            }
        }

        public static async Task TranslateLoop()
        {
            var translationTaskQueue = new TranslationTaskQueue();

            while (true)
            {
                if (Window == null)
                {
                    Caption.DisplayTranslatedCaption = "[WARNING] LiveCaptions was unexpectedly closed, restarting...";
                    Window = LiveCaptionsHandler.LaunchLiveCaptions();
                    Caption.DisplayTranslatedCaption = "";
                }

                if (pendingTextQueue.Count > 0)
                {
                    var originalSnapshot = pendingTextQueue.Dequeue();

                    if (LogOnlyFlag)
                    {
                        bool isOverwrite = await IsOverwrite(originalSnapshot);
                        await LogOnly(originalSnapshot, isOverwrite);
                    }
                    else
                    {
                        // 更新上下文历史
                        UpdateContextHistory(originalSnapshot);

                        // 确定使用哪个API - 智能选择或尝试建议的API
                        string apiToUse = DetermineApiToUse();

                        translationTaskQueue.Enqueue(token => Task.Run(
                            () => TranslateWithContext(originalSnapshot, apiToUse, token), token), originalSnapshot);
                    }

                    if (LogOnlyFlag)
                    {
                        Caption.TranslatedCaption = string.Empty;
                        Caption.DisplayTranslatedCaption = "[Paused]";
                        Caption.OverlayTranslatedCaption = "[Paused]";
                    }
                    else if (!string.IsNullOrEmpty(translationTaskQueue.Output))
                    {
                        Caption.TranslatedCaption = translationTaskQueue.Output;
                        Caption.DisplayTranslatedCaption = 
                            TextUtil.ShortenDisplaySentence(Caption.TranslatedCaption, TextUtil.VERYLONG_THRESHOLD);
                        
                        var match = Regex.Match(Caption.TranslatedCaption, @"^(\[.+\] )?(.*)$");
                        string noticePrefix = match.Groups[1].Value;
                        string translatedText = match.Groups[2].Value;
                        Caption.OverlayTranslatedCaption = noticePrefix + Caption.OverlayTranslatedPrefix + translatedText;
                    }

                    // If the original sentence is a complete sentence, pause for better visual experience.
                    if (Array.IndexOf(TextUtil.PUNC_EOS, originalSnapshot[^1]) != -1)
                        Thread.Sleep(600);
                }
                Thread.Sleep(40);
            }
        }

        // 性能优化 - 更高效的上下文管理
        private static void UpdateContextHistory(string sentence)
        {
            if (!string.IsNullOrWhiteSpace(sentence))
            {
                // 使用循环缓冲区，避免队列操作和内存分配
                contextHistory.Add(sentence);
                currentContextVersion++; // 增加上下文版本号，使缓存失效
            }
        }

        // 确定当前应该使用哪个API
        private static string DetermineApiToUse()
        {
            string currentApi = Translator.Setting.ApiName;
            
            // 有推荐API且质量评分高于当前API时，有20%概率尝试推荐的API
            if (!string.IsNullOrEmpty(lastRecommendedApi) && 
                lastRecommendedApi != currentApi && 
                apiQualityScores.TryGetValue(lastRecommendedApi, out int recommendedScore) &&
                (!apiQualityScores.TryGetValue(currentApi, out int currentScore) || recommendedScore > currentScore) &&
                random.Next(100) < 20)
            {
                return lastRecommendedApi;
            }
            
            // 否则使用用户设置的API
            return currentApi;
        }

        // 性能优化 - 优化上下文构建逻辑
        public static async Task<string> TranslateWithContext(string text, string apiName, CancellationToken token = default)
        {
            try
            {
                var sw = Setting.MainWindow.LatencyShow ? Stopwatch.StartNew() : null;
                
                string translatedText;
                string sourceLanguage = "auto";
                string targetLanguage = Setting.TargetLanguage;
                
                // 构建上下文文本 (仅对LLM类API)
                if (IsLLMBasedAPI(apiName) && contextHistory.Count > 1)
                {
                    // 为LLM创建带上下文的提示词，使用缓存避免重复构建
                    string contextPrompt = GetCachedContextPrompt(text, apiName);
                    translatedText = await TranslateAPI.TranslateWithAPI(contextPrompt, apiName, token);
                }
                else
                {
                    // 对传统API使用普通翻译方法
                    translatedText = await TranslateAPI.TranslateWithAPI(text, apiName, token);
                }
                
                if (sw != null)
                {
                    sw.Stop();
                    translatedText = $"[{sw.ElapsedMilliseconds} ms] " + translatedText;
                }

                // 评估翻译质量 - 使用轻量级评估模式减少CPU使用
                int qualityScore = TranslationQualityEvaluator.EvaluateQualityLightweight(text, translatedText);
                UpdateApiQualityScore(apiName, qualityScore);
                
                // 仅对低质量翻译尝试改进
                if (qualityScore < 70)
                {
                    var (improvedTranslation, apiSuggestion) = TranslationQualityEvaluator.GetImprovedTranslation(
                        translatedText, text, apiName, qualityScore);
                    
                    if (improvedTranslation != translatedText)
                    {
                        translatedText = improvedTranslation;
                    }
                    
                    if (apiSuggestion != apiName)
                    {
                        // 记录API建议，但不立即切换，让用户或之后的翻译决定是否采用
                        lastRecommendedApi = apiSuggestion;
                    }
                }
                
                return translatedText;
            }
            catch (OperationCanceledException)
            {
                return string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Translation failed: {ex.Message}");
                return $"[Translation Failed] {ex.Message}";
            }
        }
        
        // 性能优化 - 缓存上下文提示词
        private static string GetCachedContextPrompt(string text, string apiName)
        {
            // 如果上下文版本与缓存版本相同，且缓存存在，直接使用缓存
            if (currentContextVersion == cachedContextVersion && cachedContextString != null)
            {
                // 只需要更新当前文本
                return UpdateCurrentTextInContext(cachedContextString, text);
            }
            
            // 构建新的上下文提示
            StringBuilder contextBuilder = new StringBuilder(1024);
            
            // 智能判断是否需要加入上下文
            bool needsContext = ContextIsRelevant(text);
            
            // 仅使用最近的2-3句话作为上下文，且只有在需要上下文时
            int contextSentencesToUse = needsContext ? Math.Min(contextHistory.Count - 1, 2) : 0;
            if (contextSentencesToUse > 0)
            {
                contextBuilder.AppendLine("Previous sentences (context):");
                
                var contextItems = contextHistory.GetItems().Take(contextSentencesToUse).ToArray();
                for (int i = 0; i < contextItems.Length; i++)
                {
                    // 跳过当前文本，避免重复
                    if (string.IsNullOrEmpty(contextItems[i]) || text.Contains(contextItems[i]))
                        continue;
                        
                    contextBuilder.AppendLine($"- {contextItems[i]}");
                }
                
                contextBuilder.AppendLine("\nCurrent sentence to translate:");
            }
            
            contextBuilder.Append("🔤 ").Append(text).Append(" 🔤");
            
            // 更新缓存
            cachedContextString = contextBuilder.ToString();
            cachedContextVersion = currentContextVersion;
            
            return cachedContextString;
        }
        
        // 性能优化 - 仅更新上下文中的当前文本部分
        private static string UpdateCurrentTextInContext(string cachedContext, string currentText)
        {
            // 找到当前翻译文本标记的位置
            int startMarkerPos = cachedContext.LastIndexOf("🔤 ");
            if (startMarkerPos == -1) return cachedContext;
            
            int endMarkerPos = cachedContext.LastIndexOf(" 🔤");
            if (endMarkerPos == -1) return cachedContext;
            
            // 替换当前文本
            return cachedContext.Substring(0, startMarkerPos + 2) + 
                   currentText + 
                   cachedContext.Substring(endMarkerPos);
        }
        
        // 性能优化 - 智能判断是否需要为文本提供上下文
        private static bool ContextIsRelevant(string text)
        {
            // 检查文本中是否含有上下文相关的词汇
            string[] words = text.Split(new char[] { ' ', ',', '.', '?', '!', '，', '。', '？', '！' }, 
                StringSplitOptions.RemoveEmptyEntries);
                
            foreach (string word in words)
            {
                if (contextKeywords.Contains(word))
                {
                    return true;
                }
            }
            
            // 检查是否有代词起始的句子
            return Regex.IsMatch(text, @"^\s*(This|That|These|Those|It|They|He|She|I|We|You|The|A|An)\b", 
                RegexOptions.IgnoreCase);
        }

        // 判断是否为基于LLM的API
        private static bool IsLLMBasedAPI(string apiName)
        {
            return apiName == "OpenAI" || apiName == "Ollama" || apiName == "OpenRouter";
        }

        // 更新API质量评分
        private static void UpdateApiQualityScore(string apiName, int qualityScore)
        {
            if (!apiQualityScores.ContainsKey(apiName))
            {
                apiQualityScores[apiName] = qualityScore;
            }
            else
            {
                // 使用加权平均值更新评分，新分数权重为0.2
                apiQualityScores[apiName] = (int)(apiQualityScores[apiName] * 0.8 + qualityScore * 0.2);
            }

            TranslationQualityEvaluator.RecordQualityForAPI(
                apiName, qualityScore, "auto", Setting.TargetLanguage);
        }

        public static async Task<string> Translate(string text, CancellationToken token = default)
        {
            return await TranslateWithContext(text, Setting.ApiName, token);
        }

        public static async Task Log(string originalText, string translatedText, 
            bool isOverwrite = false, CancellationToken token = default)
        {
            string targetLanguage, apiName;
            if (Setting != null)
            {
                targetLanguage = Setting.TargetLanguage;
                apiName = Setting.ApiName;
            }
            else
            {
                targetLanguage = "N/A";
                apiName = "N/A";
            }

            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, translatedText, targetLanguage, apiName);
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Logging history failed: {ex.Message}");
            }
        }

        public static async Task LogOnly(string originalText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, "N/A", "N/A", "LogOnly");
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Logging history failed: {ex.Message}");
            }
        }
        
        public static async Task AddLogCard(CancellationToken token = default)
        {
            var lastLog = await SQLiteHistoryLogger.LoadLastTranslation(token);
            if (lastLog == null)
                return;
            if (Caption?.LogCards.Count >= Setting?.MainWindow.CaptionLogMax)
                Caption.LogCards.Dequeue();
            Caption?.LogCards.Enqueue(lastLog);
            Caption?.OnPropertyChanged("DisplayLogCards");
        }
        
        public static async Task<bool> IsOverwrite(string originalText, CancellationToken token = default)
        {
            // If this text is too similar to the last one, rewrite it when logging.
            string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(token);
            if (lastOriginalText == null)
                return false;
            double similarity = TextUtil.Similarity(originalText, lastOriginalText);
            return similarity > 0.66;
        }
    }
    
    // 性能优化 - 实现高效的循环缓冲区，减少内存分配
    public class CircularBuffer<T>
    {
        private readonly T[] _buffer;
        private int _start;
        private int _count;
        
        public int Count => _count;
        public int Capacity => _buffer.Length;
        
        public CircularBuffer(int capacity)
        {
            _buffer = new T[capacity];
            _start = 0;
            _count = 0;
        }
        
        public void Add(T item)
        {
            if (_count == _buffer.Length)
            {
                // 缓冲区已满，覆盖最早的项
                _buffer[_start] = item;
                _start = (_start + 1) % _buffer.Length;
            }
            else
            {
                // 缓冲区未满，添加到末尾
                _buffer[(_start + _count) % _buffer.Length] = item;
                _count++;
            }
        }
        
        public IEnumerable<T> GetItems()
        {
            for (int i = 0; i < _count; i++)
            {
                yield return _buffer[(_start + i) % _buffer.Length];
            }
        }
        
        public T GetItem(int index)
        {
            if (index < 0 || index >= _count)
                throw new IndexOutOfRangeException();
                
            return _buffer[(_start + index) % _buffer.Length];
        }
    }
}