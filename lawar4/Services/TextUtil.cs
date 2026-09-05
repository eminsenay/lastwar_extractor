using System.Globalization;
using System.Text;

namespace lawar4.Services;

public static class TextUtil
{
    public static readonly string[] DayOrder =
        { "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };

    /// <summary>NFKC normalize, casefold, map dotless-i to i, keep only alphanumerics.</summary>
    public static string NormalizeName(string value)
    {
        string text = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        text = text.Replace('\u0131', 'i'); // ı -> i
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Ratcliff/Obershelp similarity ratio, equivalent to Python difflib
    /// SequenceMatcher(None, a, b).ratio(), used only for fuzzy suggestion ordering.
    /// </summary>
    public static double SequenceRatio(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
            return 1.0;
        int total = a.Length + b.Length;
        if (total == 0)
            return 1.0;
        int matches = MatchingChars(a.AsSpan(), b.AsSpan());
        return 2.0 * matches / total;
    }

    private static int MatchingChars(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.IsEmpty || b.IsEmpty)
            return 0;

        // Find the longest matching block.
        int bestI = 0, bestJ = 0, bestLen = 0;
        for (int i = 0; i < a.Length; i++)
        {
            for (int j = 0; j < b.Length; j++)
            {
                int len = 0;
                while (i + len < a.Length && j + len < b.Length && a[i + len] == b[j + len])
                    len++;
                if (len > bestLen)
                {
                    bestLen = len;
                    bestI = i;
                    bestJ = j;
                }
            }
        }

        if (bestLen == 0)
            return 0;

        return bestLen
            + MatchingChars(a[..bestI], b[..bestJ])
            + MatchingChars(a[(bestI + bestLen)..], b[(bestJ + bestLen)..]);
    }
}
