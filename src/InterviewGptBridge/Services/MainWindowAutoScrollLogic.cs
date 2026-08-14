namespace InterviewGptBridge.Services;

public static class MainWindowAutoScrollLogic
{
    public const int MinimumMeaningfulWords = 5;
    public const int MaximumPendingSpeechWords = 32;
    public const int MaximumPendingSpeechSegments = 3;
    public const double RequiredMatchScore = 0.22;
    public const double TargetViewportRatio = 0.46;
    public const double CenterBandBottomRatio = 0.58;
    public const double MaximumAcceptedTargetViewportRatio = 1.35;
    public const double ConfidentFarMatchScore = 0.32;
    public const double MinimumSpeechRms = 90;
    public const int MinimumSpeechPeak = 900;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "that", "this", "with", "from", "into", "onto", "then", "than", "they",
        "them", "their", "there", "these", "those", "when", "where", "while", "what", "which", "because",
        "about", "after", "before", "through", "between", "around", "your", "youre", "would", "could",
        "should", "have", "has", "had", "been", "being", "also", "just", "very", "more", "most", "some",
        "over", "under", "once", "each", "every", "first", "second", "third", "role", "project",
        "my", "your", "our", "his", "her", "its", "im", "i", "is", "as", "an", "at", "to", "of",
        "in", "on", "it", "we", "he", "she", "us", "be", "or", "if", "so",
        "blankaudio", "blank", "audio", "uh", "um", "hmm"
    };

    public static AutoScrollChunk CreateChunk(int index, string text, double top)
    {
        var words = NormalizeWords(text).ToArray();
        return new AutoScrollChunk(index, text, Math.Max(0, top), BuildConceptSet(text), words);
    }

    public static IReadOnlyList<AutoScrollChunk> CreateChunks(IEnumerable<(string Text, double Top)> chunks)
    {
        var result = new List<AutoScrollChunk>();
        foreach (var chunk in chunks)
        {
            if (string.IsNullOrWhiteSpace(chunk.Text))
            {
                continue;
            }

            var next = CreateChunk(result.Count, chunk.Text, chunk.Top);
            if (next.Words.Length >= 4)
            {
                result.Add(next);
            }
        }

        return result;
    }

    public static bool IsUsableTranscript(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = RemoveNoiseMarkers(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var meaningfulWords = NormalizeWords(normalized).ToArray();
        if (meaningfulWords.Length == 0)
        {
            return false;
        }

        if (meaningfulWords.Length == 1 &&
            (normalized.Length <= 8 || normalized.Equals("you", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    public static AudioEnergy MeasurePcm16AudioEnergy(byte[] pcmBytes)
    {
        if (pcmBytes.Length < 2)
        {
            return new AudioEnergy(0, 0, 0);
        }

        long squareSum = 0;
        var peak = 0;
        var sampleCount = pcmBytes.Length / 2;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BitConverter.ToInt16(pcmBytes, i * 2);
            var absolute = Math.Abs((int)sample);
            peak = Math.Max(peak, absolute);
            squareSum += (long)sample * sample;
        }

        var rms = Math.Sqrt(squareSum / (double)Math.Max(1, sampleCount));
        return new AudioEnergy(rms, peak, sampleCount);
    }

    public static bool HasLikelySpeechEnergy(byte[] pcmBytes)
    {
        var energy = MeasurePcm16AudioEnergy(pcmBytes);
        return energy.SampleCount >= 8000 &&
            (energy.Rms >= MinimumSpeechRms || energy.Peak >= MinimumSpeechPeak);
    }

    public static string RemoveNoiseMarkers(string text)
    {
        return (text ?? string.Empty)
            .Replace("[BLANK_AUDIO]", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("(BLANK_AUDIO)", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("<BLANK_AUDIO>", " ", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    public static (int Index, double Score) FindBestChunkMatch(
        string recognizedText,
        IReadOnlyList<AutoScrollChunk> chunks,
        int activeChunkIndex)
    {
        var recognizedConcepts = BuildConceptSet(recognizedText);
        if (recognizedConcepts.Count == 0)
        {
            return (-1, 0);
        }

        var bestScore = 0.0;
        var bestIndex = -1;

        for (var i = 0; i < chunks.Count; i++)
        {
            if (activeChunkIndex >= 0 && i < activeChunkIndex)
            {
                continue;
            }

            var chunk = chunks[i];
            var overlap = chunk.Concepts.Count(recognizedConcepts.Contains);
            var coverage = (double)overlap / Math.Max(1, chunk.Concepts.Count);
            var speechFit = (double)overlap / Math.Max(1, recognizedConcepts.Count);
            var fuzzyFit = GetFuzzyWordFit(recognizedText, chunk);
            var phraseFit = GetPhraseWindowFit(recognizedText, chunk);
            var distancePenalty = activeChunkIndex < 0 ? 0 : Math.Max(0, i - activeChunkIndex - 2) * 0.04;
            var score = Math.Max(
                phraseFit,
                (coverage * 0.42) + (speechFit * 0.2) + (fuzzyFit * 0.38)) - distancePenalty;

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        return (bestIndex, bestScore);
    }

    public static ScrollDecision DecideScroll(
        double chunkTop,
        double scrollY,
        double viewportHeight,
        double documentHeight,
        double matchScore = 0)
    {
        if (viewportHeight <= 0 || documentHeight <= 0)
        {
            return new ScrollDecision(false, 0, "missing scroll state");
        }

        var desiredScrollY = Math.Clamp(
            chunkTop - (viewportHeight * TargetViewportRatio),
            0,
            Math.Max(0, documentHeight - viewportHeight));
        var currentViewportTop = chunkTop - scrollY;
        if (currentViewportTop <= viewportHeight * CenterBandBottomRatio)
        {
            return new ScrollDecision(true, 0, "already in center reading band");
        }

        if (desiredScrollY <= scrollY + 8)
        {
            return new ScrollDecision(true, 0, "already near reading position");
        }

        var remaining = desiredScrollY - scrollY;
        if (remaining > viewportHeight * MaximumAcceptedTargetViewportRatio)
        {
            if (matchScore >= ConfidentFarMatchScore)
            {
                return new ScrollDecision(true, remaining, "center confident far reading part");
            }

            return new ScrollDecision(false, 0, "target too far from current reading band");
        }

        return new ScrollDecision(true, remaining, "center reading part");
    }

    public static IEnumerable<string> NormalizeWords(string text)
    {
        return RemoveNoiseMarkers(text)
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()))
            .Where(word => word.Length >= 2 && !StopWords.Contains(word));
    }

    public static HashSet<string> BuildConceptSet(string text)
    {
        var concepts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in NormalizeWords(text))
        {
            concepts.Add(ToConcept(word));
        }

        return concepts;
    }

    private static string ToConcept(string word)
    {
        var stemmed = Stem(word);
        return stemmed switch
        {
            "aws" or "amazon" or "bedrock" or "s3" or "lambda" or "azure" or "gcp" or "cloud" => "cloud",
            "open" or "opensearch" or "search" or "retriev" or "retrieval" or "vector" or "embedding" or "embed" or "chunk" => "retrieval",
            "rag" or "genai" or "ai" or "claude" or "model" or "prompt" or "llm" => "ai",
            "python" or "api" or "backend" or "rest" or "service" or "function" => "backend",
            "data" or "sql" or "etl" or "pipeline" or "document" or "index" or "nlp" => "data",
            "evaluat" or "test" or "regression" or "accuracy" or "precision" or "f1" or "golden" => "evaluation",
            "latency" or "optimization" or "optimiza" or "perform" or "production" => "production",
            "financial" or "entity" or "extract" or "analyst" => "analysis",
            "cloud" or "deploy" or "operation" or "system" => "platform",
            "log" or "metric" or "dashboard" or "timeline" or "report" or "signal" => "observability",
            "customer" or "user" or "support" or "people" or "team" or "teammate" => "human",
            "incident" or "recover" or "trouble" or "fail" or "midnight" => "reliability",
            "runbook" or "note" or "migration" or "step" or "quality" or "check" => "process",
            "react" or "component" or "interface" or "keyboard" or "focus" or "accessibility" => "frontend",
            "learn" or "understand" or "explain" or "discover" or "answer" => "learning",
            "calm" or "trust" or "care" or "reliable" or "quiet" => "confidence",
            _ => stemmed
        };
    }

    private static string Stem(string word)
    {
        foreach (var suffix in new[] { "ing", "tion", "sion", "ments", "ment", "ers", "ies", "ed", "ly", "s" })
        {
            if (word.Length > suffix.Length + 3 && word.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return word[..^suffix.Length];
            }
        }

        return word;
    }

    private static double GetPhraseWindowFit(string recognizedText, AutoScrollChunk chunk)
    {
        var spokenWords = NormalizeWords(recognizedText).ToArray();
        if (spokenWords.Length < MinimumMeaningfulWords || chunk.Words.Length == 0)
        {
            return 0;
        }

        var window = Math.Min(6, spokenWords.Length);
        var bestRun = 0;

        for (var spokenStart = 0; spokenStart <= spokenWords.Length - window; spokenStart++)
        {
            for (var chunkStart = 0; chunkStart < chunk.Words.Length; chunkStart++)
            {
                var run = 0;
                while (spokenStart + run < spokenWords.Length &&
                    chunkStart + run < chunk.Words.Length &&
                    GetWordSimilarity(spokenWords[spokenStart + run], chunk.Words[chunkStart + run]) >= 0.72)
                {
                    run++;
                }

                bestRun = Math.Max(bestRun, run);
                if (bestRun >= window)
                {
                    return 1.0;
                }
            }
        }

        return bestRun < MinimumMeaningfulWords
            ? 0
            : (double)bestRun / Math.Max(window, Math.Min(spokenWords.Length, chunk.Words.Length));
    }

    private static double GetFuzzyWordFit(string recognizedText, AutoScrollChunk chunk)
    {
        var spokenWords = NormalizeWords(recognizedText).ToArray();
        if (spokenWords.Length == 0 || chunk.Words.Length == 0)
        {
            return 0;
        }

        var matched = 0.0;
        foreach (var chunkWord in chunk.Words)
        {
            var best = spokenWords.Max(spokenWord => GetWordSimilarity(spokenWord, chunkWord));
            if (best >= 0.72)
            {
                matched += best;
            }
        }

        return matched / Math.Max(1, chunk.Words.Length);
    }

    private static double GetOrderedPhraseFit(string recognizedText, AutoScrollChunk chunk)
    {
        var spokenWords = NormalizeWords(recognizedText).ToArray();
        if (spokenWords.Length == 0 || chunk.Words.Length == 0)
        {
            return 0;
        }

        var spokenIndex = 0;
        var matched = 0;
        foreach (var chunkWord in chunk.Words)
        {
            while (spokenIndex < spokenWords.Length)
            {
                if (GetWordSimilarity(spokenWords[spokenIndex], chunkWord) >= 0.72)
                {
                    matched++;
                    spokenIndex++;
                    break;
                }

                spokenIndex++;
            }
        }

        return (double)matched / Math.Max(1, Math.Min(spokenWords.Length, chunk.Words.Length));
    }

    private static double GetWordSimilarity(string left, string right)
    {
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (ToConcept(left).Equals(ToConcept(right), StringComparison.OrdinalIgnoreCase))
        {
            return 0.92;
        }

        if (left.Length < 4 || right.Length < 4)
        {
            return 0;
        }

        if (left.StartsWith(right, StringComparison.OrdinalIgnoreCase) ||
            right.StartsWith(left, StringComparison.OrdinalIgnoreCase))
        {
            return 0.82;
        }

        var distance = GetLevenshteinDistance(left, right);
        return 1.0 - ((double)distance / Math.Max(left.Length, right.Length));
    }

    private static int GetLevenshteinDistance(string left, string right)
    {
        var distances = new int[left.Length + 1, right.Length + 1];

        for (var i = 0; i <= left.Length; i++)
        {
            distances[i, 0] = i;
        }

        for (var j = 0; j <= right.Length; j++)
        {
            distances[0, j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                distances[i, j] = Math.Min(
                    Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                    distances[i - 1, j - 1] + cost);
            }
        }

        return distances[left.Length, right.Length];
    }
}

public sealed record AutoScrollChunk(
    int Index,
    string Text,
    double Top,
    HashSet<string> Concepts,
    string[] Words);

public sealed record ScrollDecision(bool Accepted, double Pixels, string Reason);

public sealed record AudioEnergy(double Rms, int Peak, int SampleCount);
