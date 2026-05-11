using System.Text;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public sealed class CaptionSegmentStabilizer
    {
        private const int RecentFinalLimit = 48;

        private readonly Queue<string> recentFinalOrder = new();
        private readonly HashSet<string> recentFinalKeys = new(StringComparer.Ordinal);

        private long nextId = 1;
        private int nextIndex = 1;
        private bool hasSeenSnapshot;
        private string activePartialText = string.Empty;
        private string lastPartialEnqueuedText = string.Empty;
        private DateTime activePartialChangedUtc = DateTime.MinValue;
        private DateTime lastPartialEnqueuedUtc = DateTime.MinValue;

        public CaptionStabilizerUpdate Process(
            string fullText,
            TimeSpan partialStableDelay,
            TimeSpan partialTranslationInterval,
            TimeSpan idleFinalDelay)
        {
            if (string.IsNullOrWhiteSpace(fullText))
                return CaptionStabilizerUpdate.Empty;

            DateTime nowUtc = DateTime.UtcNow;
            var segments = new List<CaptionSegment>();
            string latestCaption = ExtractLatestCaption(fullText, out int latestBoundaryIndex);
            string translationCandidate = ExtractTranslationCandidate(latestCaption);

            var completedSentences = ExtractCompletedSentences(fullText);
            if (!hasSeenSnapshot)
            {
                SeedInitialCompletedSentences(fullText, completedSentences, segments, nowUtc);
                hasSeenSnapshot = true;
            }
            else
            {
                foreach (string sentence in completedSentences)
                    TryAddFinalSegment(sentence, "punctuation", segments, nowUtc);
            }

            ProcessActiveCandidate(
                translationCandidate,
                partialStableDelay,
                partialTranslationInterval,
                idleFinalDelay,
                segments,
                nowUtc);

            return new CaptionStabilizerUpdate(
                latestCaption,
                translationCandidate,
                latestBoundaryIndex,
                segments);
        }

        private void SeedInitialCompletedSentences(
            string fullText,
            IReadOnlyList<string> completedSentences,
            List<CaptionSegment> segments,
            DateTime nowUtc)
        {
            if (completedSentences.Count == 0)
                return;

            bool endsWithSentenceBoundary = TextUtil.IsSentenceBoundaryAt(fullText, fullText.Length - 1);
            int lastIndexToSeed = endsWithSentenceBoundary ? completedSentences.Count - 2 : completedSentences.Count - 1;

            for (int i = 0; i <= lastIndexToSeed; i++)
                RememberFinal(completedSentences[i]);

            if (endsWithSentenceBoundary)
                TryAddFinalSegment(completedSentences[^1], "punctuation", segments, nowUtc);
        }

        private void ProcessActiveCandidate(
            string translationCandidate,
            TimeSpan partialStableDelay,
            TimeSpan partialTranslationInterval,
            TimeSpan idleFinalDelay,
            List<CaptionSegment> segments,
            DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(translationCandidate))
                return;

            if (TextUtil.HasTerminalSentenceBoundary(translationCandidate))
                return;

            if (string.CompareOrdinal(activePartialText, translationCandidate) != 0)
            {
                activePartialText = translationCandidate;
                activePartialChangedUtc = nowUtc;
                return;
            }

            TimeSpan stableDuration = nowUtc - activePartialChangedUtc;
            if (stableDuration < partialStableDelay)
                return;

            if (Encoding.UTF8.GetByteCount(translationCandidate) < TextUtil.SHORT_THRESHOLD)
                return;

            if (stableDuration >= idleFinalDelay)
            {
                TryAddFinalSegment(translationCandidate, "idleInterval", segments, nowUtc);
                lastPartialEnqueuedText = string.Empty;
                return;
            }

            bool intervalElapsed = nowUtc - lastPartialEnqueuedUtc >= partialTranslationInterval;
            if (!intervalElapsed && string.CompareOrdinal(lastPartialEnqueuedText, translationCandidate) == 0)
                return;

            segments.Add(CreateSegment(translationCandidate, isPartial: true, "partialStable", nowUtc));
            lastPartialEnqueuedText = translationCandidate;
            lastPartialEnqueuedUtc = nowUtc;
        }

        private void TryAddFinalSegment(
            string text,
            string trigger,
            List<CaptionSegment> segments,
            DateTime nowUtc)
        {
            text = text.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            string key = NormalizeFinalKey(text);
            if (recentFinalKeys.Contains(key))
                return;

            RememberFinal(text);
            segments.Add(CreateSegment(text, isPartial: false, trigger, nowUtc));
        }

        private CaptionSegment CreateSegment(string text, bool isPartial, string trigger, DateTime nowUtc)
        {
            return new CaptionSegment(
                nextId++,
                nextIndex++,
                text.Trim(),
                isPartial,
                nowUtc,
                activePartialChangedUtc == DateTime.MinValue ? nowUtc : activePartialChangedUtc,
                trigger);
        }

        private void RememberFinal(string text)
        {
            string key = NormalizeFinalKey(text);
            if (recentFinalKeys.Contains(key))
                return;

            recentFinalKeys.Add(key);
            recentFinalOrder.Enqueue(key);

            while (recentFinalOrder.Count > RecentFinalLimit)
            {
                string removed = recentFinalOrder.Dequeue();
                recentFinalKeys.Remove(removed);
            }
        }

        private static string ExtractLatestCaption(string fullText, out int boundaryIndex)
        {
            boundaryIndex = GetBoundaryBeforeLatestCaption(fullText);
            string latestCaption = fullText.Substring(boundaryIndex + 1);

            if (boundaryIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < TextUtil.SHORT_THRESHOLD)
            {
                int previousBoundary = TextUtil.FindLastSentenceBoundary(fullText[..boundaryIndex]);
                if (previousBoundary >= 0)
                {
                    boundaryIndex = previousBoundary;
                    latestCaption = fullText.Substring(boundaryIndex + 1);
                }
            }

            return latestCaption.TrimStart();
        }

        private static string ExtractTranslationCandidate(string latestCaption)
        {
            int lastBoundary = TextUtil.FindLastSentenceBoundary(latestCaption);
            if (lastBoundary >= 0)
                return latestCaption.Substring(0, lastBoundary + 1).Trim();

            return latestCaption.Trim();
        }

        private static int GetBoundaryBeforeLatestCaption(string fullText)
        {
            if (string.IsNullOrEmpty(fullText))
                return -1;

            if (TextUtil.IsSentenceBoundaryAt(fullText, fullText.Length - 1))
                return TextUtil.FindLastSentenceBoundary(fullText[..^1]);

            return TextUtil.FindLastSentenceBoundary(fullText);
        }

        private static List<string> ExtractCompletedSentences(string fullText)
        {
            var sentences = new List<string>();
            int start = 0;

            foreach (int boundary in TextUtil.FindSentenceBoundaries(fullText))
            {
                if (boundary < start)
                    continue;

                string sentence = fullText.Substring(start, boundary - start + 1).Trim();
                if (!string.IsNullOrWhiteSpace(sentence))
                    sentences.Add(sentence);

                start = boundary + 1;
            }

            return sentences;
        }

        private static string NormalizeFinalKey(string text)
        {
            return string.Join(" ", text.Trim().Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
