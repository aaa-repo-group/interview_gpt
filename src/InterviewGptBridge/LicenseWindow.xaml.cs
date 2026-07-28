using System.Windows;
using InterviewGptBridge.Licensing;

namespace InterviewGptBridge;

public partial class LicenseWindow : Window
{
    private readonly string _deviceId;

    public string? AcceptedLicenseKey { get; private set; }
    public DateTimeOffset? AcceptedExpiresUtc { get; private set; }

    public LicenseWindow(string deviceId, string statusMessage)
    {
        InitializeComponent();

        _deviceId = LicenseKeyService.NormalizeDeviceId(deviceId);
        DeviceIdTextBox.Text = LicenseKeyService.FormatDeviceId(_deviceId);
        StatusText.Text = statusMessage;
        LicenseKeyTextBox.Focus();
    }

    private void CopyDeviceIdButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(DeviceIdTextBox.Text);
            StatusText.Text = "Device key copied.";
        }
        catch
        {
            StatusText.Text = "Could not copy the device key.";
        }
    }

    private void AuthorizeButton_Click(object sender, RoutedEventArgs e)
    {
        var licenseKey = LicenseKeyTextBox.Text.Trim();
        var result = LicenseKeyService.Validate(licenseKey, _deviceId);
        if (!result.IsValid)
        {
            StatusText.Text = result.Message;
            return;
        }

        AcceptedLicenseKey = licenseKey;
        AcceptedExpiresUtc = result.ExpiresUtc;
        DialogResult = true;
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
