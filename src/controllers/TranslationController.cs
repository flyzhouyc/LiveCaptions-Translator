using System;
using System.Threading.Tasks;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.controllers
{
    public class TranslationController
    {
        public static event Action? TranslationLogged;
        public async Task<string> TranslateAndLogAsync(string text)
        {
            string targetLanguage = App.Settings.TargetLanguage;
            string apiName = App.Settings.ApiName;

            string translatedText;
            try
            {
                translatedText = await TranslateAPI.TranslateFunc(text);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Translation failed: {ex.Message}");
                return $"[Translation Failed] {ex.Message}";
            }

            return translatedText;
        }
    }
}