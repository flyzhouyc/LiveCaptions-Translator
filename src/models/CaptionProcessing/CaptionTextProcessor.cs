using System;
using System.Text;

namespace LiveCaptionsTranslator.models.CaptionProcessing
{
    public static class CaptionTextProcessor
    {
        public static readonly char[] PUNC_EOS = ".?!。？！".ToCharArray();
        public static readonly char[] PUNC_COMMA = ",，、—\n".ToCharArray();

public static string ProcessFullText(string fullText)
{
    StringBuilder sb = new StringBuilder(fullText);
    foreach (char eos in PUNC_EOS)
    {
        int index = sb.ToString().IndexOf($"{eos}\n");
        while (index != -1)
        {
            sb[index + 1] = eos;
            sb.Remove(index, 1);
            index = sb.ToString().IndexOf($"{eos}\n");
        }
    }
    return sb.ToString();
}

public static int GetLastEOSIndex(string fullText)
{
    if (string.IsNullOrEmpty(fullText)) return -1;

    ReadOnlySpan<char> span = fullText.AsSpan();
    if (PUNC_EOS.AsSpan().Contains(span[^1]))
    {
        span = span[..^1];
        for (int i = span.Length - 1; i >= 0; i--)
        {
            if (PUNC_EOS.AsSpan().Contains(span[i]))
            {
                return i;
            }
        }
    }
    else
    {
        for (int i = span.Length - 1; i >= 0; i--)
        {
            if (PUNC_EOS.AsSpan().Contains(span[i]))
            {
                return i;
            }
        }
    }

    return -1;
}

public static string ExtractLatestCaption(string fullText, int lastEOSIndex)
{
    if (lastEOSIndex < -1) return fullText;

    ReadOnlySpan<char> span = fullText.AsSpan(lastEOSIndex + 1);
    string latestCaption = span.ToString();

    // Ensure appropriate caption length
    while (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < 15)
    {
        lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(PUNC_EOS);
        span = fullText.AsSpan(lastEOSIndex + 1);
        latestCaption = span.ToString();
    }

    while (Encoding.UTF8.GetByteCount(latestCaption) > 170)
    {
        int commaIndex = span.IndexOfAny(PUNC_COMMA);
        if (commaIndex < 0 || commaIndex + 1 == span.Length)
            break;
        span = span[(commaIndex + 1)..];
        latestCaption = span.ToString();
    }

    return latestCaption;
}

private static readonly HashSet<char> PUNC_EOS_SET = new HashSet<char>(PUNC_EOS);
private static readonly HashSet<char> PUNC_COMMA_SET = new HashSet<char>(PUNC_COMMA);

public static bool ShouldTriggerTranslation(string caption, ref int syncCount, int maxSyncInterval)
{
    bool shouldTranslate = false;

    if (PUNC_EOS_SET.Contains(caption[^1]) ||
        PUNC_COMMA_SET.Contains(caption[^1]))
    {
        syncCount = 0;
        shouldTranslate = true;
    }
    else if (syncCount > maxSyncInterval)
    {
        syncCount = 0;
        shouldTranslate = true;
    }

    return shouldTranslate;
}
    }
}
