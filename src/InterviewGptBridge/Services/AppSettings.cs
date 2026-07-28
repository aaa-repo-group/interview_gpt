namespace InterviewGptBridge.Services;

public sealed class AppSettings
{
    public string? DeviceId { get; set; }
    public bool EnableDevTools { get; set; }
    public int CaptionPollMs { get; set; } = 120;
    public OverlaySettings Overlay { get; set; } = new();
    public HotKeySettings HotKeys { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public LicenseSettings License { get; set; } = new();
}

public sealed class HotKeySettings
{
    public string ToggleOverlay { get; set; } = "Ctrl+Alt+Down";
}

public sealed class OverlaySettings
{
    public double Left { get; set; } = 80;
    public double Top { get; set; } = 80;
    public double Width { get; set; } = 680;
    public double Height { get; set; } = 280;
    public double FontSize { get; set; } = 18;
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
