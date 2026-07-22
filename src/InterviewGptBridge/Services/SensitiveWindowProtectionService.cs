using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace InterviewGptBridge.Services;

public interface ISensitiveWindowProtectionService
{
    bool Enabled { get; }

    SensitiveWindowProtectionSummary CurrentSummary { get; }

    IReadOnlyList<SensitiveWindowProtectionSnapshot> RegisteredWindows { get; }

    event EventHandler<SensitiveWindowProtectionSummary>? StatusChanged;

    void Register(Window window, string purpose);

    void Unregister(Window window);

    void SetEnabled(bool enabled);

    void ReapplyAll();

    SensitiveWindowProtectionSnapshot? GetStatus(Window window);
}

public sealed class SensitiveWindowProtectionService : ISensitiveWindowProtectionService, IDisposable
{
    private const int WmDestroy = 0x0002;
    private const int WmDisplayChange = 0x007E;
    private const int WmNcDestroy = 0x0082;
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;

    private readonly Dictionary<Window, WindowRegistration> _windows = new();
    private readonly SensitiveWindowProtectionRegistry _registry;
    private bool _disposed;

    public SensitiveWindowProtectionService()
        : this(
            new SensitiveWindowProtection(),
            new WindowsSensitiveWindowProtectionSupport(),
            new TraceSensitiveWindowProtectionLogger())
    {
    }

    internal SensitiveWindowProtectionService(
        SensitiveWindowProtection protection,
        ISensitiveWindowProtectionSupport support,
        ISensitiveWindowProtectionLogger logger)
    {
        _registry = new SensitiveWindowProtectionRegistry(protection, support, logger);
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
        SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
    }

    public bool Enabled => _registry.Enabled;

    public SensitiveWindowProtectionSummary CurrentSummary => _registry.Summary;

    public IReadOnlyList<SensitiveWindowProtectionSnapshot> RegisteredWindows => _registry.Snapshots;

    public event EventHandler<SensitiveWindowProtectionSummary>? StatusChanged;

    public void Register(Window window, string purpose)
    {
        ThrowIfDisposed();

        if (_windows.ContainsKey(window))
        {
            var existing = _windows[window];
            existing.Purpose = purpose;
            _registry.Register(existing.WindowId, existing.WindowType, purpose, () => GetCurrentHwnd(window));
            RaiseStatusChanged();
            return;
        }

        var registration = new WindowRegistration(window, purpose);
        _windows.Add(window, registration);

        window.SourceInitialized += Window_SourceInitialized;
        window.IsVisibleChanged += Window_IsVisibleChanged;
        window.Activated += Window_Activated;
        window.Closed += Window_Closed;

        _registry.Register(registration.WindowId, registration.WindowType, registration.Purpose, () => GetCurrentHwnd(window));
        AttachHwndHook(registration);
        RaiseStatusChanged();
    }

    public void Unregister(Window window)
    {
        if (!_windows.Remove(window, out var registration))
        {
            return;
        }

        window.SourceInitialized -= Window_SourceInitialized;
        window.IsVisibleChanged -= Window_IsVisibleChanged;
        window.Activated -= Window_Activated;
        window.Closed -= Window_Closed;
        DetachHwndHook(registration);
        _registry.Unregister(registration.WindowId);
        RaiseStatusChanged();
    }

    public void SetEnabled(bool enabled)
    {
        ThrowIfDisposed();
        _registry.SetEnabled(enabled);
        RaiseStatusChanged();
    }

    public void ReapplyAll()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var registration in _windows.Values.ToArray())
        {
            AttachHwndHook(registration);
        }

        _registry.ReapplyAll();
        RaiseStatusChanged();
    }

    public SensitiveWindowProtectionSnapshot? GetStatus(Window window)
    {
        return _windows.TryGetValue(window, out var registration)
            ? _registry.GetSnapshot(registration.WindowId)
            : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;

        foreach (var window in _windows.Keys.ToArray())
        {
            Unregister(window);
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window && _windows.TryGetValue(window, out var registration))
        {
            AttachHwndHook(registration);
            _registry.Reapply(registration.WindowId);
            RaiseStatusChanged();
        }
    }

    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is Window window && _windows.TryGetValue(window, out var registration))
        {
            AttachHwndHook(registration);
            _registry.Reapply(registration.WindowId);
            RaiseStatusChanged();
        }
    }

    private void Window_Activated(object? sender, EventArgs e)
    {
        if (sender is Window window && _windows.TryGetValue(window, out var registration))
        {
            AttachHwndHook(registration);
            _registry.Reapply(registration.WindowId);
            RaiseStatusChanged();
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            Unregister(window);
        }
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        ReapplyAll();
    }

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            ReapplyAll();
        }
    }

    private void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect)
        {
            ReapplyAll();
        }
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmDisplayChange:
                ReapplyAll();
                break;
            case WmPowerBroadcast:
                if (wParam.ToInt32() is PbtApmResumeSuspend or PbtApmResumeAutomatic)
                {
                    ReapplyAll();
                }

                break;
            case WmDestroy:
            case WmNcDestroy:
                ClearDestroyedHwnd(hwnd);
                break;
        }

        return IntPtr.Zero;
    }

    private void ClearDestroyedHwnd(IntPtr hwnd)
    {
        var registration = _windows.Values.FirstOrDefault(item => item.LastHookedHwnd == hwnd);
        if (registration is null)
        {
            return;
        }

        _registry.Clear(registration.WindowId);
        DetachHwndHook(registration);
        RaiseStatusChanged();
    }

    private void AttachHwndHook(WindowRegistration registration)
    {
        var hwnd = GetCurrentHwnd(registration.Window);
        if (hwnd == IntPtr.Zero || hwnd == registration.LastHookedHwnd)
        {
            return;
        }

        DetachHwndHook(registration);
        var source = HwndSource.FromHwnd(hwnd);
        if (source is null)
        {
            return;
        }

        source.AddHook(WindowMessageHook);
        registration.HwndSource = source;
        registration.LastHookedHwnd = hwnd;
    }

    private void DetachHwndHook(WindowRegistration registration)
    {
        if (registration.HwndSource is not null)
        {
            registration.HwndSource.RemoveHook(WindowMessageHook);
            registration.HwndSource = null;
        }

        registration.LastHookedHwnd = IntPtr.Zero;
    }

    private static IntPtr GetCurrentHwnd(Window window)
    {
        return PresentationSource.FromVisual(window) is HwndSource source
            ? source.Handle
            : IntPtr.Zero;
    }

    private void RaiseStatusChanged()
    {
        StatusChanged?.Invoke(this, _registry.Summary);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class WindowRegistration
    {
        public WindowRegistration(Window window, string purpose)
        {
            Window = window;
            Purpose = purpose;
            WindowType = window.GetType().Name;
            WindowId = WindowType + "#" + window.GetHashCode().ToString("X");
        }

        public Window Window { get; }

        public string WindowId { get; }

        public string WindowType { get; }

        public string Purpose { get; set; }

        public HwndSource? HwndSource { get; set; }

        public IntPtr LastHookedHwnd { get; set; }
    }
}
