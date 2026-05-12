using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    /// <summary>
    /// Aligns translations with their corresponding source segments by segment ID.
    /// 
    /// Problem: Live Captions provides a continuous English stream; translations arrive
    /// asynchronously with delay. The overlay must show Chinese synced to the correct English.
    /// 
    /// Solution: Track source segments with timestamps and update translations by the
    /// stable CaptionSegment.Id generated before enqueueing translation work.
    /// </summary>
    public sealed class CaptionAligner
    {
        private const int MaxSegments = 20;
        private const double MatchThreshold = 0.65;

        private readonly List<AlignedSegment> segments = new();
        private readonly object lockObj = new();

        /// <summary>
        /// Represents an English segment that may or may not have a paired translation.
        /// </summary>
        public sealed class AlignedSegment
        {
            public long Id { get; }
            public string SourceText { get; private set; }
            public DateTime CreatedAtUtc { get; }
            public string? TranslatedText { get; set; }
            public DateTime? TranslatedAtUtc { get; set; }
            public bool IsFinal { get; set; }
            public bool IsSuperseded { get; set; }

            public AlignedSegment(long id, string sourceText, bool isFinal, DateTime createdAtUtc)
            {
                Id = id;
                SourceText = sourceText;
                IsFinal = isFinal;
                CreatedAtUtc = createdAtUtc;
            }

            public bool HasTranslation => !string.IsNullOrEmpty(TranslatedText);

            public void UpdateSource(string sourceText, bool isFinal)
            {
                SourceText = sourceText;
                IsFinal = IsFinal || isFinal;
            }
        }

        /// <summary>
        /// Add a new source segment to the alignment tracking.
        /// </summary>
        public void AddSourceSegment(CaptionSegment captionSegment)
        {
            lock (lockObj)
            {
                var existing = segments.FirstOrDefault(s => s.Id == captionSegment.Id);
                if (existing != null)
                {
                    existing.UpdateSource(captionSegment.SourceText, captionSegment.IsFinal);
                    return;
                }

                for (int i = segments.Count - 1; i >= 0; i--)
                {
                    var previous = segments[i];
                    if (previous.IsFinal || previous.IsSuperseded)
                        continue;
                    if (TextAlignmentUtil.IsLikelyRevision(previous.SourceText, captionSegment.SourceText))
                        previous.IsSuperseded = true;
                }

                var segment = new AlignedSegment(
                    captionSegment.Id,
                    captionSegment.SourceText,
                    captionSegment.IsFinal,
                    captionSegment.CreatedAtUtc);
                segments.Add(segment);

                while (segments.Count > MaxSegments)
                    segments.RemoveAt(0);
            }
        }

        /// <summary>
        /// Update the translation for the exact segment that produced this output.
        /// </summary>
        public bool UpdateTranslation(long segmentId, string translatedText, bool isFinal)
        {
            if (string.IsNullOrWhiteSpace(translatedText))
                return false;
            if (translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]"))
                return false;

            lock (lockObj)
            {
                var segment = segments.FirstOrDefault(s => s.Id == segmentId);
                if (segment == null || segment.IsSuperseded)
                    return false;

                segment.TranslatedText = translatedText;
                segment.TranslatedAtUtc = DateTime.UtcNow;
                if (isFinal)
                    segment.IsFinal = true;
                return true;
            }
        }

        /// <summary>
        /// Mark a segment as finalized (translation is complete).
        /// </summary>
        public void FinalizeSegment(long segmentId)
        {
            lock (lockObj)
            {
                var seg = segments.FirstOrDefault(s => s.Id == segmentId);
                if (seg != null)
                    seg.IsFinal = true;
            }
        }

        /// <summary>
        /// Get the current aligned display data for the overlay.
        /// Returns the most recent source segment and its aligned translation.
        /// Also returns previous aligned segments for history display.
        /// </summary>
        public AlignedDisplay GetAlignedDisplay(long currentSegmentId, int historyCount = 2)
        {
            lock (lockObj)
            {
                var current = segments.FirstOrDefault(s => s.Id == currentSegmentId && !s.IsSuperseded) ??
                              segments.LastOrDefault(s => !s.IsSuperseded);
                if (current == null)
                    return AlignedDisplay.Empty;

                var history = new List<AlignedPair>();
                var finalsWithTranslation = segments
                    .Where(s => s.Id != current.Id && s.IsFinal && s.HasTranslation && !s.IsSuperseded)
                    .Reverse()
                    .Take(Math.Max(0, historyCount))
                    .Reverse()
                    .ToList();

                foreach (var seg in finalsWithTranslation)
                    history.Add(new AlignedPair(seg.SourceText, seg.TranslatedText!));

                string currentSource = current.SourceText;
                string? currentTranslation = current.TranslatedText;

                return new AlignedDisplay(
                    currentSource,
                    currentTranslation ?? string.Empty,
                    history);
            }
        }

        /// <summary>
        /// Get the source text that should be shown as the "previous" original caption
        /// in the overlay, i.e., finalized English sentences before the current one.
        /// </summary>
        public string GetPreviousOriginalText(int count = 2)
        {
            lock (lockObj)
            {
                var previous = segments
                    .Where(s => s.IsFinal && !s.IsSuperseded)
                    .Reverse()
                    .Take(count)
                    .Reverse()
                    .Select(s => s.SourceText)
                    .ToList();

                return string.Join(" ", previous);
            }
        }

        /// <summary>
        /// Get the aligned translation for a specific source text.
        /// Uses LCS to find the best matching segment.
        /// </summary>
        public string? FindTranslationForSource(string sourceText)
        {
            lock (lockObj)
            {
                double bestScore = 0;
                string? bestTranslation = null;

                foreach (var seg in segments)
                {
                    if (!seg.HasTranslation) continue;
                    double score = Math.Max(
                        TextAlignmentUtil.LongestCommonSubsequenceSimilarity(sourceText, seg.SourceText),
                        TextAlignmentUtil.DynamicTimeWarpingSimilarity(sourceText, seg.SourceText));
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestTranslation = seg.TranslatedText;
                    }
                }

                return bestScore >= MatchThreshold ? bestTranslation : null;
            }
        }
        /// <summary>
        /// Clear all segments (e.g., when LiveCaptions restarts).
        /// </summary>
        public void Clear()
        {
            lock (lockObj)
            {
                segments.Clear();
            }
        }
    }

    /// <summary>
    /// A paired English source + Chinese translation.
    /// </summary>
    public sealed class AlignedPair
    {
        public string SourceText { get; }
        public string TranslatedText { get; }

        public AlignedPair(string sourceText, string translatedText)
        {
            SourceText = sourceText;
            TranslatedText = translatedText;
        }
    }

    /// <summary>
    /// Display-ready aligned caption data for the overlay.
    /// </summary>
    public sealed class AlignedDisplay
    {
        public static readonly AlignedDisplay Empty = new(string.Empty, string.Empty, Array.Empty<AlignedPair>());

        public string CurrentSource { get; }
        public string CurrentTranslation { get; }
        public IReadOnlyList<AlignedPair> History { get; }

        /// <summary>
        /// Formatted text for the overlay's original caption area,
        /// showing previous finalized English + current English.
        /// </summary>
        public string OverlayOriginalText
        {
            get
            {
                if (History.Count == 0)
                    return CurrentSource;

                var prev = string.Join(Environment.NewLine, History.Select(h => h.SourceText));
                return prev + Environment.NewLine + CurrentSource;
            }
        }

        /// <summary>
        /// Formatted text for the overlay's translation area,
        /// showing previous finalized translations + current translation.
        /// </summary>
        public string OverlayTranslatedText
        {
            get
            {
                if (string.IsNullOrEmpty(CurrentTranslation) && History.Count == 0)
                    return string.Empty;

                var parts = new List<string>();
                foreach (var h in History)
                    parts.Add(h.TranslatedText);

                if (!string.IsNullOrEmpty(CurrentTranslation))
                    parts.Add(CurrentTranslation);

                return string.Join(Environment.NewLine, parts);
            }
        }

        public AlignedDisplay(string currentSource, string currentTranslation, IReadOnlyList<AlignedPair> history)
        {
            CurrentSource = currentSource;
            CurrentTranslation = currentTranslation;
            History = history;
        }
    }
}
