using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using InterviewGptBridge.Services;
using Xunit;

namespace InterviewGptBridge.WindowsIntegrationTests;

public sealed class SensitiveWindowProtectionWindowsIntegrationTests
{
    [Fact]
    public void RealWpfWindow_CanBeProtectedReopenedAndCleared_WhenOptedInOnSupportedWindows()
    {
        if (!ShouldRunIntegrationTests())
        {
            return;
        }

        Exception? exception = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                RunStaTest();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(15)), "The WPF integration test did not complete.");

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private static bool ShouldRunIntegrationTests()
    {
        return string.Equals(Environment.GetEnvironmentVariable("RUN_WINDOWS_INTEGRATION_TESTS"), "1", StringComparison.Ordinal)
            && Environment.UserInteractive
            && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
    }

    private static void RunStaTest()
    {
        using var service = new SensitiveWindowProtectionService();
        var window = new Window
        {
            Width = 320,
            Height = 180,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Title = "Sensitive Window Protection Integration Test"
        };

        try
        {
            service.Register(window, "Integration test sensitive top-level window.");
            service.SetEnabled(true);

            window.Show();
            service.ReapplyAll();

            var protectedSnapshot = service.GetStatus(window);
            Assert.Equal(SensitiveWindowProtectionStatus.Applied, protectedSnapshot?.Result.Status);

            window.Hide();
            window.Show();
            service.ReapplyAll();

            var reopenedSnapshot = service.GetStatus(window);
            Assert.Equal(SensitiveWindowProtectionStatus.Applied, reopenedSnapshot?.Result.Status);

            service.SetEnabled(false);
            var disabledSnapshot = service.GetStatus(window);
            Assert.Equal(SensitiveWindowProtectionStatus.Disabled, disabledSnapshot?.Result.Status);
        }
        finally
        {
            window.Close();
        }
    }
}
