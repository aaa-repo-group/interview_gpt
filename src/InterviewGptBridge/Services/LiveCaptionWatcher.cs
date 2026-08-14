using System.Diagnostics;
using System.Windows.Automation;

namespace InterviewGptBridge.Services;

public sealed class LiveCaptionWatcher : IDisposable
{
    private const int MaxCollectDepth = 11;
    private const int MaxCollectMilliseconds = 85;

    private readonly TimeSpan _pollInterval;
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _pollTask;
    private AutomationElement? _captionWindow;
    private List<AutomationElement> _captionTextLeaves = [];
    private int _cachedReadsSinceDiscovery;
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
            _captionTextLeaves.Clear();
            _cachedReadsSinceDiscovery = 0;
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
                else if (string.IsNullOrWhiteSpace(caption))
                {
                    _lastCaption = string.Empty;
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

        var candidates = new List<string>();
        if (_cachedReadsSinceDiscovery < 20)
        {
            ReadCachedTextLeaves(candidates);
            if (candidates.Count > 0)
            {
                _cachedReadsSinceDiscovery++;
            }
        }

        var discoveredLeaves = new List<AutomationElement>();
        if (candidates.Count == 0)
        {
            var stopwatch = Stopwatch.StartNew();
            CollectText(window, candidates, discoveredLeaves, depth: 0, stopwatch);
        }

        if (candidates.Count == 0)
        {
            var fallback = TryReadTextPattern(window);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                candidates.Add(fallback);
            }
        }

        if (discoveredLeaves.Count > 0)
        {
            _captionTextLeaves = discoveredLeaves;
            _cachedReadsSinceDiscovery = 0;
        }
        else if (candidates.Count == 0)
        {
            _captionTextLeaves.Clear();
            _cachedReadsSinceDiscovery = 0;
        }

        return LiveCaptionTextNormalizer.NormalizeSnapshot(candidates);
    }

    private AutomationElement? GetLiveCaptionWindow()
    {
        if (IsLiveCaptionWindow(_captionWindow))
        {
            return _captionWindow;
        }

        _captionWindow = null;
        _captionTextLeaves.Clear();
        _cachedReadsSinceDiscovery = 0;
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

    private void ReadCachedTextLeaves(List<string> candidates)
    {
        if (_captionTextLeaves.Count == 0)
        {
            return;
        }

        foreach (var element in _captionTextLeaves.ToArray())
        {
            if (TryReadCaptionTextLeaf(element, out var text))
            {
                candidates.Add(text);
            }
        }
    }

    private static void CollectText(
        AutomationElement root,
        List<string> candidates,
        List<AutomationElement> discoveredLeaves,
        int depth,
        Stopwatch stopwatch)
    {
        if (depth > MaxCollectDepth || stopwatch.ElapsedMilliseconds > MaxCollectMilliseconds)
        {
            return;
        }

        try
        {
            if (TryAppendElementText(root, candidates, discoveredLeaves))
            {
                return;
            }

            var walker = TreeWalker.ControlViewWalker;
            for (var child = walker.GetFirstChild(root); child is not null; child = walker.GetNextSibling(child))
            {
                CollectText(child, candidates, discoveredLeaves, depth + 1, stopwatch);
                if (stopwatch.ElapsedMilliseconds > MaxCollectMilliseconds)
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

    private static bool TryAppendElementText(
        AutomationElement element,
        List<string> candidates,
        List<AutomationElement> discoveredLeaves)
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

            if (controlType == ControlType.Document)
            {
                return false;
            }

            if (IsCaptionTextLeaf(controlType))
            {
                if (TryReadCaptionTextLeaf(element, out var text))
                {
                    candidates.Add(text);
                    discoveredLeaves.Add(element);
                    return true;
                }

                return false;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsCaptionTextLeaf(ControlType controlType)
    {
        return controlType == ControlType.Text ||
               controlType == ControlType.Edit;
    }

    private static bool TryReadCaptionTextLeaf(AutomationElement element, out string text)
    {
        text = string.Empty;

        try
        {
            text = element.Current.Name;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            text = TryReadTextPattern(element);
            return !string.IsNullOrWhiteSpace(text);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string TryReadTextPattern(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) &&
                pattern is TextPattern textPattern)
            {
                return textPattern.DocumentRange.GetText(1200) ?? string.Empty;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

}
