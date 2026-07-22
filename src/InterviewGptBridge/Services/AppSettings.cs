namespace InterviewGptBridge.Services;

public sealed class AppSettings
{
    public string? DeviceId { get; set; }
    public bool EnableDevTools { get; set; }
    public int CaptionPollMs { get; set; } = 120;
    public OverlaySettings Overlay { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
}

public sealed class OverlaySettings
{
    public double Left { get; set; } = 80;
    public double Top { get; set; } = 80;
    public double Width { get; set; } = 680;
    public double Height { get; set; } = 280;
    public double Opacity { get; set; } = 0.88;
    public bool Topmost { get; set; } = true;
}

public sealed class PrivacySettings
{
    public bool ManualRedactionEnabled { get; set; }
    public bool RedactWhenInactive { get; set; } = true;
    public bool SensitiveWindowProtectionEnabled { get; set; } = true;
    public bool SensitiveWindowProtectionUserConfigured { get; set; }
}
