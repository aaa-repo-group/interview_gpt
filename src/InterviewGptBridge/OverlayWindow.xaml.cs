using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InterviewGptBridge.Services;

namespace InterviewGptBridge;

public partial class OverlayWindow : Window
{
    private readonly ISensitiveWindowProtectionService _sensitiveWindowProtectionService;
    private bool _loading;
    private bool _privacyLoading;
    private bool _allowClose;
    private bool _manualRedaction;
    private bool _redactWhenInactive = true;
    private bool _mouseSelecting;
    private bool _scrollingProgrammatically;
    private bool _autoScrollToBottom = true;
    private string? _pendingCaption;
    private string _lastCaption = string.Empty;
    private ScrollViewer? _captionScrollViewer;
    private DispatcherOperation? _deferredScrollOperation;

    public event EventHandler<string>? TextSubmitted;
    public event EventHandler<OverlaySettings>? SettingsChanged;
    public event Action<bool>? ManualRedactionChanged;

    public OverlayWindow(ISensitiveWindowProtectionService sensitiveWindowProtectionService)
    {
        _sensitiveWindowProtectionService = sensitiveWindowProtectionService;
        InitializeComponent();
        LocationChanged += (_, _) => RaiseSettingsChanged();
        SizeChanged += (_, _) => RaiseSettingsChanged();
        Loaded += (_, _) => AttachCaptionScrollViewer();
        Activated += (_, _) => ApplyPrivacyCover();
        Deactivated += (_, _) => ApplyPrivacyCover();
        PreviewKeyDown += OverlayWindow_PreviewKeyDown;
        Closing += HandleClosing;
        Closed += (_, _) => _sensitiveWindowProtectionService.StatusChanged -= SensitiveWindowProtectionService_StatusChanged;
        DataObject.AddCopyingHandler(CaptionTextBox, CaptionTextBox_Copying);

        _sensitiveWindowProtectionService.StatusChanged += SensitiveWindowProtectionService_StatusChanged;
        _sensitiveWindowProtectionService.Register(this, "Caption overlay window for live captions, selected text, private notes, and submitted prompt content.");
        UpdateSensitiveWindowProtectionIndicator(_sensitiveWindowProtectionService.GetStatus(this));
    }

    public void LoadFrom(OverlaySettings settings)
    {
        _loading = true;
        var opacity = Math.Clamp(settings.Opacity, 0.35, 1);
        Left = settings.Left;
        Top = settings.Top;
        Width = Math.Max(MinWidth, settings.Width);
        Height = Math.Max(MinHeight, settings.Height);
        OpacitySlider.Value = opacity;
        Opacity = opacity;
        TopmostCheckBox.IsChecked = settings.Topmost;
        Topmost = settings.Topmost;
        _loading = false;
    }

    public void LoadPrivacyFrom(PrivacySettings settings)
    {
        _redactWhenInactive = settings.RedactWhenInactive;
        SetManualRedactionCore(settings.ManualRedactionEnabled, notify: false);
    }

    public OverlaySettings CaptureSettings()
    {
        return new OverlaySettings
        {
            Left = RestoreBounds.Left,
            Top = RestoreBounds.Top,
            Width = RestoreBounds.Width,
            Height = RestoreBounds.Height,
            Opacity = Math.Clamp(OpacitySlider.Value, 0.35, 1),
            Topmost = TopmostCheckBox.IsChecked == true
        };
    }

    public void SetManualRedaction(bool enabled)
    {
        SetManualRedactionCore(enabled, notify: false);
    }

    public void UpdateCaption(string caption)
    {
        if (string.Equals(caption, _lastCaption, StringComparison.Ordinal))
        {
            return;
        }

        _lastCaption = caption;
        ApplyCaption(caption);
    }

    public void SetCaptureStatus(string status)
    {
        CaptureStatusText.Text = status;
    }

    public void SetSubmitStatus(string status)
    {
        SubmitStatusText.Text = status;
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void ApplyCaption(string caption)
    {
        if (_mouseSelecting)
        {
            _pendingCaption = caption;
            return;
        }

        var hadFocus = CaptionTextBox.IsKeyboardFocusWithin;
        var selectionStart = CaptionTextBox.SelectionStart;
        var selectionLength = CaptionTextBox.SelectionLength;
        var selectedText = selectionLength > 0 ? CaptionTextBox.SelectedText : string.Empty;
        var caretIndex = CaptionTextBox.CaretIndex;
        var shouldAutoScroll = _autoScrollToBottom || IsCaptionScrolledToBottom();
        var previousVerticalOffset = _captionScrollViewer?.VerticalOffset ?? 0;

        CaptionTextBox.Text = caption;

        if (selectionLength > 0)
        {
            RestoreManualSelection(caption, selectedText, selectionStart, selectionLength);
            RestoreScrollPosition(shouldAutoScroll, previousVerticalOffset);
            return;
        }

        if (hadFocus)
        {
            CaptionTextBox.CaretIndex = Math.Clamp(caretIndex, 0, CaptionTextBox.Text.Length);
            RestoreScrollPosition(shouldAutoScroll, previousVerticalOffset);
            return;
        }

        CaptionTextBox.CaretIndex = CaptionTextBox.Text.Length;
        RestoreScrollPosition(shouldAutoScroll, previousVerticalOffset);
    }

    private void RestoreManualSelection(string caption, string selectedText, int previousStart, int previousLength)
    {
        var nextStart = -1;
        if (!string.IsNullOrWhiteSpace(selectedText))
        {
            nextStart = caption.IndexOf(selectedText, StringComparison.Ordinal);
        }

        if (nextStart < 0)
        {
            nextStart = Math.Clamp(previousStart, 0, CaptionTextBox.Text.Length);
        }

        var nextLength = Math.Min(previousLength, CaptionTextBox.Text.Length - nextStart);
        CaptionTextBox.Select(nextStart, Math.Max(0, nextLength));
    }

    private void SubmitCurrentText()
    {
        if (ShouldRedact(IsActive))
        {
            SetSubmitStatus("Privacy mode active");
            return;
        }

        var text = CaptionTextBox.SelectedText;
        if (string.IsNullOrWhiteSpace(text))
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    text = System.Windows.Clipboard.GetText();
                }
            }
            catch
            {
                text = string.Empty;
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            text = CaptionTextBox.Text;
        }

        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            SetSubmitStatus("No text selected");
            return;
        }

        TextSubmitted?.Invoke(this, text);
    }

    private void OverlayWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            SetManualRedactionCore(!_manualRedaction, notify: true);
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        SubmitCurrentText();
    }

    private void CaptionTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.PageUp or Key.Home)
        {
            _autoScrollToBottom = false;
        }
        else if (e.Key is Key.Down or Key.PageDown or Key.End)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (IsCaptionScrolledToBottom())
                {
                    _autoScrollToBottom = true;
                }
            });
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (Keyboard.Modifiers == ModifierKeys.None)
            {
                SubmitCurrentText();
            }
        }
    }

    private void CaptionTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            _autoScrollToBottom = false;
        }
    }

    private void CaptionTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseSelecting = true;
    }

    private void CaptionTextBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _mouseSelecting = false;

        if (_pendingCaption is not null && !string.Equals(_pendingCaption, CaptionTextBox.Text, StringComparison.Ordinal))
        {
            var pendingCaption = _pendingCaption;
            _pendingCaption = null;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ApplyCaption(pendingCaption));
        }
    }

    private void CaptionTextBox_Copying(object sender, DataObjectCopyingEventArgs e)
    {
        if (_sensitiveWindowProtectionService.Enabled)
        {
            SetSubmitStatus("Copied text can be captured from the clipboard.");
        }
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        Opacity = Math.Clamp(OpacitySlider.Value, 0.35, 1);
        RaiseSettingsChanged();
    }

    private void TopmostCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Topmost = TopmostCheckBox.IsChecked == true;
        RaiseSettingsChanged();
    }

    private void PrivacyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_privacyLoading)
        {
            return;
        }

        SetManualRedactionCore(PrivacyCheckBox.IsChecked == true, notify: true);
    }

    private void SetManualRedactionCore(bool enabled, bool notify)
    {
        _manualRedaction = enabled;
        _privacyLoading = true;
        PrivacyCheckBox.IsChecked = enabled;
        _privacyLoading = false;

        ApplyPrivacyCover();

        if (notify)
        {
            ManualRedactionChanged?.Invoke(enabled);
        }
    }

    private void ApplyPrivacyCover()
    {
        var shouldRedact = ShouldRedact(IsActive);
        CaptionTextBox.Visibility = shouldRedact ? Visibility.Hidden : Visibility.Visible;
        PrivacyCover.Visibility = shouldRedact ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = !shouldRedact;
    }

    private void SensitiveWindowProtectionService_StatusChanged(object? sender, SensitiveWindowProtectionSummary summary)
    {
        Dispatcher.BeginInvoke(() => UpdateSensitiveWindowProtectionIndicator(_sensitiveWindowProtectionService.GetStatus(this)));
    }

    private void UpdateSensitiveWindowProtectionIndicator(SensitiveWindowProtectionSnapshot? snapshot)
    {
        if (!_sensitiveWindowProtectionService.Enabled)
        {
            ProtectionStatusText.Visibility = Visibility.Collapsed;
            ProtectionStatusText.ToolTip = null;
            return;
        }

        ProtectionStatusText.Visibility = Visibility.Visible;
        ProtectionStatusText.ToolTip = snapshot?.Result.Message
            ?? "Sensitive Window Protection status is unavailable for this window.";

        if (snapshot?.Result.IsProtected == true)
        {
            ProtectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(217, 247, 215));
            ProtectionStatusText.Text = "Capture protection on";
            return;
        }

        ProtectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 222, 179));
        ProtectionStatusText.Text = "Protection warning";
    }

    private bool ShouldRedact(bool isActive)
    {
        return _manualRedaction || (_redactWhenInactive && !isActive);
    }

    private void RaiseSettingsChanged()
    {
        if (!_loading && IsLoaded)
        {
            SettingsChanged?.Invoke(this, CaptureSettings());
        }
    }

    private void AttachCaptionScrollViewer()
    {
        CaptionTextBox.ApplyTemplate();
        _captionScrollViewer = FindVisualChild<ScrollViewer>(CaptionTextBox);
        if (_captionScrollViewer is null)
        {
            return;
        }

        _captionScrollViewer.ScrollChanged += CaptionScrollViewer_ScrollChanged;
        CaptionTextBox.PreviewMouseWheel += CaptionTextBox_PreviewMouseWheel;
        _autoScrollToBottom = IsCaptionScrolledToBottom();
    }

    private void CaptionScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_scrollingProgrammatically || _captionScrollViewer is null)
        {
            return;
        }

        if (e.VerticalChange < 0)
        {
            _autoScrollToBottom = false;
            return;
        }

        if (IsCaptionScrolledToBottom())
        {
            _autoScrollToBottom = true;
        }
    }

    private void RestoreScrollPosition(bool shouldAutoScroll, double previousVerticalOffset)
    {
        if (shouldAutoScroll)
        {
            ScrollCaptionToBottom();
            return;
        }

        RestoreCaptionScrollOffset(previousVerticalOffset);
    }

    private bool IsCaptionScrolledToBottom()
    {
        if (_captionScrollViewer is null)
        {
            return true;
        }

        return _captionScrollViewer.VerticalOffset >= _captionScrollViewer.ScrollableHeight - 1;
    }

    private void ScrollCaptionToBottom()
    {
        _autoScrollToBottom = true;
        _scrollingProgrammatically = true;

        ScrollCaptionToBottomNow();

        _deferredScrollOperation?.Abort();
        _deferredScrollOperation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            ScrollCaptionToBottomNow();
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                ScrollCaptionToBottomNow();
                _scrollingProgrammatically = false;
                _autoScrollToBottom = true;
                _deferredScrollOperation = null;
            });
        });
    }

    private void RestoreCaptionScrollOffset(double previousVerticalOffset)
    {
        if (_captionScrollViewer is null)
        {
            return;
        }

        _scrollingProgrammatically = true;
        _deferredScrollOperation?.Abort();
        _deferredScrollOperation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _captionScrollViewer.ScrollToVerticalOffset(Math.Min(previousVerticalOffset, _captionScrollViewer.ScrollableHeight));
            _scrollingProgrammatically = false;
            _deferredScrollOperation = null;
        });
    }

    private void ScrollCaptionToBottomNow()
    {
        CaptionTextBox.ScrollToEnd();
        _captionScrollViewer?.ScrollToEnd();
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var nestedChild = FindVisualChild<T>(child);
            if (nestedChild is not null)
            {
                return nestedChild;
            }
        }

        return null;
    }

    private void HandleClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            RaiseSettingsChanged();
            return;
        }

        e.Cancel = true;
        Hide();
        RaiseSettingsChanged();
    }
}
