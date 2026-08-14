namespace InterviewGptBridge.Services;

public static class LiveCaptionTextNormalizer
{
    public static string NormalizeSnapshot(IEnumerable<string> values)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var rawLine in value.Replace("\r", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var line = NormalizeLine(rawLine);
                line = StripUiChromePrefix(line);
                if (string.IsNullOrWhiteSpace(line) || IsUiChromeLine(line))
                {
                    continue;
                }

                line = CollapseRepeatedSingleLine(line);
                if (seen.Add(line))
                {
                    lines.Add(line);
                }
            }
        }

        return string.Join(' ', SelectBestCaptionLines(lines)).Trim();
    }

    public static string NormalizeSnapshot(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : NormalizeSnapshot(new[] { value });
    }

    private static IEnumerable<string> SelectBestCaptionLines(List<string> lines)
    {
        var clusters = new List<List<string>>();
        foreach (var line in lines.Where(line => SplitTokens(line).Length > 0))
        {
            var cluster = clusters.FirstOrDefault(existing =>
                existing.Any(existingLine => AreOverlappingCaptionAlternatives(existingLine, line)));
            if (cluster is null)
            {
                clusters.Add([line]);
                continue;
            }

            cluster.Add(line);
        }

        return clusters.Select(cluster => cluster
            .OrderByDescending(GetCaptionQualityScore)
            .ThenByDescending(line => SplitTokens(line).Length)
            .First());
    }

    private static double GetCaptionQualityScore(string line)
    {
        var tokens = SplitTokens(line);
        var score = (double)tokens.Length;
        if (line.EndsWith('.') || line.EndsWith('?') || line.EndsWith('!'))
        {
            score += 2;
        }

        if (tokens.Length > 0 && char.IsUpper(line.TrimStart()[0]))
        {
            score += 0.5;
        }

        if (line.EndsWith(" the", StringComparison.OrdinalIgnoreCase) ||
            line.EndsWith(" that", StringComparison.OrdinalIgnoreCase) ||
            line.EndsWith(" your", StringComparison.OrdinalIgnoreCase) ||
            line.EndsWith(" a", StringComparison.OrdinalIgnoreCase))
        {
            score -= 4;
        }

        return score;
    }

    private static bool AreOverlappingCaptionAlternatives(string left, string right)
    {
        var leftTokens = SplitTokens(left);
        var rightTokens = SplitTokens(right);
        if (leftTokens.Length == 0 || rightTokens.Length == 0)
        {
            return false;
        }

        if (ContainsTokenSequence(leftTokens, rightTokens) ||
            ContainsTokenSequence(rightTokens, leftTokens))
        {
            return true;
        }

        var prefixLength = Math.Min(Math.Min(leftTokens.Length, rightTokens.Length), 8);
        var equivalentPrefix = 0;
        for (var i = 0; i < prefixLength; i++)
        {
            if (!TokenEquivalent(leftTokens[i], rightTokens[i]))
            {
                break;
            }

            equivalentPrefix++;
        }

        if (equivalentPrefix >= Math.Min(3, prefixLength))
        {
            return true;
        }

        var overlap = CountTokenOverlap(leftTokens, rightTokens);
        var shorter = Math.Min(leftTokens.Length, rightTokens.Length);
        return shorter >= 4 && (double)overlap / shorter >= 0.55;
    }

    private static int CountTokenOverlap(string[] left, string[] right)
    {
        var rightCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in right)
        {
            rightCounts[token] = rightCounts.TryGetValue(token, out var count) ? count + 1 : 1;
        }

        var overlap = 0;
        foreach (var token in left)
        {
            var matchedToken = rightCounts.Keys.ToArray().FirstOrDefault(rightToken => TokenEquivalent(token, rightToken) && rightCounts[rightToken] > 0);
            if (matchedToken is not null)
            {
                overlap++;
                rightCounts[matchedToken]--;
            }
        }

        return overlap;
    }

    private static bool ContainsTokenSequence(string[] haystack, string[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
        {
            return false;
        }

        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            var matched = true;
            for (var i = 0; i < needle.Length; i++)
            {
                if (!TokenEquivalent(haystack[start + i], needle[i]))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
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

    private static string[] SplitTokens(string line)
    {
        return NormalizeLine(line)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(word => word.Length > 0)
            .ToArray();
    }

    private static string NormalizeLine(string value)
    {
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static string StripUiChromePrefix(string line)
    {
        const string readyPrefix = "Ready to show live captions in English (United States)";
        return line.StartsWith(readyPrefix, StringComparison.OrdinalIgnoreCase)
            ? line[readyPrefix.Length..].Trim()
            : line;
    }

    private static bool IsUiChromeLine(string line)
    {
        return line.Equals("Live captions", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("Live Captions", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("Ready to show live captions in English (United States)", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("Settings", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("Pause", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("Resume", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("Close", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("Minimize", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("Dock", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("Undock", StringComparison.OrdinalIgnoreCase);
    }

    private static string CollapseRepeatedSingleLine(string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 4 || words.Length % 2 != 0)
        {
            return line;
        }

        var half = words.Length / 2;
        for (var index = 0; index < half; index++)
        {
            if (!string.Equals(words[index], words[index + half], StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }

        return string.Join(' ', words.Take(half));
    }
}
