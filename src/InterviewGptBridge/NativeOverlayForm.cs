using System.ComponentModel;
using System.Runtime.InteropServices;
using InterviewGptBridge.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace InterviewGptBridge;

public sealed class NativeOverlayForm : Forms.Form
{
    private const int GwlExStyle = -20;
    private const int WmNcHitTest = 0x0084;
    private const int WmSetCursor = 0x0020;
    private const int HtTransparent = -1;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly string _protectionWindowId;
    private readonly ISensitiveWindowProtectionService _sensitiveWindowProtectionService;
    private readonly ArrowTextBox _captionTextBox = new();
    private readonly Forms.Panel _controlBar = new();
    private readonly Forms.TrackBar _fontSizeTrackBar = new();
    private readonly Forms.TrackBar _opacityTrackBar = new();
    private readonly Forms.Timer _opacityReapplyTimer = new();
    private bool _loading;
    private bool _allowClose;
    private bool _clickThrough;
    private bool _keepAboveMainWindow = true;
    private bool _mouseSelecting;
    private bool _autoScrollToBottom = true;
    private string? _pendingCaption;
    private string _lastCaption = string.Empty;
    private double _windowOpacity = 1.0;

    public event EventHandler<string>? TextSubmitted;
    public event EventHandler<OverlaySettings>? SettingsChanged;

    public NativeOverlayForm(ISensitiveWindowProtectionService sensitiveWindowProtectionService)
    {
        _sensitiveWindowProtectionService = sensitiveWindowProtectionService;
        _protectionWindowId = nameof(NativeOverlayForm) + "#" + GetHashCode().ToString("X");

        InitializeNativeUi();

        _opacityReapplyTimer.Interval = 750;
        _opacityReapplyTimer.Tick += (_, _) => ApplyWindowOpacity();

        Load += (_, _) =>
        {
            ApplyClickThrough();
            ApplyWindowOpacity();
        };
        HandleCreated += (_, _) =>
        {
            ApplyClickThrough();
            ApplyWindowOpacity();
            _sensitiveWindowProtectionService.ReapplyAll();
        };
        Move += (_, _) => RaiseSettingsChanged();
        Resize += (_, _) =>
        {
            LayoutNativeUi();
            RaiseSettingsChanged();
        };
        FormClosing += HandleClosing;
        FormClosed += (_, _) =>
        {
            _opacityReapplyTimer.Stop();
            _sensitiveWindowProtectionService.UnregisterWindowHandle(_protectionWindowId);
        };

        _sensitiveWindowProtectionService.RegisterWindowHandle(
            _protectionWindowId,
            nameof(NativeOverlayForm),
            "Native caption overlay window for live captions, selected text, private notes, and submitted prompt content.",
            () => IsDisposed ? IntPtr.Zero : Handle);
    }

    public void LoadFrom(OverlaySettings settings)
    {
        _loading = true;
        var fontSize = Math.Clamp((int)Math.Round(settings.FontSize), 12, 36);
        Width = Math.Max(MinimumSize.Width, (int)Math.Round(settings.Width));
        Height = Math.Max(MinimumSize.Height, (int)Math.Round(settings.Height));
        ApplyVisiblePosition(settings.Left, settings.Top);

        _windowOpacity = WindowOpacityController.Normalize(settings.WindowOpacity);
        _fontSizeTrackBar.Value = fontSize;
        _captionTextBox.Font = new Drawing.Font("Segoe UI", fontSize, Drawing.FontStyle.Regular);
        _opacityTrackBar.Value = ToPercent(_windowOpacity);
        TopMost = true;
        _clickThrough = settings.ClickThrough;
        _keepAboveMainWindow = settings.KeepAboveMainWindow;
        _loading = false;

        LayoutNativeUi();
        ApplyClickThrough();
        ApplyWindowOpacity();
    }

    public OverlaySettings CaptureSettings()
    {
        var bounds = WindowState == Forms.FormWindowState.Normal ? Bounds : RestoreBounds;
        return new OverlaySettings
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            FontSize = Math.Clamp(_fontSizeTrackBar.Value, 12, 36),
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
        var percent = ToPercent(_windowOpacity);
        if (_opacityTrackBar.Value != percent)
        {
            _opacityTrackBar.Value = percent;
        }

        ApplyWindowOpacity();
        RaiseSettingsChanged();
    }

    public void SetCaptionFontSize(double fontSize)
    {
        var nextFontSize = Math.Clamp((int)Math.Round(fontSize, MidpointRounding.AwayFromZero), 12, 36);
        if (_fontSizeTrackBar.Value != nextFontSize)
        {
            _fontSizeTrackBar.Value = nextFontSize;
        }

        _captionTextBox.Font = new Drawing.Font("Segoe UI", nextFontSize, Drawing.FontStyle.Regular);
        RaiseSettingsChanged();
    }

    public void SetKeepAboveMainWindow(bool enabled)
    {
        _keepAboveMainWindow = enabled;
        RaiseSettingsChanged();
    }

    public void EnsureAboveMainWindow()
    {
        if (!Visible || !_keepAboveMainWindow)
        {
            return;
        }

        TopMost = true;
        if (Handle != IntPtr.Zero)
        {
            SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
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

    protected override void WndProc(ref Forms.Message m)
    {
        if (_clickThrough && m.Msg == WmNcHitTest)
        {
            m.Result = new IntPtr(HtTransparent);
            return;
        }

        base.WndProc(ref m);
    }

    private void InitializeNativeUi()
    {
        SuspendLayout();

        Text = "ThanksAAA";
        Width = 680;
        Height = 280;
        MinimumSize = new Drawing.Size(360, 150);
        StartPosition = Forms.FormStartPosition.Manual;
        BackColor = Drawing.Color.FromArgb(21, 25, 34);
        ForeColor = Drawing.Color.FromArgb(244, 247, 251);
        Icon = AppIcon.LoadDrawingIcon();
        ShowInTaskbar = false;
        TopMost = true;

        _captionTextBox.Multiline = true;
        _captionTextBox.ReadOnly = true;
        _captionTextBox.WordWrap = true;
        _captionTextBox.ScrollBars = Forms.ScrollBars.Vertical;
        _captionTextBox.BorderStyle = Forms.BorderStyle.None;
        _captionTextBox.BackColor = Drawing.Color.FromArgb(21, 25, 34);
        _captionTextBox.ForeColor = Drawing.Color.FromArgb(244, 247, 251);
        _captionTextBox.Cursor = Forms.Cursors.Arrow;
        _captionTextBox.Font = new Drawing.Font("Segoe UI", 18, Drawing.FontStyle.Regular);
        _captionTextBox.Padding = new Forms.Padding(12);
        _captionTextBox.KeyDown += CaptionTextBox_KeyDown;
        _captionTextBox.MouseDown += (_, _) => _mouseSelecting = true;
        _captionTextBox.MouseUp += (_, _) => FinishMouseSelection();
        _captionTextBox.MouseWheel += (_, e) =>
        {
            if (e.Delta > 0)
            {
                _autoScrollToBottom = false;
            }
        };

        _controlBar.BackColor = Drawing.Color.FromArgb(32, 38, 50);
        _controlBar.Height = 30;

        ConfigureTrackBar(_fontSizeTrackBar, 12, 36, 18);
        ConfigureTrackBar(_opacityTrackBar, 0, 100, 100);
        _fontSizeTrackBar.ValueChanged += FontSizeTrackBar_ValueChanged;
        _opacityTrackBar.ValueChanged += OpacityTrackBar_ValueChanged;

        _controlBar.Controls.Add(_fontSizeTrackBar);
        _controlBar.Controls.Add(_opacityTrackBar);
        Controls.Add(_captionTextBox);
        Controls.Add(_controlBar);

        LayoutNativeUi();
        ResumeLayout();
    }

    private static void ConfigureTrackBar(Forms.TrackBar trackBar, int minimum, int maximum, int value)
    {
        trackBar.AutoSize = false;
        trackBar.Minimum = minimum;
        trackBar.Maximum = maximum;
        trackBar.Value = value;
        trackBar.TickFrequency = 1;
        trackBar.TickStyle = Forms.TickStyle.None;
    }

    private void LayoutNativeUi()
    {
        var barHeight = _controlBar.Height;
        _captionTextBox.SetBounds(0, 0, ClientSize.Width, Math.Max(0, ClientSize.Height - barHeight));
        _controlBar.SetBounds(0, Math.Max(0, ClientSize.Height - barHeight), ClientSize.Width, barHeight);

        var gap = 8;
        var width = Math.Max(80, (_controlBar.ClientSize.Width - gap - 16) / 2);
        _fontSizeTrackBar.SetBounds(8, 4, width, 22);
        _opacityTrackBar.SetBounds(_fontSizeTrackBar.Right + gap, 4, width, 22);
    }

    private void ApplyCaption(string caption)
    {
        if (_mouseSelecting)
        {
            _pendingCaption = caption;
            return;
        }

        var hadFocus = _captionTextBox.Focused;
        var selectionStart = _captionTextBox.SelectionStart;
        var selectionLength = _captionTextBox.SelectionLength;
        var selectedText = selectionLength > 0 ? _captionTextBox.SelectedText : string.Empty;
        var shouldAutoScroll = _autoScrollToBottom || IsScrolledToBottom();

        _captionTextBox.Text = caption;

        if (selectionLength > 0)
        {
            RestoreManualSelection(caption, selectedText, selectionStart, selectionLength);
        }
        else if (hadFocus)
        {
            _captionTextBox.SelectionStart = Math.Clamp(selectionStart, 0, _captionTextBox.TextLength);
            _captionTextBox.SelectionLength = 0;
        }
        else
        {
            _captionTextBox.SelectionStart = _captionTextBox.TextLength;
        }

        if (shouldAutoScroll)
        {
            ScrollCaptionToBottom();
        }
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
            nextStart = Math.Clamp(previousStart, 0, _captionTextBox.TextLength);
        }

        _captionTextBox.SelectionStart = nextStart;
        _captionTextBox.SelectionLength = Math.Max(0, Math.Min(previousLength, _captionTextBox.TextLength - nextStart));
    }

    private void FinishMouseSelection()
    {
        _mouseSelecting = false;
        if (_pendingCaption is null || string.Equals(_pendingCaption, _captionTextBox.Text, StringComparison.Ordinal))
        {
            return;
        }

        var pendingCaption = _pendingCaption;
        _pendingCaption = null;
        BeginInvoke(() => ApplyCaption(pendingCaption));
    }

    private void CaptionTextBox_KeyDown(object? sender, Forms.KeyEventArgs e)
    {
        if (e.KeyCode is Forms.Keys.Up or Forms.Keys.PageUp or Forms.Keys.Home)
        {
            _autoScrollToBottom = false;
        }

        if (e.KeyCode == Forms.Keys.Enter && e.Modifiers == Forms.Keys.None)
        {
            e.Handled = true;
            SubmitCurrentText();
        }
    }

    private void SubmitCurrentText()
    {
        var text = _captionTextBox.SelectedText;
        if (string.IsNullOrWhiteSpace(text))
        {
            try
            {
                if (Forms.Clipboard.ContainsText())
                {
                    text = Forms.Clipboard.GetText();
                }
            }
            catch
            {
                text = string.Empty;
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            text = _captionTextBox.Text;
        }

        text = text.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            TextSubmitted?.Invoke(this, text);
        }
    }

    private void FontSizeTrackBar_ValueChanged(object? sender, EventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _captionTextBox.Font = new Drawing.Font("Segoe UI", _fontSizeTrackBar.Value, Drawing.FontStyle.Regular);
        RaiseSettingsChanged();
    }

    private void OpacityTrackBar_ValueChanged(object? sender, EventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _windowOpacity = WindowOpacityController.Normalize(_opacityTrackBar.Value / 100.0);
        ApplyWindowOpacity();
        RaiseSettingsChanged();
    }

    private void ApplyClickThrough()
    {
        SetTransparentStyle(Handle, _clickThrough);
    }

    private void ApplyWindowOpacity()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        WindowOpacityController.ApplyWindowTree(Handle, _windowOpacity);
        ApplyClickThrough();

        if (_windowOpacity < WindowOpacityController.MaximumOpacity)
        {
            if (!_opacityReapplyTimer.Enabled)
            {
                _opacityReapplyTimer.Start();
            }
        }
        else
        {
            _opacityReapplyTimer.Stop();
        }
    }

    private void ApplyVisiblePosition(double savedLeft, double savedTop)
    {
        var virtualLeft = (int)System.Windows.SystemParameters.VirtualScreenLeft;
        var virtualTop = (int)System.Windows.SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + (int)System.Windows.SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + (int)System.Windows.SystemParameters.VirtualScreenHeight;
        var maxLeft = Math.Max(virtualLeft, virtualRight - Width);
        var maxTop = Math.Max(virtualTop, virtualBottom - Height);

        Left = double.IsFinite(savedLeft)
            ? Math.Clamp((int)Math.Round(savedLeft), virtualLeft, maxLeft)
            : virtualLeft + 80;
        Top = double.IsFinite(savedTop)
            ? Math.Clamp((int)Math.Round(savedTop), virtualTop, maxTop)
            : virtualTop + 80;
    }

    private bool IsScrolledToBottom()
    {
        return GetScrollPos(_captionTextBox.Handle, SbVert) >= Math.Max(0, GetScrollMax(_captionTextBox.Handle, SbVert) - 2);
    }

    private void ScrollCaptionToBottom()
    {
        _autoScrollToBottom = true;
        _captionTextBox.SelectionStart = _captionTextBox.TextLength;
        _captionTextBox.ScrollToCaret();
    }

    private void RaiseSettingsChanged()
    {
        if (!_loading && IsHandleCreated)
        {
            SettingsChanged?.Invoke(this, CaptureSettings());
        }
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

    private static int ToPercent(double opacity)
    {
        return (int)Math.Round(WindowOpacityController.Normalize(opacity) * 100, MidpointRounding.AwayFromZero);
    }

    private static void SetTransparentStyle(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(hwnd, GwlExStyle);
        if (enabled)
        {
            style |= WsExLayered | WsExTransparent;
        }
        else
        {
            style &= ~WsExTransparent;
        }

        SetWindowLong(hwnd, GwlExStyle, style);
    }

    private sealed class ArrowTextBox : Forms.TextBox
    {
        protected override void WndProc(ref Forms.Message m)
        {
            if (m.Msg == WmSetCursor)
            {
                Forms.Cursor.Current = Forms.Cursors.Arrow;
                m.Result = new IntPtr(1);
                return;
            }

            base.WndProc(ref m);
        }
    }

    private const int SbVert = 1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetScrollPos(IntPtr hwnd, int bar);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetScrollMax(IntPtr hwnd, int bar);
}
