using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace InterviewGptBridge.Services;

public static class WindowOpacityController
{
    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;
    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr lParam);

    public const double MinimumOpacity = 0.0;
    public const double MaximumOpacity = 1.0;

    public static double Normalize(double opacity)
    {
        return double.IsFinite(opacity)
            ? Math.Clamp(opacity, MinimumOpacity, MaximumOpacity)
            : MaximumOpacity;
    }

    public static byte ToAlpha(double opacity)
    {
        return (byte)Math.Round(Normalize(opacity) * 255, MidpointRounding.AwayFromZero);
    }

    public static void Apply(Window window, double opacity)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        Apply(hwnd, opacity);
    }

    public static void ApplyWindowTree(Window window, double opacity)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        Apply(hwnd, opacity);
        ApplyWindowTree(hwnd, opacity);
    }

    public static void ApplyWindowTree(IntPtr hwnd, double opacity)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        Apply(hwnd, opacity);
        EnumChildWindows(hwnd, (childHwnd, _) =>
        {
            Apply(childHwnd, opacity);
            return true;
        }, IntPtr.Zero);
    }

    public static void Apply(IntPtr hwnd, double opacity)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(hwnd, GwlExStyle);
        if ((style & WsExLayered) == 0)
        {
            SetWindowLong(hwnd, GwlExStyle, style | WsExLayered);
        }

        SetLayeredWindowAttributes(hwnd, 0, ToAlpha(opacity), LwaAlpha);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowProc enumProc, IntPtr lParam);
}
