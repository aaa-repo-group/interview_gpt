using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Runtime.InteropServices;
using InterviewGptBridge.Services;
using Xunit;
using Forms = System.Windows.Forms;

namespace InterviewGptBridge.WindowsIntegrationTests;

public sealed class NativeOverlayFormWindowsIntegrationTests
{
    private const int EmGetFirstVisibleLine = 0x00CE;

    [Fact]
    public void UpdateCaption_DoesNotThrow_WhenSelectionAndBottomScrollAreActive()
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

        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), "The CaptionBridge integration test did not complete.");

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    [Fact]
    public void UpdateCaption_RendersImmediatelyWhileMouseSelectionIsActive()
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
                RunSelectionUpdateTest();
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

        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), "The CaptionBridge selection update test did not complete.");

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private static void RunStaTest()
    {
        using var service = new SensitiveWindowProtectionService();
        using var form = new NativeOverlayForm(service)
        {
            Width = 420,
            Height = 160,
            Left = -32000,
            Top = -32000,
            StartPosition = Forms.FormStartPosition.Manual
        };

        form.Show();
        Forms.Application.DoEvents();

        var firstCaption = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 24).Select(index => $"Caption line {index} keeps the textbox scrollable."));
        form.UpdateCaption(firstCaption);
        Forms.Application.DoEvents();

        var captionTextBox = form.Controls.OfType<Forms.TextBox>().Single();
        captionTextBox.Focus();
        var selectionStart = Math.Max(0, captionTextBox.TextLength - 80);
        captionTextBox.SelectionStart = selectionStart;
        captionTextBox.SelectionLength = captionTextBox.TextLength - selectionStart;

        form.UpdateCaption(firstCaption + Environment.NewLine + "A final selection update arrives while text is selected.");
        var firstVisibleLineImmediatelyAfterUpdate = GetFirstVisibleLine(captionTextBox);
        Forms.Application.DoEvents();

        Assert.Contains("final selection update", captionTextBox.Text);
        Assert.InRange(captionTextBox.SelectionStart, 0, captionTextBox.TextLength);
        Assert.True(
            firstVisibleLineImmediatelyAfterUpdate > 0,
            "CaptionBridge should not visibly jump to the first line while applying scrollable caption updates.");

        form.CloseForExit();
        Forms.Application.DoEvents();
    }

    private static void RunSelectionUpdateTest()
    {
        using var service = new SensitiveWindowProtectionService();
        using var form = new NativeOverlayForm(service)
        {
            Width = 420,
            Height = 160,
            Left = -32000,
            Top = -32000,
            StartPosition = Forms.FormStartPosition.Manual
        };

        form.Show();
        Forms.Application.DoEvents();

        form.UpdateCaption("First live caption words arrive.");
        Forms.Application.DoEvents();

        var captionTextBox = form.Controls.OfType<Forms.TextBox>().Single();
        captionTextBox.SelectionStart = 0;
        captionTextBox.SelectionLength = captionTextBox.TextLength;
        SetPrivateBoolean(form, "_mouseSelecting", true);

        form.UpdateCaption("First live caption words arrive. More live caption words should render now.");
        Forms.Application.DoEvents();

        Assert.Contains("More live caption words should render now.", captionTextBox.Text);

        form.CloseForExit();
        Forms.Application.DoEvents();
    }

    private static void SetPrivateBoolean(object instance, string fieldName, bool value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(instance, value);
    }

    private static int GetFirstVisibleLine(Forms.TextBox textBox)
    {
        return SendMessage(textBox.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
