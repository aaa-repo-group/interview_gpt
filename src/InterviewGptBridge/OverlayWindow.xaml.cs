using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using InterviewGptBridge.Services;
using WpfDataObject = System.Windows.DataObject;

namespace InterviewGptBridge;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly ISensitiveWindowProtectionService _sensitiveWindowProtectionService;
    private bool _loading;
    private bool _allowClose;
    private bool _clickThrough;
    private bool _keepAboveMainWindow = true;
    private double _windowOpacity = 1.0;
    private HwndSource? _hwndSource;
    private bool _mouseSelecting;
    private bool _scrollingProgrammatically;
    private bool _autoScrollToBottom = true;
    private string? _pendingCaption;
    private string _lastCaption = string.Empty;
    private ScrollViewer? _captionScrollViewer;
    private DispatcherOperation? _deferredScrollOperation;

    public event EventHandler<string>? TextSubmitted;
    public event EventHandler<OverlaySettings>? SettingsChanged;

    public OverlayWindow(ISensitiveWindowProtectionService sensitiveWindowProtectionService)
    {
        _sensitiveWindowProtectionService = sensitiveWindowProtectionService;
        InitializeComponent();
        AltTabWindowHider.HideFromAltTab(this);
        LocationChanged += (_, _) => RaiseSettingsChanged();
        SizeChanged += (_, _) => RaiseSettingsChanged();
        Loaded += (_, _) => AttachCaptionScrollViewer();
        SourceInitialized += (_, _) =>
        {
            AttachHwndHook();
            ApplyClickThrough();
            ApplyWindowOpacity();
        };
        PreviewKeyDown += OverlayWindow_PreviewKeyDown;
        Closing += HandleClosing;
        Closed += (_, _) =>
        {
            DetachHwndHook();
            _sensitiveWindowProtectionService.StatusChanged -= SensitiveWindowProtectionService_StatusChanged;
        };
        WpfDataObject.AddCopyingHandler(CaptionTextBox, CaptionTextBox_Copying);

        _sensitiveWindowProtectionService.StatusChanged += SensitiveWindowProtectionService_StatusChanged;
        _sensitiveWindowProtectionService.Register(this, "Caption overlay window for live captions, selected text, private notes, and submitted prompt content.");
        UpdateSensitiveWindowProtectionIndicator(_sensitiveWindowProtectionService.GetStatus(this));
    }

    public void LoadFrom(OverlaySettings settings)
    {
        _loading = true;
        var fontSize = Math.Clamp(settings.FontSize, 12, 36);
        Width = Math.Max(MinWidth, settings.Width);
        Height = Math.Max(MinHeight, settings.Height);
        ApplyVisiblePosition(settings.Left, settings.Top);
        FontSizeSlider.Value = fontSize;
        CaptionTextBox.FontSize = fontSize;
        _windowOpacity = WindowOpacityController.Normalize(settings.WindowOpacity);
        CaptionOpacitySlider.Value = _windowOpacity * 100;
        Topmost = true;
        _clickThrough = settings.ClickThrough;
        _keepAboveMainWindow = settings.KeepAboveMainWindow;
        ApplyClickThrough();
        ApplyWindowOpacity();
        _loading = false;
    }

    public OverlaySettings CaptureSettings()
    {
        return new OverlaySettings
        {
            Left = RestoreBounds.Left,
            Top = RestoreBounds.Top,
            Width = RestoreBounds.Width,
            Height = RestoreBounds.Height,
            FontSize = Math.Clamp(FontSizeSlider.Value, 12, 36),
            WindowOpacity = _windowOpacity,
            Topmost = true,
            ClickThrough = _clickThrough,
            KeepAboveMainWindow = _keepAboveMainWindow
        };
    }

    public void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;

        ApplyClickThrough();
        ApplyWindowOpacity();
        RaiseSettingsChanged();
    }

    public void SetWindowOpacity(double opacity)
    {
        _windowOpacity = WindowOpacityController.Normalize(opacity);
        if (CaptionOpacitySlider is not null)
        {
            CaptionOpacitySlider.Value = _windowOpacity * 100;
        }

        ApplyWindowOpacity();
        RaiseSettingsChanged();
    }

    public void SetKeepAboveMainWindow(bool enabled)
    {
        _keepAboveMainWindow = enabled;
        RaiseSettingsChanged();
    }

    public void EnsureAboveMainWindow()
    {
        if (!IsVisible || !_keepAboveMainWindow)
        {
            return;
        }

        Topmost = true;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
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
            return;
        }

        TextSubmitted?.Invoke(this, text);
    }

    private void OverlayWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
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
        }
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        if (CaptionTextBox is null)
        {
            return;
        }

        CaptionTextBox.FontSize = Math.Clamp(FontSizeSlider.Value, 12, 36);
        RaiseSettingsChanged();
    }

    private void CaptionOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _windowOpacity = WindowOpacityController.Normalize(CaptionOpacitySlider.Value / 100);
        ApplyWindowOpacity();
        RaiseSettingsChanged();
    }

    private void SensitiveWindowProtectionService_StatusChanged(object? sender, SensitiveWindowProtectionSummary summary)
    {
        Dispatcher.BeginInvoke(() => UpdateSensitiveWindowProtectionIndicator(_sensitiveWindowProtectionService.GetStatus(this)));
    }

    private void UpdateSensitiveWindowProtectionIndicator(SensitiveWindowProtectionSnapshot? snapshot)
    {
        if (!_sensitiveWindowProtectionService.Enabled)
        {
            return;
        }
    }

    private void ApplyClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(hwnd, GwlExStyle);
        if (_clickThrough)
        {
            style |= WsExTransparent | WsExLayered;
        }
        else
        {
            style &= ~WsExTransparent;
        }

        SetWindowLong(hwnd, GwlExStyle, style);
    }

    private void ApplyWindowOpacity()
    {
        Opacity = WindowOpacityController.Normalize(_windowOpacity);
    }

    private void AttachHwndHook()
    {
        if (_hwndSource is not null)
        {
            return;
        }

        _hwndSource = HwndSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(OverlayWindowProc);
    }

    private void DetachHwndHook()
    {
        if (_hwndSource is null)
        {
            return;
        }

        _hwndSource.RemoveHook(OverlayWindowProc);
        _hwndSource = null;
    }

    private IntPtr OverlayWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_clickThrough && msg == WmNcHitTest)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        return IntPtr.Zero;
    }

    private void ApplyVisiblePosition(double savedLeft, double savedTop)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        var maxLeft = Math.Max(virtualLeft, virtualRight - Width);
        var maxTop = Math.Max(virtualTop, virtualBottom - Height);

        Left = double.IsFinite(savedLeft)
            ? Math.Clamp(savedLeft, virtualLeft, maxLeft)
            : virtualLeft + 80;
        Top = double.IsFinite(savedTop)
            ? Math.Clamp(savedTop, virtualTop, maxTop)
            : virtualTop + 80;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int width, int height, uint flags);
}
