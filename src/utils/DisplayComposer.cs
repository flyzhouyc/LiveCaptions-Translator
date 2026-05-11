using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public enum DisplayAction
    {
        Applied,
        SkippedPartial,
        Duplicate,
        NoContent,
        Paused,
    }

    public sealed class DisplayComposer
    {
        private readonly Caption caption;

        public DisplayComposer(Caption caption)
        {
            this.caption = caption;
        }

        // Option A: partial translations are produced (and cached in the queue's
        // Output struct) but never written to UI; only final segments update the
        // display, so overlay/main translated text no longer flickers mid-sentence.
        public DisplayAction Apply(TranslationOutput output, bool logOnly)
        {
            if (logOnly)
            {
                ApplyPaused();
                return DisplayAction.Paused;
            }

            if (!output.IsFinal)
                return DisplayAction.SkippedPartial;

            string translatedText = output.TranslatedText;
            if (string.IsNullOrEmpty(translatedText))
                return DisplayAction.NoContent;

            string trimmedContent = RegexPatterns.NoticePrefix().Replace(translatedText, string.Empty).Trim();
            if (string.IsNullOrEmpty(trimmedContent))
                return DisplayAction.NoContent;

            if (string.CompareOrdinal(caption.TranslatedCaption, translatedText) == 0)
                return DisplayAction.Duplicate;

            ApplyTranslation(translatedText);
            return DisplayAction.Applied;
        }

        private void ApplyPaused()
        {
            caption.TranslatedCaption = string.Empty;
            caption.DisplayTranslatedCaption = "[Paused]";
            caption.OverlayNoticePrefix = "[Paused]";
            caption.OverlayCurrentTranslation = string.Empty;
        }

        private void ApplyTranslation(string translatedText)
        {
            caption.TranslatedCaption = translatedText;
            caption.DisplayTranslatedCaption =
                TextUtil.ShortenDisplaySentence(caption.TranslatedCaption, TextUtil.VERYLONG_THRESHOLD);

            if (caption.TranslatedCaption.Contains("[ERROR]") || caption.TranslatedCaption.Contains("[WARNING]"))
            {
                caption.OverlayCurrentTranslation = caption.TranslatedCaption;
            }
            else
            {
                var match = RegexPatterns.NoticePrefixAndTranslation().Match(caption.TranslatedCaption);
                caption.OverlayNoticePrefix = match.Groups[1].Value.Trim();
                caption.OverlayCurrentTranslation = match.Groups[2].Value.Trim();
            }
        }
    }
}
