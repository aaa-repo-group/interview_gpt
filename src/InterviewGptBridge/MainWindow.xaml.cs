using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InterviewGptBridge.Services;
using Microsoft.Web.WebView2.Core;

namespace InterviewGptBridge;

public partial class MainWindow : Window
{
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
    private bool _overlayStarted;
    private bool _isExiting;
    private bool _servicesShutdown;

    public MainWindow()
    {
        InitializeComponent();

        _settings = _settingsStore.Load();
        NormalizeSettings();

        _trayController = new TrayController();
        _trayController.ShowMainRequested += (_, _) => Dispatcher.BeginInvoke(ShowMainWindow);
        _trayController.ShowOverlayRequested += (_, _) => Dispatcher.BeginInvoke(ShowOverlayWindow);
        _trayController.SettingsRequested += (_, _) => Dispatcher.BeginInvoke(ShowSettingsWindow);
        _trayController.HideAllRequested += (_, _) => Dispatcher.BeginInvoke(HideAllWindows);
        _trayController.PrivacyModeChanged += enabled => Dispatcher.BeginInvoke(() => SetManualRedaction(enabled));
        _trayController.ExitRequested += (_, _) => Dispatcher.BeginInvoke(ExitApplication);
        _trayController.SetPrivacyMode(_settings.Privacy.ManualRedactionEnabled);

        _sensitiveWindowProtectionService.StatusChanged += (_, summary) =>
            Dispatcher.BeginInvoke(() => UpdateSensitiveWindowProtectionIndicator(summary));
        _sensitiveWindowProtectionService.Register(this, "Embedded ChatGPT WebView host window for confidential AI responses, prompts, secrets, and authentication content.");
        _sensitiveWindowProtectionService.SetEnabled(_settings.Privacy.SensitiveWindowProtectionEnabled);

        _chatReadyProbeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        _chatReadyProbeTimer.Tick += async (_, _) => await ProbeChatGptReadyAsync();

        Loaded += async (_, _) => await InitializeBrowserAsync();
        Activated += (_, _) => ApplyMainPrivacyCover();
        Deactivated += (_, _) => ApplyMainPrivacyCover();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Closing += (_, e) => HandleClosing(e);

        ApplyMainPrivacyCover();
        UpdateSensitiveWindowProtectionIndicator(_sensitiveWindowProtectionService.CurrentSummary);
    }

    private void NormalizeSettings()
    {
        _settings.Overlay ??= new OverlaySettings();
        _settings.Privacy ??= new PrivacySettings();

        if (!_settings.Privacy.SensitiveWindowProtectionUserConfigured)
        {
            if (!_settings.Privacy.SensitiveWindowProtectionEnabled)
            {
                _settings.Privacy.SensitiveWindowProtectionEnabled = true;
                _settingsStore.Save(_settings);
            }
        }
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
        _overlayWindow.LoadPrivacyFrom(_settings.Privacy);
        _overlayWindow.TextSubmitted += async (_, text) => await SubmitPromptAsync(text);
        _overlayWindow.SettingsChanged += (_, overlaySettings) =>
        {
            _settings.Overlay = overlaySettings;
            _settingsStore.Save(_settings);
        };
        _overlayWindow.ManualRedactionChanged += enabled => SetManualRedaction(enabled);
        _overlayWindow.Show();
        _sensitiveWindowProtectionService.ReapplyAll();
        _trayController.SetOverlayAvailable(true);

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
                    _overlayWindow?.SetCaptureStatus(status);
                }
            });
        };
        _captionWatcher.Start();

    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            SetManualRedaction(!_settings.Privacy.ManualRedactionEnabled);
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
                _overlayWindow?.SetSubmitStatus("Sent to ChatGPT");
            }
            else
            {
                _overlayWindow?.SetSubmitStatus(submission?.Reason ?? "Could not find the ChatGPT prompt");
            }
        }
        catch (Exception ex)
        {
            _overlayWindow?.SetSubmitStatus("Submit failed: " + ex.Message);
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

    private void SetManualRedaction(bool enabled)
    {
        var changed = _settings.Privacy.ManualRedactionEnabled != enabled;
        _settings.Privacy.ManualRedactionEnabled = enabled;

        if (changed)
        {
            _settingsStore.Save(_settings);
        }

        _trayController.SetPrivacyMode(enabled);
        _overlayWindow?.SetManualRedaction(enabled);
        ApplyMainPrivacyCover();
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
            ProtectionBanner.Background = new SolidColorBrush(Color.FromArgb(230, 38, 51, 37));
            ProtectionBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(77, 138, 74));
            ProtectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(217, 247, 215));
            ProtectionStatusText.Text = "Capture protection on";
            return;
        }

        ProtectionBanner.Background = new SolidColorBrush(Color.FromArgb(230, 59, 43, 25));
        ProtectionBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(168, 112, 44));
        ProtectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 222, 179));
        ProtectionStatusText.Text = "Protection warning";
    }

    private void ApplyWebViewSensitiveContentSettings()
    {
        if (Browser.CoreWebView2 is not null)
        {
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = !_settings.Privacy.SensitiveWindowProtectionEnabled;
        }
    }

    private void ApplyMainPrivacyCover()
    {
        var shouldRedact = ShouldRedact(IsActive);
        Browser.Visibility = shouldRedact ? Visibility.Hidden : Visibility.Visible;
        PrivacyCover.Visibility = shouldRedact ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool ShouldRedact(bool isActive)
    {
        return _settings.Privacy.ManualRedactionEnabled ||
               (_settings.Privacy.RedactWhenInactive && !isActive);
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
        Activate();
        ApplyMainPrivacyCover();
    }

    private void ShowOverlayWindow()
    {
        if (_overlayWindow is null)
        {
            return;
        }

        _overlayWindow.Show();
        _overlayWindow.Activate();
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
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.LoadFrom(_settings.Privacy, _sensitiveWindowProtectionService.CurrentSummary);
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
}
