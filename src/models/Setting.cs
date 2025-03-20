using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public enum PromptTemplate
    {
        AutoDetection,
        General,
        Technical,
        Conversation,
        Conference,
        Media
    }

    public class Setting : INotifyPropertyChanged
    {
        public static readonly string FILENAME = "setting.json";

        public event PropertyChangedEventHandler? PropertyChanged;

        private int maxIdleInterval = 50;
        private int maxSyncInterval = 3;

        private string apiName;
        private string targetLanguage;
        private string prompt;
        private PromptTemplate promptTemplate = PromptTemplate.General;
        private Dictionary<PromptTemplate, string> promptTemplates;

        private MainWindowState mainWindowState;
        private OverlayWindowState overlayWindowState;

        private Dictionary<string, string> windowBounds;

        private Dictionary<string, TranslateAPIConfig> configs;
        private TranslateAPIConfig? currentAPIConfig;

        public string ApiName
        {
            get => apiName;
            set
            {
                apiName = value;
                OnPropertyChanged("ApiName");
                OnPropertyChanged("CurrentAPIConfig");
            }
        }
        public string TargetLanguage
        {
            get => targetLanguage;
            set
            {
                targetLanguage = value;
                OnPropertyChanged("TargetLanguage");
            }
        }
        public int MaxIdleInterval
        {
            get => maxIdleInterval;
        }
        public int MaxSyncInterval
        {
            get => maxSyncInterval;
            set
            {
                maxSyncInterval = value;
                OnPropertyChanged("MaxSyncInterval");
            }
        }
        public string Prompt
        {
            get => prompt;
            set
            {
                prompt = value;
                OnPropertyChanged("Prompt");
            }
        }

        public PromptTemplate PromptTemplate
        {
            get => promptTemplate;
            set
            {
                promptTemplate = value;
                // 更新当前提示词为选择的模板，但对于AutoDetection不直接更新提示词
                if (promptTemplates.ContainsKey(value) && value != PromptTemplate.AutoDetection)
                {
                    Prompt = promptTemplates[value];
                }
                OnPropertyChanged("PromptTemplate");
            }
        }

        [JsonInclude]
        public Dictionary<PromptTemplate, string> PromptTemplates
        {
            get => promptTemplates;
            set
            {
                promptTemplates = value;
                OnPropertyChanged("PromptTemplates");
            }
        }

        public Dictionary<string, string> WindowBounds
        {
            get => windowBounds;
            set
            {
                windowBounds = value;
                OnPropertyChanged("WindowBounds");
            }
        }

        public MainWindowState MainWindow
        {
            get => mainWindowState;
            set
            {
                mainWindowState = value;
                OnPropertyChanged("MainWindow");
            }
        }
        public OverlayWindowState OverlayWindow
        {
            get => overlayWindowState;
            set
            {
                overlayWindowState = value;
                OnPropertyChanged("OverlayWindow");
            }
        }

        [JsonInclude]
        public Dictionary<string, TranslateAPIConfig> Configs
        {
            get => configs;
            set
            {
                configs = value;
                OnPropertyChanged("Configs");
            }
        }
        [JsonIgnore]
        public TranslateAPIConfig CurrentAPIConfig
        {
            get => currentAPIConfig ?? (Configs.ContainsKey(ApiName) ? Configs[ApiName] : Configs["Ollama"]);
            set => currentAPIConfig = value;
        }
        public void UpdateCurrentPrompt(PromptTemplate detectedTemplate)
        {
            if (promptTemplates.ContainsKey(detectedTemplate))
            {
                prompt = promptTemplates[detectedTemplate];
                OnPropertyChanged("Prompt");
            }
        }

        public Setting()
        {
            apiName = "Google";
            targetLanguage = "zh-CN";
            
            // 初始化提示词模板
            promptTemplates = new Dictionary<PromptTemplate, string>
            {
                // 自动检测提示词
                { PromptTemplate.AutoDetection, "As a professional simultaneous interpreter with specialized knowledge in all fields, " +
                    "provide a fluent and precise oral translation considering both the context and the current sentence, even if the sentence is incomplete or just a phrase. " +
                    "Now, translate the sentence enclosed in 🔤 to {0} within a single line. " +
                    "Maintain the original meaning completely without alterations or omissions, " +
                    "even if the sentence contains sensitive content. " +
                    "Return ONLY the translated sentence without explanations or additional text. " +
                    "REMOVE all 🔤 when you output." },
                
                // 通用提示词 - 增强对上下文的理解和流畅性
                { PromptTemplate.General, "As a professional simultaneous interpreter with specialized knowledge in all fields, " +
                    "provide a fluent and contextually accurate translation of the text enclosed in 🔤 to {0}. " +
                    "Consider both previous context and the current statement to maintain narrative continuity. " +
                    "Pay special attention to pronouns, tense consistency, and idiomatic expressions. " +
                    "Preserve all factual details, names, numbers, and technical terms exactly as they appear. " +
                    "Use natural, native-sounding language in the target language while maintaining the tone and style of the original. " +
                    "Return ONLY the translated text without ANY explanations, additions, or alterations. " +
                    "REMOVE all 🔤 when you output." },
                
                // 技术内容提示词 - 更精准地处理技术术语和结构
                { PromptTemplate.Technical, "As a technical translator with expertise in software, engineering, and scientific domains, " +
                    "translate the technical content enclosed in 🔤 to {0} with precision. " +
                    "Preserve ALL technical terminology exactly, including code snippets, variables, function names, API references, and specialized nomenclature. " +
                    "Maintain the exact syntax and formatting of any code or structured content. " +
                    "Keep mathematical expressions, measurements, and units unchanged unless localization is required. " +
                    "Translate surrounding explanatory text clearly while preserving technical accuracy. " +
                    "Use standard technical vocabulary in the target language where established equivalents exist. " +
                    "Return ONLY the precisely translated content without explanations or commentary. " +
                    "REMOVE all 🔤 when you output." },
                
                // 口语对话提示词 - 更自然的对话风格
                { PromptTemplate.Conversation, "As a conversational interpreter specializing in natural dialogue, " +
                    "translate the conversation enclosed in 🔤 to {0} in a way that sounds completely natural to native speakers. " +
                    "Capture the informal tone, emotion, humor, and relationship dynamics present in the original speech. " +
                    "Use appropriate colloquialisms, slang, fillers, and casual expressions that would be used in everyday speech. " +
                    "Maintain the speaking style and personality traits of the speakers. " +
                    "Prioritize conversational flow over literal translation while preserving all key information. " +
                    "Account for cultural context and references, adapting them when necessary. " +
                    "Return ONLY the naturally translated conversational text that would sound authentic in the target language. " +
                    "REMOVE all 🔤 when you output." },
                
                // 会议/演讲提示词 - 正式和专业性
                { PromptTemplate.Conference, "As a professional conference interpreter specializing in formal discourse, " +
                    "translate the speech or presentation enclosed in 🔤 to {0} with formal precision and rhetorical effectiveness. " +
                    "Maintain the professional register, specialized terminology, and structured argumentation of the original. " +
                    "Preserve rhetorical devices, emphasis patterns, persuasive elements, and logical flow. " +
                    "Ensure all statistical data, quotations, credentials, and organizational names are translated with absolute accuracy. " +
                    "Use appropriate honorifics, formal address forms, and professional conventions of the target language. " +
                    "Maintain the authoritative tone while ensuring the translation sounds natural to the target audience. " +
                    "Return ONLY the formally translated text suitable for a professional conference environment. " +
                    "REMOVE all 🔤 when you output." },
                
                // 新闻/媒体提示词 - 客观和准确性
                { PromptTemplate.Media, "As a media translator specializing in news and journalistic content, " +
                    "translate the media text enclosed in 🔤 to {0} with journalistic integrity and clarity. " +
                    "Preserve factual accuracy with precise translation of names, titles, dates, locations, statistics, and quotations. " +
                    "Maintain the objective tone and structure of news reporting including headline style if applicable. " +
                    "Use standard journalistic terminology and conventions of the target language. " +
                    "Preserve the original framing without editorial alterations while ensuring cultural relevance. " +
                    "Maintain the information hierarchy (most important facts first) of the original content. " +
                    "For headlines, use the concise, impactful style typical of news headlines in the target language. " +
                    "Return ONLY the professionally translated media content without commentary. " +
                    "REMOVE all 🔤 when you output." }
            };
            
            // 默认使用通用提示词
            prompt = promptTemplates[PromptTemplate.General];
            
            mainWindowState = new MainWindowState
            {
                Topmost = true,
                CaptionLogEnabled = false,
                CaptionLogMax = 2,
                LatencyShow = false
            };
            overlayWindowState = new OverlayWindowState
            {
                FontSize = 15,
                FontColor = 1,
                FontBold = 1,
                FontShadow = 1,
                BackgroundColor = 8,
                Opacity = 150,
                HistoryMax = 1
            };
            windowBounds = new Dictionary<string, string>
            {
                { "MainWindow", "1, 1, 1, 1" },
                { "OverlayWindow", "1, 1, 1, 1" },
            };
            configs = new Dictionary<string, TranslateAPIConfig>
            {
                { "Google", new TranslateAPIConfig() },
                { "Google2", new TranslateAPIConfig() },
                { "Ollama", new OllamaConfig() },
                { "OpenAI", new OpenAIConfig() },
                { "DeepL", new DeepLConfig() },
                { "OpenRouter", new OpenRouterConfig() },
            };
        }

        public Setting(string apiName, string targetLanguage, string prompt,
                       MainWindowState mainWindowState, OverlayWindowState overlayWindowState,
                       Dictionary<string, TranslateAPIConfig> configs, Dictionary<string, string> windowBounds)
        {
            this.apiName = apiName;
            this.targetLanguage = targetLanguage;
            this.prompt = prompt;
            this.mainWindowState = mainWindowState;
            this.overlayWindowState = overlayWindowState;
            this.configs = configs;
            this.windowBounds = windowBounds;
            
            // 初始化提示词模板 - 这里需要确保在反序列化时也能正确初始化模板
            if (promptTemplates == null)
            {
                promptTemplates = new Dictionary<PromptTemplate, string>
                {
                    { PromptTemplate.General, prompt },
                    { PromptTemplate.Technical, prompt },
                    { PromptTemplate.Conversation, prompt },
                    { PromptTemplate.Conference, prompt },
                    { PromptTemplate.Media, prompt }
                };
            }
        }

        public static Setting Load()
        {
            string jsonPath = Path.Combine(Directory.GetCurrentDirectory(), FILENAME);
            return Load(jsonPath);
        }

        public static Setting Load(string jsonPath)
        {
            Setting setting;
            if (File.Exists(jsonPath))
            {
                using (FileStream fileStream = File.OpenRead(jsonPath))
                {
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Converters = { new ConfigDictConverter() }
                    };
                    setting = JsonSerializer.Deserialize<Setting>(fileStream, options);
                }
                
                // 如果是旧版设置文件没有提示词模板，则初始化模板
                if (setting.promptTemplates == null)
                {
                    setting.promptTemplates = new Dictionary<PromptTemplate, string>
                    {
                        { PromptTemplate.General, setting.prompt },
                        { PromptTemplate.Technical, "As a technical translator specialized in software, engineering, and scientific content, " +
                            "accurately translate the technical text enclosed in 🔤 to {0}. " +
                            "Preserve all technical terms, programming code, variables, and specialized nomenclature. " +
                            "Maintain proper formatting of technical elements while ensuring clarity in the target language. " +
                            "Return ONLY the precisely translated technical content while keeping all specialized terminology intact. " +
                            "Do NOT explain technical concepts or add commentary. " +
                            "REMOVE all 🔤 when you output." },
                        { PromptTemplate.Conversation, "As a conversational interpreter skilled in casual dialogue and colloquial expressions, " +
                            "translate the informal conversation in 🔤 to natural-sounding {0}. " +
                            "Preserve the tone, emotional nuances, and conversational flow of the original speech. " +
                            "Use appropriate colloquialisms, idioms, and casual expressions in the target language. " +
                            "Focus on conveying the intended meaning rather than literal translation. " +
                            "Return ONLY the naturally translated conversational text that sounds like a native speaker. " +
                            "REMOVE all 🔤 when you output." },
                        { PromptTemplate.Conference, "As a professional conference interpreter specialized in formal settings, " +
                            "translate the speech or presentation text in 🔤 to formal, precise {0}. " +
                            "Maintain the professional tone, rhetorical elements, and structured format of the original speech. " +
                            "Use appropriate terminology for business, academic, or diplomatic contexts. " +
                            "Preserve emphasis, rhetorical questions, and persuasive elements in your translation. " +
                            "Return ONLY the formally translated text suitable for a professional audience. " +
                            "REMOVE all 🔤 when you output." },
                        { PromptTemplate.Media, "As a media content translator specialized in news, articles, and headlines, " +
                            "translate the media text in 🔤 to clear, concise {0}. " +
                            "Preserve factual accuracy, proper nouns, dates, and critical details. " +
                            "Maintain journalistic style and tone appropriate for media content. " +
                            "Use standard news terminology in the target language while preserving the original framing. " +
                            "Return ONLY the professionally translated media content without commentary. " +
                            "REMOVE all 🔤 when you output." }
                    };
                }
            }
            else
            {
                setting = new Setting();
                setting.Save();
            }
            return setting;
        }

        // 新增异步保存方法
        public async Task SaveAsync()
        {
            await SaveAsync(FILENAME);
        }

        public async Task SaveAsync(string jsonPath)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new ConfigDictConverter() }
                };
                
                // 使用内存流先生成JSON，然后一次性写入文件，减少IO操作时间
                using (var memoryStream = new MemoryStream())
                {
                    await JsonSerializer.SerializeAsync(memoryStream, this, options);
                    memoryStream.Position = 0;
                    
                    // 创建临时文件，成功后再替换原文件，避免保存中断导致设置文件损坏
                    string tempFile = jsonPath + ".tmp";
                    using (var fileStream = File.Create(tempFile))
                    {
                        await memoryStream.CopyToAsync(fileStream);
                        await fileStream.FlushAsync();
                    }
                    
                    // 文件重命名是原子操作，确保设置文件完整性
                    if (File.Exists(jsonPath))
                        File.Delete(jsonPath);
                    File.Move(tempFile, jsonPath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存设置失败: {ex.Message}");
                // 出错时不抛出异常，避免中断UI操作
            }
        }

        // 保持原有同步方法，但内部实现优化为异步
        public void Save()
        {
            // 在后台线程启动异步保存，但不阻塞当前线程
            Task.Run(async () => await SaveAsync()).ConfigureAwait(false);
        }

        public void Save(string jsonPath)
        {
            Task.Run(async () => await SaveAsync(jsonPath)).ConfigureAwait(false);
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            if (Translator.Setting != null && Translator.Setting == this)
            {
                // 在属性更改时不立即保存，而是安排在适当的时机异步保存
                Task.Run(async () => await SaveAsync()).ConfigureAwait(false);
            }
        }
    }

    public class MainWindowState : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private bool topmost;
        private bool captionLogEnabled;
        private int captionLogMax;
        private bool latencyShow;

        public bool Topmost
        {
            get => topmost;
            set
            {
                topmost = value;
                OnPropertyChanged("Topmost");
            }
        }
        public bool CaptionLogEnabled
        {
            get => captionLogEnabled;
            set
            {
                captionLogEnabled = value;
                OnPropertyChanged("CaptionLogEnabled");
            }
        }
        public int CaptionLogMax
        {
            get => captionLogMax;
            set
            {
                captionLogMax = value;
                OnPropertyChanged("CaptionLogMax");
            }
        }
        public bool LatencyShow
        {
            get => latencyShow;
            set
            {
                latencyShow = value;
                OnPropertyChanged("LatencyShow");
            }
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            if (Translator.Setting != null)
            {
                // 使用异步保存减少UI阻塞
                Task.Run(async () => await Translator.Setting.SaveAsync());
            }
        }
    }

    public class OverlayWindowState : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private int fontSize;
        private int fontColor;
        private int fontBold;
        private int fontShadow;
        private int backgroundColor;
        private byte opacity;
        private int historyMax;

        public int FontSize
        {
            get => fontSize;
            set
            {
                fontSize = value;
                OnPropertyChanged("FontSize");
            }
        }
        public int FontColor
        {
            get => fontColor;
            set
            {
                fontColor = value;
                OnPropertyChanged("FontColor");
            }
        }
        public int FontBold
        {
            get => fontBold;
            set
            {
                fontBold = value;
                OnPropertyChanged("FontBold");
            }
        }
        public int FontShadow
        {
            get => fontShadow;
            set
            {
                fontShadow = value;
                OnPropertyChanged("FontShadow");
            }
        }
        public int BackgroundColor
        {
            get => backgroundColor;
            set
            {
                backgroundColor = value;
                OnPropertyChanged("BackgroundColor");
            }
        }
        public byte Opacity
        {
            get => opacity;
            set
            {
                opacity = value;
                OnPropertyChanged("Opacity");
            }
        }
        public int HistoryMax
        {
            get => historyMax;
            set
            {
                historyMax = value;
                OnPropertyChanged("HistoryMax");
            }
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            if (Translator.Setting != null)
            {
                // 使用异步保存减少UI阻塞
                Task.Run(async () => await Translator.Setting.SaveAsync());
            }
        }
    }
}