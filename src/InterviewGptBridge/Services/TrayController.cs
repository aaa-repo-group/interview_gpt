using Forms = System.Windows.Forms;

namespace InterviewGptBridge.Services;

public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _showOverlayMenuItem;
    private readonly Forms.ToolStripMenuItem _privacyModeMenuItem;
    private bool _disposed;

    public event EventHandler? ShowMainRequested;
    public event EventHandler? ShowOverlayRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? HideAllRequested;
    public event Action<bool>? PrivacyModeChanged;
    public event EventHandler? ExitRequested;

    public TrayController()
    {
        _showOverlayMenuItem = new Forms.ToolStripMenuItem("Show captions");
        _showOverlayMenuItem.Click += (_, _) => ShowOverlayRequested?.Invoke(this, EventArgs.Empty);
        _showOverlayMenuItem.Enabled = false;

        var openMainMenuItem = new Forms.ToolStripMenuItem("Open ChatGPT");
        openMainMenuItem.Click += (_, _) => ShowMainRequested?.Invoke(this, EventArgs.Empty);

        var hideAllMenuItem = new Forms.ToolStripMenuItem("Hide windows");
        hideAllMenuItem.Click += (_, _) => HideAllRequested?.Invoke(this, EventArgs.Empty);

        var settingsMenuItem = new Forms.ToolStripMenuItem("Settings...");
        settingsMenuItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        _privacyModeMenuItem = new Forms.ToolStripMenuItem("Privacy mode")
        {
            CheckOnClick = true
        };
        _privacyModeMenuItem.Click += (_, _) => PrivacyModeChanged?.Invoke(_privacyModeMenuItem.Checked);

        var exitMenuItem = new Forms.ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(openMainMenuItem);
        menu.Items.Add(_showOverlayMenuItem);
        menu.Items.Add(settingsMenuItem);
        menu.Items.Add(hideAllMenuItem);
        menu.Items.Add(_privacyModeMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitMenuItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = AppIcon.LoadDrawingIcon(),
            Text = "Browser",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainRequested?.Invoke(this, EventArgs.Empty);
    }

    public void SetOverlayAvailable(bool available)
    {
        _showOverlayMenuItem.Enabled = available;
    }

    public void SetPrivacyMode(bool enabled)
    {
        _privacyModeMenuItem.Checked = enabled;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }
}
