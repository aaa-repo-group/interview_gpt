using System.Diagnostics;
using System.Windows.Automation;

namespace InterviewGptBridge.Services;

public sealed class LiveCaptionWatcher : IDisposable
{
    private readonly TimeSpan _pollInterval;
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _pollTask;
    private AutomationElement? _captionWindow;
    private string _lastCaption = string.Empty;
    private string _lastStatus = string.Empty;

    public event EventHandler<string>? CaptionChanged;
    public event EventHandler<string>? StatusChanged;

    public LiveCaptionWatcher(TimeSpan pollInterval)
    {
        _pollInterval = pollInterval;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_pollTask is not null)
            {
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _pollTask = Task.Run(() => PollLoopAsync(_cancellationTokenSource.Token));
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _cancellationTokenSource?.Cancel();
            _captionWindow = null;
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var caption = ReadLiveCaptionSnapshot();
                if (!string.IsNullOrWhiteSpace(caption) &&
                    !string.Equals(caption, _lastCaption, StringComparison.Ordinal))
                {
                    _lastCaption = caption;
                    CaptionChanged?.Invoke(this, caption);
                }

                SetStatus(string.IsNullOrWhiteSpace(caption)
                    ? "Waiting for Windows Live Captions"
                    : "Following Windows Live Captions");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                _captionWindow = null;
                SetStatus("Waiting for Windows Live Captions");
            }

            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void SetStatus(string status)
    {
        if (string.Equals(status, _lastStatus, StringComparison.Ordinal))
        {
            return;
        }

        _lastStatus = status;
        StatusChanged?.Invoke(this, status);
    }

    private string ReadLiveCaptionSnapshot()
    {
        var window = GetLiveCaptionWindow();
        if (window is null)
        {
            return string.Empty;
        }

        var stopwatch = Stopwatch.StartNew();
        var candidates = new List<string>();
        CollectText(window, candidates, depth: 0, stopwatch);
        return Normalize(candidates);
    }

    private AutomationElement? GetLiveCaptionWindow()
    {
        if (IsLiveCaptionWindow(_captionWindow))
        {
            return _captionWindow;
        }

        _captionWindow = null;
        var windows = AutomationElement.RootElement.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

        foreach (AutomationElement window in windows)
        {
            if (IsLiveCaptionWindow(window))
            {
                _captionWindow = window;
                return window;
            }
        }

        return null;
    }

    private static bool IsLiveCaptionWindow(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            var name = element.Current.Name ?? string.Empty;
            if (name.Contains("Live Captions", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Live captions", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var processId = element.Current.ProcessId;
            if (processId <= 0)
            {
                return false;
            }

            using var process = Process.GetProcessById(processId);
            var processName = process.ProcessName;
            return processName.Contains("LiveCaptions", StringComparison.OrdinalIgnoreCase) ||
                   processName.Contains("LiveCaption", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void CollectText(AutomationElement root, List<string> candidates, int depth, Stopwatch stopwatch)
    {
        if (depth > 8 || stopwatch.ElapsedMilliseconds > 80)
        {
            return;
        }

        try
        {
            if (TryAppendElementText(root, candidates))
            {
                return;
            }

            var walker = TreeWalker.ControlViewWalker;
            for (var child = walker.GetFirstChild(root); child is not null; child = walker.GetNextSibling(child))
            {
                CollectText(child, candidates, depth + 1, stopwatch);
                if (stopwatch.ElapsedMilliseconds > 80)
                {
                    return;
                }
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch
        {
        }
    }

    private static bool TryAppendElementText(AutomationElement element, List<string> candidates)
    {
        try
        {
            var controlType = element.Current.ControlType;
            if (controlType == ControlType.Button ||
                controlType == ControlType.Menu ||
                controlType == ControlType.MenuBar ||
                controlType == ControlType.MenuItem ||
                controlType == ControlType.ScrollBar ||
                controlType == ControlType.Separator)
            {
                return true;
            }

            var text = TryReadTextPattern(element);
            if (!string.IsNullOrWhiteSpace(text))
            {
                candidates.Add(text);
                return true;
            }

            if (controlType == ControlType.Text ||
                controlType == ControlType.Document ||
                controlType == ControlType.Edit)
            {
                text = element.Current.Name;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    candidates.Add(text);
                }

                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static string TryReadTextPattern(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) &&
                pattern is TextPattern textPattern)
            {
                return textPattern.DocumentRange.GetText(4000) ?? string.Empty;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string Normalize(IEnumerable<string> values)
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

        return string.Join(' ', lines).Trim();
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var rawLines = value
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        string? previous = null;
        foreach (var line in rawLines)
        {
            var normalizedLine = NormalizeLine(line);
            if (string.IsNullOrWhiteSpace(normalizedLine) ||
                string.Equals(normalizedLine, previous, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lines.Add(normalizedLine);
            previous = normalizedLine;
        }

        return string.Join(' ', lines).Trim();
    }

    private static string NormalizeLine(string value)
    {
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static bool IsUiChromeLine(string line)
    {
        return line.Equals("Live captions", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("Live Captions", StringComparison.OrdinalIgnoreCase) ||
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
