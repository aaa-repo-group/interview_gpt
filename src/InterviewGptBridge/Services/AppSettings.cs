namespace InterviewGptBridge.Services;

public sealed class AppSettings
{
    public string? DeviceId { get; set; }
    public bool EnableDevTools { get; set; }
    public int CaptionPollMs { get; set; } = 60;
    public double MainWindowOpacity { get; set; } = 1.0;
    public bool MainWindowClickThrough { get; set; }
    public OverlaySettings Overlay { get; set; } = new();
    public HotKeySettings HotKeys { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public LicenseSettings License { get; set; } = new();
}

public sealed class HotKeySettings
{
    public string ToggleMainWindow { get; set; } = "Ctrl+Alt+M";
    public string ToggleAllWindows { get; set; } = "Ctrl+Alt+H";
    public string ToggleOverlay { get; set; } = "Ctrl+Alt+Down";
    public string ToggleMainClickThrough { get; set; } = "Ctrl+Alt+T";
    public string ToggleCaptionClickThrough { get; set; } = "Ctrl+Alt+C";
    public string IncreaseMainOpacity { get; set; } = "Ctrl+Alt+Right";
    public string DecreaseMainOpacity { get; set; } = "Ctrl+Alt+Left";
    public string IncreaseCaptionOpacity { get; set; } = "Ctrl+Alt+Shift+Right";
    public string DecreaseCaptionOpacity { get; set; } = "Ctrl+Alt+Shift+Left";
    public string IncreaseCaptionFontSize { get; set; } = "Ctrl+Alt+Up";
    public string DecreaseCaptionFontSize { get; set; } = "Ctrl+Alt+Shift+Down";
    public string ToggleCaptionAboveMain { get; set; } = "Ctrl+Alt+A";
    public string ToggleCaptureProtection { get; set; } = "Ctrl+Alt+P";
}

public sealed class OverlaySettings
{
    public double Left { get; set; } = 80;
    public double Top { get; set; } = 80;
    public double Width { get; set; } = 680;
    public double Height { get; set; } = 280;
    public double FontSize { get; set; } = 18;
    public double WindowOpacity { get; set; } = 1.0;
    public bool Topmost { get; set; } = true;
    public bool ClickThrough { get; set; }
    public bool KeepAboveMainWindow { get; set; } = true;
}

public sealed class PrivacySettings
{
    public bool ManualRedactionEnabled { get; set; }
    public bool RedactWhenInactive { get; set; }
    public bool SensitiveWindowProtectionEnabled { get; set; } = true;
    public bool SensitiveWindowProtectionUserConfigured { get; set; }
}

public sealed class LicenseSettings
{
    public string LicenseKey { get; set; } = string.Empty;
    public string AuthorizedDeviceId { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresUtc { get; set; }
}
