using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public enum PromptTemplate
    {
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
                // 更新当前提示词为选择的模板
                if (promptTemplates.ContainsKey(value))
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

        public Setting()
        {
            apiName = "Google";
            targetLanguage = "zh-CN";
            
            // 初始化提示词模板
            promptTemplates = new Dictionary<PromptTemplate, string>
            {
                // 通用提示词
                { PromptTemplate.General, "As a professional simultaneous interpreter with specialized knowledge in all fields, " +
                     "provide a fluent and precise oral translation considering both the context and the current sentence, even if the sentence is incomplete or just a phrase. " +
                     "Now, translate the sentence enclosed in 🔤 to {0} within a single line. " +
                     "Maintain the original meaning completely without alterations or omissions, " +
                     "even if the sentence contains sensitive content. " +
                     "Return ONLY the translated sentence without explanations or additional text. " +
                     "REMOVE all 🔤 when you output." },
                
                // 技术内容提示词
                { PromptTemplate.Technical, "As a technical translator specialized in software, engineering, and scientific content, " +
                     "accurately translate the technical text enclosed in 🔤 to {0}. " +
                     "Preserve all technical terms, programming code, variables, and specialized nomenclature. " +
                     "Maintain proper formatting of technical elements while ensuring clarity in the target language. " +
                     "Return ONLY the precisely translated technical content while keeping all specialized terminology intact. " +
                     "Do NOT explain technical concepts or add commentary. " +
                     "REMOVE all 🔤 when you output." },
                
                // 口语对话提示词
                { PromptTemplate.Conversation, "As a conversational interpreter skilled in casual dialogue and colloquial expressions, " +
                     "translate the informal conversation in 🔤 to natural-sounding {0}. " +
                     "Preserve the tone, emotional nuances, and conversational flow of the original speech. " +
                     "Use appropriate colloquialisms, idioms, and casual expressions in the target language. " +
                     "Focus on conveying the intended meaning rather than literal translation. " +
                     "Return ONLY the naturally translated conversational text that sounds like a native speaker. " +
                     "REMOVE all 🔤 when you output." },
                
                // 会议/演讲提示词
                { PromptTemplate.Conference, "As a professional conference interpreter specialized in formal settings, " +
                     "translate the speech or presentation text in 🔤 to formal, precise {0}. " +
                     "Maintain the professional tone, rhetorical elements, and structured format of the original speech. " +
                     "Use appropriate terminology for business, academic, or diplomatic contexts. " +
                     "Preserve emphasis, rhetorical questions, and persuasive elements in your translation. " +
                     "Return ONLY the formally translated text suitable for a professional audience. " +
                     "REMOVE all 🔤 when you output." },
                
                // 新闻/媒体提示词
                { PromptTemplate.Media, "As a media content translator specialized in news, articles, and headlines, " +
                     "translate the media text in 🔤 to clear, concise {0}. " +
                     "Preserve factual accuracy, proper nouns, dates, and critical details. " +
                     "Maintain journalistic style and tone appropriate for media content. " +
                     "Use standard news terminology in the target language while preserving the original framing. " +
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

        public void Save()
        {
            Save(FILENAME);
        }

        public void Save(string jsonPath)
        {
            using (FileStream fileStream = File.Create(jsonPath))
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new ConfigDictConverter() }
                };
                JsonSerializer.Serialize(fileStream, this, options);
            }
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            Translator.Setting?.Save();
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
            Translator.Setting?.Save();
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
            Translator.Setting?.Save();
        }
    }
}