namespace InterviewGptBridge.Services;

public static class CaptionHistoryMerger
{
    public static string Merge(string history, string previousSnapshot, string snapshot, bool repeatedAfterSilence)
    {
        return MergeDetailed(history, previousSnapshot, snapshot, repeatedAfterSilence).History;
    }

    public static CaptionHistoryMergeResult MergeDetailed(string history, string previousSnapshot, string snapshot, bool repeatedAfterSilence)
    {
        snapshot = NormalizeCaptionText(snapshot);
        history = NormalizeCaptionText(history);
        previousSnapshot = NormalizeCaptionText(previousSnapshot);

        if (string.IsNullOrWhiteSpace(snapshot))
        {
            return CaptionHistoryMergeResult.NoChange(history);
        }

        if (string.IsNullOrWhiteSpace(history))
        {
            return new CaptionHistoryMergeResult(snapshot, 0, 0, snapshot);
        }

        if (repeatedAfterSilence)
        {
            var insertion = BuildAppendInsertion(history, snapshot);
            return new CaptionHistoryMergeResult(history + insertion, history.Length, 0, insertion);
        }

        if (!string.IsNullOrWhiteSpace(previousSnapshot) && IsReplacementForPreviousSnapshot(previousSnapshot, snapshot))
        {
            var trimmedHistory = TrimTrailingWords(history, CountWords(previousSnapshot));
            var insertion = BuildAppendInsertion(trimmedHistory, snapshot);
            var replaceStart = trimmedHistory.Length;
            var nextHistory = trimmedHistory + insertion;
            return new CaptionHistoryMergeResult(
                nextHistory,
                replaceStart,
                history.Length - replaceStart,
                insertion);
        }

        var suffix = GetNonOverlappingSuffix(history, snapshot);
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return CaptionHistoryMergeResult.NoChange(history);
        }

        var appendInsertion = BuildAppendInsertion(history, suffix);
        return new CaptionHistoryMergeResult(history + appendInsertion, history.Length, 0, appendInsertion);
    }

    private static string GetNonOverlappingSuffix(string history, string snapshot)
    {
        var historyWords = SplitWords(history);
        var snapshotWords = SplitWords(snapshot);
        var historyTokens = ToTokens(historyWords);
        var snapshotTokens = ToTokens(snapshotWords);
        if (historyTokens.Length == 0 || snapshotTokens.Length == 0)
        {
            return snapshot;
        }

        if (ContainsTokenSequence(historyTokens, snapshotTokens))
        {
            return string.Empty;
        }

        var maxOverlap = Math.Min(Math.Min(historyTokens.Length, snapshotTokens.Length), 28);
        for (var overlap = maxOverlap; overlap >= 1; overlap--)
        {
            if (TokensEqual(historyTokens.AsSpan(historyTokens.Length - overlap, overlap), snapshotTokens.AsSpan(0, overlap)))
            {
                return string.Join(' ', snapshotWords.Skip(overlap));
            }
        }

        return snapshot;
    }

    private static bool IsWordPrefix(string prefix, string text)
    {
        var prefixTokens = ToTokens(SplitWords(prefix));
        var textTokens = ToTokens(SplitWords(text));
        if (prefixTokens.Length == 0 || prefixTokens.Length > textTokens.Length)
        {
            return false;
        }

        return TokensEqual(prefixTokens.AsSpan(), textTokens.AsSpan(0, prefixTokens.Length));
    }

    private static bool IsReplacementForPreviousSnapshot(string previousSnapshot, string snapshot)
    {
        var previousTokens = ToTokens(SplitWords(previousSnapshot));
        var snapshotTokens = ToTokens(SplitWords(snapshot));
        if (previousTokens.Length == 0 || snapshotTokens.Length == 0)
        {
            return false;
        }

        if (IsWordPrefix(previousSnapshot, snapshot) ||
            ContainsTokenSequence(snapshotTokens, previousTokens))
        {
            return true;
        }

        var prefixLength = Math.Min(Math.Min(previousTokens.Length, snapshotTokens.Length), 8);
        var equivalentPrefix = 0;
        for (var i = 0; i < prefixLength; i++)
        {
            if (!TokenEquivalent(previousTokens[i], snapshotTokens[i]))
            {
                break;
            }

            equivalentPrefix++;
        }

        if (equivalentPrefix >= Math.Min(3, prefixLength))
        {
            return true;
        }

        var overlap = CountTokenOverlap(previousTokens, snapshotTokens);
        var shorter = Math.Min(previousTokens.Length, snapshotTokens.Length);
        return shorter >= 8 && (double)overlap / shorter >= 0.72;
    }

    private static bool ContainsTokenSequence(string[] haystack, string[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
        {
            return false;
        }

        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            if (TokensEqual(haystack.AsSpan(start, needle.Length), needle.AsSpan()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TokensEqual(ReadOnlySpan<string> left, ReadOnlySpan<string> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (!TokenEquivalent(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static int CountTokenOverlap(string[] left, string[] right)
    {
        var matchedRightIndexes = new bool[right.Length];
        var overlap = 0;

        foreach (var leftToken in left)
        {
            for (var i = 0; i < right.Length; i++)
            {
                if (matchedRightIndexes[i] || !TokenEquivalent(leftToken, right[i]))
                {
                    continue;
                }

                matchedRightIndexes[i] = true;
                overlap++;
                break;
            }
        }

        return overlap;
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

    private static string TrimTrailingWords(string text, int wordCount)
    {
        var words = SplitWords(text);
        if (wordCount <= 0 || wordCount >= words.Length)
        {
            return string.Empty;
        }

        return string.Join(' ', words.Take(words.Length - wordCount));
    }

    private static int CountWords(string text)
    {
        return SplitWords(text).Length;
    }

    private static string[] SplitWords(string text)
    {
        return NormalizeCaptionText(text).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string[] ToTokens(string[] words)
    {
        return words
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(word => word.Length > 0)
            .ToArray();
    }

    private static string AppendWithSpace(string left, string right)
    {
        left = NormalizeCaptionText(left);
        right = NormalizeCaptionText(right);
        if (string.IsNullOrWhiteSpace(left))
        {
            return right;
        }

        return string.IsNullOrWhiteSpace(right) ? left : left + " " + right;
    }

    private static string BuildAppendInsertion(string left, string right)
    {
        left = NormalizeCaptionText(left);
        right = NormalizeCaptionText(right);
        if (string.IsNullOrWhiteSpace(right))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(left) ? right : " " + right;
    }

    private static string NormalizeCaptionText(string text)
    {
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}

public sealed record CaptionHistoryMergeResult(
    string History,
    int ReplaceStart,
    int ReplaceLength,
    string InsertedText)
{
    public bool HasChange => ReplaceLength > 0 || InsertedText.Length > 0;

    public static CaptionHistoryMergeResult NoChange(string history)
    {
        return new CaptionHistoryMergeResult(history, history.Length, 0, string.Empty);
    }
}
