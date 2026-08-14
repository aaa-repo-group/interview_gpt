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
    private const int WmSetRedraw = 0x000B;
    private const int WmVScroll = 0x0115;
    private const int HtTransparent = -1;
    private const int SbBottomCommand = 7;
    private const uint SifRange = 0x0001;
    private const uint SifPage = 0x0002;
    private const uint SifPos = 0x0004;
    private const uint SifBottomStatus = SifRange | SifPage | SifPos;
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
    private readonly Forms.Timer _bottomScrollTimer = new();
    private bool _loading;
    private bool _allowClose;
    private bool _clickThrough;
    private bool _keepAboveMainWindow = true;
    private bool _mouseSelecting;
    private bool _keyboardSelecting;
    private bool _autoScrollToBottom = true;
    private string _lastCaption = string.Empty;
    private string _captionHistory = string.Empty;
    private string _lastRenderedCaption = string.Empty;
    private bool _selectionTracksEnd;
    private int _trackedSelectionStart = -1;
    private string _trackedAnchorPrefix = string.Empty;
    private double _windowOpacity = 1.0;

    public event EventHandler<string>? TextSubmitted;
    public event EventHandler<string>? TextPreparedForEdit;
    public event EventHandler<OverlaySettings>? SettingsChanged;

    public NativeOverlayForm(ISensitiveWindowProtectionService sensitiveWindowProtectionService)
    {
        _sensitiveWindowProtectionService = sensitiveWindowProtectionService;
        _protectionWindowId = nameof(NativeOverlayForm) + "#" + GetHashCode().ToString("X");
        AltTabWindowHider.HideFromAltTab(this);

        InitializeNativeUi();

        _opacityReapplyTimer.Interval = 750;
        _opacityReapplyTimer.Tick += (_, _) => ApplyWindowOpacity();
        _bottomScrollTimer.Interval = 30;
        _bottomScrollTimer.Tick += (_, _) =>
        {
            _bottomScrollTimer.Stop();
            ScrollCaptionToBottomNow();
        };

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
            _bottomScrollTimer.Stop();
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
        if (string.IsNullOrWhiteSpace(caption))
        {
            return;
        }

        var previousHistory = _captionHistory;
        var repeatedAfterSilence = string.Equals(caption, _lastCaption, StringComparison.Ordinal);
        var merge = CaptionHistoryMerger.MergeDetailed(_captionHistory, _lastCaption, caption, repeatedAfterSilence);
        _captionHistory = merge.History;
        _lastCaption = caption;

        ApplyCaption(merge, previousHistory);
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
        _captionTextBox.HideSelection = false;
        _captionTextBox.WordWrap = true;
        _captionTextBox.ScrollBars = Forms.ScrollBars.Vertical;
        _captionTextBox.BorderStyle = Forms.BorderStyle.None;
        _captionTextBox.BackColor = Drawing.Color.FromArgb(21, 25, 34);
        _captionTextBox.ForeColor = Drawing.Color.FromArgb(244, 247, 251);
        _captionTextBox.Cursor = Forms.Cursors.Arrow;
        _captionTextBox.Font = new Drawing.Font("Segoe UI", 18, Drawing.FontStyle.Regular);
        _captionTextBox.Padding = new Forms.Padding(12);
        _captionTextBox.KeyDown += CaptionTextBox_KeyDown;
        _captionTextBox.KeyUp += CaptionTextBox_KeyUp;
        _captionTextBox.MouseDown += (_, _) =>
        {
            _mouseSelecting = true;
            _captionTextBox.Capture = true;
            ClearTrackedSelection();
        };
        _captionTextBox.MouseUp += (_, _) => FinishMouseSelection();
        _captionTextBox.MouseCaptureChanged += (_, _) =>
        {
            if (_mouseSelecting && Forms.Control.MouseButtons != Forms.MouseButtons.Left)
            {
                FinishMouseSelection();
            }
        };
        _captionTextBox.LostFocus += (_, _) =>
        {
            _mouseSelecting = false;
            _keyboardSelecting = false;
        };
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

    private void ApplyCaption(CaptionHistoryMergeResult merge, string previousHistory)
    {
        var caption = merge.History;
        if (string.Equals(caption, _lastRenderedCaption, StringComparison.Ordinal))
        {
            if (_autoScrollToBottom || _selectionTracksEnd)
            {
                QueueScrollCaptionToBottom();
            }

            return;
        }

        var previousCaption = _lastRenderedCaption;
        var hadFocus = _captionTextBox.Focused;
        var selectionStart = _captionTextBox.SelectionStart;
        var selectionLength = _captionTextBox.SelectionLength;
        var selectionEnd = selectionStart + selectionLength;
        var selectedText = selectionLength > 0 ? _captionTextBox.SelectedText : string.Empty;
        var shouldAutoScroll = _autoScrollToBottom || _selectionTracksEnd || IsScrolledToBottom();
        var selectionReachedOldEnd = selectionEnd >= Math.Max(0, previousCaption.Length - 2);

        SetTextBoxRedraw(false);
        try
        {
            ApplyCaptionTextChange(merge, previousHistory, caption);
            _lastRenderedCaption = caption;

            if (IsSelectionInProgress)
            {
                RestoreMappedSelectionDuringMouseSelection(
                    previousCaption,
                    caption,
                    selectionStart,
                    selectionEnd,
                    selectionReachedOldEnd,
                    shouldAutoScroll);
            }
            else if (_selectionTracksEnd && _trackedSelectionStart >= 0)
            {
                var nextStart = CaptionSelectionAnchor.MapStart(
                    previousCaption,
                    caption,
                    _trackedSelectionStart,
                    _trackedAnchorPrefix);
                _trackedSelectionStart = Math.Clamp(nextStart, 0, _captionTextBox.TextLength);
                _trackedAnchorPrefix = GetPrefix(_captionTextBox.Text, _trackedSelectionStart);
                SetSelection(_trackedSelectionStart, _captionTextBox.TextLength - _trackedSelectionStart);
            }
            else if (selectionLength > 0)
            {
                RestoreManualSelection(caption, selectedText, selectionStart, selectionLength);
            }
            else if (hadFocus)
            {
                SetSelection(selectionStart, 0);
            }
            else
            {
                SetSelection(_captionTextBox.TextLength, 0);
            }

            if (shouldAutoScroll)
            {
                ScrollCaptionToBottomNow();
                QueueScrollCaptionToBottom();
            }
        }
        finally
        {
            SetTextBoxRedraw(true);
        }
    }

    private void ApplyCaptionTextChange(CaptionHistoryMergeResult merge, string previousHistory, string caption)
    {
        if (!merge.HasChange ||
            !string.Equals(previousHistory, _lastRenderedCaption, StringComparison.Ordinal))
        {
            _captionTextBox.Text = caption;
            return;
        }

        var replaceStart = Math.Clamp(merge.ReplaceStart, 0, _captionTextBox.TextLength);
        var replaceLength = Math.Max(0, Math.Min(merge.ReplaceLength, _captionTextBox.TextLength - replaceStart));
        var wasReadOnly = _captionTextBox.ReadOnly;
        try
        {
            _captionTextBox.ReadOnly = false;
            _captionTextBox.SelectionStart = replaceStart;
            _captionTextBox.SelectionLength = replaceLength;
            _captionTextBox.SelectedText = merge.InsertedText;
        }
        finally
        {
            _captionTextBox.ReadOnly = wasReadOnly;
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

        SetSelection(nextStart, previousLength);
    }

    private void RestoreMappedSelectionDuringMouseSelection(
        string previousCaption,
        string caption,
        int previousStart,
        int previousEnd,
        bool selectionReachedOldEnd,
        bool shouldAutoScroll)
    {
        var nextStart = CaptionSelectionAnchor.MapStart(
            previousCaption,
            caption,
            previousStart,
            GetPrefix(previousCaption, previousStart));
        var nextEnd = selectionReachedOldEnd && shouldAutoScroll
            ? caption.Length
            : CaptionSelectionAnchor.MapStart(
                previousCaption,
                caption,
                previousEnd,
                GetPrefix(previousCaption, previousEnd));

        if (nextEnd < nextStart)
        {
            (nextStart, nextEnd) = (nextEnd, nextStart);
        }

        SetSelection(nextStart, nextEnd - nextStart);
    }

    private void FinishMouseSelection()
    {
        _mouseSelecting = false;
        if (_captionTextBox.Capture)
        {
            _captionTextBox.Capture = false;
        }

        UpdateSelectionTrackingFromCurrentSelection();
    }

    private void CaptionTextBox_KeyDown(object? sender, Forms.KeyEventArgs e)
    {
        if (e.KeyCode is Forms.Keys.Up or Forms.Keys.PageUp or Forms.Keys.Home)
        {
            _autoScrollToBottom = false;
            ClearTrackedSelection();
        }

        if ((e.Modifiers & Forms.Keys.Shift) == Forms.Keys.Shift &&
            e.KeyCode is Forms.Keys.Left or Forms.Keys.Right or Forms.Keys.Up or Forms.Keys.Down or Forms.Keys.Home or Forms.Keys.End)
        {
            _keyboardSelecting = true;
            ClearTrackedSelection();
        }

        if (e.KeyCode == Forms.Keys.A && e.Modifiers == Forms.Keys.Control)
        {
            e.Handled = true;
            _captionTextBox.SelectAll();
            UpdateSelectionTrackingFromCurrentSelection();
            return;
        }

        if (e.KeyCode == Forms.Keys.Enter && e.Modifiers == Forms.Keys.Shift)
        {
            e.Handled = true;
            var nextSelectionStart = _captionTextBox.SelectionStart + _captionTextBox.SelectionLength;
            PrepareCurrentTextForEdit();
            BeginAutoSelectionFrom(nextSelectionStart);
            return;
        }

        if (e.KeyCode == Forms.Keys.Enter && e.Modifiers == Forms.Keys.None)
        {
            e.Handled = true;
            var nextSelectionStart = _captionTextBox.SelectionStart + _captionTextBox.SelectionLength;
            SubmitCurrentText();
            BeginAutoSelectionFrom(nextSelectionStart);
        }
    }

    private void CaptionTextBox_KeyUp(object? sender, Forms.KeyEventArgs e)
    {
        if (e.KeyCode is Forms.Keys.Left or Forms.Keys.Right or Forms.Keys.Up or Forms.Keys.Down or Forms.Keys.Home or Forms.Keys.End or Forms.Keys.PageUp or Forms.Keys.PageDown)
        {
            UpdateSelectionTrackingFromCurrentSelection();
        }

        if ((Forms.Control.ModifierKeys & Forms.Keys.Shift) != Forms.Keys.Shift)
        {
            _keyboardSelecting = false;
        }
    }

    private bool IsSelectionInProgress => _mouseSelecting || _keyboardSelecting;

    private void SubmitCurrentText()
    {
        var text = GetCurrentTransferText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            TextSubmitted?.Invoke(this, text);
        }
    }

    private void PrepareCurrentTextForEdit()
    {
        var text = GetCurrentTransferText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            TextPreparedForEdit?.Invoke(this, text);
        }
    }

    private string GetCurrentTransferText()
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

        return text.Trim();
    }

    private void UpdateSelectionTrackingFromCurrentSelection()
    {
        var selectionStart = _captionTextBox.SelectionStart;
        var selectionLength = _captionTextBox.SelectionLength;
        if (selectionLength <= 0)
        {
            _selectionTracksEnd = _autoScrollToBottom || IsScrolledToBottom();
            _trackedSelectionStart = _selectionTracksEnd ? _captionTextBox.TextLength : -1;
            _trackedAnchorPrefix = _selectionTracksEnd ? _captionTextBox.Text : string.Empty;
            return;
        }

        var selectionEnd = selectionStart + selectionLength;
        _selectionTracksEnd = selectionEnd >= Math.Max(0, _captionTextBox.TextLength - 2);
        _trackedSelectionStart = _selectionTracksEnd ? selectionStart : -1;
        _trackedAnchorPrefix = _selectionTracksEnd ? GetPrefix(_captionTextBox.Text, selectionStart) : string.Empty;
        _autoScrollToBottom = _selectionTracksEnd;
    }

    private void BeginAutoSelectionFrom(int start)
    {
        _trackedSelectionStart = Math.Clamp(start, 0, _captionTextBox.TextLength);
        _trackedAnchorPrefix = GetPrefix(_captionTextBox.Text, _trackedSelectionStart);
        _selectionTracksEnd = true;
        _autoScrollToBottom = true;
        SetTextBoxRedraw(false);
        try
        {
            SetSelection(_trackedSelectionStart, _captionTextBox.TextLength - _trackedSelectionStart);
            ScrollCaptionToBottomNow();
        }
        finally
        {
            SetTextBoxRedraw(true);
        }

        QueueScrollCaptionToBottom();
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

        WindowOpacityController.Apply(Handle, _windowOpacity);
        ApplyClickThrough();
        _opacityReapplyTimer.Stop();
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
        if (_captionTextBox.IsDisposed || !_captionTextBox.IsHandleCreated)
        {
            return _autoScrollToBottom;
        }

        var info = new ScrollInfo
        {
            cbSize = Marshal.SizeOf<ScrollInfo>(),
            fMask = SifBottomStatus
        };

        if (!GetScrollInfo(_captionTextBox.Handle, SbVert, ref info))
        {
            return _autoScrollToBottom;
        }

        var page = info.nPage == 0 || info.nPage > int.MaxValue ? 1 : (int)info.nPage;
        var maxScrollablePosition = Math.Max(info.nMin, info.nMax - page + 1);
        return info.nPos >= maxScrollablePosition - 2;
    }

    private void QueueScrollCaptionToBottom()
    {
        _autoScrollToBottom = true;
        if (!_bottomScrollTimer.Enabled)
        {
            _bottomScrollTimer.Start();
        }
    }

    private void ScrollCaptionToBottomNow()
    {
        if (!IsHandleCreated || !_captionTextBox.IsHandleCreated)
        {
            return;
        }

        _autoScrollToBottom = true;
        SendMessage(_captionTextBox.Handle, WmVScroll, new IntPtr(SbBottomCommand), IntPtr.Zero);
    }

    private void SetSelection(int start, int length)
    {
        start = Math.Clamp(start, 0, _captionTextBox.TextLength);
        length = Math.Max(0, Math.Min(length, _captionTextBox.TextLength - start));
        _captionTextBox.SelectionStart = start;
        _captionTextBox.SelectionLength = length;
    }

    private void ClearTrackedSelection()
    {
        _selectionTracksEnd = false;
        _trackedSelectionStart = -1;
        _trackedAnchorPrefix = string.Empty;
    }

    private void SetTextBoxRedraw(bool enabled)
    {
        if (_captionTextBox.IsDisposed || !_captionTextBox.IsHandleCreated)
        {
            return;
        }

        try
        {
            SendMessage(_captionTextBox.Handle, WmSetRedraw, enabled ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
            if (enabled)
            {
                _captionTextBox.Invalidate();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string GetPrefix(string text, int length)
    {
        length = Math.Clamp(length, 0, text.Length);
        return length == 0 ? string.Empty : text[..length];
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
    private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref ScrollInfo scrollInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        public int cbSize;
        public uint fMask;
        public int nMin;
        public int nMax;
        public uint nPage;
        public int nPos;
        public int nTrackPos;
    }
}
