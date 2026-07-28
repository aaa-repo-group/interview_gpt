using System.Windows;
using InterviewGptBridge.Licensing;

namespace InterviewGptLicenseTool;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DeviceIdTextBox.Focus();
    }

    private void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        var deviceId = LicenseKeyService.NormalizeDeviceId(DeviceIdTextBox.Text);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            StatusText.Text = "Enter a device key.";
            LicenseKeyTextBox.Clear();
            return;
        }

        try
        {
            var issuedUtc = DateTimeOffset.UtcNow;
            var licenseKey = LicenseKeyService.CreateSixMonthLicense(deviceId, issuedUtc);
            var validation = LicenseKeyService.Validate(licenseKey, deviceId, issuedUtc);
            LicenseKeyTextBox.Text = licenseKey;
            StatusText.Text = validation.ExpiresUtc is null
                ? "License generated."
                : "License generated. Expires " + validation.ExpiresUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + ".";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not generate license: " + ex.Message;
            LicenseKeyTextBox.Clear();
        }
    }

    private void CopyLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LicenseKeyTextBox.Text))
        {
            StatusText.Text = "Generate a license first.";
            return;
        }

        try
        {
            Clipboard.SetText(LicenseKeyTextBox.Text);
            StatusText.Text = "License key copied.";
        }
        catch
        {
            StatusText.Text = "Could not copy the license key.";
        }
    }
}
