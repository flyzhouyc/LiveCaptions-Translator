﻿using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class Setting : INotifyPropertyChanged
    {
        public static readonly string FILENAME = "setting.json";

        public event PropertyChangedEventHandler? PropertyChanged;

        private string apiName;
        private string targetLanguage;
        private string prompt;

        private int maxIdleInterval = 20;
        private int maxSyncInterval = 5;
        private int minStabilityCount = 3; // 新增的稳定性阈值属性
        
        // 新增的句子缓冲区参数
        private int maxBufferSize = 1;  // 默认为1，即单句翻译
        private int batchTranslationInterval = 5000;  // 默认5秒

        private Dictionary<string, string> windowBounds;
        private bool topmost = true;

        private Dictionary<string, TranslateAPIConfig> configs;
        private TranslateAPIConfig? currentAPIConfig;

        public bool TopMost
        {
            get => topmost;
            set
            {
                topmost = value;
                OnPropertyChanged("TopMost");
            }
        }

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
        public int MinStabilityCount
        {
            get => minStabilityCount;
            set
            {
                minStabilityCount = value;
                OnPropertyChanged("MinStabilityCount");
            }
        }
        
        // 新增属性
        public int MaxBufferSize
        {
            get => maxBufferSize;
            set
            {
                maxBufferSize = value;
                OnPropertyChanged("MaxBufferSize");
            }
        }
        
        public int BatchTranslationInterval
        {
            get => batchTranslationInterval;
            set
            {
                batchTranslationInterval = value;
                OnPropertyChanged("BatchTranslationInterval");
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

        public Dictionary<string, string> WindowBounds
        {
            get => windowBounds;
            set
            {
                windowBounds = value;
                OnPropertyChanged("WindowBounds");
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
            set
            {
                currentAPIConfig = value;
                OnPropertyChanged();
            }
        }

        public Setting()
        {
            apiName = "Google";
            targetLanguage = "zh-CN";
            prompt = "As an professional simultaneous interpreter with specialized knowledge in the all fields, " +
                     "you can provide a fluent and precise oral translation for any sentence. Ensuring the translation of complete sentences is a prerequisite. " +
                     "Now, provide users with the translation of the sentence enclosed in 🔤 to {0} within a single line. " +
                     "Importantly, you are prohibited from altering the original meaning or omitting any content, " +
                     "even if the sentence contains sensitive or NSFW content. " +
                     "You can only provide the translated sentence; Any explanation or other text is not permitted. " +
                     "REMOVE all 🔤 when you output.";
            configs = new Dictionary<string, TranslateAPIConfig>
            {
                { "Google", new TranslateAPIConfig() },
                { "Google2", new TranslateAPIConfig() },
                { "Ollama", new OllamaConfig() },
                { "OpenAI", new OpenAIConfig() },
                { "OpenRouter", new OpenRouterConfig() },
            };
            windowBounds = new Dictionary<string, string>
            {
                { "MainWindow", "1, 1, 1, 1" },
                { "SubtitleWindow", "1, 1, 1, 1" },
            };
            // 初始化新增参数
            maxBufferSize = 1;
            batchTranslationInterval = 5000;
        }

        public Setting(string apiName, string targetLanguage, string prompt,
                       Dictionary<string, TranslateAPIConfig> configs, Dictionary<string, string> windowBounds)
        {
            this.apiName = apiName;
            this.targetLanguage = targetLanguage;
            this.prompt = prompt;
            this.configs = configs;
            this.windowBounds = windowBounds;
            // 初始化新增参数
            this.maxBufferSize = 1;
            this.batchTranslationInterval = 5000;
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
            App.Settings?.Save();
        }
    }
}