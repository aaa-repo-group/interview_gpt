using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Interop;
using InterviewGptBridge.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace InterviewGptBridge;

public sealed class NativeMainForm : Forms.Form
{
    private const string MainWindowRegistrationId = "NativeMainForm#Main";
    private const string FooterWindowRegistrationId = "NativeMainForm#Footer";
    private const int WmHotKey = 0x0312;
    private const int WmNcHitTest = 0x0084;
    private const int ToggleOverlayHotKeyId = 101;
    private const int ToggleMainWindowHotKeyId = 102;
    private const int ToggleAllWindowsHotKeyId = 103;
    private const int ToggleMainClickThroughHotKeyId = 104;
    private const int ToggleCaptionClickThroughHotKeyId = 105;
    private const int IncreaseMainOpacityHotKeyId = 106;
    private const int DecreaseMainOpacityHotKeyId = 107;
    private const int IncreaseCaptionOpacityHotKeyId = 108;
    private const int DecreaseCaptionOpacityHotKeyId = 109;
    private const int IncreaseCaptionFontSizeHotKeyId = 110;
    private const int DecreaseCaptionFontSizeHotKeyId = 111;
    private const int ToggleCaptionAboveMainHotKeyId = 112;
    private const int ToggleCaptureProtectionHotKeyId = 113;
    private const int HtTransparent = -1;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private static readonly JsonSerializerOptions SubmitJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsStore _settingsStore = new();
    private readonly ISensitiveWindowProtectionService _sensitiveWindowProtectionService = new SensitiveWindowProtectionService();
    private readonly WebView2 _browser = new();
    private readonly Forms.Form _footerWindow = new();
    private readonly Forms.Panel _footerPanel = new();
    private readonly Forms.Label _mainOpacityLabel = new();
    private readonly Forms.TrackBar _mainOpacityTrackBar = new();
    private readonly Forms.Label _mainOpacityValueLabel = new();
    private readonly Forms.CheckBox _mainClickThroughCheckBox = new();
    private readonly Forms.Panel _statusBanner = new();
    private readonly Forms.Label _statusText = new();
    private readonly Forms.Panel _protectionBanner = new();
    private readonly Forms.Label _protectionStatusText = new();
    private readonly Forms.ToolTip _toolTip = new();
    private readonly Forms.Timer _chatReadyProbeTimer;
    private readonly Forms.Timer _opacityReapplyTimer;
    private readonly TrayController _trayController;
    private AppSettings _settings;
    private LiveCaptionWatcher? _captionWatcher;
    private NativeOverlayForm? _overlayWindow;
    private SettingsWindow? _settingsWindow;
    private int _opacityReapplyTicksRemaining;
    private bool _mainWindowClickThrough;
    private bool _syncingFooterControls;
    private bool _overlayStarted;
    private bool _isExiting;
    private bool _servicesShutdown;
    private readonly HashSet<int> _registeredHotKeyIds = new();

    public NativeMainForm()
    {
        _settings = _settingsStore.Load();
        NormalizeSettings();
        _mainWindowClickThrough = _settings.MainWindowClickThrough;

        InitializeNativeUi();
        TopMost = true;

        _trayController = new TrayController();
        _trayController.ShowMainRequested += (_, _) => BeginOnUi(ShowMainWindow);
        _trayController.ShowOverlayRequested += (_, _) => BeginOnUi(ShowOverlayWindow);
        _trayController.SettingsRequested += (_, _) => BeginOnUi(ShowSettingsWindow);
        _trayController.HideAllRequested += (_, _) => BeginOnUi(HideAllWindows);
        _trayController.ClickThroughChanged += enabled => BeginOnUi(() => SetOverlayClickThrough(enabled));
        _trayController.ExitRequested += (_, _) => BeginOnUi(ExitApplication);
        _trayController.SetClickThrough(_settings.Overlay.ClickThrough);

        _sensitiveWindowProtectionService.StatusChanged += (_, summary) =>
            BeginOnUi(() => UpdateSensitiveWindowProtectionIndicator(summary));
        _sensitiveWindowProtectionService.RegisterWindowHandle(
            MainWindowRegistrationId,
            nameof(NativeMainForm),
            "Native ChatGPT WebView host window for confidential AI responses, prompts, secrets, and authentication content.",
            () => IsDisposed ? IntPtr.Zero : Handle);
        _sensitiveWindowProtectionService.RegisterWindowHandle(
            FooterWindowRegistrationId,
            "NativeMainFooterForm",
            "Native main-window footer controls for opacity and click-through settings.",
            GetFooterWindowHandleForProtection);
        _sensitiveWindowProtectionService.SetEnabled(_settings.Privacy.SensitiveWindowProtectionEnabled);

        _chatReadyProbeTimer = new Forms.Timer
        {
            Interval = 900
        };
        _chatReadyProbeTimer.Tick += async (_, _) => await ProbeChatGptReadyAsync();

        _opacityReapplyTimer = new Forms.Timer
        {
            Interval = 250
        };
        _opacityReapplyTimer.Tick += (_, _) => ReapplyMainWindowOpacityFromTimer();

        Load += async (_, _) =>
        {
            LiveCaptionsLauncher.LaunchAfterDelay(System.Windows.Application.Current.Dispatcher, TimeSpan.FromMilliseconds(700));
            StartOverlayAndCaptionWatcher();
            await InitializeBrowserAsync();
        };
        Shown += (_, _) =>
        {
            RegisterOverlayHotKeys();
            ShowFooterWindow();
            ApplyMainWindowOpacity();
            QueueMainWindowOpacityReapplyBurst();
        };
        Activated += (_, _) =>
        {
            _sensitiveWindowProtectionService.ReapplyAll();
            ShowFooterWindow();
            EnsureOverlayAboveMainWindow();
        };
        HandleCreated += (_, _) =>
        {
            RegisterOverlayHotKeys();
            ApplyMainWindowOpacity();
            QueueMainWindowOpacityReapplyBurst();
            _sensitiveWindowProtectionService.ReapplyAll();
        };
        HandleDestroyed += (_, _) =>
        {
            UnregisterOverlayHotKeys();
        };
        FormClosing += HandleClosing;
        Move += (_, _) => LayoutFooterWindow();
        Resize += (_, _) => LayoutMainControls();
        VisibleChanged += (_, _) => SyncFooterWindowVisibility();

        RegisterOverlayHotKeys();
        ShowBrowserContent();
        UpdateSensitiveWindowProtectionIndicator(_sensitiveWindowProtectionService.CurrentSummary);
    }

    private void InitializeNativeUi()
    {
        SuspendLayout();

        Text = "ThanksAAA";
        Width = 1180;
        Height = 820;
        MinimumSize = new Drawing.Size(360, 520);
        StartPosition = Forms.FormStartPosition.CenterScreen;
        BackColor = Drawing.Color.FromArgb(17, 20, 24);
        ForeColor = Drawing.Color.FromArgb(244, 247, 251);
        ShowInTaskbar = false;
        Icon = AppIcon.LoadDrawingIcon();
        KeyPreview = true;

        _browser.Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Bottom | Forms.AnchorStyles.Left | Forms.AnchorStyles.Right;
        _browser.DefaultBackgroundColor = Drawing.Color.FromArgb(17, 20, 24);

        _footerWindow.Text = "Controls";
        _footerWindow.FormBorderStyle = Forms.FormBorderStyle.None;
        _footerWindow.ShowInTaskbar = false;
        _footerWindow.StartPosition = Forms.FormStartPosition.Manual;
        _footerWindow.TopMost = true;
        _footerWindow.Height = 38;
        _footerWindow.MinimumSize = new Drawing.Size(260, 38);
        _footerWindow.BackColor = Drawing.Color.FromArgb(32, 38, 50);
        _footerWindow.ForeColor = Drawing.Color.FromArgb(244, 247, 251);
        _footerWindow.Deactivate += (_, _) =>
        {
            if (Visible && WindowState != Forms.FormWindowState.Minimized)
            {
                _footerWindow.TopMost = true;
            }
        };
        _footerWindow.HandleCreated += (_, _) => _sensitiveWindowProtectionService.ReapplyAll();
        _footerWindow.VisibleChanged += (_, _) =>
        {
            if (_footerWindow.Visible)
            {
                _sensitiveWindowProtectionService.ReapplyAll();
            }
        };

        _footerPanel.Dock = Forms.DockStyle.Fill;
        _footerPanel.BackColor = Drawing.Color.FromArgb(32, 38, 50);
        _footerPanel.Height = 38;
        _footerPanel.Padding = new Forms.Padding(10, 5, 10, 5);

        _mainOpacityLabel.AutoSize = false;
        _mainOpacityLabel.ForeColor = Drawing.Color.FromArgb(202, 209, 221);
        _mainOpacityLabel.Font = new Drawing.Font("Segoe UI", 8F, Drawing.FontStyle.Bold);
        _mainOpacityLabel.Text = "Opacity";
        _mainOpacityLabel.TextAlign = Drawing.ContentAlignment.MiddleLeft;

        _mainOpacityTrackBar.AutoSize = false;
        _mainOpacityTrackBar.Minimum = 0;
        _mainOpacityTrackBar.Maximum = 100;
        _mainOpacityTrackBar.TickFrequency = 10;
        _mainOpacityTrackBar.TickStyle = Forms.TickStyle.None;
        _mainOpacityTrackBar.Scroll += MainOpacityTrackBar_Scroll;

        _mainOpacityValueLabel.AutoSize = false;
        _mainOpacityValueLabel.ForeColor = Drawing.Color.FromArgb(202, 209, 221);
        _mainOpacityValueLabel.Font = new Drawing.Font("Segoe UI", 8F);
        _mainOpacityValueLabel.TextAlign = Drawing.ContentAlignment.MiddleRight;

        _mainClickThroughCheckBox.AutoSize = false;
        _mainClickThroughCheckBox.ForeColor = Drawing.Color.FromArgb(244, 247, 251);
        _mainClickThroughCheckBox.Font = new Drawing.Font("Segoe UI", 8F);
        _mainClickThroughCheckBox.Text = "Click through";
        _mainClickThroughCheckBox.TextAlign = Drawing.ContentAlignment.MiddleLeft;
        _mainClickThroughCheckBox.Click += MainClickThroughCheckBox_Click;

        _toolTip.SetToolTip(_mainOpacityTrackBar, "Main window opacity");
        _toolTip.SetToolTip(_mainClickThroughCheckBox, "Pass mouse clicks through the main browser area");

        _statusBanner.Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Left | Forms.AnchorStyles.Right;
        _statusBanner.BackColor = Drawing.Color.FromArgb(27, 31, 38);
        _statusBanner.Location = new Drawing.Point(12, 12);
        _statusBanner.Padding = new Forms.Padding(12, 8, 12, 8);
        _statusBanner.Size = new Drawing.Size(ClientSize.Width - 24, 48);
        _statusBanner.Visible = false;

        _statusText.Dock = Forms.DockStyle.Fill;
        _statusText.AutoEllipsis = true;
        _statusText.ForeColor = Drawing.Color.FromArgb(244, 247, 251);
        _statusText.Font = new Drawing.Font("Segoe UI", 9F);
        _statusText.TextAlign = Drawing.ContentAlignment.MiddleLeft;
        _statusBanner.Controls.Add(_statusText);

        _protectionBanner.Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Right;
        _protectionBanner.BackColor = Drawing.Color.FromArgb(38, 51, 37);
        _protectionBanner.Location = new Drawing.Point(ClientSize.Width - 184, 12);
        _protectionBanner.Padding = new Forms.Padding(10, 6, 10, 6);
        _protectionBanner.Size = new Drawing.Size(172, 32);
        _protectionBanner.Visible = false;

        _protectionStatusText.Dock = Forms.DockStyle.Fill;
        _protectionStatusText.AutoEllipsis = true;
        _protectionStatusText.ForeColor = Drawing.Color.FromArgb(217, 247, 215);
        _protectionStatusText.Font = new Drawing.Font("Segoe UI", 8F, Drawing.FontStyle.Bold);
        _protectionStatusText.TextAlign = Drawing.ContentAlignment.MiddleCenter;
        _protectionBanner.Controls.Add(_protectionStatusText);

        _footerPanel.Controls.Add(_mainOpacityLabel);
        _footerPanel.Controls.Add(_mainOpacityTrackBar);
        _footerPanel.Controls.Add(_mainOpacityValueLabel);
        _footerPanel.Controls.Add(_mainClickThroughCheckBox);
        _footerWindow.Controls.Add(_footerPanel);

        Controls.Add(_browser);
        Controls.Add(_statusBanner);
        Controls.Add(_protectionBanner);

        SyncFooterControls();
        LayoutMainControls();
        ResumeLayout();
    }

    private void LayoutMainControls()
    {
        _browser.SetBounds(0, 0, ClientSize.Width, ClientSize.Height);
        LayoutFooterWindow();
        LayoutFooterControls();
        LayoutFloatingBanners();
    }

    private void LayoutFooterControls()
    {
        var padding = _footerPanel.Padding;
        var contentLeft = padding.Left;
        var contentTop = padding.Top;
        var contentHeight = Math.Max(20, _footerPanel.Height - padding.Vertical);
        var checkWidth = Math.Min(126, Math.Max(104, _footerPanel.Width / 4));
        var valueWidth = 44;
        var labelWidth = 58;
        var gap = 8;
        var trackWidth = Math.Max(80, _footerPanel.Width - padding.Horizontal - labelWidth - valueWidth - checkWidth - (gap * 3));

        _mainOpacityLabel.SetBounds(contentLeft, contentTop, labelWidth, contentHeight);
        _mainOpacityTrackBar.SetBounds(_mainOpacityLabel.Right + gap, contentTop + 2, trackWidth, contentHeight - 2);
        _mainOpacityValueLabel.SetBounds(_mainOpacityTrackBar.Right + gap, contentTop, valueWidth, contentHeight);
        _mainClickThroughCheckBox.SetBounds(_mainOpacityValueLabel.Right + gap, contentTop, checkWidth, contentHeight);
    }

    private void ShowFooterWindow()
    {
        if (_isExiting || IsDisposed || !Visible || WindowState == Forms.FormWindowState.Minimized)
        {
            return;
        }

        LayoutFooterWindow();
        if (!_footerWindow.Visible)
        {
            _footerWindow.Show(this);
        }

        _footerWindow.TopMost = true;
        _footerWindow.BringToFront();
        _sensitiveWindowProtectionService.ReapplyAll();
    }

    private void LayoutFooterWindow()
    {
        if (_footerWindow.IsDisposed)
        {
            return;
        }

        var footerHeight = _footerWindow.Height > 0 ? _footerWindow.Height : 38;
        _footerWindow.SetBounds(Left, Math.Max(Top, Bottom - footerHeight), Math.Max(260, Width), footerHeight);
        LayoutFooterControls();
        SyncFooterWindowVisibility();
    }

    private void SyncFooterWindowVisibility()
    {
        if (_footerWindow.IsDisposed)
        {
            return;
        }

        var shouldShow = !_isExiting && Visible && WindowState != Forms.FormWindowState.Minimized;
        if (shouldShow)
        {
            if (!_footerWindow.Visible)
            {
                _footerWindow.Show(this);
            }
        }
        else
        {
            _footerWindow.Hide();
        }
    }

    private IntPtr GetFooterWindowHandleForProtection()
    {
        if (_footerWindow.IsDisposed || !_footerWindow.IsHandleCreated)
        {
            return IntPtr.Zero;
        }

        return _footerWindow.Handle;
    }

    private void LayoutFloatingBanners()
    {
        _statusBanner.Width = Math.Max(120, ClientSize.Width - 24);
        _protectionBanner.Left = Math.Max(12, ClientSize.Width - _protectionBanner.Width - 12);
        _statusBanner.BringToFront();
        _protectionBanner.BringToFront();
    }

    private void NormalizeSettings()
    {
        _settings.Overlay ??= new OverlaySettings();
        _settings.HotKeys ??= new HotKeySettings();
        _settings.Privacy ??= new PrivacySettings();
        _settings.License ??= new LicenseSettings();
        _settings.MainWindowOpacity = WindowOpacityController.Normalize(_settings.MainWindowOpacity);
        _settings.Overlay.WindowOpacity = WindowOpacityController.Normalize(_settings.Overlay.WindowOpacity);

        if (!_settings.Privacy.SensitiveWindowProtectionUserConfigured)
        {
            if (!_settings.Privacy.SensitiveWindowProtectionEnabled)
            {
                _settings.Privacy.SensitiveWindowProtectionEnabled = true;
                _settingsStore.Save(_settings);
            }
        }

        if (_settings.Privacy.RedactWhenInactive)
        {
            _settings.Privacy.RedactWhenInactive = false;
            _settingsStore.Save(_settings);
        }

        if (_settings.Privacy.ManualRedactionEnabled)
        {
            _settings.Privacy.ManualRedactionEnabled = false;
            _settingsStore.Save(_settings);
        }

    }

    private void SyncFooterControls()
    {
        if (_mainOpacityTrackBar is null || _mainClickThroughCheckBox is null)
        {
            return;
        }

        _syncingFooterControls = true;
        var opacityPercent = ToPercent(_settings.MainWindowOpacity);
        _mainOpacityTrackBar.Value = Math.Clamp(opacityPercent, _mainOpacityTrackBar.Minimum, _mainOpacityTrackBar.Maximum);
        _mainOpacityValueLabel.Text = opacityPercent.ToString("0") + "%";
        _mainClickThroughCheckBox.Checked = _settings.MainWindowClickThrough;
        _syncingFooterControls = false;
    }

    private void MainOpacityTrackBar_Scroll(object? sender, EventArgs e)
    {
        _mainOpacityValueLabel.Text = _mainOpacityTrackBar.Value.ToString("0") + "%";

        if (_syncingFooterControls)
        {
            return;
        }

        SetMainWindowOpacity(_mainOpacityTrackBar.Value / 100.0);
    }

    private void MainClickThroughCheckBox_Click(object? sender, EventArgs e)
    {
        if (_syncingFooterControls)
        {
            return;
        }

        SetMainWindowClickThrough(_mainClickThroughCheckBox.Checked);
    }

    private async Task InitializeBrowserAsync()
    {
        var currentDeviceId = DeviceIdentity.GetStableDeviceHash();
        if (!string.Equals(_settings.DeviceId, currentDeviceId, StringComparison.Ordinal))
        {
            _settings.DeviceId = currentDeviceId;
            _settingsStore.Save(_settings);
            ShowStatus("First use on this Windows device. Sign in to ChatGPT once; this app will reuse the saved WebView2 session after that.");
        }

        try
        {
            Directory.CreateDirectory(AppPaths.WebViewProfileDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: AppPaths.WebViewProfileDirectory);

            await _browser.EnsureCoreWebView2Async(environment);
            _browser.CoreWebView2.Settings.AreDevToolsEnabled = _settings.EnableDevTools;
            _browser.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
            _browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = !_settings.Privacy.SensitiveWindowProtectionEnabled;
            _browser.CoreWebView2.NewWindowRequested += Browser_NewWindowRequested;
            _browser.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _chatReadyProbeTimer.Start();
                QueueMainWindowOpacityReapplyBurst();
            };
            _browser.Source = new Uri("https://chatgpt.com/");
            QueueMainWindowOpacityReapplyBurst();
            _sensitiveWindowProtectionService.ReapplyAll();
        }
        catch (Exception ex)
        {
            ShowStatus("WebView2 failed to initialize. Install the Microsoft Edge WebView2 Runtime, then restart the app. " + ex.Message);
        }
    }

    private void Browser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && _browser.CoreWebView2 is not null)
        {
            _browser.CoreWebView2.Navigate(uri.ToString());
            ShowStatus("Opened the requested content in the protected ChatGPT window instead of creating an unprotected popup.");
        }
        else
        {
            ShowStatus("Blocked an unprotected popup window request.");
        }

        _sensitiveWindowProtectionService.ReapplyAll();
    }

    private async Task ProbeChatGptReadyAsync()
    {
        if (_browser.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var result = await _browser.CoreWebView2.ExecuteScriptAsync(ChatGptDomBridge.ProbeReadyScript);
            if (string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
            {
                _chatReadyProbeTimer.Stop();
                HideStatus();
                StartOverlayAndCaptionWatcher();
            }
        }
        catch
        {
            // ChatGPT may be navigating or replacing its app shell; the next probe will retry.
        }
    }

    private void StartOverlayAndCaptionWatcher()
    {
        if (_overlayStarted)
        {
            return;
        }

        _overlayStarted = true;
        _overlayWindow = new NativeOverlayForm(_sensitiveWindowProtectionService);
        _overlayWindow.LoadFrom(_settings.Overlay);
        _overlayWindow.TextSubmitted += async (_, text) => await SubmitPromptAsync(text);
        _overlayWindow.SettingsChanged += (_, overlaySettings) =>
        {
            _settings.Overlay = overlaySettings;
            _settingsStore.Save(_settings);
            _trayController.SetClickThrough(overlaySettings.ClickThrough);
        };
        _overlayWindow.Show();
        EnsureOverlayAboveMainWindow();
        _sensitiveWindowProtectionService.ReapplyAll();
        _trayController.SetOverlayAvailable(true);
        _trayController.SetClickThrough(_settings.Overlay.ClickThrough);

        _captionWatcher = new LiveCaptionWatcher(TimeSpan.FromMilliseconds(_settings.CaptionPollMs));
        _captionWatcher.CaptionChanged += (_, snapshot) =>
        {
            BeginOnUi(() =>
            {
                if (!_isExiting)
                {
                    _overlayWindow?.UpdateCaption(snapshot);
                }
            });
        };
        _captionWatcher.StatusChanged += (_, status) =>
        {
            BeginOnUi(() =>
            {
                if (!_isExiting)
                {
                    ShowStatus(status);
                }
            });
        };
        _captionWatcher.Start();
    }

    private async Task SubmitPromptAsync(string text)
    {
        if (_browser.CoreWebView2 is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            var result = await _browser.CoreWebView2.ExecuteScriptAsync(ChatGptDomBridge.BuildSubmitScript(text));
            var submission = JsonSerializer.Deserialize<SubmitResult>(result, SubmitJsonOptions);
            if (submission?.Ok == true)
            {
                ShowStatus("Sent to ChatGPT");
            }
            else
            {
                ShowStatus(submission?.Reason ?? "Could not find the ChatGPT prompt");
            }
        }
        catch (Exception ex)
        {
            ShowStatus("Submit failed: " + ex.Message);
        }
    }

    private void ShowStatus(string message)
    {
        _statusText.Text = message;
        _statusBanner.Visible = true;
        LayoutFloatingBanners();
    }

    private void HideStatus()
    {
        _statusBanner.Visible = false;
    }

    private void SetSensitiveWindowProtection(bool enabled)
    {
        var changed = _settings.Privacy.SensitiveWindowProtectionEnabled != enabled;
        var configuredChanged = !_settings.Privacy.SensitiveWindowProtectionUserConfigured;
        _settings.Privacy.SensitiveWindowProtectionEnabled = enabled;
        _settings.Privacy.SensitiveWindowProtectionUserConfigured = true;

        if (changed || configuredChanged)
        {
            _settingsStore.Save(_settings);
        }

        _sensitiveWindowProtectionService.SetEnabled(enabled);
        ApplyWebViewSensitiveContentSettings();
        _settingsWindow?.SetSensitiveWindowProtectionChecked(enabled);
        _settingsWindow?.SetSensitiveWindowProtectionStatus(_sensitiveWindowProtectionService.CurrentSummary);
    }

    private void SetOverlayClickThrough(bool enabled)
    {
        _settings.Overlay.ClickThrough = enabled;
        _settingsStore.Save(_settings);
        _trayController.SetClickThrough(enabled);
        _overlayWindow?.SetClickThrough(enabled);
    }

    private void SetHotKeys(HotKeySettings hotKeys)
    {
        _settings.HotKeys = hotKeys;
        _settingsStore.Save(_settings);
        RegisterOverlayHotKeys();
    }

    private void SetMainWindowOpacity(double opacity)
    {
        _settings.MainWindowOpacity = WindowOpacityController.Normalize(opacity);
        _settingsStore.Save(_settings);
        SyncFooterControls();
        _settingsWindow?.SetMainWindowOpacity(_settings.MainWindowOpacity);
        ApplyMainWindowOpacity();
        QueueMainWindowOpacityReapplyBurst();
    }

    private void SetMainWindowClickThrough(bool enabled)
    {
        _mainWindowClickThrough = enabled;
        _settings.MainWindowClickThrough = enabled;
        _settingsStore.Save(_settings);
        SyncFooterControls();
        _settingsWindow?.SetMainWindowClickThrough(enabled);
        ApplyMainWindowClickThrough();
    }

    private void SetOverlayWindowOpacity(double opacity)
    {
        _settings.Overlay.WindowOpacity = WindowOpacityController.Normalize(opacity);
        _settingsStore.Save(_settings);
        _overlayWindow?.SetWindowOpacity(_settings.Overlay.WindowOpacity);
    }

    private void SetCaptionAlwaysAboveMainWindow(bool enabled)
    {
        _settings.Overlay.KeepAboveMainWindow = enabled;
        _settingsStore.Save(_settings);
        _overlayWindow?.SetKeepAboveMainWindow(enabled);
        _settingsWindow?.SetCaptionAlwaysAboveMainWindow(enabled);

        if (enabled)
        {
            EnsureOverlayAboveMainWindow();
        }
    }

    private void UpdateSensitiveWindowProtectionIndicator(SensitiveWindowProtectionSummary summary)
    {
        if (!summary.Enabled)
        {
            _protectionBanner.Visible = false;
            _toolTip.SetToolTip(_protectionBanner, null);
            _toolTip.SetToolTip(_protectionStatusText, null);
            return;
        }

        _protectionBanner.Visible = true;
        _toolTip.SetToolTip(_protectionBanner, summary.Message);
        _toolTip.SetToolTip(_protectionStatusText, summary.Message);

        if (summary.IsProtected)
        {
            _protectionBanner.BackColor = Drawing.Color.FromArgb(38, 51, 37);
            _protectionStatusText.ForeColor = Drawing.Color.FromArgb(217, 247, 215);
            _protectionStatusText.Text = "Capture protection on";
            LayoutFloatingBanners();
            return;
        }

        _protectionBanner.BackColor = Drawing.Color.FromArgb(59, 43, 25);
        _protectionStatusText.ForeColor = Drawing.Color.FromArgb(255, 222, 179);
        _protectionStatusText.Text = "Protection warning";
        LayoutFloatingBanners();
    }

    private void ApplyWebViewSensitiveContentSettings()
    {
        if (_browser.CoreWebView2 is not null)
        {
            _browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = !_settings.Privacy.SensitiveWindowProtectionEnabled;
        }
    }

    private void ShowBrowserContent()
    {
        _browser.Visible = true;
    }

    private void ApplyMainWindowOpacity()
    {
        WindowOpacityController.ApplyWindowTree(Handle, _settings.MainWindowOpacity);
        ApplyMainWindowClickThrough();

        if (_settings.MainWindowOpacity < WindowOpacityController.MaximumOpacity || _opacityReapplyTicksRemaining > 0)
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

    private void ReapplyMainWindowOpacityFromTimer()
    {
        WindowOpacityController.ApplyWindowTree(Handle, _settings.MainWindowOpacity);
        ApplyMainWindowClickThrough();

        if (_settings.MainWindowOpacity < WindowOpacityController.MaximumOpacity)
        {
            return;
        }

        if (_opacityReapplyTicksRemaining > 0)
        {
            _opacityReapplyTicksRemaining--;
            return;
        }

        _opacityReapplyTimer.Stop();
    }

    private void QueueMainWindowOpacityReapplyBurst()
    {
        _opacityReapplyTicksRemaining = Math.Max(_opacityReapplyTicksRemaining, 40);
        ApplyMainWindowOpacity();
    }

    private void ApplyMainWindowClickThrough()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        SetTransparentStyle(Handle, _mainWindowClickThrough);
        SetTransparentStyle(_browser.Handle, _mainWindowClickThrough);
        ApplyTransparentStyleToWindowTree(_browser.Handle, _mainWindowClickThrough);
        SetTransparentStyle(_statusBanner.Handle, _mainWindowClickThrough);
        SetTransparentStyle(_statusText.Handle, _mainWindowClickThrough);
        SetTransparentStyle(_protectionBanner.Handle, _mainWindowClickThrough);
        SetTransparentStyle(_protectionStatusText.Handle, _mainWindowClickThrough);
    }

    private void EnsureOverlayAboveMainWindow()
    {
        TopMost = true;
        if (_settings.Overlay.KeepAboveMainWindow)
        {
            _overlayWindow?.EnsureAboveMainWindow();
        }
    }

    private void RegisterOverlayHotKeys()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        UnregisterOverlayHotKeys();
        TryRegisterHotKey(ToggleMainWindowHotKeyId, _settings.HotKeys.ToggleMainWindow, "toggle main window");
        TryRegisterHotKey(ToggleAllWindowsHotKeyId, _settings.HotKeys.ToggleAllWindows, "toggle all windows");
        TryRegisterHotKey(ToggleOverlayHotKeyId, _settings.HotKeys.ToggleOverlay, "toggle caption dialog");
        TryRegisterHotKey(ToggleMainClickThroughHotKeyId, _settings.HotKeys.ToggleMainClickThrough, "toggle main click-through");
        TryRegisterHotKey(ToggleCaptionClickThroughHotKeyId, _settings.HotKeys.ToggleCaptionClickThrough, "toggle caption click-through");
        TryRegisterHotKey(IncreaseMainOpacityHotKeyId, _settings.HotKeys.IncreaseMainOpacity, "increase main opacity");
        TryRegisterHotKey(DecreaseMainOpacityHotKeyId, _settings.HotKeys.DecreaseMainOpacity, "decrease main opacity");
        TryRegisterHotKey(IncreaseCaptionOpacityHotKeyId, _settings.HotKeys.IncreaseCaptionOpacity, "increase caption opacity");
        TryRegisterHotKey(DecreaseCaptionOpacityHotKeyId, _settings.HotKeys.DecreaseCaptionOpacity, "decrease caption opacity");
        TryRegisterHotKey(IncreaseCaptionFontSizeHotKeyId, _settings.HotKeys.IncreaseCaptionFontSize, "increase caption font size");
        TryRegisterHotKey(DecreaseCaptionFontSizeHotKeyId, _settings.HotKeys.DecreaseCaptionFontSize, "decrease caption font size");
        TryRegisterHotKey(ToggleCaptionAboveMainHotKeyId, _settings.HotKeys.ToggleCaptionAboveMain, "toggle caption over main");
        TryRegisterHotKey(ToggleCaptureProtectionHotKeyId, _settings.HotKeys.ToggleCaptureProtection, "toggle capture protection");
    }

    private void UnregisterOverlayHotKeys()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        foreach (var id in _registeredHotKeyIds.ToArray())
        {
            UnregisterHotKey(Handle, id);
        }

        _registeredHotKeyIds.Clear();
    }

    private void TryRegisterHotKey(int id, string gesture, string description)
    {
        if (!IsHandleCreated || string.IsNullOrWhiteSpace(gesture))
        {
            return;
        }

        if (!TryParseHotKey(gesture, out var modifiers, out var virtualKey))
        {
            ShowStatus("Could not parse hotkey for " + description + ".");
            return;
        }

        if (RegisterHotKey(Handle, id, modifiers | ModNoRepeat, virtualKey))
        {
            _registeredHotKeyIds.Add(id);
            return;
        }

        ShowStatus("Could not register hotkey for " + description + ".");
    }

    protected override void WndProc(ref Forms.Message m)
    {
        if (_mainWindowClickThrough && m.Msg == WmNcHitTest)
        {
            m.Result = new IntPtr(HtTransparent);
            return;
        }

        if (m.Msg == WmHotKey)
        {
            switch (m.WParam.ToInt32())
            {
                case ToggleMainWindowHotKeyId:
                    ToggleMainWindow();
                    break;
                case ToggleAllWindowsHotKeyId:
                    ToggleAllWindows();
                    break;
                case ToggleOverlayHotKeyId:
                    ToggleOverlayWindow();
                    break;
                case ToggleMainClickThroughHotKeyId:
                    SetMainWindowClickThrough(!_settings.MainWindowClickThrough);
                    break;
                case ToggleCaptionClickThroughHotKeyId:
                    SetOverlayClickThrough(!_settings.Overlay.ClickThrough);
                    break;
                case IncreaseMainOpacityHotKeyId:
                    AdjustMainWindowOpacity(0.05);
                    break;
                case DecreaseMainOpacityHotKeyId:
                    AdjustMainWindowOpacity(-0.05);
                    break;
                case IncreaseCaptionOpacityHotKeyId:
                    AdjustCaptionWindowOpacity(0.05);
                    break;
                case DecreaseCaptionOpacityHotKeyId:
                    AdjustCaptionWindowOpacity(-0.05);
                    break;
                case IncreaseCaptionFontSizeHotKeyId:
                    AdjustCaptionFontSize(1);
                    break;
                case DecreaseCaptionFontSizeHotKeyId:
                    AdjustCaptionFontSize(-1);
                    break;
                case ToggleCaptionAboveMainHotKeyId:
                    SetCaptionAlwaysAboveMainWindow(!_settings.Overlay.KeepAboveMainWindow);
                    break;
                case ToggleCaptureProtectionHotKeyId:
                    SetSensitiveWindowProtection(!_settings.Privacy.SensitiveWindowProtectionEnabled);
                    break;
            }

            return;
        }

        base.WndProc(ref m);
    }

    private static int ToPercent(double opacity)
    {
        return (int)Math.Round(WindowOpacityController.Normalize(opacity) * 100, MidpointRounding.AwayFromZero);
    }

    private static void ApplyTransparentStyleToWindowTree(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        EnumChildWindows(hwnd, (childHwnd, _) =>
        {
            SetTransparentStyle(childHwnd, enabled);
            return true;
        }, IntPtr.Zero);
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

    private void ToggleMainWindow()
    {
        if (Visible && WindowState != Forms.FormWindowState.Minimized)
        {
            _footerWindow.Hide();
            Hide();
            return;
        }

        ShowMainWindow();
    }

    private void ToggleAllWindows()
    {
        if (Visible || _footerWindow.Visible || (_overlayWindow?.Visible == true) || (_settingsWindow?.IsVisible == true))
        {
            HideAllWindows();
            return;
        }

        ShowMainWindow();
        ShowOverlayWindow();
    }

    private void AdjustMainWindowOpacity(double delta)
    {
        SetMainWindowOpacity(Math.Clamp(_settings.MainWindowOpacity + delta, WindowOpacityController.MinimumOpacity, WindowOpacityController.MaximumOpacity));
    }

    private void AdjustCaptionWindowOpacity(double delta)
    {
        SetOverlayWindowOpacity(Math.Clamp(_settings.Overlay.WindowOpacity + delta, WindowOpacityController.MinimumOpacity, WindowOpacityController.MaximumOpacity));
    }

    private void AdjustCaptionFontSize(double delta)
    {
        _settings.Overlay.FontSize = Math.Clamp(_settings.Overlay.FontSize + delta, 12, 36);
        _settingsStore.Save(_settings);
        _overlayWindow?.SetCaptionFontSize(_settings.Overlay.FontSize);
    }

    private void ToggleOverlayWindow()
    {
        if (_overlayWindow is null || !_overlayWindow.Visible || _overlayWindow.WindowState == Forms.FormWindowState.Minimized)
        {
            RestoreOverlayWindow();
            return;
        }

        MinimizeOverlayWindow();
    }

    private void MinimizeOverlayWindow()
    {
        if (_overlayWindow is null)
        {
            return;
        }

        _settings.Overlay = _overlayWindow.CaptureSettings();
        _settingsStore.Save(_settings);
        _overlayWindow.WindowState = Forms.FormWindowState.Minimized;
    }

    private void RestoreOverlayWindow()
    {
        if (_overlayWindow is null)
        {
            StartOverlayAndCaptionWatcher();
        }

        if (_overlayWindow is null)
        {
            return;
        }

        _overlayWindow.Show();
        _overlayWindow.WindowState = Forms.FormWindowState.Normal;
        EnsureOverlayAboveMainWindow();
        _overlayWindow.Activate();
    }

    private static bool TryParseHotKey(string gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        var key = Key.None;
        foreach (var rawPart in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawPart.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || rawPart.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModControl;
                continue;
            }

            if (rawPart.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModAlt;
                continue;
            }

            if (rawPart.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModShift;
                continue;
            }

            if (rawPart.Equals("Win", StringComparison.OrdinalIgnoreCase)
                || rawPart.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModWin;
                continue;
            }

            if (!Enum.TryParse(rawPart, ignoreCase: true, out key))
            {
                return false;
            }
        }

        if (key == Key.None)
        {
            return false;
        }

        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    private void ShutdownServices()
    {
        if (_servicesShutdown)
        {
            return;
        }

        _servicesShutdown = true;
        _chatReadyProbeTimer.Stop();
        _opacityReapplyTimer.Stop();
        _captionWatcher?.Dispose();

        if (_overlayWindow is not null)
        {
            _settings.Overlay = _overlayWindow.CaptureSettings();
            _settingsStore.Save(_settings);
            _overlayWindow.CloseForExit();
        }

        _settingsWindow?.Close();
        _footerWindow.Hide();
        _footerWindow.Close();
        _footerWindow.Dispose();
        _sensitiveWindowProtectionService.UnregisterWindowHandle(FooterWindowRegistrationId);
        _sensitiveWindowProtectionService.UnregisterWindowHandle(MainWindowRegistrationId);
        (_sensitiveWindowProtectionService as IDisposable)?.Dispose();
        _trayController.Dispose();
        LiveCaptionsLauncher.CloseIfOpen();
        UnregisterOverlayHotKeys();
        _browser.Dispose();
        _toolTip.Dispose();
        Icon?.Dispose();
    }

    private void HandleClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
        {
            ShutdownServices();
            return;
        }

        e.Cancel = true;
        _footerWindow.Hide();
        Hide();
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = Forms.FormWindowState.Normal;
        TopMost = true;
        ShowFooterWindow();
        ApplyMainWindowOpacity();
        Activate();
        ShowBrowserContent();
        EnsureOverlayAboveMainWindow();
    }

    private void ShowOverlayWindow()
    {
        if (_overlayWindow is null)
        {
            StartOverlayAndCaptionWatcher();
        }

        if (_overlayWindow is null)
        {
            return;
        }

        _overlayWindow.SetClickThrough(false);
        _overlayWindow.Show();
        _overlayWindow.WindowState = Forms.FormWindowState.Normal;
        EnsureOverlayAboveMainWindow();
        _overlayWindow.Activate();
        _trayController.SetClickThrough(false);
        _sensitiveWindowProtectionService.ReapplyAll();
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            new WindowInteropHelper(_settingsWindow)
            {
                Owner = Handle
            };

            _settingsWindow.SensitiveWindowProtectionChanged += enabled =>
                BeginOnUi(() => SetSensitiveWindowProtection(enabled));
            _settingsWindow.HotKeysChanged += hotKeys =>
                BeginOnUi(() => SetHotKeys(hotKeys));
            _settingsWindow.CaptionAlwaysAboveMainWindowChanged += enabled =>
                BeginOnUi(() => SetCaptionAlwaysAboveMainWindow(enabled));
            _settingsWindow.MainWindowOpacityChanged += opacity =>
                BeginOnUi(() => SetMainWindowOpacity(opacity));
            _settingsWindow.MainWindowClickThroughChanged += enabled =>
                BeginOnUi(() => SetMainWindowClickThrough(enabled));
            _settingsWindow.CaptionWindowOpacityChanged += opacity =>
                BeginOnUi(() => SetOverlayWindowOpacity(opacity));
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.LoadFrom(
            _settings.Privacy,
            _sensitiveWindowProtectionService.CurrentSummary,
            _settings.Overlay,
            _settings.HotKeys,
            _settings.MainWindowOpacity,
            _settings.MainWindowClickThrough);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void HideAllWindows()
    {
        _footerWindow.Hide();
        Hide();
        _overlayWindow?.Hide();
        _settingsWindow?.Hide();
    }

    private void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        ShutdownServices();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void BeginOnUi(Action action)
    {
        if (_isExiting || IsDisposed)
        {
            return;
        }

        try
        {
            if (IsHandleCreated && InvokeRequired)
            {
                BeginInvoke(action);
                return;
            }

            action();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class SubmitResult
    {
        public bool Ok { get; set; }
        public string? Reason { get; set; }
    }

    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowProc enumProc, IntPtr lParam);
}
