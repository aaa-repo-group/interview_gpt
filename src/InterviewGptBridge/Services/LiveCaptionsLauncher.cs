using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace InterviewGptBridge.Services;

public static class LiveCaptionsLauncher
{
    private const byte VkLWin = 0x5B;
    private const byte VkControl = 0x11;
    private const byte VkL = 0x4C;
    private const uint KeyEventFKeyUp = 0x0002;

    public static void LaunchAfterDelay(Dispatcher dispatcher, TimeSpan delay)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = delay
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ToggleLiveCaptions();
        };
        timer.Start();
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
