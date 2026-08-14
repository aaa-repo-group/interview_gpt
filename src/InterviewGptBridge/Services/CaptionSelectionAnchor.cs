namespace InterviewGptBridge.Services;

public static class CaptionSelectionAnchor
{
    public static int MapStart(string oldText, string newText, int oldStart, string? anchorPrefix)
    {
        oldText ??= string.Empty;
        newText ??= string.Empty;
        oldStart = Math.Clamp(oldStart, 0, oldText.Length);
        anchorPrefix = string.IsNullOrEmpty(anchorPrefix)
            ? oldText[..oldStart]
            : anchorPrefix;

        if (newText.Length == 0)
        {
            return 0;
        }

        if (oldStart == 0 || anchorPrefix.Length == 0)
        {
            return 0;
        }

        if (newText.Length >= oldStart &&
            string.CompareOrdinal(oldText, 0, newText, 0, oldStart) == 0)
        {
            return oldStart;
        }

        if (newText.StartsWith(anchorPrefix, StringComparison.Ordinal))
        {
            return anchorPrefix.Length;
        }

        if (anchorPrefix.StartsWith(newText, StringComparison.Ordinal))
        {
            return newText.Length;
        }

        var anchorTokens = Tokenize(anchorPrefix);
        var newTokens = Tokenize(newText);
        if (anchorTokens.Count == 0 || newTokens.Count == 0)
        {
            return Math.Clamp(oldStart, 0, newText.Length);
        }

        var prefixMatches = CountEquivalentPrefix(anchorTokens, newTokens);
        if (prefixMatches == anchorTokens.Count)
        {
            return newTokens[prefixMatches - 1].End;
        }

        if (prefixMatches >= Math.Max(1, anchorTokens.Count - 2) ||
            (anchorTokens.Count >= 8 && (double)prefixMatches / anchorTokens.Count >= 0.82))
        {
            return prefixMatches == 0 ? 0 : newTokens[prefixMatches - 1].End;
        }

        var maxWindow = Math.Min(10, anchorTokens.Count);
        for (var window = maxWindow; window >= 3; window--)
        {
            var start = anchorTokens.Count - window;
            var matchStart = FindTokenSequence(newTokens, anchorTokens.GetRange(start, window));
            if (matchStart >= 0)
            {
                return newTokens[matchStart + window - 1].End;
            }
        }

        return Math.Clamp(oldStart, 0, newText.Length);
    }

    private static int CountEquivalentPrefix(IReadOnlyList<TokenSpan> left, IReadOnlyList<TokenSpan> right)
    {
        var count = Math.Min(left.Count, right.Count);
        var matched = 0;
        for (var index = 0; index < count; index++)
        {
            if (!TokenEquivalent(left[index].Token, right[index].Token))
            {
                break;
            }

            matched++;
        }

        return matched;
    }

    private static int FindTokenSequence(IReadOnlyList<TokenSpan> haystack, IReadOnlyList<TokenSpan> needle)
    {
        if (needle.Count == 0 || needle.Count > haystack.Count)
        {
            return -1;
        }

        for (var start = 0; start <= haystack.Count - needle.Count; start++)
        {
            var matched = true;
            for (var index = 0; index < needle.Count; index++)
            {
                if (!TokenEquivalent(haystack[start + index].Token, needle[index].Token))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return start;
            }
        }

        return -1;
    }

    private static bool TokenEquivalent(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var shorterLength = Math.Min(left.Length, right.Length);
        return shorterLength >= 3 &&
            (left.StartsWith(right, StringComparison.OrdinalIgnoreCase) ||
             right.StartsWith(left, StringComparison.OrdinalIgnoreCase));
    }

    private static List<TokenSpan> Tokenize(string text)
    {
        var tokens = new List<TokenSpan>();
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            var end = index;
            if (end <= start)
            {
                continue;
            }

            var token = new string(text[start..end].Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (token.Length > 0)
            {
                tokens.Add(new TokenSpan(token, end));
            }
        }

        return tokens;
    }

    private sealed record TokenSpan(string Token, int End);
}
