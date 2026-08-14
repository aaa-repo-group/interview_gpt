using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InterviewGptBridge.Services;
using Microsoft.Web.WebView2.Core;
using MediaColor = System.Windows.Media.Color;

namespace InterviewGptBridge;

public partial class MainWindow : Window
{
    private const int WmHotKey = 0x0312;
    private const int ToggleOverlayHotKeyId = 101;
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
    private readonly DispatcherTimer _chatReadyProbeTimer;
    private readonly DispatcherTimer _opacityReapplyTimer;
    private readonly TrayController _trayController;
    private readonly object _captionUiSync = new();
    private AppSettings _settings;
    private LiveCaptionWatcher? _captionWatcher;
    private OverlayWindow? _overlayWindow;
    private SettingsWindow? _settingsWindow;
    private HwndSource? _hotKeySource;
    private int _opacityReapplyTicksRemaining;
    private bool _overlayStarted;
    private string? _pendingCaptionSnapshot;
    private bool _captionUiUpdateScheduled;
    private bool _isExiting;
    private bool _servicesShutdown;

    public MainWindow()
    {
        InitializeComponent();
        AltTabWindowHider.HideFromAltTab(this);

        _settings = _settingsStore.Load();
        NormalizeSettings();
        Topmost = true;

        _trayController = new TrayController();
        _trayController.ShowMainRequested += (_, _) => Dispatcher.BeginInvoke(ShowMainWindow);
        _trayController.ShowOverlayRequested += (_, _) => Dispatcher.BeginInvoke(ShowOverlayWindow);
        _trayController.SettingsRequested += (_, _) => Dispatcher.BeginInvoke(ShowSettingsWindow);
        _trayController.HideAllRequested += (_, _) => Dispatcher.BeginInvoke(HideAllWindows);
        _trayController.ClickThroughChanged += enabled => Dispatcher.BeginInvoke(() => SetOverlayClickThrough(enabled));
        _trayController.ExitRequested += (_, _) => Dispatcher.BeginInvoke(ExitApplication);
        _trayController.SetClickThrough(_settings.Overlay.ClickThrough);

        _sensitiveWindowProtectionService.StatusChanged += (_, summary) =>
            Dispatcher.BeginInvoke(() => UpdateSensitiveWindowProtectionIndicator(summary));
        _sensitiveWindowProtectionService.Register(this, "Embedded ChatGPT WebView host window for confidential AI responses, prompts, secrets, and authentication content.");
        _sensitiveWindowProtectionService.SetEnabled(_settings.Privacy.SensitiveWindowProtectionEnabled);

        _chatReadyProbeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        _chatReadyProbeTimer.Tick += async (_, _) => await ProbeChatGptReadyAsync();

        _opacityReapplyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _opacityReapplyTimer.Tick += (_, _) => ReapplyMainWindowOpacityFromTimer();

        Loaded += async (_, _) =>
        {
            LiveCaptionsLauncher.LaunchAfterDelay(Dispatcher, TimeSpan.FromMilliseconds(700));
            StartOverlayAndCaptionWatcher();
            await InitializeBrowserAsync();
        };
        SourceInitialized += MainWindow_SourceInitialized;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Activated += (_, _) => EnsureOverlayAboveMainWindow();
        Closing += (_, e) => HandleClosing(e);

        ShowBrowserContent();
        UpdateSensitiveWindowProtectionIndicator(_sensitiveWindowProtectionService.CurrentSummary);
    }

    private void NormalizeSettings()
    {
        _settings.Overlay ??= new OverlaySettings();
        _settings.HotKeys ??= new HotKeySettings();
        _settings.Privacy ??= new PrivacySettings();
        _settings.License ??= new LicenseSettings();
        var settingsChanged = false;
        _settings.Overlay.WindowOpacity = WindowOpacityController.Normalize(_settings.Overlay.WindowOpacity);
        var mainWindowOpacity = WindowOpacityController.Normalize(_settings.MainWindowOpacity);
        if (Math.Abs(mainWindowOpacity - _settings.Overlay.WindowOpacity) > 0.001)
        {
            settingsChanged = true;
        }

        _settings.MainWindowOpacity = _settings.Overlay.WindowOpacity;
        if (_settings.MainWindowClickThrough)
        {
            _settings.MainWindowClickThrough = false;
            settingsChanged = true;
        }

        if (!_settings.Privacy.SensitiveWindowProtectionUserConfigured)
        {
            if (!_settings.Privacy.SensitiveWindowProtectionEnabled)
            {
                _settings.Privacy.SensitiveWindowProtectionEnabled = true;
                settingsChanged = true;
            }
        }

        if (_settings.Privacy.RedactWhenInactive)
        {
            _settings.Privacy.RedactWhenInactive = false;
            settingsChanged = true;
        }

        if (_settings.Privacy.ManualRedactionEnabled)
        {
            _settings.Privacy.ManualRedactionEnabled = false;
            settingsChanged = true;
        }

        if (settingsChanged)
        {
            _settingsStore.Save(_settings);
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hotKeySource = HwndSource.FromVisual(this) as HwndSource;
        _hotKeySource?.AddHook(HotKeyWindowProc);
        RegisterOverlayHotKeys();
        ApplyMainWindowOpacity();
        QueueMainWindowOpacityReapplyBurst();
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

            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = _settings.EnableDevTools;
            Browser.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = !_settings.Privacy.SensitiveWindowProtectionEnabled;
            Browser.CoreWebView2.NewWindowRequested += Browser_NewWindowRequested;
            Browser.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _chatReadyProbeTimer.Start();
                QueueMainWindowOpacityReapplyBurst();
            };
            Browser.Source = new Uri("https://chatgpt.com/");
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

        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && Browser.CoreWebView2 is not null)
        {
            Browser.CoreWebView2.Navigate(uri.ToString());
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
        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var result = await Browser.CoreWebView2.ExecuteScriptAsync(ChatGptDomBridge.ProbeReadyScript);
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
        _overlayWindow = new OverlayWindow(_sensitiveWindowProtectionService);
        _overlayWindow.LoadFrom(_settings.Overlay);
        _overlayWindow.TextSubmitted += async (_, text) => await SubmitPromptAsync(text);
        _overlayWindow.SettingsChanged += (_, overlaySettings) =>
        {
            var normalizedOpacity = WindowOpacityController.Normalize(overlaySettings.WindowOpacity);
            var opacityChanged = Math.Abs(_settings.MainWindowOpacity - normalizedOpacity) > 0.001;
            _settings.Overlay = overlaySettings;
            _settings.MainWindowOpacity = normalizedOpacity;
            _settings.MainWindowClickThrough = false;
            _settingsStore.Save(_settings);
            if (opacityChanged)
            {
                _settingsWindow?.SetMainWindowOpacity(normalizedOpacity);
                _settingsWindow?.SetCaptionWindowOpacity(normalizedOpacity);
                ApplyMainWindowOpacity();
                QueueMainWindowOpacityReapplyBurst();
            }
            _trayController.SetClickThrough(overlaySettings.ClickThrough);
        };
        _overlayWindow.Show();
        EnsureOverlayAboveMainWindow();
        _sensitiveWindowProtectionService.ReapplyAll();
        _trayController.SetOverlayAvailable(true);
        _trayController.SetClickThrough(_settings.Overlay.ClickThrough);

        _captionWatcher = new LiveCaptionWatcher(GetCaptionPollInterval());
        _captionWatcher.CaptionChanged += (_, snapshot) =>
        {
            QueueCaptionSnapshot(snapshot);
        };
        _captionWatcher.StatusChanged += (_, status) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_isExiting)
                {
                    ShowStatus(status);
                }
            });
        };
        _captionWatcher.Start();

    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
        }
    }

    private TimeSpan GetCaptionPollInterval()
    {
        return TimeSpan.FromMilliseconds(Math.Clamp(_settings.CaptionPollMs, 45, 70));
    }

    private void QueueCaptionSnapshot(string snapshot)
    {
        var shouldSchedule = false;
        lock (_captionUiSync)
        {
            _pendingCaptionSnapshot = snapshot;
            if (!_captionUiUpdateScheduled)
            {
                _captionUiUpdateScheduled = true;
                shouldSchedule = true;
            }
        }

        if (shouldSchedule)
        {
            Dispatcher.BeginInvoke(FlushPendingCaptionSnapshot);
        }
    }

    private void FlushPendingCaptionSnapshot()
    {
        string? snapshot;
        lock (_captionUiSync)
        {
            snapshot = _pendingCaptionSnapshot;
            _pendingCaptionSnapshot = null;
            _captionUiUpdateScheduled = false;
        }

        if (!_isExiting && !string.IsNullOrWhiteSpace(snapshot))
        {
            _overlayWindow?.UpdateCaption(snapshot);
        }

        var shouldScheduleAgain = false;
        lock (_captionUiSync)
        {
            if (_pendingCaptionSnapshot is not null && !_captionUiUpdateScheduled)
            {
                _captionUiUpdateScheduled = true;
                shouldScheduleAgain = true;
            }
        }

        if (shouldScheduleAgain)
        {
            Dispatcher.BeginInvoke(FlushPendingCaptionSnapshot);
        }
    }
 
    private async Task SubmitPromptAsync(string text)
    {
        if (Browser.CoreWebView2 is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            var result = await Browser.CoreWebView2.ExecuteScriptAsync(ChatGptDomBridge.BuildSubmitScript(text));
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
        StatusText.Text = message;
        StatusBanner.Visibility = Visibility.Collapsed;
    }

    private void HideStatus()
    {
        StatusBanner.Visibility = Visibility.Collapsed;
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
        SetOverlayWindowOpacity(opacity);
    }

    private void SetOverlayWindowOpacity(double opacity)
    {
        var normalizedOpacity = WindowOpacityController.Normalize(opacity);
        _settings.Overlay.WindowOpacity = normalizedOpacity;
        _settings.MainWindowOpacity = normalizedOpacity;
        _settings.MainWindowClickThrough = false;
        _settingsStore.Save(_settings);
        _settingsWindow?.SetMainWindowOpacity(normalizedOpacity);
        _settingsWindow?.SetCaptionWindowOpacity(normalizedOpacity);
        _overlayWindow?.SetWindowOpacity(normalizedOpacity);
        ApplyMainWindowOpacity();
        QueueMainWindowOpacityReapplyBurst();
    }

    private void SetCaptionAlwaysAboveMainWindow(bool enabled)
    {
        _settings.Overlay.KeepAboveMainWindow = enabled;
        _settingsStore.Save(_settings);
        _overlayWindow?.SetKeepAboveMainWindow(enabled);

        if (enabled)
        {
            EnsureOverlayAboveMainWindow();
        }
    }

    private void UpdateSensitiveWindowProtectionIndicator(SensitiveWindowProtectionSummary summary)
    {
        ProtectionBanner.Visibility = Visibility.Collapsed;
        ProtectionStatusText.ToolTip = summary.Message;

        if (summary.IsProtected)
        {
            ProtectionBanner.Background = new SolidColorBrush(MediaColor.FromArgb(230, 38, 51, 37));
            ProtectionBanner.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(77, 138, 74));
            ProtectionStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(217, 247, 215));
            ProtectionStatusText.Text = "Capture protection on";
            return;
        }

        ProtectionBanner.Background = new SolidColorBrush(MediaColor.FromArgb(230, 59, 43, 25));
        ProtectionBanner.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(168, 112, 44));
        ProtectionStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 222, 179));
        ProtectionStatusText.Text = "Protection warning";
    }

    private void ApplyWebViewSensitiveContentSettings()
    {
        if (Browser.CoreWebView2 is not null)
        {
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = !_settings.Privacy.SensitiveWindowProtectionEnabled;
        }
    }

    private void ShowBrowserContent()
    {
        Browser.Visibility = Visibility.Visible;
    }

    private void ApplyMainWindowOpacity()
    {
        WindowOpacityController.ApplyWindowTree(this, _settings.MainWindowOpacity);

        if (_opacityReapplyTicksRemaining > 0)
        {
            if (!_opacityReapplyTimer.IsEnabled)
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
        WindowOpacityController.ApplyWindowTree(this, _settings.MainWindowOpacity);

        if (_opacityReapplyTicksRemaining > 0)
        {
            _opacityReapplyTicksRemaining--;
            return;
        }

        _opacityReapplyTimer.Stop();
    }

    private void QueueMainWindowOpacityReapplyBurst()
    {
        _opacityReapplyTicksRemaining = Math.Max(_opacityReapplyTicksRemaining, 4);
        ApplyMainWindowOpacity();
    }

    private void EnsureOverlayAboveMainWindow()
    {
        Topmost = true;
        if (_settings.Overlay.KeepAboveMainWindow)
        {
            _overlayWindow?.EnsureAboveMainWindow();
        }
    }

    private void RegisterOverlayHotKeys()
    {
        if (_hotKeySource is null)
        {
            return;
        }

        UnregisterOverlayHotKeys();
        TryRegisterHotKey(ToggleOverlayHotKeyId, _settings.HotKeys.ToggleOverlay, "toggle caption dialog");
    }

    private void UnregisterOverlayHotKeys()
    {
        if (_hotKeySource is null)
        {
            return;
        }

        UnregisterHotKey(_hotKeySource.Handle, ToggleOverlayHotKeyId);
    }

    private void TryRegisterHotKey(int id, string gesture, string description)
    {
        if (_hotKeySource is null || string.IsNullOrWhiteSpace(gesture))
        {
            return;
        }

        if (!TryParseHotKey(gesture, out var modifiers, out var virtualKey))
        {
            ShowStatus("Could not parse hotkey for " + description + ".");
            return;
        }

        if (!RegisterHotKey(_hotKeySource.Handle, id, modifiers | ModNoRepeat, virtualKey))
        {
            ShowStatus("Could not register hotkey for " + description + ".");
        }
    }

    private IntPtr HotKeyWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotKey)
        {
            return IntPtr.Zero;
        }

        handled = true;
        switch (wParam.ToInt32())
        {
            case ToggleOverlayHotKeyId:
                ToggleOverlayWindow();
                break;
        }

        return IntPtr.Zero;
    }

    private void ToggleOverlayWindow()
    {
        if (_overlayWindow is null || !_overlayWindow.IsVisible || _overlayWindow.WindowState == WindowState.Minimized)
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
        _overlayWindow.WindowState = WindowState.Minimized;
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
        _overlayWindow.WindowState = WindowState.Normal;
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
        (_sensitiveWindowProtectionService as IDisposable)?.Dispose();
        _trayController.Dispose();
        LiveCaptionsLauncher.CloseIfOpen();
        UnregisterOverlayHotKeys();
        if (_hotKeySource is not null)
        {
            _hotKeySource.RemoveHook(HotKeyWindowProc);
            _hotKeySource = null;
        }
        Browser.Dispose();
    }

    private void HandleClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
        {
            ShutdownServices();
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void ShowMainWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            AltTabWindowHider.HideFromAltTab(handle);
        }

        Show();
        WindowState = WindowState.Normal;
        Topmost = true;
        ApplyMainWindowOpacity();
        Activate();
        ShowBrowserContent();
        Browser.Focus();
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
        _overlayWindow.WindowState = WindowState.Normal;
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
            if (IsVisible)
            {
                _settingsWindow.Owner = this;
            }

            _settingsWindow.SensitiveWindowProtectionChanged += enabled =>
                Dispatcher.BeginInvoke(() => SetSensitiveWindowProtection(enabled));
            _settingsWindow.HotKeysChanged += hotKeys =>
                Dispatcher.BeginInvoke(() => SetHotKeys(hotKeys));
            _settingsWindow.CaptionAlwaysAboveMainWindowChanged += enabled =>
                Dispatcher.BeginInvoke(() => SetCaptionAlwaysAboveMainWindow(enabled));
            _settingsWindow.MainWindowOpacityChanged += opacity =>
                Dispatcher.BeginInvoke(() => SetMainWindowOpacity(opacity));
            _settingsWindow.CaptionWindowOpacityChanged += opacity =>
                Dispatcher.BeginInvoke(() => SetOverlayWindowOpacity(opacity));
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.LoadFrom(_settings.Privacy, _sensitiveWindowProtectionService.CurrentSummary, _settings.Overlay, _settings.HotKeys, _settings.MainWindowOpacity);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void HideAllWindows()
    {
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
        System.Windows.Application.Current.Shutdown();
    }

    private sealed class SubmitResult
    {
        public bool Ok { get; set; }
        public string? Reason { get; set; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

}
