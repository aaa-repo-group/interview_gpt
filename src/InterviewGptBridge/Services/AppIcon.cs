using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InterviewGptBridge.Services;

public static class AppIcon
{
    private const string IconUri = "pack://application:,,,/Assets/main_icon.ico";
    private const string PngIconUri = "pack://application:,,,/Assets/main_icon.png";

    public static Icon LoadDrawingIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri(IconUri));
            if (resource?.Stream is null)
            {
                return (Icon)SystemIcons.Application.Clone();
            }

            using var stream = resource.Stream;
            using var icon = new Icon(stream);
            return (Icon)icon.Clone();
        }
        catch
        {
            return LoadDrawingIconFromPng();
        }
    }

    public static ImageSource LoadImageSource()
    {
        return BitmapFrame.Create(new Uri(PngIconUri, UriKind.Absolute));
    }

    private static Icon LoadDrawingIconFromPng()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri(PngIconUri));
            if (resource?.Stream is null)
            {
                return (Icon)SystemIcons.Application.Clone();
            }

            using var bitmap = new Bitmap(resource.Stream);
            using var resized = new Bitmap(bitmap, new System.Drawing.Size(32, 32));
            var handle = resized.GetHicon();
            try
            {
                using var icon = Icon.FromHandle(handle);
                return (Icon)icon.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
