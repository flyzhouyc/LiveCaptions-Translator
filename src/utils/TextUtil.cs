﻿using System.Text;
using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.utils
{
    public static class TextUtil
    {
        public static readonly char[] PUNC_EOS = ".?!。？！".ToCharArray();
        public static readonly char[] PUNC_COMMA = ",，、—\n".ToCharArray();
        
        public const int SHORT_THRESHOLD = 12;
        public const int MEDIUM_THRESHOLD = 32;
        public const int LONG_THRESHOLD = 160;
        public const int VERYLONG_THRESHOLD = 200;

        public static string ShortenDisplaySentence(string text, int maxByteLength)
        {
            while (Encoding.UTF8.GetByteCount(text) >= maxByteLength)
            {
                int commaIndex = text.IndexOfAny(PUNC_COMMA);
                if (commaIndex < 0 || commaIndex + 1 >= text.Length)
                    break;
                text = text.Substring(commaIndex + 1);
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

                char lastChar = splits[i][^1];
                bool isCJ = (lastChar >= '\u4E00' && lastChar <= '\u9FFF') ||
                            (lastChar >= '\u3400' && lastChar <= '\u4DBF') ||
                            (lastChar >= '\u3040' && lastChar <= '\u30FF');
                bool isKorean = (lastChar >= '\uAC00' && lastChar <= '\uD7AF');

                if (Encoding.UTF8.GetByteCount(splits[i]) >= byteThreshold)
                    splits[i] += isCJ && !isKorean ? "。" : ". ";
                else
                    splits[i] += isCJ && !isKorean ? "——" : "—";
            }
            return string.Join("", splits);
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
            var protocolMatch = Regex.Match(url, @"^(https?:\/\/)");
            string protocol = protocolMatch.Success ? protocolMatch.Value : "";

            string rest = url.Substring(protocol.Length);
            rest = Regex.Replace(rest, @"\/{2,}", "/");
            rest = rest.TrimEnd('/');

            return protocol + rest;
        }
    }
}
