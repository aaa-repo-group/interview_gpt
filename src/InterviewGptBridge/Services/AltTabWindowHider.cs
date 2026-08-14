using System.Runtime.InteropServices;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace InterviewGptBridge.Services;

public static class AltTabWindowHider
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    public static void HideFromAltTab(Forms.Form form)
    {
        form.ShowInTaskbar = false;

        if (form.IsHandleCreated)
        {
            HideFromAltTab(form.Handle);
        }

        form.HandleCreated += (_, _) => HideFromAltTab(form.Handle);
        form.Shown += (_, _) => HideFromAltTab(form.Handle);
        form.VisibleChanged += (_, _) =>
        {
            if (form.Visible && form.IsHandleCreated)
            {
                HideFromAltTab(form.Handle);
            }
        };
        form.Activated += (_, _) => HideFromAltTab(form.Handle);
    }

    public static void HideFromAltTab(System.Windows.Window window)
    {
        window.ShowInTaskbar = false;

        void Apply()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
            {
                HideFromAltTab(handle);
            }
        }

        Apply();

        window.SourceInitialized += (_, _) => Apply();
        window.Loaded += (_, _) => Apply();
        window.Activated += (_, _) => Apply();
        window.IsVisibleChanged += (_, _) =>
        {
            if (window.IsVisible)
            {
                Apply();
            }
        };
    }

    public static void HideFromAltTab(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(hwnd, GwlExStyle);
        style &= ~WsExAppWindow;
        style |= WsExToolWindow;
        SetWindowLong(hwnd, GwlExStyle, style);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int width, int height, uint flags);
}
