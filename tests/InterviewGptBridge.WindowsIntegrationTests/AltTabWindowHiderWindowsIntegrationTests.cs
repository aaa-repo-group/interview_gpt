using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using InterviewGptBridge.Services;
using Xunit;
using Forms = System.Windows.Forms;

namespace InterviewGptBridge.WindowsIntegrationTests;

public sealed class AltTabWindowHiderWindowsIntegrationTests
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;

    [Fact]
    public void WinFormsForm_HideFromAltTab_RemovesTaskbarFlagAndAppliesToolWindowStyle()
    {
        if (!Environment.UserInteractive || !OperatingSystem.IsWindows())
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

        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), "The Alt+Tab integration test did not complete.");

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private static void RunStaTest()
    {
        using var form = new Forms.Form
        {
            Width = 160,
            Height = 80,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = true,
            StartPosition = Forms.FormStartPosition.Manual,
            Text = "AltTabWindowHider Integration Test"
        };

        AltTabWindowHider.HideFromAltTab(form);
        form.Show();

        var style = GetWindowLong(form.Handle, GwlExStyle);
        Assert.False(form.ShowInTaskbar);
        Assert.NotEqual(0, style & WsExToolWindow);
        Assert.Equal(0, style & WsExAppWindow);

        form.Close();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
}
