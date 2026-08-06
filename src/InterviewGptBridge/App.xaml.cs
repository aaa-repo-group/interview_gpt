using System.Threading;
using System.Windows;
using InterviewGptBridge.Licensing;
using InterviewGptBridge.Services;
using Forms = System.Windows.Forms;

namespace InterviewGptBridge;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private NativeMainForm? _mainForm;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _singleInstanceMutex = new Mutex(true, "InterviewGptBridge.SingleInstance", out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);

#if !NO_LICENSE
        if (!AuthorizeDevice())
        {
            Shutdown();
            return;
        }
#endif

        _mainForm = new NativeMainForm();
        _mainForm.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainForm?.Dispose();
        _mainForm = null;

        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static bool AuthorizeDevice()
    {
        var settingsStore = new SettingsStore();
        var settings = settingsStore.Load();
        settings.License ??= new LicenseSettings();

        var deviceId = DeviceIdentity.GetStableDeviceHash();
        var validation = LicenseKeyService.Validate(settings.License.LicenseKey, deviceId);
        if (validation.IsValid)
        {
            SaveLicenseMetadata(settingsStore, settings, deviceId, settings.License.LicenseKey, validation.ExpiresUtc);
            return true;
        }

        var statusMessage = string.IsNullOrWhiteSpace(settings.License.LicenseKey)
            ? "Unknown device. Copy this device key and enter a license key to continue."
            : validation.Message;
        var licenseWindow = new LicenseWindow(deviceId, statusMessage);
        if (licenseWindow.ShowDialog() != true || string.IsNullOrWhiteSpace(licenseWindow.AcceptedLicenseKey))
        {
            return false;
        }

        settings = settingsStore.Load();
        settings.License ??= new LicenseSettings();
        SaveLicenseMetadata(settingsStore, settings, deviceId, licenseWindow.AcceptedLicenseKey, licenseWindow.AcceptedExpiresUtc);
        return true;
    }

    private static void SaveLicenseMetadata(
        SettingsStore settingsStore,
        AppSettings settings,
        string deviceId,
        string licenseKey,
        DateTimeOffset? expiresUtc)
    {
        settings.License.LicenseKey = licenseKey.Trim();
        settings.License.AuthorizedDeviceId = LicenseKeyService.NormalizeDeviceId(deviceId);
        settings.License.ExpiresUtc = expiresUtc;
        settingsStore.Save(settings);
    }
}
