using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;

namespace LiveCaptionsTranslator.models
{
    public class TranslationWarmup
    {
        private static readonly string WARMUP_DATA_PATH = "data/warmup_phrases.json";
        private static readonly TranslationWarmup _instance = new TranslationWarmup();
        private readonly Dictionary<string, List<string>> _commonPhrases;
        private bool _isWarmedUp = false;

        public static TranslationWarmup Instance => _instance;

        private TranslationWarmup()
        {
            _commonPhrases = new Dictionary<string, List<string>>
            {
                ["en"] = new List<string>
                {
                    "Hello, how are you?",
                    "Thank you very much.",
                    "I understand.",
                    "Could you please repeat that?",
                    "Let me explain.",
                    "That's interesting.",
                    "I agree with you.",
                    "What do you think?",
                    "In my opinion,",
                    "For example,"
                },
                ["zh"] = new List<string>
                {
                    "你好，最近怎么样？",
                    "非常感谢。",
                    "我明白了。",
                    "请再说一遍好吗？",
                    "让我来解释一下。",
                    "这很有趣。",
                    "我同意你的观点。",
                    "你觉得呢？",
                    "我认为，",
                    "比如说，"
                }
            };
        }

        public async Task WarmupAsync()
        {
            if (_isWarmedUp) return;

            try
            {
                // 加载自定义预热短语
                await LoadCustomPhrasesAsync();

                // 预热翻译API
                var tasks = new List<Task>();
                foreach (var language in _commonPhrases.Keys)
                {
                    foreach (var phrase in _commonPhrases[language])
                    {
                        // 异步预热，不等待结果
                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                await TranslateAPI.TranslateFunc(phrase);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Warmup translation failed for phrase '{phrase}': {ex.Message}");
                            }
                        }));
                    }
                }

                // 等待所有预热任务完成，设置超时时间
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
                _isWarmedUp = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Translation warmup failed: {ex.Message}");
                // 即使预热失败也继续运行
                _isWarmedUp = true;
            }
        }

        private async Task LoadCustomPhrasesAsync()
        {
            try
            {
                if (File.Exists(WARMUP_DATA_PATH))
                {
                    var json = await File.ReadAllTextAsync(WARMUP_DATA_PATH);
                    var customPhrases = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
                    
                    // 合并自定义短语
                    foreach (var (language, phrases) in customPhrases)
                    {
                        if (_commonPhrases.ContainsKey(language))
                        {
                            _commonPhrases[language].AddRange(phrases);
                        }
                        else
                        {
                            _commonPhrases[language] = phrases;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load custom warmup phrases: {ex.Message}");
            }
        }

        public bool IsWarmedUp => _isWarmedUp;
    }
}
