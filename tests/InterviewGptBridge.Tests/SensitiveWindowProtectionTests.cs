using InterviewGptBridge.Services;
using Xunit;

namespace InterviewGptBridge.Tests;

public sealed class SensitiveWindowProtectionTests
{
    [Theory]
    [InlineData(false, 10, 0, 19045, false)]
    [InlineData(true, 10, 0, 19040, false)]
    [InlineData(true, 10, 0, 19041, true)]
    [InlineData(true, 10, 0, 22631, true)]
    public void Support_GatesExcludeFromCaptureByOperatingSystemVersion(
        bool isWindows,
        int major,
        int minor,
        int build,
        bool expected)
    {
        var support = new WindowsSensitiveWindowProtectionSupport(
            new FakeRuntimeInfo(isWindows, new Version(major, minor, build)));

        Assert.Equal(expected, support.SupportsExcludeFromCapture);
    }

    [Fact]
    public void Apply_WhenEnabledAndSupported_SetsExcludeFromCapture()
    {
        var api = new FakeDisplayAffinityApi();
        var protection = new SensitiveWindowProtection(api, new FakeSupport(supported: true));
        var hwnd = new IntPtr(1234);

        var result = protection.Apply(hwnd, enabled: true);

        Assert.Equal(SensitiveWindowProtectionStatus.Applied, result.Status);
        Assert.True(result.IsProtected);
        Assert.Single(api.Calls);
        Assert.Equal(hwnd, api.Calls[0].Hwnd);
        Assert.Equal(SensitiveWindowProtection.WdaExcludeFromCapture, api.Calls[0].Affinity);
    }

    [Fact]
    public void Apply_WhenExcludeFromCaptureFails_FallsBackToMonitorAffinity()
    {
        var api = new FakeDisplayAffinityApi
        {
            FailedAffinity = SensitiveWindowProtection.WdaExcludeFromCapture,
            LastError = 87
        };
        var protection = new SensitiveWindowProtection(api, new FakeSupport(supported: true));

        var result = protection.Apply(new IntPtr(1234), enabled: true);

        Assert.Equal(SensitiveWindowProtectionStatus.Applied, result.Status);
        Assert.Equal(SensitiveWindowProtection.WdaMonitor, result.Affinity);
        Assert.Equal(
            new[] { SensitiveWindowProtection.WdaExcludeFromCapture, SensitiveWindowProtection.WdaMonitor },
            api.Calls.Select(call => call.Affinity).ToArray());
    }

    [Fact]
    public void Apply_WhenDisabled_ClearsDisplayAffinityWithWdaNone()
    {
        var api = new FakeDisplayAffinityApi();
        var protection = new SensitiveWindowProtection(api, new FakeSupport(supported: true));
        var hwnd = new IntPtr(5678);

        var result = protection.Apply(hwnd, enabled: false);

        Assert.Equal(SensitiveWindowProtectionStatus.Disabled, result.Status);
        Assert.Single(api.Calls);
        Assert.Equal(hwnd, api.Calls[0].Hwnd);
        Assert.Equal(SensitiveWindowProtection.WdaNone, api.Calls[0].Affinity);
    }

    [Fact]
    public void Apply_WhenEnabledOnUnsupportedOs_DoesNotCallDisplayAffinity()
    {
        var api = new FakeDisplayAffinityApi();
        var protection = new SensitiveWindowProtection(api, new FakeSupport(supported: false));

        var result = protection.Apply(new IntPtr(1234), enabled: true);

        Assert.Equal(SensitiveWindowProtectionStatus.Unsupported, result.Status);
        Assert.True(result.IsWarning);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void Apply_WhenNativeCallFails_ReturnsWin32Error()
    {
        var api = new FakeDisplayAffinityApi
        {
            Result = false,
            LastError = 5
        };
        var protection = new SensitiveWindowProtection(api, new FakeSupport(supported: true));

        var result = protection.Apply(new IntPtr(1234), enabled: true);

        Assert.Equal(SensitiveWindowProtectionStatus.Failed, result.Status);
        Assert.Equal(5, result.ErrorCode);
        Assert.True(result.IsWarning);
        Assert.Single(api.Calls);
    }

    [Fact]
    public void Apply_WhenEnabledBeforeWindowHandleExists_ReturnsWindowUnavailable()
    {
        var api = new FakeDisplayAffinityApi();
        var protection = new SensitiveWindowProtection(api, new FakeSupport(supported: true));

        var result = protection.Apply(IntPtr.Zero, enabled: true);

        Assert.Equal(SensitiveWindowProtectionStatus.WindowUnavailable, result.Status);
        Assert.True(result.IsWarning);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void Apply_WhenInvalidNonZeroHwndFails_ReturnsNativeFailure()
    {
        var api = new FakeDisplayAffinityApi
        {
            Result = false,
            LastError = 1400
        };
        var protection = new SensitiveWindowProtection(api, new FakeSupport(supported: true));

        var result = protection.Apply(new IntPtr(9999), enabled: true);

        Assert.Equal(SensitiveWindowProtectionStatus.Failed, result.Status);
        Assert.Equal(1400, result.ErrorCode);
        Assert.Equal(SensitiveWindowProtection.WdaExcludeFromCapture, api.Calls.Single().Affinity);
    }

    [Fact]
    public void Registry_SetEnabledAtRuntime_AppliesAndClearsRegisteredWindow()
    {
        var hwnd = new IntPtr(100);
        var api = new FakeDisplayAffinityApi();
        var logger = new FakeLogger();
        var registry = CreateRegistry(api, logger);
        registry.Register("main", "MainWindow", "Confidential AI responses", () => hwnd);
        api.Calls.Clear();

        registry.SetEnabled(true);
        registry.SetEnabled(false);

        Assert.Equal(
            new[] { SensitiveWindowProtection.WdaExcludeFromCapture, SensitiveWindowProtection.WdaNone },
            api.Calls.Select(call => call.Affinity).ToArray());
        Assert.Contains(logger.Events, entry => entry.EventName == "protection_applied");
        Assert.Contains(logger.Events, entry => entry.EventName == "protection_cleared");
    }

    [Fact]
    public void Registry_SetEnabled_AppliesProtectionToMultipleRegisteredWindows()
    {
        var api = new FakeDisplayAffinityApi();
        var registry = CreateRegistry(api);
        registry.Register("main", "MainWindow", "Confidential AI responses", () => new IntPtr(1));
        registry.Register("overlay", "OverlayWindow", "Caption overlay", () => new IntPtr(2));
        api.Calls.Clear();

        registry.SetEnabled(true);

        Assert.Equal(2, registry.Summary.RegisteredWindowCount);
        Assert.Equal(2, registry.Summary.ProtectedWindowCount);
        Assert.Equal(new[] { new IntPtr(1), new IntPtr(2) }, api.Calls.Select(call => call.Hwnd).ToArray());
        Assert.All(api.Calls, call => Assert.Equal(SensitiveWindowProtection.WdaExcludeFromCapture, call.Affinity));
    }

    [Fact]
    public void Registry_ReopenWindow_ReappliesProtectionAfterHwndBecomesAvailable()
    {
        var hwnd = IntPtr.Zero;
        var api = new FakeDisplayAffinityApi();
        var logger = new FakeLogger();
        var registry = CreateRegistry(api, logger);
        registry.Register("overlay", "OverlayWindow", "Caption overlay", () => hwnd);

        registry.SetEnabled(true);
        Assert.Equal(SensitiveWindowProtectionStatus.WindowUnavailable, registry.GetSnapshot("overlay")?.Result.Status);
        Assert.Contains(logger.Events, entry => entry.EventName == "missing_hwnd");
        Assert.Empty(api.Calls.Where(call => call.Affinity == SensitiveWindowProtection.WdaExcludeFromCapture));

        hwnd = new IntPtr(222);
        registry.Reapply("overlay");

        Assert.Equal(SensitiveWindowProtectionStatus.Applied, registry.GetSnapshot("overlay")?.Result.Status);
        Assert.Equal(new IntPtr(222), api.Calls.Last().Hwnd);
        Assert.Equal(SensitiveWindowProtection.WdaExcludeFromCapture, api.Calls.Last().Affinity);
    }

    [Fact]
    public void Registry_HandleRecreation_TracksNewHwndAndReappliesProtection()
    {
        var hwnd = new IntPtr(10);
        var api = new FakeDisplayAffinityApi();
        var registry = CreateRegistry(api);
        registry.Register("main", "MainWindow", "Confidential AI responses", () => hwnd);
        registry.SetEnabled(true);
        api.Calls.Clear();

        hwnd = new IntPtr(11);
        registry.Reapply("main");

        var snapshot = registry.GetSnapshot("main");
        Assert.Equal(new IntPtr(11), snapshot?.Hwnd);
        Assert.Equal(SensitiveWindowProtectionStatus.Applied, snapshot?.Result.Status);
        Assert.Equal(
            new[] { new IntPtr(10), new IntPtr(11) },
            api.Calls.Select(call => call.Hwnd).ToArray());
        Assert.Equal(
            new[] { SensitiveWindowProtection.WdaNone, SensitiveWindowProtection.WdaExcludeFromCapture },
            api.Calls.Select(call => call.Affinity).ToArray());
    }

    [Fact]
    public void Registry_Unregister_ClearsDisplayAffinity()
    {
        var hwnd = new IntPtr(44);
        var api = new FakeDisplayAffinityApi();
        var registry = CreateRegistry(api);
        registry.Register("main", "MainWindow", "Confidential AI responses", () => hwnd);
        registry.SetEnabled(true);
        api.Calls.Clear();

        registry.Unregister("main");

        Assert.Single(api.Calls);
        Assert.Equal(hwnd, api.Calls[0].Hwnd);
        Assert.Equal(SensitiveWindowProtection.WdaNone, api.Calls[0].Affinity);
        Assert.Empty(registry.Snapshots);
    }

    [Fact]
    public void Registry_UnsupportedOs_FailsSafelyAndLogsUnsupportedWithoutNativeCall()
    {
        var api = new FakeDisplayAffinityApi();
        var logger = new FakeLogger();
        var registry = CreateRegistry(api, logger, supported: false);
        registry.Register("main", "MainWindow", "Confidential AI responses", () => new IntPtr(55));

        registry.SetEnabled(true);

        Assert.False(registry.Summary.Supported);
        Assert.Equal(SensitiveWindowProtectionStatus.Unsupported, registry.GetSnapshot("main")?.Result.Status);
        Assert.DoesNotContain(api.Calls, call => call.Affinity == SensitiveWindowProtection.WdaExcludeFromCapture);
        Assert.Contains(logger.Events, entry => entry.EventName == "unsupported_os");
    }

    private static SensitiveWindowProtectionRegistry CreateRegistry(
        FakeDisplayAffinityApi api,
        FakeLogger? logger = null,
        bool supported = true)
    {
        var support = new FakeSupport(supported);
        var protection = new SensitiveWindowProtection(api, support);
        return new SensitiveWindowProtectionRegistry(protection, support, logger ?? new FakeLogger());
    }

    private sealed class FakeDisplayAffinityApi : IWindowDisplayAffinityApi
    {
        public List<(IntPtr Hwnd, uint Affinity)> Calls { get; } = [];

        public bool Result { get; set; } = true;

        public int LastError { get; set; }

        public uint? FailedAffinity { get; set; }

        private uint _currentAffinity;

        public bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity)
        {
            Calls.Add((hwnd, affinity));
            if (FailedAffinity == affinity)
            {
                return false;
            }

            if (Result)
            {
                _currentAffinity = affinity;
            }

            return Result;
        }

        public bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity)
        {
            affinity = _currentAffinity;
            return true;
        }

        public int GetLastError() => LastError;
    }

    private sealed class FakeSupport(bool supported) : ISensitiveWindowProtectionSupport
    {
        public bool SupportsExcludeFromCapture { get; } = supported;

        public bool SupportsMonitorAffinity { get; } = supported;

        public string UnsupportedReason => "Unsupported test OS.";
    }

    private sealed class FakeRuntimeInfo(bool isWindows, Version version) : IWindowsRuntimeInfo
    {
        public bool IsWindows { get; } = isWindows;

        public Version Version { get; } = version;
    }

    private sealed class FakeLogger : ISensitiveWindowProtectionLogger
    {
        public List<SensitiveWindowProtectionLogEntry> Events { get; } = [];

        public void Log(SensitiveWindowProtectionLogEntry entry)
        {
            Events.Add(entry);
        }
    }
}
