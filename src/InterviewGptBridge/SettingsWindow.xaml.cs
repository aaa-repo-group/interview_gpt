using System.Windows;
using System.Windows.Media;
using InterviewGptBridge.Services;

namespace InterviewGptBridge;

public partial class SettingsWindow : Window
{
    private bool _loading;

    public event Action<bool>? SensitiveWindowProtectionChanged;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public void LoadFrom(PrivacySettings privacy, SensitiveWindowProtectionSummary summary)
    {
        _loading = true;
        SensitiveWindowProtectionCheckBox.IsChecked = privacy.SensitiveWindowProtectionEnabled;
        _loading = false;

        SetSensitiveWindowProtectionStatus(summary);
    }

    public void SetSensitiveWindowProtectionStatus(SensitiveWindowProtectionSummary summary)
    {
        if (!summary.Enabled)
        {
            SensitiveProtectionStatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        SensitiveProtectionStatusPanel.Visibility = Visibility.Visible;

        if (summary.IsProtected)
        {
            SensitiveProtectionStatusPanel.Background = new SolidColorBrush(Color.FromRgb(38, 51, 37));
            SensitiveProtectionStatusPanel.BorderBrush = new SolidColorBrush(Color.FromRgb(77, 138, 74));
            SensitiveProtectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(217, 247, 215));
            SensitiveProtectionStatusText.Text = "Enabled for supported Windows capture APIs. This feature reduces capture through supported Windows APIs. It cannot prevent every remote-access, administrative, camera, driver-level, or hardware-based capture method.";
            return;
        }

        SensitiveProtectionStatusPanel.Background = new SolidColorBrush(Color.FromRgb(59, 43, 25));
        SensitiveProtectionStatusPanel.BorderBrush = new SolidColorBrush(Color.FromRgb(168, 112, 44));
        SensitiveProtectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 222, 179));
        SensitiveProtectionStatusText.Text = summary.Message;
    }

    private void SensitiveWindowProtectionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SensitiveWindowProtectionChanged?.Invoke(SensitiveWindowProtectionCheckBox.IsChecked == true);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
