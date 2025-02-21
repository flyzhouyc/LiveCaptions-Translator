using System;
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

        public async Task<string> TranslateAndLogAsync(string text)
        {
            string targetLanguage = App.Settings.TargetLanguage;
            string apiName = App.Settings.ApiName;

            // 尝试累积句子
            string? completeSentence = SentenceProcessor.AccumulateSentence(_accumulatedSentence ?? "", text, MAX_SENTENCE_LENGTH);

            // 如果没有完整句子，更新累积句子并返回空
            if (completeSentence == null)
            {
                _accumulatedSentence = (_accumulatedSentence + " " + text).Trim();
                return string.Empty;
            }

            // 重置累积句子
            _accumulatedSentence = null;

            string translatedText;
            try
            {
                translatedText = await TranslateAPI.TranslateFunc(completeSentence);
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
                    await SQLiteHistoryLogger.LogTranslationAsync(completeSentence, translatedText, targetLanguage, apiName);
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
