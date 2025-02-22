﻿using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveCaptionsTranslator.models
{
    public class Setting : INotifyPropertyChanged
    {
        public static readonly string FILENAME = "setting.json";

        public event PropertyChangedEventHandler? PropertyChanged;

        private string apiName;
        private string targetLanguage;
        private Dictionary<string, TranslateAPIConfig> configs;

        private int maxIdleInterval = 10;
        private int maxSyncInterval = 7;
        private int minTranslationLength = 120;
        private int minCaptionBytes = 15;
        private int optimalCaptionLength = 100;
        private bool useAutomaticOptimalLength = true;
        private double optimalLengthAdjustmentFactor = 1.0;

        public int OptimalCaptionLength
        {
            get => optimalCaptionLength;
            set
            {
                optimalCaptionLength = Math.Clamp(value, 50, 200);
                OnPropertyChanged("OptimalCaptionLength");
            }
        }

        public bool UseAutomaticOptimalLength
        {
            get => useAutomaticOptimalLength;
            set
            {
                useAutomaticOptimalLength = value;
                OnPropertyChanged("UseAutomaticOptimalLength");
            }
        }

        public double OptimalLengthAdjustmentFactor
        {
            get => optimalLengthAdjustmentFactor;
            set
            {
                optimalLengthAdjustmentFactor = Math.Clamp(value, 0.5, 2.0);
                OnPropertyChanged("OptimalLengthAdjustmentFactor");
            }
        }

        public int MinCaptionBytes
        {
            get => minCaptionBytes;
            set
            {
                minCaptionBytes = Math.Clamp(value, 0, 20);
                OnPropertyChanged("MinCaptionBytes");
            }
        }

        public int MinTranslationLength
        {
            get => minTranslationLength;
            set
            {
                minTranslationLength = value;
                OnPropertyChanged("MinTranslationLength");
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
            get => Configs[apiName];
        }

        public Setting()
        {
            apiName = "Ollama";
            targetLanguage = "zh-CN";
            configs = new Dictionary<string, TranslateAPIConfig>
            {
                { "Ollama", new OllamaConfig() },
                { "OpenAI", new OpenAIConfig() },
                { "GoogleTranslate", new GoogleTranslateConfig() },
            };
        }

        public Setting(string apiName, string sourceLanguage, string targetLanguage,
                       Dictionary<string, TranslateAPIConfig> configs)
        {
            this.apiName = apiName;
            this.targetLanguage = targetLanguage;
            this.configs = configs;
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
