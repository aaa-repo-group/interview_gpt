using Forms = System.Windows.Forms;

namespace InterviewGptBridge.Services;

public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _showOverlayMenuItem;
    private readonly Forms.ToolStripMenuItem _clickThroughMenuItem;
    private bool _disposed;

    public event EventHandler? ShowMainRequested;
    public event EventHandler? ShowOverlayRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? HideAllRequested;
    public event Action<bool>? ClickThroughChanged;
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

        _clickThroughMenuItem = new Forms.ToolStripMenuItem("Caption click-through")
        {
            CheckOnClick = true,
            Enabled = false
        };
        _clickThroughMenuItem.Click += (_, _) => ClickThroughChanged?.Invoke(_clickThroughMenuItem.Checked);

        var exitMenuItem = new Forms.ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(openMainMenuItem);
        menu.Items.Add(_showOverlayMenuItem);
        menu.Items.Add(settingsMenuItem);
        menu.Items.Add(hideAllMenuItem);
        menu.Items.Add(_clickThroughMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitMenuItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = AppIcon.LoadDrawingIcon(),
            Text = "Dropbox",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainRequested?.Invoke(this, EventArgs.Empty);
    }

    public void SetOverlayAvailable(bool available)
    {
        _showOverlayMenuItem.Enabled = available;
        _clickThroughMenuItem.Enabled = available;
    }

    public void SetClickThrough(bool enabled)
    {
        _clickThroughMenuItem.Checked = enabled;
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
