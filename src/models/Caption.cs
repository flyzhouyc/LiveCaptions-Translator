using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class Caption : INotifyPropertyChanged
    {
        public const int MAX_CONTEXTS = 10;

        private static Caption? instance = null;
        public event PropertyChangedEventHandler? PropertyChanged;

        private string displayOriginalCaption = string.Empty;
        private string displayTranslatedCaption = string.Empty;
        private string overlayOriginalCaption = " ";
        private string overlayCurrentTranslation = " ";
        private string overlayNoticePrefix = " ";
        private readonly object contextsLock = new();
        private readonly Queue<TranslationHistoryEntry> contexts = new(MAX_CONTEXTS);

        public string OriginalCaption { get; set; } = string.Empty;
        public string TranslatedCaption { get; set; } = string.Empty;

        public IEnumerable<TranslationHistoryEntry> AwareContexts => GetPreviousContexts(Translator.Setting.NumContexts);
        public string AwareContextsCaption => GetPreviousText(Translator.Setting.NumContexts, TextType.Caption);

        public IEnumerable<TranslationHistoryEntry> DisplayLogCards =>
            GetPreviousContexts(Translator.Setting.DisplaySentences).Reverse();

        public string DisplayOriginalCaption
        {
            get => displayOriginalCaption;
            set
            {
                if (string.CompareOrdinal(displayOriginalCaption, value) == 0)
                    return;
                displayOriginalCaption = value;
                OnPropertyChanged("DisplayOriginalCaption");
            }
        }
        public string DisplayTranslatedCaption
        {
            get => displayTranslatedCaption;
            set
            {
                if (string.CompareOrdinal(displayTranslatedCaption, value) == 0)
                    return;
                displayTranslatedCaption = value;
                OnPropertyChanged("DisplayTranslatedCaption");
            }
        }

        public string OverlayOriginalCaption
        {
            get => overlayOriginalCaption;
            set
            {
                if (string.CompareOrdinal(overlayOriginalCaption, value) == 0)
                    return;
                overlayOriginalCaption = value;
                OnPropertyChanged("OverlayOriginalCaption");
            }
        }
        public string OverlayNoticePrefix
        {
            get => overlayNoticePrefix;
            set
            {
                if (string.CompareOrdinal(overlayNoticePrefix, value) == 0)
                    return;
                overlayNoticePrefix = value;
                OnPropertyChanged("OverlayNoticePrefix");
            }
        }
        public string OverlayCurrentTranslation
        {
            get => overlayCurrentTranslation;
            set
            {
                if (string.CompareOrdinal(overlayCurrentTranslation, value) == 0)
                    return;
                overlayCurrentTranslation = value;
                OnPropertyChanged("OverlayCurrentTranslation");
            }
        }

        public string OverlayPreviousTranslation
        {
            get
            {
                string previousTranslation = GetPreviousText(Translator.Setting.DisplaySentences, TextType.Translation);
                return string.IsNullOrEmpty(previousTranslation) ? string.Empty : previousTranslation + Environment.NewLine;
            }
        }

        private Caption()
        {
        }

        public int ContextCount
        {
            get
            {
                lock (contextsLock)
                    return contexts.Count;
            }
        }

        public bool HasContexts => ContextCount > 0;

        public void AddContext(TranslationHistoryEntry entry)
        {
            lock (contextsLock)
            {
                if (contexts.Count >= MAX_CONTEXTS)
                    contexts.Dequeue();
                contexts.Enqueue(entry);
            }
        }

        public void ClearContexts()
        {
            lock (contextsLock)
            {
                contexts.Clear();
            }
        }

        public static Caption GetInstance()
        {
            if (instance != null)
                return instance;
            instance = new Caption();
            return instance;
        }

        public string GetPreviousText(int count, TextType textType)
        {
            var previousContexts = GetPreviousContexts(count).ToArray();
            if (previousContexts.Length == 0)
                return string.Empty;

            var builder = new StringBuilder();
            foreach (var entry in previousContexts)
            {
                string current = textType == TextType.Caption ? entry.SourceText : entry.TranslatedText;
                current = RegexPatterns.NoticePrefix().Replace(current, "");
                if (string.IsNullOrWhiteSpace(current))
                    continue;

                if (builder.Length > 0)
                {
                    char last = builder[builder.Length - 1];
                    if (Array.IndexOf(TextUtil.PUNC_EOS, last) == -1)
                        builder.Append(TextUtil.isCJChar(last) ? "。" : ". ");
                    else if (!TextUtil.isCJChar(last))
                        builder.Append(' ');
                }

                builder.Append(current);
            }

            var prev = builder.ToString();

            if (textType == TextType.Translation)
                prev = RegexPatterns.NoticePrefix().Replace(prev, "");
            if (!string.IsNullOrEmpty(prev) && Array.IndexOf(TextUtil.PUNC_EOS, prev[^1]) == -1)
                prev += TextUtil.isCJChar(prev[^1]) ? "。" : ".";
            if (!string.IsNullOrEmpty(prev) && Encoding.UTF8.GetByteCount(prev[^1].ToString()) < 2)
                prev += " ";
            return prev;
        }

        public IEnumerable<TranslationHistoryEntry> GetPreviousContexts(int count)
        {
            if (count <= 0)
                return [];

            TranslationHistoryEntry[] snapshot;
            lock (contextsLock)
                snapshot = contexts.ToArray();

            if (snapshot.Length == 0)
                return [];

            return snapshot
                .Reverse().Take(count).Reverse()
                .Where(entry => entry != null && string.CompareOrdinal(entry.TranslatedText, "N/A") != 0 &&
                                !entry.TranslatedText.Contains("[ERROR]") &&
                                !entry.TranslatedText.Contains("[WARNING]"))
                .ToArray();
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }

    public enum TextType
    {
        Caption,
        Translation
    }
}
