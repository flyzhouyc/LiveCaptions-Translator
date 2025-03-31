using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
        private static readonly ConcurrentQueue<string> pendingTextQueue = new ConcurrentQueue<string>();
        
        // 优化：使用高效的循环缓冲区
        private static readonly OptimizedCircularBuffer<string> contextHistory = new OptimizedCircularBuffer<string>(8);
        
        // 使用线程安全的字典缓存转换后的上下文字符串
        private static readonly ConcurrentDictionary<string, string> contextPromptCache = new ConcurrentDictionary<string, string>();
        private static int contextCacheSize = 0;
        private static readonly int MaxContextCacheSize = 20;
        
        // 使用线程安全集合
        private static readonly ConcurrentDictionary<string, int> apiQualityScores = new ConcurrentDictionary<string, int>();
        private static string lastRecommendedApi = string.Empty;
        private static readonly Random random = new Random();
        
        // 预编译正则表达式并缓存
        private static readonly Regex rxAcronymFix = new Regex(@"([A-Z])\s*\.\s*([A-Z])(?![A-Za-z]+)", 
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex rxAcronymFix2 = new Regex(@"([A-Z])\s*\.\s*([A-Z])(?=[A-Za-z]+)", 
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex rxPunctuationFix = new Regex(@"\s*([.!?,])\s*", 
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex rxAsianPunctuationFix = new Regex(@"\s*([。！？，、])\s*", 
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 识别内容类型的正则表达式 - 增强版本
        private static readonly Regex rxTechnicalContent = new Regex(
            @"(function|class|method|API|algorithm|code|software|hardware|\bSQL\b|\bJSON\b|\bHTML\b|\bCSS\b|\bAPI\b|\bC\+\+\b|\bJava\b|\bPython\b|\bserver\b|\bdatabase\b|\bquery\b|\bframework\b|\blibrary\b|\bcomponent\b|\binterface\b)", 
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex rxConversationalContent = new Regex(
            @"(\bhey\b|\bhi\b|\bhello\b|\bwhat's up\b|\bhow are you\b|\bnice to meet\b|\btalk to you|\bchit chat\b|\bbye\b|\bsee you\b|\bthanks\b|\bthank you\b|\bexcuse me\b|\bsorry\b|\bplease\b|\bby the way\b|\bwell\b|\bactually\b)", 
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex rxConferenceContent = new Regex(
            @"(\bpresent\b|\bconference\b|\bmeeting\b|\bstatement\b|\bannounce\b|\binvestor\b|\bstakeholder\b|\bcolleagues\b|\banalyst\b|\breport\b|\bresearch\b|\bprofessor\b|\bagenda\b|\bminutes\b|\bslide\b|\bchart\b|\bgraph\b|\bquarterly\b|\bstrategic\b|\bcommittee\b|\bboard\b)", 
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex rxNewsContent = new Regex(
            @"(\breport\b|\bnews\b|\bheadline\b|\btoday\b|\bbreaking\b|\banalysis\b|\bstudy finds\b|\baccording to\b|\binvestigation\b|\bofficial\b|\bstatement\b|\bpress\b|\breported\b|\bannounced\b|\breleased\b|\bpublished\b|\bstated\b|\bconfirmed\b)", 
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 其他有用的正则表达式
        private static readonly Regex rxNumbersMatch = new Regex(@"\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex rxUrlPattern = new Regex(@"https?://[^\s]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex rxEmailMatch = new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", 
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex rxDuplicateWordsPattern = new Regex(@"\b(\w+)\b(?:\s+\1\b)+", 
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex rxConsecutivePunctuation = new Regex(@"[,.?!;，。？！；]{2,}", 
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        
        // 上下文重要词检测
        private static readonly HashSet<string> contextKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "this", "that", "these", "those", "it", "they", "he", "she", "him", "her", "his", "hers", "their", "them",
            "the", "a", "an", "and", "but", "or", "so", "because", "if", "when", "where", "how", "why", "what", "who",
            "which", "whose", "我", "你", "他", "她", "它", "我们", "你们", "他们", "她们", "这", "那", "这些", "那些", "因为", "所以"
        };
        
        // 其他优化缓存
        private static readonly StringBuilderPool _stringBuilderPool = new StringBuilderPool(initialCapacity: 1024, maxPoolSize: 10);
        
        // 性能监控相关变量
        private static readonly Stopwatch performanceMonitor = new Stopwatch();
        private static int consecutiveHighLatencyCount = 0;
        private static int basePollingInterval = 25; // 默认轮询间隔(毫秒)
        private static int currentPollingInterval = 25;
        private static int consecutiveLowCPUCount = 0;
        
        // 自适应采样窗口
        private static readonly SamplingRateController samplingController = new SamplingRateController();
        
        // 内存监控与优化
        private static DateTime lastMemoryCheckTime = DateTime.MinValue;
        private static readonly TimeSpan memoryCheckInterval = TimeSpan.FromMinutes(2);
        private static long lastMemoryUsageMB = 0;
        private static bool isMemoryOptimizationActive = false;

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
            
            // 初始化性能监控
            performanceMonitor.Start();
        }

        public static async Task SyncLoop()
        {
            int idleCount = 0;
            int syncCount = 0;
            int failureCount = 0; // 记录连续失败次数
            string lastFullText = string.Empty; // 用于比较文本是否变化
            
            // 性能优化 - 共享的StringBuilder
            StringBuilder textProcessor = _stringBuilderPool.Get();
            
            // 动态调整采样率相关
            DateTime lastCPUCheck = DateTime.MinValue;
            int cpuCheckInterval = 5000; // 5秒检查一次CPU使用率
            
            // 更精确的变化检测
            long lastTextHash = 0;
            
            // 周期性内存检查
            DateTime lastMemCheck = DateTime.Now;
            TimeSpan memCheckInterval = TimeSpan.FromMinutes(5);

            try
            {
                while (true)
                {
                    try
                    {
                        // 性能优化 - 动态调整轮询间隔
                        AdjustPollingInterval();
                        
                        // 定期检查CPU使用率并调整采样策略
                        if ((DateTime.Now - lastCPUCheck).TotalMilliseconds > cpuCheckInterval)
                        {
                            MonitorSystemPerformance();
                            lastCPUCheck = DateTime.Now;
                        }
                        
                        // 定期检查内存并优化
                        if ((DateTime.Now - lastMemCheck) > memCheckInterval)
                        {
                            await CheckAndOptimizeMemoryAsync().ConfigureAwait(false);
                            lastMemCheck = DateTime.Now;
                        }
                    
                        if (Window == null)
                        {
                            // 睡眠更长时间，减少重试频率
                            await Task.Delay(2000).ConfigureAwait(false);
                            continue;
                        }

                        string fullText = string.Empty;
                        try
                        {
                            // Check LiveCaptions.exe still alive
                            var info = Window.Current;
                            var name = info.Name;
                            
                            // 获取LiveCaptions识别的文本之前测量时间
                            performanceMonitor.Restart();
                            
                            // Get the text recognized by LiveCaptions (10-20ms)
                            fullText = LiveCaptionsHandler.GetCaptions(Window);
                            
                            // 基于实际文本变化率调整采样间隔
                            if (!string.IsNullOrEmpty(fullText))
                            {
                                long currentHash = ComputeHash(fullText);
                                if (currentHash != lastTextHash)
                                {
                                    // 文本发生变化，更新采样策略
                                    samplingController.RegisterTextChange();
                                    lastTextHash = currentHash;
                                }
                                else
                                {
                                    // 文本未变化，标记为静止状态
                                    samplingController.RegisterNoChange();
                                }
                                
                                // 应用采样间隔
                                currentPollingInterval = samplingController.GetCurrentSamplingInterval(basePollingInterval);
                            }
                            
                            // 记录获取文本的时间，如果超过阈值则调整策略
                            performanceMonitor.Stop();
                            if (performanceMonitor.ElapsedMilliseconds > 25)
                            {
                                consecutiveHighLatencyCount++;
                                if (consecutiveHighLatencyCount > 5)
                                {
                                    // 连续多次高延迟，增加轮询间隔
                                    currentPollingInterval = Math.Min(currentPollingInterval + 5, 100);
                                    consecutiveHighLatencyCount = 0;
                                }
                            }
                            else
                            {
                                consecutiveHighLatencyCount = 0;
                            }
                            
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
                            
                            await Task.Delay(500).ConfigureAwait(false); // 出错时稍微延长等待时间
                            continue;
                        }
                        
                        // 性能优化 - 检查文本是否变化，如无变化则跳过处理
                        if (string.IsNullOrEmpty(fullText) || fullText == lastFullText)
                        {
                            // 无变化时增加空闲计数并使用较长的睡眠时间
                            idleCount++;
                            await Task.Delay(currentPollingInterval * 2).ConfigureAwait(false); // 空闲时延长睡眠时间
                            continue;
                        }
                        
                        // 更新上次文本
                        lastFullText = fullText;

                        // 性能优化 - 使用StringBuilder进行文本处理，减少字符串分配
                        textProcessor.Clear();
                        textProcessor.Append(fullText);
                        
                        // 优化 - 使用一次性的字符串处理，避免多次中间结果分配
                        string processedText = ProcessTextOptimized(fullText);
                        
                        // Prevent adding the last sentence from previous running to log cards
                        // before the first sentence is completed.
                        if (processedText.IndexOfAny(TextUtil.PUNC_EOS) == -1 && Caption.LogCards.Count > 0)
                        {
                            Caption.LogCards.Clear();
                            Caption.OnPropertyChanged("DisplayLogCards");
                        }

                        // 优化 - 一次性获取最后句子的索引
                        (int lastEOSIndex, string latestCaption) = ExtractLatestCaption(processedText);
                        
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
                        
                        // 性能优化 - 动态调整睡眠时间
                        await Task.Delay(currentPollingInterval).ConfigureAwait(false);
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
                        await Task.Delay(1000).ConfigureAwait(false); // 出现未知错误时延长等待时间
                    }
                }
            }
            finally
            {
                // 归还StringBuilder到对象池
                _stringBuilderPool.Return(textProcessor);
            }
        }
        
        /// <summary>
        /// 检查并优化内存使用
        /// </summary>
        private static async Task CheckAndOptimizeMemoryAsync()
        {
            if (isMemoryOptimizationActive)
                return;
                
            try
            {
                isMemoryOptimizationActive = true;
                
                // 获取当前内存使用
                var process = Process.GetCurrentProcess();
                long currentMemoryMB = process.WorkingSet64 / (1024 * 1024);
                
                // 如果内存使用增长超过50%或超过300MB
                if ((lastMemoryUsageMB > 0 && currentMemoryMB > lastMemoryUsageMB * 1.5) || currentMemoryMB > 300)
                {
                    // 运行在低优先级的线程上进行内存优化
                    await Task.Run(() => {
                        // 清理上下文缓存
                        CleanupContextCache();
                        
                        // 手动触发垃圾回收
                        GC.Collect(1, GCCollectionMode.Optimized);
                        GC.WaitForPendingFinalizers();
                        
                        if (currentMemoryMB > 350)
                        {
                            // 内存使用很高，进行更激进的优化
                            GC.Collect(2, GCCollectionMode.Forced);
                        }
                    }).ConfigureAwait(false);
                }
                
                // 更新内存使用统计
                lastMemoryUsageMB = process.WorkingSet64 / (1024 * 1024);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"内存优化时发生错误: {ex.Message}");
            }
            finally
            {
                isMemoryOptimizationActive = false;
            }
        }
        
        /// <summary>
        /// 高效文本处理方法，一次性完成所有文本处理，减少中间字符串分配
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string ProcessTextOptimized(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
                
            // 预先分配足够大小的StringBuilder，避免扩容
            var sb = _stringBuilderPool.Get(text.Length * 2);
            try
            {
                // 处理首字母缩写词
                string processed = rxAcronymFix.Replace(text, "$1$2");
                processed = rxAcronymFix2.Replace(processed, "$1 $2");
                
                // 处理标点符号周围的空白
                processed = rxPunctuationFix.Replace(processed, "$1 ");
                processed = rxAsianPunctuationFix.Replace(processed, "$1");
                
                // 替换句子内的换行符
                processed = TextUtil.ReplaceNewlines(processed, TextUtil.MEDIUM_THRESHOLD);
                
                // 检测内容类型并更新提示词
                if (IsLLMBasedAPI(Setting.ApiName) && Setting.PromptTemplate == PromptTemplate.AutoDetection)
                {
                    DetectContentTypeAndUpdatePrompt(processed);
                }
                
                return processed;
            }
            finally
            {
                _stringBuilderPool.Return(sb);
            }
        }
        
        /// <summary>
        /// 性能优化 - 检测内容类型并更新提示词模板
        /// </summary>
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
        
        /// <summary>
        /// 一次性提取最后一个句子及其索引，避免多次搜索
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (int lastIndex, string caption) ExtractLatestCaption(string text)
        {
            int lastEOSIndex;
            
            if (text.Length == 0)
                return (-1, string.Empty);
                
            if (Array.IndexOf(TextUtil.PUNC_EOS, text[^1]) != -1)
                lastEOSIndex = text[0..^1].LastIndexOfAny(TextUtil.PUNC_EOS);
            else
                lastEOSIndex = text.LastIndexOfAny(TextUtil.PUNC_EOS);
                
            if (lastEOSIndex == -1)
                return (-1, text);
                
            return (lastEOSIndex, text.Substring(lastEOSIndex + 1));
        }
        
        /// <summary>
        /// 高效计算字符串哈希值，用于快速比较文本变化
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long ComputeHash(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
                
            // 使用简单但高效的FNV-1a哈希算法
            const uint prime = 16777619;
            const uint offsetBasis = 2166136261;
            
            uint hash = offsetBasis;
            for (int i = 0; i < text.Length; i++)
            {
                hash ^= text[i];
                hash *= prime;
            }
            
            return hash;
        }
        
        // 清理上下文缓存
        private static void CleanupContextCache()
        {
            if (contextPromptCache.Count <= MaxContextCacheSize / 2)
                return;
                
            // 创建临时缓存，只保留一半的缓存项
            var tempCache = new ConcurrentDictionary<string, string>();
            int count = 0;
            int targetCount = contextPromptCache.Count / 2;
            
            // 只保留最近的一半缓存项
            foreach (var kvp in contextPromptCache.OrderByDescending(k => k.Key))
            {
                if (count < targetCount)
                {
                    tempCache.TryAdd(kvp.Key, kvp.Value);
                    count++;
                }
                else
                {
                    break;
                }
            }
            
            contextPromptCache.Clear();
            foreach (var kvp in tempCache)
            {
                contextPromptCache.TryAdd(kvp.Key, kvp.Value);
            }
            
            contextCacheSize = tempCache.Count;
        }
        
        // 性能优化 - 监控系统性能并动态调整策略
        private static void MonitorSystemPerformance()
        {
            try
            {
                // 检测CPU使用率
                var process = Process.GetCurrentProcess();
                double cpuUsage = process.TotalProcessorTime.TotalMilliseconds / 
                                  (Environment.ProcessorCount * process.UserProcessorTime.TotalMilliseconds);
                
                // 根据CPU使用率调整策略
                if (cpuUsage > 0.7) // 70% CPU使用率
                {
                    // CPU负载高，增加轮询间隔，减少处理频率
                    currentPollingInterval = Math.Min(currentPollingInterval + 10, 100);
                    consecutiveLowCPUCount = 0;
                }
                else if (cpuUsage < 0.3) // 30% CPU使用率
                {
                    // CPU负载低
                    consecutiveLowCPUCount++;
                    if (consecutiveLowCPUCount > 3)
                    {
                        // 连续多次低CPU负载，可以适当减少轮询间隔
                        currentPollingInterval = Math.Max(currentPollingInterval - 5, basePollingInterval);
                        consecutiveLowCPUCount = 0;
                    }
                }
                
                // 检测内存使用
                long memoryUsed = process.WorkingSet64 / (1024 * 1024); // MB
                
                // 定期检查内存使用情况
                if ((memoryUsed > 300 && (DateTime.Now - lastMemoryCheckTime).TotalMinutes > 1) ||
                    memoryUsed > 400)
                {
                    // 执行内存优化
                    Task.Run(async () => await CheckAndOptimizeMemoryAsync());
                    lastMemoryCheckTime = DateTime.Now;
                }
            }
            catch
            {
                // 忽略性能监控错误
            }
        }
        
        // 性能优化 - 动态调整轮询间隔
        private static void AdjustPollingInterval()
        {
            // 这个方法根据各种因素动态调整轮询间隔
            // 比如考虑LiveCaptions的响应时间、CPU使用率等
            
            // 例如：根据队列长度调整
            if (pendingTextQueue.Count > 3)
            {
                // 积压的翻译请求过多，暂时增加轮询间隔，减轻负担
                currentPollingInterval = Math.Min(currentPollingInterval + 5, 100);
            }
            else if (pendingTextQueue.Count == 0 && currentPollingInterval > basePollingInterval)
            {
                // 翻译队列为空，可以考虑逐渐恢复到基础轮询间隔
                currentPollingInterval = Math.Max(currentPollingInterval - 1, basePollingInterval);
            }
        }

        // 优化：高效的翻译循环，使用TranslationTaskQueue
        public static async Task TranslateLoop()
        {
            var translationTaskQueue = new TranslationTaskQueue();
            DateTime lastTranslationTime = DateTime.Now;
            int translationIdleThreshold = 5000; // 5秒无翻译活动的阈值

            while (true)
            {
                try
                {
                    // 检查LiveCaptions窗口状态
                    if (Window == null)
                    {
                        Caption.DisplayTranslatedCaption = "[警告] LiveCaptions意外关闭，正在重启...";
                        Window = LiveCaptionsHandler.LaunchLiveCaptions();
                        Caption.DisplayTranslatedCaption = "";
                    }

                    // 处理待翻译文本队列
                    if (pendingTextQueue.TryDequeue(out string originalText))
                    {
                        lastTranslationTime = DateTime.Now;

                        try
                        {
                            if (LogOnlyFlag)
                            {
                                bool isOverwrite = await IsOverwrite(originalText).ConfigureAwait(false);
                                await LogOnly(originalText, isOverwrite).ConfigureAwait(false);
                            }
                            else
                            {
                                // 更新上下文历史
                                UpdateContextHistory(originalText);

                                // 确定使用哪个API - 智能选择或尝试建议的API
                                string apiToUse = DetermineApiToUse();
                                
                                // 优化：使用高效的翻译队列处理
                                translationTaskQueue.Enqueue(originalText, true);
                            }
                        }
                        catch (Exception ex)
                        {
                            // 处理翻译过程中的异常
                            Console.WriteLine($"翻译处理异常: {ex.Message}");
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
                        // 性能优化 - 根据是否有完整句子动态调整等待时间
                        if (Array.IndexOf(TextUtil.PUNC_EOS, originalText[^1]) != -1)
                            await Task.Delay(Math.Min(600, currentPollingInterval * 10)).ConfigureAwait(false); // 限制最大等待时间
                    }
                    else
                    {
                        // 性能优化 - 翻译队列为空时使用更长的睡眠时间
                        // 检查距离上次翻译的时间
                        TimeSpan idleTime = DateTime.Now - lastTranslationTime;
                        if (idleTime.TotalMilliseconds > translationIdleThreshold)
                        {
                            // 长时间无翻译活动，使用更长的睡眠时间，降低资源占用
                            await Task.Delay(Math.Min(200, currentPollingInterval * 4)).ConfigureAwait(false);
                        }
                        else
                        {
                            // 正常睡眠
                            await Task.Delay(currentPollingInterval * 2).ConfigureAwait(false);
                        }
                    }
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
                    await Task.Delay(1000).ConfigureAwait(false);
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
            
            // 使缓存无效，增加版本号
            Interlocked.Increment(ref contextCacheSize);
            
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
                    translatedText = await TranslateAPI.TranslateWithAPI(contextPrompt, apiName, token).ConfigureAwait(false);
                }
                else
                {
                    // 对传统API使用普通翻译方法
                    translatedText = await TranslateAPI.TranslateWithAPI(text, apiName, token).ConfigureAwait(false);
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
            // 从缓存获取
            string cacheKey = $"{apiName}_{text}";
            if (contextPromptCache.TryGetValue(cacheKey, out string cachedPrompt))
            {
                return cachedPrompt;
            }
            
            // 构建新的上下文提示
            var contextBuilder = _stringBuilderPool.Get(1024);
            try
            {
                // 智能判断是否需要加入上下文
                bool needsContext = ContextIsRelevant(text);
                
                // 根据内容类型调整上下文句子数量
                int contextSentencesToUse = 0;
                
                if (needsContext) 
                {
                    // 调整上下文长度 - 对于技术性和正式场合的内容需要更多上下文
                    if (rxTechnicalContent.IsMatch(text))
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
                string result = contextBuilder.ToString();
                contextPromptCache[cacheKey] = result;
                
                // 如果缓存太大，安排清理
                if (Interlocked.Increment(ref contextCacheSize) > MaxContextCacheSize * 2)
                {
                    Task.Run(() => CleanupContextCache());
                }
                
                return result;
            }
            finally
            {
                _stringBuilderPool.Return(contextBuilder);
            }
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
                // 使用预定义的上下文关键词集合
                if (contextKeywords.Contains(lowerWord))
                {
                    return true;
                }
            }
            
            // 检查是否有代词起始的句子 (使用预编译的正则表达式)
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
            if (words.Length < 4 && 
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
            // 使用ConcurrentDictionary的原子操作更新评分
            apiQualityScores.AddOrUpdate(
                apiName,
                qualityScore,
                (key, oldValue) => (int)(oldValue * 0.8 + qualityScore * 0.2)
            );

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
    
    /// <summary>
    /// StringBuilder对象池，用于减少字符串处理的内存分配
    /// </summary>
    public class StringBuilderPool
    {
        private readonly ObjectPool<StringBuilder> _pool;
        private readonly int _initialCapacity;
        
        public StringBuilderPool(int initialCapacity = 1024, int maxPoolSize = 32)
        {
            _initialCapacity = initialCapacity;
            _pool = new ObjectPool<StringBuilder>(
                // 创建新对象
                () => new StringBuilder(initialCapacity),
                // 重置对象
                sb => { sb.Clear(); if (sb.Capacity > initialCapacity * 3) sb.Capacity = initialCapacity; },
                // 最大池大小
                maxPoolSize
            );
        }
        
        public StringBuilder Get(int capacity = 0)
        {
            var sb = _pool.Get();
            if (capacity > 0 && sb.Capacity < capacity)
            {
                sb.Capacity = capacity;
            }
            return sb;
        }
        
        public void Return(StringBuilder sb)
        {
            if (sb == null)
                return;
                
            _pool.Return(sb);
        }
    }
    
    /// <summary>
    /// 采样率控制器，用于基于文本变化率动态调整采样间隔
    /// </summary>
    public class SamplingRateController
    {
        // 文本变化统计
        private readonly Queue<DateTime> _recentChanges = new Queue<DateTime>();
        private readonly int _historySize = 20;
        private DateTime _lastChangeTime = DateTime.MinValue;
        private int _consecutiveNoChanges = 0;
        
        // 采样率参数
        private const int MIN_INTERVAL = 20;    // 最小轮询间隔(毫秒)
        private const int MAX_INTERVAL = 200;   // 最大轮询间隔(毫秒)
        private const int FAST_INTERVAL = 25;   // 快速轮询间隔(毫秒)
        private const int SLOW_INTERVAL = 100;  // 慢速轮询间隔(毫秒)
        
        /// <summary>
        /// 注册文本变化事件
        /// </summary>
        public void RegisterTextChange()
        {
            DateTime now = DateTime.Now;
            _lastChangeTime = now;
            _recentChanges.Enqueue(now);
            
            // 保持历史记录大小
            while (_recentChanges.Count > _historySize)
            {
                _recentChanges.Dequeue();
            }
            
            // 重置无变化计数
            _consecutiveNoChanges = 0;
        }
        
        /// <summary>
        /// 注册无变化事件
        /// </summary>
        public void RegisterNoChange()
        {
            _consecutiveNoChanges++;
        }
        
        /// <summary>
        /// 获取当前适合的采样间隔
        /// </summary>
        public int GetCurrentSamplingInterval(int baseInterval)
        {
            // 如果没有变化历史，使用基础间隔
            if (_recentChanges.Count < 2)
                return baseInterval;
                
            // 如果连续多次无变化，逐步增加间隔
            if (_consecutiveNoChanges > 10)
            {
                // 每10次无变化增加一定比例，最大不超过最大间隔
                return Math.Min(baseInterval * (1 + (_consecutiveNoChanges / 10)), MAX_INTERVAL);
            }
            
            // 计算平均变化频率
            DateTime oldestChange = _recentChanges.Peek();
            TimeSpan timeSpan = DateTime.Now - oldestChange;
            
            if (timeSpan.TotalSeconds < 1)
                return FAST_INTERVAL; // 变化非常频繁，使用快速轮询
            
            // 计算每秒平均变化次数
            double changesPerSecond = _recentChanges.Count / timeSpan.TotalSeconds;
            
            if (changesPerSecond > 2)
            {
                // 变化频繁，使用快速采样
                return FAST_INTERVAL;
            }
            else if (changesPerSecond < 0.5)
            {
                // 变化缓慢，使用慢速采样
                return SLOW_INTERVAL;
            }
            else
            {
                // 根据变化频率线性调整采样间隔
                double factor = 1.0 - Math.Min(changesPerSecond / 2.0, 1.0);
                return (int)(MIN_INTERVAL + factor * (SLOW_INTERVAL - MIN_INTERVAL));
            }
        }
    }
}