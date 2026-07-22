using System.Drawing;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InterviewGptBridge.Services;

public static class AppIcon
{
    private const string IconUri = "pack://application:,,,/Assets/Browser.ico";

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
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    public static ImageSource LoadImageSource()
    {
        return BitmapFrame.Create(new Uri(IconUri, UriKind.Absolute));
    }
}
