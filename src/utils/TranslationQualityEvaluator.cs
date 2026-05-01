using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.utils
{
    public class TranslationQualityEvaluator
    {
        public const int GoodQualityThreshold = 70;

        private static readonly ConcurrentDictionary<string, int> EvaluationHistory = new();

        private static readonly Regex rxNumbersMatch = new(@"\d+", RegexOptions.Compiled);
        private static readonly Regex rxEmailMatch = new(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled);
        private static readonly Regex rxUrlMatch = new(@"https?://\S+|www\.\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex rxCodeTokenMatch = new(@"\b[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.Compiled);
        private static readonly Regex rxQuotedTokenMatch = new("[\"']([A-Za-z0-9_\\-.]{2,})[\"']", RegexOptions.Compiled);
        private static readonly Regex rxDuplicateWordsPattern = new(@"\b(\w+)\b(?:\s+\1\b)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex rxConsecutivePunctuation = new(@"[,.?!;\uFF0C\u3002\uFF1F\uFF01\uFF1B]{2,}", RegexOptions.Compiled);

        public static int EvaluateQuality(string sourceText, string translation)
        {
            if (string.IsNullOrEmpty(sourceText) || string.IsNullOrEmpty(translation))
                return 0;

            int score = 100;

            double lengthRatio = (double)translation.Length / sourceText.Length;
            if (lengthRatio < 0.5 || lengthRatio > 2.0)
                score -= 20;

            if (!CheckEntityPreservation(sourceText, translation))
                score -= 15;

            if (!CheckFormatConsistency(sourceText, translation))
                score -= 10;

            if (!CheckEndingPunctuation(sourceText, translation))
                score -= 5;

            score -= (100 - EstimateTextFluency(translation)) / 5;

            return Math.Clamp(score, 0, 100);
        }

        public static int EvaluateQualityEnhanced(string sourceText, string translation)
        {
            if (string.IsNullOrEmpty(sourceText) || string.IsNullOrEmpty(translation))
                return 0;

            int score = 100;

            double lengthRatio = (double)translation.Length / sourceText.Length;
            if (lengthRatio < 0.5 || lengthRatio > 2.0)
                score -= 20;

            int entityRetentionScore = EvaluateEntityRetentionScore(sourceText, translation);
            if (entityRetentionScore < 60)
                score -= 25;
            else if (entityRetentionScore < 75)
                score -= 15;
            else if (entityRetentionScore < 90)
                score -= 8;

            if (!CheckFormatConsistencyEnhanced(sourceText, translation))
                score -= 12;

            if (!CheckEndingPunctuation(sourceText, translation))
                score -= 5;

            score -= (100 - EstimateTextFluency(translation)) / 5;

            return Math.Clamp(score, 0, 100);
        }

        public static int EvaluateQualityLightweight(string sourceText, string translation)
        {
            if (string.IsNullOrEmpty(sourceText) || string.IsNullOrEmpty(translation))
                return 0;

            int score = 100;

            double lengthRatio = (double)translation.Length / sourceText.Length;
            if (lengthRatio < 0.5 || lengthRatio > 2.0)
                score -= 20;

            if (rxNumbersMatch.Matches(sourceText).Count > 0)
            {
                var sourceNumbers = rxNumbersMatch.Matches(sourceText).Cast<Match>().Select(m => m.Value).ToArray();
                var translationNumbers = rxNumbersMatch.Matches(translation).Cast<Match>().Select(m => m.Value).ToArray();

                if (sourceNumbers.Length > 0 && translationNumbers.Length < sourceNumbers.Length / 2)
                    score -= 15;
            }

            bool isSourceQuestion = sourceText.Contains('?') || sourceText.Contains('\uFF1F');
            bool isTranslationQuestion = translation.Contains('?') || translation.Contains('\uFF1F');

            if (isSourceQuestion != isTranslationQuestion)
                score -= 10;

            bool isSourceExclamation = sourceText.Contains('!') || sourceText.Contains('\uFF01');
            bool isTranslationExclamation = translation.Contains('!') || translation.Contains('\uFF01');

            if (isSourceExclamation != isTranslationExclamation)
                score -= 10;

            bool sourceHasEnding = HasEndingPunctuation(sourceText);
            bool translationHasEnding = HasEndingPunctuation(translation);

            if (sourceHasEnding && !translationHasEnding)
                score -= 5;

            if (rxDuplicateWordsPattern.IsMatch(translation))
                score -= 10;

            if (translation.Length < 10 && translation.Length < sourceText.Length / 3)
                score -= 10;

            return Math.Clamp(score, 0, 100);
        }

        private static bool CheckEntityPreservation(string sourceText, string translation)
        {
            return EvaluateEntityRetentionScore(sourceText, translation) >= 70;
        }

        private static int EvaluateEntityRetentionScore(string sourceText, string translation)
        {
            var tokens = ExtractProtectedTokens(sourceText);
            if (tokens.Count == 0)
                return 100;

            int preserved = tokens.Count(t => ContainsToken(translation, t));
            double ratio = (double)preserved / tokens.Count;

            if (ratio >= 0.9) return 100;
            if (ratio >= 0.75) return 85;
            if (ratio >= 0.6) return 70;
            if (ratio >= 0.4) return 55;
            return 30;
        }

        private static HashSet<string> ExtractProtectedTokens(string sourceText)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in rxNumbersMatch.Matches(sourceText))
                tokens.Add(match.Value);

            foreach (Match match in rxEmailMatch.Matches(sourceText))
                tokens.Add(match.Value);

            foreach (Match match in rxUrlMatch.Matches(sourceText))
                tokens.Add(match.Value);

            foreach (Match match in rxQuotedTokenMatch.Matches(sourceText))
            {
                if (match.Groups.Count > 1)
                    tokens.Add(match.Groups[1].Value);
            }

            foreach (Match match in rxCodeTokenMatch.Matches(sourceText))
            {
                string token = match.Value;
                if (IsLikelyTermToken(token))
                    tokens.Add(token);
            }

            return tokens;
        }

        private static bool ContainsToken(string text, string token)
        {
            if (string.IsNullOrEmpty(token))
                return true;

            if (token.All(char.IsDigit))
                return text.Contains(token, StringComparison.Ordinal);

            return text.Contains(token, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLikelyTermToken(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length < 2)
                return false;

            if (token.Any(char.IsDigit) || token.Contains('_'))
                return true;

            bool hasUpper = token.Any(char.IsUpper);
            bool hasLower = token.Any(char.IsLower);

            if (hasUpper && hasLower)
                return true;

            return token.All(char.IsUpper) && token.Length >= 2;
        }

        private static bool CheckFormatConsistencyEnhanced(string sourceText, string translation)
        {
            return CheckFormatConsistency(sourceText, translation) &&
                   CheckLineBreakConsistency(sourceText, translation) &&
                   CheckBracketBalance(sourceText, translation) &&
                   CheckQuoteBalance(sourceText, translation);
        }

        private static bool CheckLineBreakConsistency(string sourceText, string translation)
        {
            int sourceLines = sourceText.Count(c => c == '\n');
            int translationLines = translation.Count(c => c == '\n');

            if (sourceLines == 0 && translationLines == 0)
                return true;

            return Math.Abs(sourceLines - translationLines) <= 1;
        }

        private static bool CheckBracketBalance(string sourceText, string translation)
        {
            var pairs = new (char open, char close)[]
            {
                ('(', ')'),
                ('[', ']'),
                ('{', '}'),
                ('<', '>')
            };

            foreach (var pair in pairs)
            {
                int sourceOpen = sourceText.Count(c => c == pair.open);
                int sourceClose = sourceText.Count(c => c == pair.close);
                int translationOpen = translation.Count(c => c == pair.open);
                int translationClose = translation.Count(c => c == pair.close);

                if (sourceOpen == 0 && sourceClose == 0)
                    continue;

                if (sourceOpen != translationOpen || sourceClose != translationClose)
                    return false;
            }

            return true;
        }

        private static bool CheckQuoteBalance(string sourceText, string translation)
        {
            int sourceDoubleQuotes = sourceText.Count(c => c == '"');
            int translationDoubleQuotes = translation.Count(c => c == '"');
            if (sourceDoubleQuotes > 0 && sourceDoubleQuotes != translationDoubleQuotes)
                return false;

            int sourceSingleQuotes = sourceText.Count(c => c == '\'');
            int translationSingleQuotes = translation.Count(c => c == '\'');
            if (sourceSingleQuotes > 0 && sourceSingleQuotes != translationSingleQuotes)
                return false;

            return true;
        }

        private static bool CheckFormatConsistency(string sourceText, string translation)
        {
            bool isSourceQuestion = sourceText.EndsWith('?') || sourceText.EndsWith('\uFF1F');
            bool isTranslationQuestion = translation.EndsWith('?') || translation.EndsWith('\uFF1F');

            if (isSourceQuestion != isTranslationQuestion)
                return false;

            bool isSourceExclamation = sourceText.EndsWith('!') || sourceText.EndsWith('\uFF01');
            bool isTranslationExclamation = translation.EndsWith('!') || translation.EndsWith('\uFF01');

            return isSourceExclamation == isTranslationExclamation;
        }

        private static bool CheckEndingPunctuation(string sourceText, string translation)
        {
            return HasEndingPunctuation(sourceText) == HasEndingPunctuation(translation);
        }

        private static bool HasEndingPunctuation(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            char c = text[^1];
            return c is '.' or '?' or '!' or '\u3002' or '\uFF1F' or '\uFF01';
        }

        private static int EstimateTextFluency(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            int score = 100;

            if (rxDuplicateWordsPattern.IsMatch(text))
                score -= 15;

            if (text.Length < 10)
                score -= 10;

            if (rxConsecutivePunctuation.IsMatch(text))
                score -= 10;

            return Math.Clamp(score, 0, 100);
        }

        public static void RecordQualityForAPI(string apiName, int qualityScore, string sourceLanguage, string targetLanguage)
        {
            string key = $"{apiName}_{sourceLanguage}_{targetLanguage}";

            EvaluationHistory.AddOrUpdate(
                key,
                qualityScore,
                (_, existingScore) => (int)(existingScore * 0.9 + qualityScore * 0.1)
            );
        }

        public static string? GetBestAPIForLanguagePair(string sourceLanguage, string targetLanguage, List<string> availableAPIs)
        {
            var candidates = availableAPIs
                .Select(api => new
                {
                    API = api,
                    Score = EvaluationHistory.TryGetValue($"{api}_{sourceLanguage}_{targetLanguage}", out int score) ? score : 50
                })
                .OrderByDescending(x => x.Score)
                .ToList();

            return candidates.FirstOrDefault()?.API ?? availableAPIs.FirstOrDefault();
        }

        public static (string ImprovedTranslation, string ApiSuggestion) GetImprovedTranslation(
            string originalTranslation, string sourceText, string currentApi, int qualityScore)
        {
            if (qualityScore >= GoodQualityThreshold)
                return (originalTranslation, currentApi);

            string improvedTranslation = originalTranslation;
            string apiSuggestion = currentApi;

            if (rxNumbersMatch.Matches(sourceText).Count > 0)
            {
                var sourceNumbers = rxNumbersMatch.Matches(sourceText).Cast<Match>().Select(m => m.Value).ToArray();
                var translationNumbers = rxNumbersMatch.Matches(originalTranslation).Cast<Match>().Select(m => m.Value).ToArray();

                if (sourceNumbers.Length > 0 &&
                    translationNumbers.Length < sourceNumbers.Length / 2 &&
                    (currentApi == "OpenAI" || currentApi == "Ollama"))
                {
                    apiSuggestion = "Google";
                }
            }

            if (!CheckFormatConsistency(sourceText, originalTranslation))
            {
                bool sourceIsQuestion = sourceText.EndsWith('?') || sourceText.EndsWith('\uFF1F');
                bool translationIsQuestion = originalTranslation.EndsWith('?') || originalTranslation.EndsWith('\uFF1F');
                bool sourceIsExclamation = sourceText.EndsWith('!') || sourceText.EndsWith('\uFF01');
                bool translationIsExclamation = originalTranslation.EndsWith('!') || originalTranslation.EndsWith('\uFF01');

                if (sourceIsQuestion && !translationIsQuestion)
                    improvedTranslation += "?";
                else if (sourceIsExclamation && !translationIsExclamation)
                    improvedTranslation += "!";
            }

            if (TextUtil.isCJChar(sourceText.FirstOrDefault()) &&
                currentApi != "Google" &&
                currentApi != "Google2")
            {
                apiSuggestion = "Google";
            }

            return (improvedTranslation, apiSuggestion);
        }
    }
}
