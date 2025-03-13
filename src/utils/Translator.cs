using System.Diagnostics;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public static class Translator
    {
        public static event Action? TranslationLogged;

        public static async Task<string> Translate(string text, CancellationToken token = default)
        {
            string translatedText;
            try
            {
                Stopwatch? sw = null;
                if (App.Setting.MainWindow.LatencyShow)
                {
                    sw = Stopwatch.StartNew();
                }

                translatedText = await TranslateAPI.TranslateFunction(text, token);

                if (sw != null)
                {
                    sw.Stop();
                    translatedText = $"[{sw.ElapsedMilliseconds} ms] " + translatedText;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Translation failed: {ex.Message}");
                return $"[Translation Failed] {ex.Message}";
            }
            return translatedText;
        }

        public static async Task Log(string originalText, string translatedText, 
    bool isOverwrite = false, CancellationToken token = default)
        {
            // 防止空字符串日志问题
            if (string.IsNullOrEmpty(originalText))
                return;
                
            // 如果翻译结果为空，使用占位符
            string logTranslatedText = string.IsNullOrEmpty(translatedText) ? 
                "[No translation result]" : translatedText;
            
            string targetLanguage, apiName;
            if (App.Setting != null)
            {
                targetLanguage = App.Setting.TargetLanguage;
                apiName = App.Setting.ApiName;
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
                    
                await SQLiteHistoryLogger.LogTranslation(originalText, logTranslatedText, targetLanguage, apiName);
                
                // 确保事件被触发
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
}