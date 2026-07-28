using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace InterviewGptBridge.Services;

public static class LiveCaptionsLauncher
{
    private const byte VkLWin = 0x5B;
    private const byte VkControl = 0x11;
    private const byte VkL = 0x4C;
    private const int WmClose = 0x0010;
    private const uint KeyEventFKeyUp = 0x0002;
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    public static void LaunchAfterDelay(Dispatcher dispatcher, TimeSpan delay)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = delay
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            OpenIfNeeded();
        };
        timer.Start();
    }

    public static void CloseIfOpen()
    {
        foreach (var hwnd in FindLiveCaptionsWindows())
        {
            PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static void OpenIfNeeded()
    {
        if (FindLiveCaptionsWindows().Count == 0)
        {
            ToggleLiveCaptions();
        }
    }

    private static void ToggleLiveCaptions()
    {
        keybd_event(VkLWin, 0, 0, UIntPtr.Zero);
        keybd_event(VkControl, 0, 0, UIntPtr.Zero);
        keybd_event(VkL, 0, 0, UIntPtr.Zero);
        keybd_event(VkL, 0, KeyEventFKeyUp, UIntPtr.Zero);
        keybd_event(VkControl, 0, KeyEventFKeyUp, UIntPtr.Zero);
        keybd_event(VkLWin, 0, KeyEventFKeyUp, UIntPtr.Zero);
    }

    private static List<IntPtr> FindLiveCaptionsWindows()
    {
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            var title = GetWindowTitle(hwnd);
            if (title.Contains("Live captions", StringComparison.OrdinalIgnoreCase))
            {
                windows.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
