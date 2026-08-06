using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using InterviewGptBridge.Services;
using MediaColor = System.Windows.Media.Color;

namespace InterviewGptBridge;

public partial class SettingsWindow : Window
{
    private bool _loading;

    public event Action<bool>? SensitiveWindowProtectionChanged;
    public event Action<HotKeySettings>? HotKeysChanged;
    public event Action<bool>? CaptionAlwaysAboveMainWindowChanged;
    public event Action<double>? MainWindowOpacityChanged;
    public event Action<bool>? MainWindowClickThroughChanged;
    public event Action<double>? CaptionWindowOpacityChanged;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public void LoadFrom(
        PrivacySettings privacy,
        SensitiveWindowProtectionSummary summary,
        OverlaySettings overlay,
        HotKeySettings hotKeys,
        double mainWindowOpacity,
        bool mainWindowClickThrough = false)
    {
        _loading = true;
        SensitiveWindowProtectionCheckBox.IsChecked = privacy.SensitiveWindowProtectionEnabled;
        CaptionAlwaysAboveMainCheckBox.IsChecked = overlay.KeepAboveMainWindow;
        MainWindowOpacitySlider.Value = ToPercent(mainWindowOpacity);
        MainWindowClickThroughCheckBox.IsChecked = mainWindowClickThrough;
        CaptionWindowOpacitySlider.Value = ToPercent(overlay.WindowOpacity);
        UpdateOpacityText(MainWindowOpacityText, MainWindowOpacitySlider.Value);
        UpdateOpacityText(CaptionWindowOpacityText, CaptionWindowOpacitySlider.Value);
        ToggleOverlayHotKeyTextBox.Text = hotKeys.ToggleOverlay;
        ToggleMainWindowHotKeyTextBox.Text = hotKeys.ToggleMainWindow;
        ToggleAllWindowsHotKeyTextBox.Text = hotKeys.ToggleAllWindows;
        ToggleMainClickThroughHotKeyTextBox.Text = hotKeys.ToggleMainClickThrough;
        ToggleCaptionClickThroughHotKeyTextBox.Text = hotKeys.ToggleCaptionClickThrough;
        IncreaseMainOpacityHotKeyTextBox.Text = hotKeys.IncreaseMainOpacity;
        DecreaseMainOpacityHotKeyTextBox.Text = hotKeys.DecreaseMainOpacity;
        IncreaseCaptionOpacityHotKeyTextBox.Text = hotKeys.IncreaseCaptionOpacity;
        DecreaseCaptionOpacityHotKeyTextBox.Text = hotKeys.DecreaseCaptionOpacity;
        IncreaseCaptionFontSizeHotKeyTextBox.Text = hotKeys.IncreaseCaptionFontSize;
        DecreaseCaptionFontSizeHotKeyTextBox.Text = hotKeys.DecreaseCaptionFontSize;
        ToggleCaptionAboveMainHotKeyTextBox.Text = hotKeys.ToggleCaptionAboveMain;
        ToggleCaptureProtectionHotKeyTextBox.Text = hotKeys.ToggleCaptureProtection;
        _loading = false;

        SetSensitiveWindowProtectionStatus(summary);
    }

    public void SetMainWindowOpacity(double opacity)
    {
        _loading = true;
        MainWindowOpacitySlider.Value = ToPercent(opacity);
        UpdateOpacityText(MainWindowOpacityText, MainWindowOpacitySlider.Value);
        _loading = false;
    }

    public void SetMainWindowClickThrough(bool enabled)
    {
        _loading = true;
        MainWindowClickThroughCheckBox.IsChecked = enabled;
        _loading = false;
    }

    public void SetSensitiveWindowProtectionChecked(bool enabled)
    {
        _loading = true;
        SensitiveWindowProtectionCheckBox.IsChecked = enabled;
        _loading = false;
    }

    public void SetCaptionAlwaysAboveMainWindow(bool enabled)
    {
        _loading = true;
        CaptionAlwaysAboveMainCheckBox.IsChecked = enabled;
        _loading = false;
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
            SensitiveProtectionStatusPanel.Background = new SolidColorBrush(MediaColor.FromRgb(38, 51, 37));
            SensitiveProtectionStatusPanel.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(77, 138, 74));
            SensitiveProtectionStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(217, 247, 215));
            SensitiveProtectionStatusText.Text = "Enabled for supported Windows capture APIs. This feature reduces capture through supported Windows APIs. It cannot prevent every remote-access, administrative, camera, driver-level, or hardware-based capture method.";
            return;
        }

        SensitiveProtectionStatusPanel.Background = new SolidColorBrush(MediaColor.FromRgb(59, 43, 25));
        SensitiveProtectionStatusPanel.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(168, 112, 44));
        SensitiveProtectionStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 222, 179));
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

    private void CaptionAlwaysAboveMainCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        CaptionAlwaysAboveMainWindowChanged?.Invoke(CaptionAlwaysAboveMainCheckBox.IsChecked == true);
    }

    private void MainWindowOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MainWindowOpacityText is not null)
        {
            UpdateOpacityText(MainWindowOpacityText, e.NewValue);
        }

        if (!_loading)
        {
            MainWindowOpacityChanged?.Invoke(FromPercent(e.NewValue));
        }
    }

    private void MainWindowClickThroughCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        MainWindowClickThroughChanged?.Invoke(MainWindowClickThroughCheckBox.IsChecked == true);
    }

    private void CaptionWindowOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CaptionWindowOpacityText is not null)
        {
            UpdateOpacityText(CaptionWindowOpacityText, e.NewValue);
        }

        if (!_loading)
        {
            CaptionWindowOpacityChanged?.Invoke(FromPercent(e.NewValue));
        }
    }

    private void HotKeyTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;

        if (sender is not System.Windows.Controls.TextBox textBox)
        {
            return;
        }

        if (e.Key is Key.Back or Key.Delete)
        {
            textBox.Clear();
            RaiseHotKeysChanged();
            return;
        }

        var key = NormalizeKey(e);
        if (IsModifierKey(key))
        {
            return;
        }

        textBox.Text = FormatHotKey(Keyboard.Modifiers, key);
        RaiseHotKeysChanged();
    }

    private void RaiseHotKeysChanged()
    {
        if (_loading)
        {
            return;
        }

        HotKeysChanged?.Invoke(new HotKeySettings
        {
            ToggleOverlay = ToggleOverlayHotKeyTextBox.Text.Trim(),
            ToggleMainWindow = ToggleMainWindowHotKeyTextBox.Text.Trim(),
            ToggleAllWindows = ToggleAllWindowsHotKeyTextBox.Text.Trim(),
            ToggleMainClickThrough = ToggleMainClickThroughHotKeyTextBox.Text.Trim(),
            ToggleCaptionClickThrough = ToggleCaptionClickThroughHotKeyTextBox.Text.Trim(),
            IncreaseMainOpacity = IncreaseMainOpacityHotKeyTextBox.Text.Trim(),
            DecreaseMainOpacity = DecreaseMainOpacityHotKeyTextBox.Text.Trim(),
            IncreaseCaptionOpacity = IncreaseCaptionOpacityHotKeyTextBox.Text.Trim(),
            DecreaseCaptionOpacity = DecreaseCaptionOpacityHotKeyTextBox.Text.Trim(),
            IncreaseCaptionFontSize = IncreaseCaptionFontSizeHotKeyTextBox.Text.Trim(),
            DecreaseCaptionFontSize = DecreaseCaptionFontSizeHotKeyTextBox.Text.Trim(),
            ToggleCaptionAboveMain = ToggleCaptionAboveMainHotKeyTextBox.Text.Trim(),
            ToggleCaptureProtection = ToggleCaptureProtectionHotKeyTextBox.Text.Trim()
        });
    }

    private static Key NormalizeKey(System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        key = key == Key.ImeProcessed ? e.ImeProcessedKey : key;
        key = key == Key.DeadCharProcessed ? e.DeadCharProcessedKey : key;
        return key;
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl
            or Key.RightCtrl
            or Key.LeftAlt
            or Key.RightAlt
            or Key.LeftShift
            or Key.RightShift
            or Key.LWin
            or Key.RWin;
    }

    private static string FormatHotKey(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private static double ToPercent(double opacity)
    {
        return Math.Round(WindowOpacityController.Normalize(opacity) * 100, MidpointRounding.AwayFromZero);
    }

    private static double FromPercent(double percent)
    {
        return WindowOpacityController.Normalize(percent / 100);
    }

    private static void UpdateOpacityText(TextBlock textBlock, double percent)
    {
        textBlock.Text = Math.Round(percent, MidpointRounding.AwayFromZero).ToString("0") + "%";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
