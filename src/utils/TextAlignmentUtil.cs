namespace LiveCaptionsTranslator.utils
{
    public static class TextAlignmentUtil
    {
        public static double LongestCommonSubsequenceSimilarity(string text1, string text2)
        {
            var tokens1 = Tokenize(text1);
            var tokens2 = Tokenize(text2);
            if (tokens1.Count == 0 && tokens2.Count == 0)
                return 1.0;
            if (tokens1.Count == 0 || tokens2.Count == 0)
                return 0.0;

            int lcs = LongestCommonSubsequenceLength(tokens1, tokens2);
            return (double)lcs / Math.Max(tokens1.Count, tokens2.Count);
        }

        public static double DynamicTimeWarpingSimilarity(string text1, string text2)
        {
            var tokens1 = Tokenize(text1);
            var tokens2 = Tokenize(text2);
            if (tokens1.Count == 0 && tokens2.Count == 0)
                return 1.0;
            if (tokens1.Count == 0 || tokens2.Count == 0)
                return 0.0;

            int rows = tokens1.Count + 1;
            int cols = tokens2.Count + 1;
            var previous = new double[cols];
            var current = new double[cols];

            for (int j = 0; j < cols; j++)
                previous[j] = double.PositiveInfinity;
            previous[0] = 0;

            for (int i = 1; i < rows; i++)
            {
                current[0] = double.PositiveInfinity;
                for (int j = 1; j < cols; j++)
                {
                    double cost = TokenDistance(tokens1[i - 1], tokens2[j - 1]);
                    current[j] = cost + Math.Min(
                        Math.Min(previous[j], current[j - 1]),
                        previous[j - 1]);
                }

                (previous, current) = (current, previous);
            }

            double normalizedDistance = previous[cols - 1] / Math.Max(tokens1.Count, tokens2.Count);
            return Math.Clamp(1.0 - normalizedDistance, 0.0, 1.0);
        }

        public static bool IsLikelyRevision(string previousText, string currentText)
        {
            string previous = Normalize(previousText);
            string current = Normalize(currentText);
            if (previous.Length == 0 || current.Length == 0)
                return false;
            if (string.CompareOrdinal(previous, current) == 0)
                return true;
            if (previous.StartsWith(current, StringComparison.Ordinal) ||
                current.StartsWith(previous, StringComparison.Ordinal))
                return true;

            double lcs = LongestCommonSubsequenceSimilarity(previous, current);
            if (lcs >= 0.72)
                return true;

            double dtw = DynamicTimeWarpingSimilarity(previous, current);
            return lcs >= 0.62 && dtw >= 0.55;
        }

        private static int LongestCommonSubsequenceLength(IReadOnlyList<string> tokens1, IReadOnlyList<string> tokens2)
        {
            if (tokens1.Count > tokens2.Count)
                (tokens1, tokens2) = (tokens2, tokens1);

            var previous = new int[tokens1.Count + 1];
            var current = new int[tokens1.Count + 1];

            for (int j = 1; j <= tokens2.Count; j++)
            {
                for (int i = 1; i <= tokens1.Count; i++)
                {
                    current[i] = string.CompareOrdinal(tokens1[i - 1], tokens2[j - 1]) == 0
                        ? previous[i - 1] + 1
                        : Math.Max(previous[i], current[i - 1]);
                }

                (previous, current) = (current, previous);
                Array.Clear(current);
            }

            return previous[tokens1.Count];
        }

        private static double TokenDistance(string token1, string token2)
        {
            if (string.CompareOrdinal(token1, token2) == 0)
                return 0.0;
            if (string.Equals(token1, token2, StringComparison.OrdinalIgnoreCase))
                return 0.15;
            if (token1.Contains(token2, StringComparison.Ordinal) ||
                token2.Contains(token1, StringComparison.Ordinal))
                return 0.45;
            return 1.0;
        }

        private static List<string> Tokenize(string text)
        {
            text = Normalize(text);
            var tokens = new List<string>();

            for (int i = 0; i < text.Length;)
            {
                char ch = text[i];
                if (char.IsWhiteSpace(ch))
                {
                    i++;
                    continue;
                }

                if (char.IsLetterOrDigit(ch))
                {
                    int start = i;
                    i++;
                    while (i < text.Length && char.IsLetterOrDigit(text[i]))
                        i++;
                    tokens.Add(text[start..i]);
                    continue;
                }

                if (char.IsSurrogate(ch) && i + 1 < text.Length && char.IsSurrogatePair(ch, text[i + 1]))
                {
                    tokens.Add(text.Substring(i, 2));
                    i += 2;
                    continue;
                }

                tokens.Add(ch.ToString());
                i++;
            }

            return tokens;
        }

        private static string Normalize(string text)
        {
            return string.Join(" ", text.Trim().ToLowerInvariant().Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
