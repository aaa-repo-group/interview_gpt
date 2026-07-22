using System.IO;

namespace InterviewGptBridge.Services;

public static class AppPaths
{
    public static string RootDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InterviewGptBridge");

    public static string WebViewProfileDirectory { get; } =
        Path.Combine(RootDirectory, "WebView2Profile");

    public static string SettingsPath { get; } =
        Path.Combine(RootDirectory, "settings.json");
}
