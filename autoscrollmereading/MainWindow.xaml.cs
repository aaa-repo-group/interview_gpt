using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Utils;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace AutoScrollMeReading;

public partial class MainWindow : Window
{
    private const int MinimumFinalWords = 5;
    private const long MinimumBaseEnglishModelBytes = 130_000_000;

    private readonly List<TextBlock> _sentenceBlocks = [];
    private readonly Dictionary<string, int> _phraseToSentenceIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HashSet<string>> _sentenceConcepts = [];
    private readonly List<string[]> _sentenceWords = [];
    private readonly Queue<string> _pendingCommittedSpeech = [];
    private readonly DispatcherTimer _scrollTimer;
    private readonly DispatcherTimer _transcriptionTimer;
    private readonly List<byte> _audioBuffer = [];
    private readonly object _audioLock = new();
    private WaveInEvent? _waveIn;
    private WhisperFactory? _whisperFactory;
    private bool _isTranscribing;
    private int _activeSentenceIndex = -1;
    private double _targetScrollOffset;

    private const string ReadingContent = """
        Maya joined her first cloud operations team after years of fixing laptops for a small school district. On her first week, she watched a senior engineer explain how one tiny log line could uncover a failing deployment. She learned to treat dashboards as stories, not decorations, because every metric described a customer waiting somewhere. When an incident arrived at midnight, Maya stayed calm by reading the timeline aloud and checking each assumption. By the end of the quarter, she had written a runbook that helped new teammates recover a service in minutes.

        Jordan moved from graphic design into front end engineering because he loved making tools feel understandable. His first React project was messy, but he discovered that small components made large interfaces less frightening. He paired with an accessibility specialist and finally understood that keyboard focus is part of the product, not a bonus. After several releases, Jordan became the person who slowed meetings down just enough to ask what users would actually see. The team trusted him because his pull requests connected technical choices with the human moments they protected.

        Priya began as a database analyst, sorting strange records from old support systems into something teams could trust. She built a habit of naming every migration step clearly so future engineers would know why a column had changed. During a difficult audit, her notes helped leadership answer questions without guessing or hiding behind vague reports. Priya later automated the nightly quality checks and gave the support team a simple signal before customers noticed trouble. Her favorite lesson was that reliable software often feels quiet because someone cared enough to remove the noise.
        """;

    public MainWindow()
    {
        InitializeComponent();
        _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollTimer.Tick += SmoothScrollTowardTarget;
        _transcriptionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(4200) };
        _transcriptionTimer.Tick += (_, _) => _ = TranscribeBufferedAudioAsync();
        BuildReadingContent();
        Loaded += async (_, _) => await StartListeningAsync();
        Closed += (_, _) => StopListening();
    }

    public double AnimatedVerticalOffset
    {
        get => ReaderScroll.VerticalOffset;
        set => ReaderScroll.ScrollToVerticalOffset(value);
    }

    private void BuildReadingContent()
    {
        var pages = SplitIntoPages(ReadingContent, 5).ToArray();
        var sentenceIndex = 0;

        for (var pageIndex = 0; pageIndex < pages.Length; pageIndex++)
        {
            var section = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(212, 216, 220)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(42, 34, 42, 38),
                Margin = new Thickness(0, 0, 0, 30),
                MinHeight = 620
            };

            var stack = new StackPanel();
            section.Child = stack;

            stack.Children.Add(new TextBlock
            {
                Text = $"Page {pageIndex + 1}",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(85, 96, 108)),
                Margin = new Thickness(0, 0, 0, 14)
            });

            foreach (var sentence in pages[pageIndex])
            {
                var block = new TextBlock
                {
                    Text = sentence,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 25,
                    LineHeight = 39,
                    Foreground = new SolidColorBrush(Color.FromRgb(32, 38, 46)),
                    Margin = new Thickness(0, 0, 0, 25),
                    Tag = sentenceIndex
                };

                _sentenceBlocks.Add(block);
                stack.Children.Add(block);
                AddRecognitionPhrases(sentence, sentenceIndex);
                _sentenceConcepts.Add(BuildConceptSet(sentence));
                _sentenceWords.Add(NormalizeWords(sentence).ToArray());
                sentenceIndex++;
            }

            ContentHost.Children.Add(section);
        }
    }

    private static IEnumerable<string[]> SplitIntoPages(string content, int sentencesPerPage)
    {
        var sentences = SplitIntoSentences(content).ToArray();

        for (var i = 0; i < sentences.Length; i += sentencesPerPage)
        {
            yield return sentences.Skip(i).Take(sentencesPerPage).ToArray();
        }
    }

    private static IEnumerable<string> SplitIntoSentences(string content)
    {
        var normalized = content.ReplaceLineEndings(" ");
        var start = 0;

        for (var i = 0; i < normalized.Length; i++)
        {
            var isSentenceEnd = normalized[i] is '.' or '?' or '!';
            var isEndOfText = i == normalized.Length - 1;

            if (!isSentenceEnd && !isEndOfText)
            {
                continue;
            }

            var length = i - start + 1;
            var sentence = normalized.Substring(start, length).Trim();
            if (!string.IsNullOrWhiteSpace(sentence))
            {
                yield return sentence;
            }

            start = i + 1;
        }
    }

    private void AddRecognitionPhrases(string sentence, int index)
    {
        _phraseToSentenceIndex[sentence] = index;

        var words = sentence
            .TrimEnd('.', ',', ';', ':', '!', '?')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var start = 0; start < words.Length - 3; start += 2)
        {
            var phrase = string.Join(' ', words.Skip(start).Take(Math.Min(6, words.Length - start)));
            _phraseToSentenceIndex[phrase] = index;
        }
    }

    private async Task StartListeningAsync()
    {
        try
        {
            ListeningIndicator.Fill = new SolidColorBrush(Color.FromRgb(235, 184, 60));
            StatusText.Text = "Loading local Whisper model...";

            var modelPath = await EnsureWhisperModelAsync();
            _whisperFactory = WhisperFactory.FromPath(modelPath);

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100
            };

            _waveIn.DataAvailable += (_, e) =>
            {
                lock (_audioLock)
                {
                    _audioBuffer.AddRange(e.Buffer.Take(e.BytesRecorded));
                }
            };

            _waveIn.StartRecording();
            _transcriptionTimer.Start();

            ListeningIndicator.Fill = new SolidColorBrush(Color.FromRgb(46, 204, 113));
            StatusText.Text = "Whisper is listening. Read naturally.";
        }
        catch (Exception ex)
        {
            ListeningIndicator.Fill = new SolidColorBrush(Color.FromRgb(220, 90, 70));
            StatusText.Text = $"Whisper listener unavailable: {ex.Message}";
        }
    }

    private void StopListening()
    {
        _transcriptionTimer.Stop();
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _whisperFactory?.Dispose();
    }

    private async Task<string> EnsureWhisperModelAsync()
    {
        var modelDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoScrollMeReading",
            "Models");
        Directory.CreateDirectory(modelDirectory);

        var modelPath = Path.Combine(modelDirectory, "ggml-base.en.bin");
        if (File.Exists(modelPath) && new FileInfo(modelPath).Length >= MinimumBaseEnglishModelBytes)
        {
            return modelPath;
        }

        if (File.Exists(modelPath))
        {
            File.Delete(modelPath);
        }

        var tempModelPath = $"{modelPath}.download";
        if (File.Exists(tempModelPath))
        {
            File.Delete(tempModelPath);
        }

        StatusText.Text = "Downloading Whisper model. Please keep the app open...";
        await using (var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.BaseEn))
        await using (var fileWriter = File.OpenWrite(tempModelPath))
        {
            await modelStream.CopyToAsync(fileWriter);
        }

        var downloadedSize = new FileInfo(tempModelPath).Length;
        if (downloadedSize < MinimumBaseEnglishModelBytes)
        {
            File.Delete(tempModelPath);
            throw new InvalidOperationException(
                $"Whisper model download was incomplete ({downloadedSize / 1_000_000} MB). Please restart the app to try again.");
        }

        File.Move(tempModelPath, modelPath);

        return modelPath;
    }

    private async Task TranscribeBufferedAudioAsync()
    {
        if (_isTranscribing || _whisperFactory is null)
        {
            return;
        }

        byte[] pcmBytes;
        lock (_audioLock)
        {
            if (_audioBuffer.Count < 16000 * 2 * 2)
            {
                return;
            }

            pcmBytes = _audioBuffer.ToArray();
            _audioBuffer.Clear();
        }

        _isTranscribing = true;
        StatusText.Text = "Understanding what you just read...";

        try
        {
            var text = await Task.Run(async () => await TranscribePcmAsync(pcmBytes));
            await Dispatcher.InvokeAsync(() =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    StatusText.Text = $"Heard: {TrimForStatus(text)}";
                    FollowRecognizedText(text);
                }
                else
                {
                    StatusText.Text = "Listening...";
                }
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => StatusText.Text = $"Transcription failed: {ex.Message}");
        }
        finally
        {
            _isTranscribing = false;
        }
    }

    private async Task<string> TranscribePcmAsync(byte[] pcmBytes)
    {
        using var wavStream = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(wavStream), new WaveFormat(16000, 16, 1)))
        {
            writer.Write(pcmBytes, 0, pcmBytes.Length);
        }

        wavStream.Position = 0;

        using var processor = _whisperFactory!.CreateBuilder()
            .WithLanguage("en")
            .Build();

        var parts = new List<string>();
        await foreach (var result in processor.ProcessAsync(wavStream))
        {
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                parts.Add(result.Text.Trim());
            }
        }

        return string.Join(' ', parts);
    }

    private void FollowRecognizedText(string text)
    {
        AddCommittedSpeech(text);

        var speechWindow = string.Join(' ', _pendingCommittedSpeech);
        var meaningfulWordCount = NormalizeWords(speechWindow).Count();

        if (meaningfulWordCount < MinimumFinalWords)
        {
            StatusText.Text = $"Listening for sentence ending: {TrimForStatus(speechWindow)}";
            return;
        }

        var match = FindBestSentenceMatch(speechWindow);
        var requiredScore = 0.22;

        if (match.Index < 0 || match.Score < requiredScore)
        {
            StatusText.Text = $"No content match yet ({match.Score:0.00}): {TrimForStatus(speechWindow)}";
            return;
        }

        if (match.Index < _activeSentenceIndex)
        {
            StatusText.Text = $"Holding sentence {_activeSentenceIndex + 1}";
            return;
        }

        HighlightSentence(match.Index);
        ScrollSentenceToTop(match.Index);
        _pendingCommittedSpeech.Clear();
        StatusText.Text = $"Following sentence {match.Index + 1} ({match.Score:0.00})";
    }

    private (int Index, double Score) FindBestSentenceMatch(string recognizedText)
    {
        if (_phraseToSentenceIndex.TryGetValue(recognizedText, out var directIndex))
        {
            return (directIndex, 1.0);
        }

        var recognizedConcepts = BuildConceptSet(recognizedText);
        if (recognizedConcepts.Count == 0)
        {
            return (-1, 0);
        }

        var bestScore = 0.0;
        var bestIndex = -1;

        for (var i = 0; i < _sentenceBlocks.Count; i++)
        {
            if (_activeSentenceIndex >= 0 && i < _activeSentenceIndex)
            {
                continue;
            }

            var sentenceConcepts = _sentenceConcepts[i];
            var overlap = sentenceConcepts.Count(recognizedConcepts.Contains);
            var coverage = (double)overlap / Math.Max(1, sentenceConcepts.Count);
            var speechFit = (double)overlap / Math.Max(1, recognizedConcepts.Count);
            var fuzzyFit = GetFuzzyWordFit(recognizedText, i);
            var distancePenalty = _activeSentenceIndex < 0 ? 0 : Math.Max(0, i - _activeSentenceIndex - 2) * 0.04;
            var score = (coverage * 0.42) + (speechFit * 0.2) + (fuzzyFit * 0.38) - distancePenalty;

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        return (bestIndex, bestScore);
    }

    private static IEnumerable<string> NormalizeWords(string text)
    {
        return text
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()))
            .Where(word => word.Length > 2);
    }

    private static HashSet<string> BuildConceptSet(string text)
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
            "cloud" or "service" or "deploy" or "operation" or "system" => "platform",
            "log" or "metric" or "dashboard" or "timeline" or "report" or "signal" => "observability",
            "customer" or "user" or "support" or "people" or "team" or "teammate" => "human",
            "incident" or "recover" or "trouble" or "fail" or "midnight" => "reliability",
            "runbook" or "note" or "migration" or "step" or "quality" or "check" => "process",
            "react" or "component" or "interface" or "keyboard" or "focus" or "accessibility" => "frontend",
            "database" or "record" or "column" or "audit" or "data" => "data",
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

    private double GetFuzzyWordFit(string recognizedText, int sentenceIndex)
    {
        var spokenWords = NormalizeWords(recognizedText).ToArray();
        if (spokenWords.Length == 0)
        {
            return 0;
        }

        var sentenceWords = _sentenceWords[sentenceIndex];
        var matched = 0.0;

        foreach (var sentenceWord in sentenceWords)
        {
            var best = spokenWords.Max(spokenWord => GetWordSimilarity(spokenWord, sentenceWord));
            if (best >= 0.72)
            {
                matched += best;
            }
        }

        return matched / Math.Max(1, sentenceWords.Length);
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

    private void AddCommittedSpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _pendingCommittedSpeech.Enqueue(text);

        while (_pendingCommittedSpeech.Count > 3 || NormalizeWords(string.Join(' ', _pendingCommittedSpeech)).Count() > 32)
        {
            _pendingCommittedSpeech.Dequeue();
        }
    }

    private void HighlightSentence(int activeIndex)
    {
        for (var i = 0; i < _sentenceBlocks.Count; i++)
        {
            var isActive = i == activeIndex;
            _sentenceBlocks[i].Foreground = new SolidColorBrush(isActive
                ? Color.FromRgb(12, 92, 120)
                : Color.FromRgb(32, 38, 46));
            _sentenceBlocks[i].FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
            _sentenceBlocks[i].Background = isActive
                ? new SolidColorBrush(Color.FromRgb(226, 245, 242))
                : Brushes.Transparent;
        }
    }

    private void ScrollSentenceToTop(int index)
    {
        if (index == _activeSentenceIndex && IsNearViewportTop(_sentenceBlocks[index]))
        {
            return;
        }

        _activeSentenceIndex = index;
        var sentence = _sentenceBlocks[index];
        ReaderScroll.UpdateLayout();

        var position = sentence.TransformToAncestor(ReaderScroll).Transform(new Point(0, 0));
        var targetOffset = ReaderScroll.VerticalOffset + position.Y - 24;
        _targetScrollOffset = Math.Max(0, Math.Min(targetOffset, ReaderScroll.ScrollableHeight));

        if (_targetScrollOffset <= ReaderScroll.VerticalOffset)
        {
            return;
        }

        if (!_scrollTimer.IsEnabled)
        {
            _scrollTimer.Start();
        }
    }

    private void SmoothScrollTowardTarget(object? sender, EventArgs e)
    {
        var remaining = _targetScrollOffset - ReaderScroll.VerticalOffset;
        if (remaining <= 0.5)
        {
            ReaderScroll.ScrollToVerticalOffset(_targetScrollOffset);
            _scrollTimer.Stop();
            return;
        }

        var speed = remaining > ReaderScroll.ViewportHeight * 0.75 ? 14 : 4.25;
        var nextOffset = ReaderScroll.VerticalOffset + Math.Min(remaining, speed);
        ReaderScroll.ScrollToVerticalOffset(nextOffset);
    }

    private bool IsNearViewportTop(FrameworkElement element)
    {
        if (ReaderScroll.ViewportHeight <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        var position = element.TransformToAncestor(ReaderScroll).Transform(new Point(0, 0));
        return position.Y is > 12 and < 60;
    }

    private static string TrimForStatus(string text)
    {
        return text.Length <= 56 ? text : $"{text[..53]}...";
    }

    public static readonly DependencyProperty AnimatedVerticalOffsetProperty =
        DependencyProperty.Register(
            nameof(AnimatedVerticalOffset),
            typeof(double),
            typeof(MainWindow),
            new PropertyMetadata(0.0, OnAnimatedVerticalOffsetChanged));

    private static void OnAnimatedVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MainWindow)d).AnimatedVerticalOffset = (double)e.NewValue;
    }
}
