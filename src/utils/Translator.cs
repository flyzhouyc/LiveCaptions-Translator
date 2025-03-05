using System.Diagnostics;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public static class Translator
    {
        public static event Action? TranslationLogged;

        public static async Task<string> Translate(string text, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(text))
            {
                Console.WriteLine("翻译文本为空");
                return string.Empty;
            }

            Console.WriteLine($"开始翻译: {text}");
            string translatedText;
            
            try
            {
    #if DEBUG
                var sw = Stopwatch.StartNew();
    #endif
                // 获取当前使用的翻译API
                var translateFunc = TranslateAPI.TranslateFunc;
                if (translateFunc == null)
                {
                    return "[翻译失败] 未找到可用的翻译API";
                }
                
                // 调用翻译API
                translatedText = await translateFunc(text, token);
                
                if (string.IsNullOrEmpty(translatedText))
                {
                    Console.WriteLine("翻译API返回结果为空");
                    return "[翻译API返回结果为空]";
                }
                
    #if DEBUG
                sw.Stop();
                translatedText = $"[{sw.ElapsedMilliseconds} ms] " + translatedText;
    #endif

                Console.WriteLine($"翻译完成: {translatedText}");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("翻译操作被取消");
                return string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] 翻译失败: {ex.Message}");
                return $"[翻译失败] {ex.Message}";
            }
            
            return translatedText;
        }

        public static async Task Log(string originalText, string translatedText, Setting? setting,
            bool isOverWrite = false, CancellationToken token = default)
        {
            string targetLanguage, apiName;
            if (setting != null)
            {
                targetLanguage = App.Settings.TargetLanguage;
                apiName = App.Settings.ApiName;
            } 
            else
            {
                targetLanguage = "N/A";
                apiName = "N/A";
            }

            try
            {
                if (isOverWrite)
                    await SQLiteHistoryLogger.DeleteLatestTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, translatedText, targetLanguage, apiName, token);
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
            bool isOverWrite = false, CancellationToken token = default)
        {
            try
            {
                if (isOverWrite)
                    await SQLiteHistoryLogger.DeleteLatestTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, "N/A", "N/A", "LogOnly", token);
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
    }
}