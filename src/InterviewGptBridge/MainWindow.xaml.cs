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
    private readonly TrayController _trayController;
    private AppSettings _settings;
    private LiveCaptionWatcher? _captionWatcher;
    private OverlayWindow? _overlayWindow;
    private SettingsWindow? _settingsWindow;
    private HwndSource? _hotKeySource;
    private bool _overlayStarted;
    private bool _isExiting;
    private bool _servicesShutdown;

    public MainWindow()
    {
        InitializeComponent();

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

        if (_settings.Overlay.ClickThrough)
        {
            _settings.Overlay.ClickThrough = false;
            _settingsStore.Save(_settings);
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hotKeySource = HwndSource.FromVisual(this) as HwndSource;
        _hotKeySource?.AddHook(HotKeyWindowProc);
        RegisterOverlayHotKeys();
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
            Browser.CoreWebView2.NavigationCompleted += (_, _) => _chatReadyProbeTimer.Start();
            Browser.Source = new Uri("https://chatgpt.com/");
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
            Dispatcher.BeginInvoke(() =>
            {
                if (!_isExiting)
                {
                    _overlayWindow?.UpdateCaption(snapshot);
                }
            });
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
        StatusBanner.Visibility = Visibility.Visible;
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
        if (!summary.Enabled)
        {
            ProtectionBanner.Visibility = Visibility.Collapsed;
            ProtectionStatusText.ToolTip = null;
            return;
        }

        ProtectionBanner.Visibility = Visibility.Visible;
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
        Show();
        WindowState = WindowState.Normal;
        Topmost = true;
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
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.LoadFrom(_settings.Privacy, _sensitiveWindowProtectionService.CurrentSummary, _settings.Overlay, _settings.HotKeys);
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
