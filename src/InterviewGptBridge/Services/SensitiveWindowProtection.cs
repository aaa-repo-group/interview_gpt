using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InterviewGptBridge.Services;

public enum SensitiveWindowProtectionStatus
{
    Disabled,
    Applied,
    Unsupported,
    Failed,
    WindowUnavailable
}

public sealed record SensitiveWindowProtectionResult(
    SensitiveWindowProtectionStatus Status,
    string Message,
    int? ErrorCode = null,
    uint? Affinity = null)
{
    public bool IsProtected => Status == SensitiveWindowProtectionStatus.Applied;

    public bool IsWarning =>
        Status is SensitiveWindowProtectionStatus.Unsupported
            or SensitiveWindowProtectionStatus.Failed
            or SensitiveWindowProtectionStatus.WindowUnavailable;

    public static SensitiveWindowProtectionResult Disabled() =>
        new(SensitiveWindowProtectionStatus.Disabled, "Sensitive Window Protection is off.");
}

public sealed record SensitiveWindowProtectionSnapshot(
    string WindowId,
    string WindowType,
    string Purpose,
    IntPtr Hwnd,
    bool Enabled,
    SensitiveWindowProtectionResult Result);

public sealed record SensitiveWindowProtectionSummary(
    bool Enabled,
    bool Supported,
    string Message,
    int RegisteredWindowCount,
    int ProtectedWindowCount,
    int WarningCount,
    IReadOnlyList<SensitiveWindowProtectionSnapshot> Windows)
{
    public bool IsProtected => Enabled && RegisteredWindowCount > 0 && ProtectedWindowCount == RegisteredWindowCount;

    public bool HasWarning => Enabled && WarningCount > 0;
}

public sealed record SensitiveWindowProtectionLogEntry(
    DateTimeOffset Timestamp,
    string EventName,
    string WindowId,
    string WindowType,
    string Purpose,
    IntPtr Hwnd,
    SensitiveWindowProtectionStatus Status,
    string Message,
    int? ErrorCode = null,
    uint? Affinity = null);

public interface IWindowDisplayAffinityApi
{
    bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    int GetLastError();
}

public interface IWindowsRuntimeInfo
{
    bool IsWindows { get; }

    Version Version { get; }
}

public interface ISensitiveWindowProtectionSupport
{
    bool SupportsExcludeFromCapture { get; }

    bool SupportsMonitorAffinity { get; }

    string UnsupportedReason { get; }
}

public interface ISensitiveWindowProtectionLogger
{
    void Log(SensitiveWindowProtectionLogEntry entry);
}

internal sealed class SensitiveWindowProtectionRegistry
{
    private readonly Dictionary<string, SensitiveWindowRegistration> _registrations = new(StringComparer.Ordinal);
    private readonly SensitiveWindowProtection _protection;
    private readonly ISensitiveWindowProtectionSupport _support;
    private readonly ISensitiveWindowProtectionLogger _logger;
    private bool _enabled;

    public SensitiveWindowProtectionRegistry(
        SensitiveWindowProtection protection,
        ISensitiveWindowProtectionSupport support,
        ISensitiveWindowProtectionLogger logger,
        bool enabled = false)
    {
        _protection = protection;
        _support = support;
        _logger = logger;
        _enabled = enabled;
    }

    public bool Enabled => _enabled;

    public IReadOnlyList<SensitiveWindowProtectionSnapshot> Snapshots =>
        _registrations.Values
            .Select(registration => registration.ToSnapshot(_enabled))
            .ToArray();

    public SensitiveWindowProtectionSummary Summary => BuildSummary();

    public SensitiveWindowProtectionSnapshot? GetSnapshot(string windowId) =>
        _registrations.TryGetValue(windowId, out var registration)
            ? registration.ToSnapshot(_enabled)
            : null;

    public void Register(string windowId, string windowType, string purpose, Func<IntPtr> getHwnd)
    {
        if (_registrations.TryGetValue(windowId, out var existing))
        {
            existing.WindowType = windowType;
            existing.Purpose = purpose;
            existing.GetHwnd = getHwnd;
            Log("register_updated", existing, existing.LastResult, existing.LastHwnd);
            Reapply(windowId);
            return;
        }

        var registration = new SensitiveWindowRegistration(windowId, windowType, purpose, getHwnd);
        _registrations.Add(windowId, registration);
        Log("registered", registration, registration.LastResult, IntPtr.Zero);
        Reapply(windowId);
    }

    public void Unregister(string windowId)
    {
        if (!_registrations.Remove(windowId, out var registration))
        {
            return;
        }

        ClearRegistration(registration, "unregister_clear");
        Log("unregistered", registration, registration.LastResult, registration.LastHwnd);
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            LogGlobal("set_enabled_noop", SensitiveWindowProtectionStatus.Disabled, enabled ? "Protection was already enabled." : "Protection was already disabled.");
            ReapplyAll();
            return;
        }

        _enabled = enabled;
        LogGlobal("set_enabled", SensitiveWindowProtectionStatus.Disabled, enabled ? "Protection enabled." : "Protection disabled.");
        ReapplyAll();
    }

    public void ReapplyAll()
    {
        LogGlobal("reapply_all_attempt", SensitiveWindowProtectionStatus.Disabled, "Reapplying Sensitive Window Protection to all registered windows.");

        foreach (var windowId in _registrations.Keys.ToArray())
        {
            Reapply(windowId);
        }
    }

    public void Reapply(string windowId)
    {
        if (!_registrations.TryGetValue(windowId, out var registration))
        {
            return;
        }

        var hwnd = registration.GetHwnd();
        if (hwnd != registration.LastHwnd)
        {
            if (registration.LastHwnd != IntPtr.Zero)
            {
                ClearHwnd(registration, registration.LastHwnd, "stale_hwnd_clear");
            }

            registration.LastHwnd = hwnd;
            Log("hwnd_changed", registration, registration.LastResult, hwnd);
        }

        if (_enabled)
        {
            ApplyRegistration(registration, hwnd);
            return;
        }

        ClearRegistration(registration, "protection_cleared");
    }

    public void Clear(string windowId)
    {
        if (_registrations.TryGetValue(windowId, out var registration))
        {
            ClearRegistration(registration, "protection_cleared");
        }
    }

    private void ApplyRegistration(SensitiveWindowRegistration registration, IntPtr hwnd)
    {
        Log("reapply_attempt", registration, registration.LastResult, hwnd);
        var result = _protection.Apply(hwnd, enabled: true);
        registration.LastResult = result;
        registration.LastHwnd = hwnd;

        if (result.Status == SensitiveWindowProtectionStatus.Unsupported)
        {
            Log("unsupported_os", registration, result, hwnd);
        }
        else if (result.Status == SensitiveWindowProtectionStatus.WindowUnavailable)
        {
            Log("missing_hwnd", registration, result, hwnd);
        }
        else if (result.Status == SensitiveWindowProtectionStatus.Failed)
        {
            Log("native_api_failure", registration, result, hwnd, result.Affinity ?? SensitiveWindowProtection.WdaExcludeFromCapture);
        }
        else if (result.Status == SensitiveWindowProtectionStatus.Applied)
        {
            Log("protection_applied", registration, result, hwnd, result.Affinity ?? SensitiveWindowProtection.WdaExcludeFromCapture);
        }
    }

    private void ClearRegistration(SensitiveWindowRegistration registration, string eventName)
    {
        var hwnd = registration.GetHwnd();
        if (hwnd == IntPtr.Zero)
        {
            hwnd = registration.LastHwnd;
        }

        if (hwnd == IntPtr.Zero)
        {
            registration.LastResult = SensitiveWindowProtectionResult.Disabled();
            registration.LastHwnd = IntPtr.Zero;
            Log(eventName, registration, registration.LastResult, hwnd, SensitiveWindowProtection.WdaNone);
            return;
        }

        ClearHwnd(registration, hwnd, eventName);
    }

    private void ClearHwnd(SensitiveWindowRegistration registration, IntPtr hwnd, string eventName)
    {
        var result = _protection.Apply(hwnd, enabled: false);
        registration.LastResult = result;
        registration.LastHwnd = hwnd;

        var logEvent = result.Status == SensitiveWindowProtectionStatus.Failed
            ? "native_api_failure"
            : eventName;

        Log(logEvent, registration, result, hwnd, SensitiveWindowProtection.WdaNone);
    }

    private SensitiveWindowProtectionSummary BuildSummary()
    {
        var snapshots = Snapshots;
        if (!_enabled)
        {
            return new SensitiveWindowProtectionSummary(
                Enabled: false,
                Supported: _support.SupportsExcludeFromCapture,
                Message: SensitiveWindowProtectionResult.Disabled().Message,
                RegisteredWindowCount: snapshots.Count,
                ProtectedWindowCount: 0,
                WarningCount: 0,
                Windows: snapshots);
        }

        if (!_support.SupportsExcludeFromCapture && !_support.SupportsMonitorAffinity)
        {
            return new SensitiveWindowProtectionSummary(
                Enabled: true,
                Supported: false,
                Message: _support.UnsupportedReason,
                RegisteredWindowCount: snapshots.Count,
                ProtectedWindowCount: 0,
                WarningCount: snapshots.Count,
                Windows: snapshots);
        }

        var protectedCount = snapshots.Count(snapshot => snapshot.Result.IsProtected);
        var warningCount = snapshots.Count(snapshot => snapshot.Result.IsWarning);
        var message = warningCount > 0
            ? "Sensitive Window Protection is enabled, but at least one sensitive window is not currently protected."
            : "Sensitive Window Protection is enabled for supported Windows capture APIs.";

        return new SensitiveWindowProtectionSummary(
            Enabled: true,
            Supported: true,
            Message: message,
            RegisteredWindowCount: snapshots.Count,
            ProtectedWindowCount: protectedCount,
            WarningCount: warningCount,
            Windows: snapshots);
    }

    private void Log(
        string eventName,
        SensitiveWindowRegistration registration,
        SensitiveWindowProtectionResult result,
        IntPtr hwnd,
        uint? affinity = null)
    {
        _logger.Log(new SensitiveWindowProtectionLogEntry(
            DateTimeOffset.UtcNow,
            eventName,
            registration.WindowId,
            registration.WindowType,
            registration.Purpose,
            hwnd,
            result.Status,
            result.Message,
            result.ErrorCode,
            affinity));
    }

    private void LogGlobal(string eventName, SensitiveWindowProtectionStatus status, string message)
    {
        _logger.Log(new SensitiveWindowProtectionLogEntry(
            DateTimeOffset.UtcNow,
            eventName,
            string.Empty,
            string.Empty,
            string.Empty,
            IntPtr.Zero,
            status,
            message));
    }

    private sealed class SensitiveWindowRegistration
    {
        public SensitiveWindowRegistration(string windowId, string windowType, string purpose, Func<IntPtr> getHwnd)
        {
            WindowId = windowId;
            WindowType = windowType;
            Purpose = purpose;
            GetHwnd = getHwnd;
        }

        public string WindowId { get; }

        public string WindowType { get; set; }

        public string Purpose { get; set; }

        public Func<IntPtr> GetHwnd { get; set; }

        public IntPtr LastHwnd { get; set; }

        public SensitiveWindowProtectionResult LastResult { get; set; } = SensitiveWindowProtectionResult.Disabled();

        public SensitiveWindowProtectionSnapshot ToSnapshot(bool enabled) =>
            new(WindowId, WindowType, Purpose, LastHwnd, enabled, LastResult);
    }
}

public sealed class SensitiveWindowProtection
{
    public const uint WdaNone = 0x00000000;
    public const uint WdaMonitor = 0x00000001;
    public const uint WdaExcludeFromCapture = 0x00000011;

    private readonly IWindowDisplayAffinityApi _displayAffinityApi;
    private readonly ISensitiveWindowProtectionSupport _support;

    public SensitiveWindowProtection()
        : this(new User32WindowDisplayAffinityApi(), new WindowsSensitiveWindowProtectionSupport())
    {
    }

    public SensitiveWindowProtection(
        IWindowDisplayAffinityApi displayAffinityApi,
        ISensitiveWindowProtectionSupport support)
    {
        _displayAffinityApi = displayAffinityApi;
        _support = support;
    }

    public SensitiveWindowProtectionResult Apply(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero)
        {
            if (!enabled)
            {
                return SensitiveWindowProtectionResult.Disabled();
            }

            return new SensitiveWindowProtectionResult(
                SensitiveWindowProtectionStatus.WindowUnavailable,
                "The window handle is not available yet. Protection will be retried when the window is shown.");
        }

        if (enabled && !_support.SupportsExcludeFromCapture && !_support.SupportsMonitorAffinity)
        {
            return new SensitiveWindowProtectionResult(
                SensitiveWindowProtectionStatus.Unsupported,
                _support.UnsupportedReason);
        }

        if (!enabled)
        {
            return SetAndVerify(hwnd, WdaNone, "Sensitive Window Protection is off.");
        }

        if (_support.SupportsExcludeFromCapture)
        {
            var excludeResult = SetAndVerify(
                hwnd,
                WdaExcludeFromCapture,
                "This feature reduces capture through supported Windows APIs. It cannot prevent every remote-access, administrative, camera, driver-level, or hardware-based capture method.");

            if (excludeResult.IsProtected || !_support.SupportsMonitorAffinity)
            {
                return excludeResult;
            }

            if (excludeResult.ErrorCode is not null and not 87)
            {
                return excludeResult;
            }
        }

        return SetAndVerify(
            hwnd,
            WdaMonitor,
            "Capture protection is active in monitor-only compatibility mode. Supported capture APIs should show this window as blank or unavailable.");
    }

    private SensitiveWindowProtectionResult SetAndVerify(IntPtr hwnd, uint affinity, string successMessage)
    {
        var enabled = affinity != WdaNone;
        try
        {
            if (_displayAffinityApi.SetWindowDisplayAffinity(hwnd, affinity))
            {
                if (_displayAffinityApi.GetWindowDisplayAffinity(hwnd, out var actualAffinity) && actualAffinity != affinity)
                {
                    return new SensitiveWindowProtectionResult(
                        SensitiveWindowProtectionStatus.Failed,
                        $"Windows accepted the display-affinity request but reported 0x{actualAffinity:X} instead of 0x{affinity:X}.",
                        Affinity: affinity);
                }

                return enabled
                    ? new SensitiveWindowProtectionResult(
                        SensitiveWindowProtectionStatus.Applied,
                        successMessage,
                        Affinity: affinity)
                    : SensitiveWindowProtectionResult.Disabled();
            }

            var errorCode = _displayAffinityApi.GetLastError();
            var message = errorCode == 0
                ? "Windows rejected the display-affinity request."
                : $"Windows rejected the display-affinity request: {new Win32Exception(errorCode).Message}";

            return new SensitiveWindowProtectionResult(
                SensitiveWindowProtectionStatus.Failed,
                message,
                errorCode,
                affinity);
        }
        catch (Exception ex)
        {
            return new SensitiveWindowProtectionResult(
                SensitiveWindowProtectionStatus.Failed,
                "Could not apply Sensitive Window Protection: " + ex.Message,
                Affinity: affinity);
        }
    }
}

public sealed class WindowsSensitiveWindowProtectionSupport : ISensitiveWindowProtectionSupport
{
    private readonly IWindowsRuntimeInfo _runtimeInfo;

    public WindowsSensitiveWindowProtectionSupport()
        : this(new SystemWindowsRuntimeInfo())
    {
    }

    public WindowsSensitiveWindowProtectionSupport(IWindowsRuntimeInfo runtimeInfo)
    {
        _runtimeInfo = runtimeInfo;
    }

    public bool SupportsExcludeFromCapture =>
        _runtimeInfo.IsWindows && _runtimeInfo.Version >= new Version(10, 0, 19041);

    public bool SupportsMonitorAffinity =>
        _runtimeInfo.IsWindows;

    public string UnsupportedReason
    {
        get
        {
            if (!_runtimeInfo.IsWindows)
            {
                return "Sensitive Window Protection uses Windows display-affinity APIs and is only available on Windows.";
            }

            return "Sensitive Window Protection requires Windows 10 version 2004, build 19041, or newer. "
                + $"This system reports Windows {_runtimeInfo.Version}.";
        }
    }
}

public sealed class SystemWindowsRuntimeInfo : IWindowsRuntimeInfo
{
    public bool IsWindows => OperatingSystem.IsWindows();

    public Version Version => Environment.OSVersion.Version;
}

public sealed class TraceSensitiveWindowProtectionLogger : ISensitiveWindowProtectionLogger
{
    public void Log(SensitiveWindowProtectionLogEntry entry)
    {
        Trace.TraceInformation(
            "SensitiveWindowProtection event={0} windowId={1} windowType={2} purpose={3} hwnd=0x{4:X} status={5} errorCode={6} affinity={7} message={8}",
            entry.EventName,
            entry.WindowId,
            entry.WindowType,
            entry.Purpose,
            entry.Hwnd.ToInt64(),
            entry.Status,
            entry.ErrorCode?.ToString() ?? string.Empty,
            entry.Affinity?.ToString("X") ?? string.Empty,
            entry.Message);
    }
}

public sealed class User32WindowDisplayAffinityApi : IWindowDisplayAffinityApi
{
    public bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity) =>
        NativeMethods.SetWindowDisplayAffinity(hwnd, affinity);

    public bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity) =>
        NativeMethods.GetWindowDisplayAffinity(hwnd, out affinity);

    public int GetLastError() => Marshal.GetLastWin32Error();

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint dwAffinity);
    }
}
