namespace LiveCaptionsTranslator.models
{
    public sealed class CaptionSegment
    {
        public CaptionSegment(
            long id,
            int index,
            string sourceText,
            bool isPartial,
            DateTime createdAtUtc,
            DateTime stableSinceUtc,
            string trigger)
        {
            Id = id;
            Index = index;
            SourceText = sourceText;
            IsPartial = isPartial;
            CreatedAtUtc = createdAtUtc;
            StableSinceUtc = stableSinceUtc;
            Trigger = trigger;
        }

        public long Id { get; }
        public int Index { get; }
        public string SourceText { get; }
        public bool IsPartial { get; }
        public bool IsFinal => !IsPartial;
        public DateTime CreatedAtUtc { get; }
        public DateTime StableSinceUtc { get; }
        public string Trigger { get; }
    }

    public sealed class CaptionStabilizerUpdate
    {
        public CaptionStabilizerUpdate(
            string latestCaption,
            string translationCandidate,
            int latestCaptionBoundaryIndex,
            IReadOnlyList<CaptionSegment> segments)
        {
            LatestCaption = latestCaption;
            TranslationCandidate = translationCandidate;
            LatestCaptionBoundaryIndex = latestCaptionBoundaryIndex;
            Segments = segments;
        }

        public static CaptionStabilizerUpdate Empty { get; } =
            new(string.Empty, string.Empty, -1, Array.Empty<CaptionSegment>());

        public string LatestCaption { get; }
        public string TranslationCandidate { get; }
        public int LatestCaptionBoundaryIndex { get; }
        public IReadOnlyList<CaptionSegment> Segments { get; }
    }

    public readonly struct TranslationOutput
    {
        public TranslationOutput(
            long segmentId,
            int segmentIndex,
            string sourceText,
            string translatedText,
            bool isFinal,
            string trigger)
        {
            SegmentId = segmentId;
            SegmentIndex = segmentIndex;
            SourceText = sourceText;
            TranslatedText = translatedText;
            IsFinal = isFinal;
            Trigger = trigger;
        }

        public static TranslationOutput Empty { get; } =
            new(0, 0, string.Empty, string.Empty, false, string.Empty);

        public long SegmentId { get; }
        public int SegmentIndex { get; }
        public string SourceText { get; }
        public string TranslatedText { get; }
        public bool IsFinal { get; }
        public string Trigger { get; }
    }
}
