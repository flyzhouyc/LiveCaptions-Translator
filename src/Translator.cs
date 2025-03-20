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

        // 识别内容类型的正则表达式 - 增强版本
        private static readonly Regex rxTechnicalContent = new Regex(@"(function|class|method|API|algorithm|code|software|hardware|\bSQL\b|\bJSON\b|\bHTML\b|\bCSS\b|\bAPI\b|\bC\+\+\b|\bJava\b|\bPython\b|\bserver\b|\bdatabase\b|\bquery\b|\bframework\b|\blibrary\b|\bcomponent\b|\binterface\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex rxConversationalContent = new Regex(@"(\bhey\b|\bhi\b|\bhello\b|\bwhat's up\b|\bhow are you\b|\bnice to meet\b|\btalk to you|\bchit chat\b|\bbye\b|\bsee you\b|\bthanks\b|\bthank you\b|\bexcuse me\b|\bsorry\b|\bplease\b|\bby the way\b|\bwell\b|\bactually\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex rxConferenceContent = new Regex(@"(\bpresent\b|\bconference\b|\bmeeting\b|\bstatement\b|\bannounce\b|\binvestor\b|\bstakeholder\b|\bcolleagues\b|\banalyst\b|\breport\b|\bresearch\b|\bprofessor\b|\bagenda\b|\bminutes\b|\bslide\b|\bchart\b|\bgraph\b|\bquarterly\b|\bstrategic\b|\bcommittee\b|\bboard\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex rxNewsContent = new Regex(@"(\breport\b|\bnews\b|\bheadline\b|\btoday\b|\bbreaking\b|\banalysis\b|\bstudy finds\b|\baccording to\b|\binvestigation\b|\bofficial\b|\bstatement\b|\bpress\b|\breported\b|\bannounced\b|\breleased\b|\bpublished\b|\bstated\b|\bconfirmed\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 其他有用的正则表达式
        private static readonly Regex rxNumbersMatch = new Regex(@"\d+", RegexOptions.Compiled);
        private static readonly Regex rxUrlPattern = new Regex(@"https?://[^\s]+", RegexOptions.Compiled);
        
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
            int failureCount = 0; // 记录连续失败次数
            
            // 性能优化 - 重用StringBuilder以减少内存分配
            StringBuilder textProcessor = new StringBuilder(1024);

            while (true)
            {
                try
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
                        
                        // 重置失败计数
                        failureCount = 0;     
                    }
                    catch (ElementNotAvailableException)
                    {
                        Window = null;
                        continue;
                    }
                    catch (Exception ex)
                    {
                        // 记录异常但继续尝试
                        Console.WriteLine($"获取字幕时发生错误: {ex.Message}");
                        
                        // 增加失败计数并检查是否超过阈值
                        failureCount++;
                        if (failureCount > 5)
                        {
                            // 尝试重启LiveCaptions
                            try
                            {
                                Window = LiveCaptionsHandler.LaunchLiveCaptions();
                                LiveCaptionsHandler.FixLiveCaptions(Window);
                            }
                            catch
                            {
                                // 忽略重启失败，下一循环会继续尝试
                            }
                            failureCount = 0;
                        }
                        
                        Thread.Sleep(500); // 出错时稍微延长等待时间
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
                catch (Exception ex)
                {
                    // 全局异常处理，确保主循环不会中断
                    Console.WriteLine($"SyncLoop发生未处理异常: {ex.Message}");
                    try
                    {
                        // 尝试恢复重要状态
                        if (Window == null)
                        {
                            Window = LiveCaptionsHandler.LaunchLiveCaptions();
                            LiveCaptionsHandler.FixLiveCaptions(Window);
                            LiveCaptionsHandler.HideLiveCaptions(Window);
                        }
                    }
                    catch
                    {
                        // 忽略恢复失败
                    }
                    Thread.Sleep(1000); // 出现未知错误时延长等待时间
                }
            }
        }

        // 性能优化 - 检测内容类型并更新提示词模板
        private static void DetectContentTypeAndUpdatePrompt(string text)
        {
            // 仅当使用LLM类API时才进行内容类型检测
            if (!IsLLMBasedAPI(Setting.ApiName))
                return;
                    
            // 如果不是自动检测模式，则不进行内容类型检测
            if (Setting.PromptTemplate != PromptTemplate.AutoDetection)
                return;
                    
            PromptTemplate detectedTemplate = PromptTemplate.General;
            
            // 创建检测计数器，用分数来决定最匹配的类型
            int technicalScore = 0;
            int conversationScore = 0;
            int conferenceScore = 0;
            int mediaScore = 0;
            
            // 技术内容检测 - 增强检测能力
            if (rxTechnicalContent.IsMatch(text))
            {
                technicalScore += 2;
            }
            
            // 添加更多技术相关词汇和模式的检测
            if (Regex.IsMatch(text, @"\b(algorithm|variable|function|method|parameter|server|database|query|API|SDK|framework|library|component|interface|implementation|architecture)\b", RegexOptions.IgnoreCase))
            {
                technicalScore += 2;
            }
            
            // 检测代码片段
            if (Regex.IsMatch(text, @"(if|else|for|while|switch|case|return|try|catch|class|interface)\s*\(") ||
                Regex.IsMatch(text, @"function\s+\w+\s*\(") ||
                Regex.IsMatch(text, @"var\s+\w+\s*=") ||
                Regex.IsMatch(text, @"const\s+\w+\s*="))
            {
                technicalScore += 3;
            }
            
            // 口语对话内容检测 - 增强
            if (rxConversationalContent.IsMatch(text))
            {
                conversationScore += 2;
            }
            
            // 添加更多对话特征检测
            if (Regex.IsMatch(text, @"\b(thanks|thank you|excuse me|sorry|please|by the way|anyway|well|actually|honestly|personally|I mean|you know)\b", RegexOptions.IgnoreCase))
            {
                conversationScore += 1;
            }
            
            // 检测问候和告别语
            if (Regex.IsMatch(text, @"^(Hi|Hello|Hey|Good morning|Good afternoon|Good evening|Bye|Goodbye|See you)", RegexOptions.IgnoreCase))
            {
                conversationScore += 2;
            }
            
            // 检测中文口语表达
            if (Regex.IsMatch(text, @"(嗨|你好|您好|早上好|下午好|晚上好|再见|拜拜|回头见)"))
            {
                conversationScore += 2;
            }
            
            // 会议/演讲内容检测 - 增强
            if (rxConferenceContent.IsMatch(text))
            {
                conferenceScore += 2;
            }
            
            // 会议特有词汇和模式
            if (Regex.IsMatch(text, @"\b(agenda|minutes|presentation|slide|chart|graph|diagram|quarterly|fiscal|strategic|objective|initiative|committee|board|chairperson|delegate|session)\b", RegexOptions.IgnoreCase))
            {
                conferenceScore += 2;
            }
            
            // 检测演讲式语言
            if (Regex.IsMatch(text, @"\b(ladies and gentlemen|thank you for|I'd like to|I am pleased to|let me introduce|in conclusion|to summarize|as you can see)\b", RegexOptions.IgnoreCase))
            {
                conferenceScore += 3;
            }
            
            // 新闻内容检测 - 增强
            if (rxNewsContent.IsMatch(text))
            {
                mediaScore += 2;
            }
            
            // 新闻特有词汇和模式
            if (Regex.IsMatch(text, @"\b(reported|announced|released|published|stated|confirmed|according to|sources say|officials|spokesman|spokesperson|breaking news|latest|update)\b", RegexOptions.IgnoreCase))
            {
                mediaScore += 2;
            }
            
            // 检测新闻标题格式
            if (Regex.IsMatch(text, @"^[A-Z][^.!?]*:|^[A-Z][^.!?]* - "))
            {
                mediaScore += 3;
            }
            
            // 句式结构检测 - 复杂正式句式可能是会议/演讲
            if (text.Length > 100 && Regex.Matches(text, @"[,;:]").Count > 3)
            {
                conferenceScore += 1;
            }
            
            // 根据最高分数确定内容类型
            int maxScore = Math.Max(Math.Max(technicalScore, conversationScore), 
                                    Math.Max(conferenceScore, mediaScore));
            
            if (maxScore == 0)
            {
                detectedTemplate = PromptTemplate.General;
            }
            else if (maxScore == technicalScore)
            {
                detectedTemplate = PromptTemplate.Technical;
            }
            else if (maxScore == conversationScore)
            {
                detectedTemplate = PromptTemplate.Conversation;
            }
            else if (maxScore == conferenceScore)
            {
                detectedTemplate = PromptTemplate.Conference;
            }
            else if (maxScore == mediaScore)
            {
                detectedTemplate = PromptTemplate.Media;
            }
            
            // 更新当前使用的模板（但不改变用户选择的PromptTemplate设置）
            Setting.UpdateCurrentPrompt(detectedTemplate);
        }

        public static async Task TranslateLoop()
        {
            var translationTaskQueue = new TranslationTaskQueue();
            int errorCount = 0;

            while (true)
            {
                try
                {
                    if (Window == null)
                    {
                        Caption.DisplayTranslatedCaption = "[警告] LiveCaptions意外关闭，正在重启...";
                        Window = LiveCaptionsHandler.LaunchLiveCaptions();
                        Caption.DisplayTranslatedCaption = "";
                    }

                    if (pendingTextQueue.Count > 0)
                    {
                        try
                        {
                            var originalSnapshot = pendingTextQueue.Dequeue();

                            try
                            {
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

                                // 重置错误计数
                                errorCount = 0;
                            }
                            catch (Exception ex)
                            {
                                // 处理翻译过程中的异常
                                Console.WriteLine($"翻译处理异常: {ex.Message}");
                                errorCount++;
                                
                                // 如果连续发生多次错误，显示错误消息给用户
                                if (errorCount > 3)
                                {
                                    Caption.DisplayTranslatedCaption = $"[翻译服务暂时不可用] {ex.Message}";
                                }
                            }

                            if (LogOnlyFlag)
                            {
                                Caption.TranslatedCaption = string.Empty;
                                Caption.DisplayTranslatedCaption = "[已暂停]";
                                Caption.OverlayTranslatedCaption = "[已暂停]";
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
                        catch (InvalidOperationException)
                        {
                            // 处理队列操作异常，例如在遍历时修改
                            Thread.Sleep(100);
                        }
                    }
                    Thread.Sleep(40);
                }
                catch (Exception ex)
                {
                    // 处理循环中的未知异常
                    Console.WriteLine($"TranslateLoop发生未处理异常: {ex.Message}");
                    try
                    {
                        // 尝试恢复翻译状态
                        if (string.IsNullOrEmpty(Caption.DisplayTranslatedCaption))
                        {
                            Caption.DisplayTranslatedCaption = "[系统恢复中...]";
                        }
                    }
                    catch
                    {
                        // 忽略恢复失败
                    }
                    Thread.Sleep(1000);
                }
            }
        }

        // 性能优化 - 更高效的上下文管理
        private static void UpdateContextHistory(string sentence)
        {
            if (string.IsNullOrWhiteSpace(sentence))
                return;
                
            // 使用循环缓冲区，避免队列操作和内存分配
            contextHistory.Add(sentence);
            currentContextVersion++; // 增加上下文版本号，使缓存失效
            
            // 优化：如果句子结尾有明确的结束标志（如句号），清理不需要的旧上下文
            // 这有助于避免不相关的旧上下文污染翻译
            if (sentence.Length > 0 && Array.IndexOf(TextUtil.PUNC_EOS, sentence[sentence.Length - 1]) != -1)
            {
                // 如果是段落结束，可以考虑清理部分历史
                if (contextHistory.Count > 3 && 
                (sentence.EndsWith(".") || sentence.EndsWith("。")) && 
                sentence.Length > 20)
                {
                    // 使用Reset方法保留最近一句作为过渡
                    string latestSentence = contextHistory.GetItem(contextHistory.Count - 1);
                    contextHistory.Reset(latestSentence);
                    currentContextVersion++; // 增加版本号使缓存失效
                }
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
            
            // 根据内容类型调整上下文句子数量
            int contextSentencesToUse = 0;
            
            if (needsContext) 
            {
                // 调整上下文长度 - 对于技术性和正式场合的内容需要更多上下文
                if (rxTechnicalContent.IsMatch(text) || rxConferenceContent.IsMatch(text))
                {
                    contextSentencesToUse = Math.Min(contextHistory.Count - 1, 3);
                }
                else
                {
                    contextSentencesToUse = Math.Min(contextHistory.Count - 1, 2);
                }
            }
            
            if (contextSentencesToUse > 0)
            {
                contextBuilder.AppendLine("Previous sentences for context (keeping the continuity of conversation):");
                
                var contextItems = contextHistory.GetItems().Take(contextSentencesToUse).ToArray();
                for (int i = 0; i < contextItems.Length; i++)
                {
                    // 跳过当前文本，避免重复
                    if (string.IsNullOrEmpty(contextItems[i]) || text.Contains(contextItems[i]))
                        continue;
                        
                    contextBuilder.AppendLine($"- {contextItems[i]}");
                }
                
                contextBuilder.AppendLine("\nCurrent sentence to translate faithfully:");
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
            // 过短的文本一般不需要上下文
            if (text.Length < 5)
                return false;

            // 检查文本中是否含有上下文相关的词汇（优化检测逻辑）
            string[] words = text.Split(new char[] { ' ', ',', '.', '?', '!', '，', '。', '？', '！' }, 
                StringSplitOptions.RemoveEmptyEntries);
                
            // 快速检查常见代词和连接词
            foreach (string word in words)
            {
                string lowerWord = word.ToLowerInvariant();
                // 更全面的代词和连接词检测
                if (contextKeywords.Contains(lowerWord))
                {
                    return true;
                }
                
                // 检查更多指示性词汇
                if (lowerWord == "then" || lowerWord == "therefore" ||
                    lowerWord == "thus" || lowerWord == "hence" ||
                    lowerWord == "consequently" || lowerWord == "so" ||
                    lowerWord == "因此" || lowerWord == "所以" ||
                    lowerWord == "那么" || lowerWord == "于是")
                {
                    return true;
                }
            }
            
            // 检查是否有代词起始的句子 (增加更多模式)
            if (Regex.IsMatch(text, @"^\s*(This|That|These|Those|It|They|He|She|I|We|You|The|Such|Their|His|Her|Its|Those)\b", 
                RegexOptions.IgnoreCase))
            {
                return true;
            }
            
            // 检查中文指示代词
            if (Regex.IsMatch(text, @"^\s*(这|那|它|他们|她们|我们|你们|其|该)\b"))
            {
                return true;
            }
            
            // 检查句子是否似乎是前一句的延续（缺少主语）
            if (Regex.IsMatch(text, @"^\s*(and|or|but|however|nevertheless|yet|still|moreover|furthermore|also)\b", 
                RegexOptions.IgnoreCase))
            {
                return true; 
            }
            
            // 如果句子非常短，也可能是上下文的一部分
            if (text.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length < 4 && 
                !Regex.IsMatch(text, @"\b(yes|no|ok|yeah|nope)\b", RegexOptions.IgnoreCase))
            {
                return true;
            }
            
            return false;
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
        
        // 新增Clear方法
        public void Clear()
        {
            _start = 0;
            _count = 0;
        }
        
        // 新增Reset方法，可以选择保留一个元素
        public void Reset(T itemToKeep = default)
        {
            Clear();
            if (itemToKeep != null && !itemToKeep.Equals(default(T)))
            {
                Add(itemToKeep);
            }
        }
    }
}