using System.Text;

namespace LiveCaptionsTranslator.utils
{
    public static class TextUtil
    {
        public static readonly char[] PUNC_EOS = ".?!。？！".ToCharArray();
        public static readonly char[] PUNC_COMMA = ",，、—\n".ToCharArray();

        public const int SHORT_THRESHOLD = 10;
        public const int MEDIUM_THRESHOLD = 40;
        public const int LONG_THRESHOLD = 160;
        public const int VERYLONG_THRESHOLD = 220;

        public const double SIM_THRESHOLD = 0.6;

        public static string ShortenDisplaySentence(string text, int maxByteLength)
        {
            while (Encoding.UTF8.GetByteCount(text) >= maxByteLength)
            {
                int puncIndex = text.IndexOfAny(PUNC_EOS.Concat(PUNC_COMMA).ToArray());
                if (puncIndex < 0 || puncIndex + 1 >= text.Length)
                    break;
                text = text.Substring(puncIndex + 1);
            }
            return text;
        }

        public static string ReplaceNewlines(string text, int byteThreshold)
        {
            string[] splits = text.Split('\n');
            for (int i = 0; i < splits.Length; i++)
            {
                splits[i] = splits[i].Trim();
                if (i == splits.Length - 1)
                    continue;
                if (splits[i].Length == 0)
                    continue;

                char lastChar = splits[i][^1];
                if (Encoding.UTF8.GetByteCount(splits[i]) >= byteThreshold)
                    splits[i] += isCJChar(lastChar) ? "。" : ". ";
                else
                    splits[i] += isCJChar(lastChar) ? "——" : "—";
            }
            return string.Join("", splits);
        }

        public static bool isCJChar(char ch)
        {
            return
                (ch >= '\u4E00' && ch <= '\u9FFF') ||   // CJ Unified Ideographs
                (ch >= '\u3400' && ch <= '\u4DBF') ||   // CJ Unified Ideographs Extension A
                (ch >= '\u3000' && ch <= '\u303F') ||   // CJ Symbols and Punctuation
                (ch >= '\u3040' && ch <= '\u309F') ||   // Hiragana
                (ch >= '\u30A0' && ch <= '\u30FF') ||   // Katakana
                (ch >= '\u31F0' && ch <= '\u31FF') ||   // Katakana Phonetic Extensions
                (ch >= '\u3200' && ch <= '\u32FF') ||   // Enclosed CJ Letters and Months
                (ch >= '\u3300' && ch <= '\u33FF');     // CJ Unit Symbols
        }

        public static double Similarity(string text1, string text2)
        {
            if (text1.StartsWith(text2) || text2.StartsWith(text1))
                return 1.0;
            int distance = LevenshteinDistance(text1, text2);
            int maxLen = Math.Max(text1.Length, text2.Length);
            return (maxLen == 0) ? 1.0 : (1.0 - (double)distance / maxLen);
        }

        public static int LevenshteinDistance(string text1, string text2)
        {
            if (string.IsNullOrEmpty(text1))
                return string.IsNullOrEmpty(text2) ? 0 : text2.Length;
            if (string.IsNullOrEmpty(text2))
                return text1.Length;

            if (text1.Length > text2.Length)
                (text2, text1) = (text1, text2);

            int len1 = text1.Length;
            int len2 = text2.Length;

            int[] previous = new int[len1 + 1];
            int[] current = new int[len1 + 1];

            for (int i = 0; i <= len1; i++)
                previous[i] = i;
            for (int j = 1; j <= len2; j++)
            {
                current[0] = j;
                for (int i = 1; i <= len1; i++)
                {
                    int cost = (text1[i - 1] == text2[j - 1]) ? 0 : 1;
                    current[i] = Math.Min(
                        Math.Min(current[i - 1] + 1, previous[i] + 1),
                        previous[i - 1] + cost);
                }
                (current, previous) = (previous, current);
            }

            return previous[len1];
        }

        public static string NormalizeUrl(string url)
        {
            var protocolMatch = RegexPatterns.HttpPrefix().Match(url);
            string protocol = protocolMatch.Success ? protocolMatch.Value : "";

            string rest = url.Substring(protocol.Length);
            rest = RegexPatterns.MultipleSlashes().Replace(rest, "/");
            rest = rest.TrimEnd('/');

            return protocol + rest;
        }

        // --- Sentence boundary detection helpers ---

        private static readonly HashSet<string> AbbrevWhitelist = new(StringComparer.OrdinalIgnoreCase)
        {
            "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st",
            "etc", "vs", "cf", "eg", "ie", "al",
            "no", "co", "ltd", "inc", "corp",
            "am", "pm", "ad", "bc"
        };

        /// <summary>
        /// Determines whether a period at the given index is a true sentence-ending period,
        /// as opposed to an abbreviation dot (Mr. U.S. 3.14 etc.)
        /// </summary>
        public static bool IsSentenceEndingPeriod(string text, int dotIndex)
        {
            if (dotIndex < 0 || dotIndex >= text.Length || text[dotIndex] != '.')
                return false;

            // Rule 0: Part of ellipsis (... or ..)
            if (dotIndex + 1 < text.Length && text[dotIndex + 1] == '.')
                return false;
            if (dotIndex >= 1 && text[dotIndex - 1] == '.')
                return false;

            // Rule 1: Preceded by a single uppercase letter (U.S. / J.K. / A.I.)
            if (dotIndex >= 1 && char.IsUpper(text[dotIndex - 1]))
            {
                if (dotIndex == 1 || !char.IsLetter(text[dotIndex - 2]))
                    return false;
            }

            // Rule 2: Preceding word is in abbreviation whitelist
            int wordStart = dotIndex - 1;
            while (wordStart >= 0 && char.IsLetter(text[wordStart])) wordStart--;
            wordStart++;
            if (wordStart < dotIndex)
            {
                string word = text.Substring(wordStart, dotIndex - wordStart);
                if (AbbrevWhitelist.Contains(word))
                    return false;
            }

            // Rule 3: Followed by space + lowercase letter — only suppress if sentence is short
            // (ASR often doesn't capitalize after real sentence breaks)
            if (dotIndex + 2 < text.Length && text[dotIndex + 1] == ' ' && char.IsLower(text[dotIndex + 2]))
            {
                // Find previous valid sentence boundary (not just previous .)
                int prevBoundary = text.LastIndexOfAny(new[] { '?', '!', ';', '。', '？', '！' }, dotIndex - 1);
                int sentenceLength = dotIndex - (prevBoundary + 1);
                if (sentenceLength < 40)
                    return false;
            }

            // Rule 4: Decimal or version number (3.14, 1.5.2).
            if (dotIndex >= 1 &&
                dotIndex + 1 < text.Length &&
                char.IsDigit(text[dotIndex - 1]) &&
                char.IsDigit(text[dotIndex + 1]))
                return false;

            // Rule 5: Decimal after spacing normalization (3. 14).
            if (dotIndex >= 1 && char.IsDigit(text[dotIndex - 1]))
            {
                int next = dotIndex + 1;
                while (next < text.Length && char.IsWhiteSpace(text[next]))
                    next++;
                if (next < text.Length && char.IsDigit(text[next]))
                    return false;
            }

            return true;
        }

        private static readonly string[] CoordConjunctions =
            { "and", "but", "so", "or", "yet", "for", "nor" };
        private static readonly string[] RelativePronouns =
            { "which", "who", "whom", "whose", "that" };
        private static readonly string[] SubordConjunctions =
            { "after", "before", "when", "while", "because", "since", "although", "if", "unless", "until" };

        /// <summary>
        /// Determines whether a comma at the given index represents a clause boundary
        /// suitable for splitting a long sentence for translation.
        /// Only triggers when the sentence is already long enough (>= minLength bytes).
        /// </summary>
        public static bool IsClauseBoundary(string text, int commaIndex, int minLength = 80)
        {
            if (commaIndex < 0 || commaIndex >= text.Length || text[commaIndex] != ',')
                return false;

            int prefixBytes = Encoding.UTF8.GetByteCount(text.Substring(0, commaIndex));

            // Only split long sentences
            if (prefixBytes < minLength)
                return false;

            // Get the next word after the comma
            int wordStart = commaIndex + 1;
            while (wordStart < text.Length && char.IsWhiteSpace(text[wordStart])) wordStart++;
            int wordEnd = wordStart;
            while (wordEnd < text.Length && char.IsLetter(text[wordEnd])) wordEnd++;
            if (wordEnd == wordStart)
                return false;

            string nextWord = text.Substring(wordStart, wordEnd - wordStart).ToLowerInvariant();

            // Exclude relative pronouns (subordinate clauses that should stay attached)
            if (RelativePronouns.Contains(nextWord))
                return false;

            // Coordinating conjunctions: split at normal threshold (80 bytes)
            if (CoordConjunctions.Contains(nextWord))
            {
                // Exclude enumerations: if there's another comma within 50 chars before, likely a list
                int lookback = Math.Max(0, commaIndex - 50);
                int otherCommas = 0;
                for (int i = lookback; i < commaIndex; i++)
                    if (text[i] == ',') otherCommas++;
                if (otherCommas >= 1)
                    return false;

                return true;
            }

            // Subordinating conjunctions: only split for very long sentences (>= 120 bytes)
            if (prefixBytes >= 120 && SubordConjunctions.Contains(nextWord))
                return true;

            return false;
        }

        /// <summary>
        /// Finds the last valid sentence-ending position in text, considering
        /// abbreviation filtering and clause boundary detection.
        /// Returns -1 if no valid boundary found.
        /// </summary>
        public static int FindLastSentenceBoundary(string text)
        {
            for (int i = text.Length - 1; i >= 0; i--)
            {
                if (IsSentenceBoundaryAt(text, i))
                    return i;
            }

            // Fallback: try clause boundary (comma + conjunction) for long text
            for (int i = text.Length - 1; i >= 0; i--)
            {
                if (text[i] == ',' && IsClauseBoundary(text, i))
                    return i;
            }

            return -1;
        }

        public static IEnumerable<int> FindSentenceBoundaries(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (IsSentenceBoundaryAt(text, i))
                    yield return i;
            }
        }

        public static bool HasTerminalSentenceBoundary(string text)
        {
            return !string.IsNullOrEmpty(text) && IsSentenceBoundaryAt(text, text.Length - 1);
        }

        public static bool IsSentenceBoundaryAt(string text, int index)
        {
            if (index < 0 || index >= text.Length)
                return false;

            char c = text[index];
            if (c == '?' || c == '!' || c == '。' || c == '？' || c == '！')
                return true;
            return c == '.' && IsSentenceEndingPeriod(text, index);
        }
    }
}
