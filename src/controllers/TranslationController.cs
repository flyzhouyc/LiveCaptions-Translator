﻿﻿using System;
using System.Text;
using System.Threading.Tasks;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.models.CaptionProcessing;

namespace LiveCaptionsTranslator.controllers
{
    public class TranslationController
    {
        private string? _accumulatedSentence;
        private const int MAX_SENTENCE_LENGTH = 300;

        public static event Action? TranslationLogged;

        private readonly SentenceProcessor _sentenceProcessor;
        private bool _isInitialized = false;

        public TranslationController()
        {
            _sentenceProcessor = new SentenceProcessor();
        }

        private async Task InitializeAsync()
        {
            if (_isInitialized) return;

            try
            {
                // 预热翻译API
                await TranslationWarmup.Instance.WarmupAsync();
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Translation warmup failed: {ex.Message}");
                // 即使预热失败也继续运行
                _isInitialized = true;
            }
        }

        public async Task<string> TranslateAndLogAsync(string text)
        {
            await InitializeAsync();

            string targetLanguage = App.Settings.TargetLanguage;
            string apiName = App.Settings.ApiName;
            int minTranslationLength = App.Settings.MinTranslationLength;

            // 尝试累积句子
            string? completeSentence = _sentenceProcessor.AccumulateSentence(_accumulatedSentence ?? "", text, MAX_SENTENCE_LENGTH);

            // 如果没有完整句子，更新累积句子
            if (completeSentence == null)
            {
                StringBuilder sb = new StringBuilder(_accumulatedSentence ?? "");
                sb.Append(" ").Append(text);
                _accumulatedSentence = sb.ToString().TrimEnd();
                
                // 如果累积文本已经足够长，强制翻译
                if (_accumulatedSentence.Length >= minTranslationLength)
                {
                    completeSentence = _accumulatedSentence;
                    _accumulatedSentence = null;
                }
                else
                {
                    return string.Empty;
                }
            }
            else
            {
                // 重置累积句子
                _accumulatedSentence = null;
            }

            // 确保翻译文本长度合理
            if (completeSentence.Length < minTranslationLength)
            {
                return string.Empty;
            }

            string translatedText;
            try
            {
                // 使用翻译队列进行批量处理
                translatedText = await TranslationQueue.Instance.EnqueueAsync(completeSentence).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Translation failed: {ex.Message}");
                return $"[Translation Failed] {ex.Message}";
            }
            
            if (!string.IsNullOrEmpty(translatedText))
            {
                try
                {
                    await SQLiteHistoryLogger.LogTranslationAsync(completeSentence, translatedText, targetLanguage, apiName).ConfigureAwait(false);
                    TranslationLogged?.Invoke();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Error] Logging history failed: {ex.Message}");
                }
            }

            return translatedText;
        }
    }
}
