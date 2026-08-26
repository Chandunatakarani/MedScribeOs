using System;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace MedScribeOS;

public partial class App : System.Windows.Application
{
    private WinForms.NotifyIcon? _trayIcon;
    private Services.GlobalHotkeyManager? _hotkeyManager;
    private MainWindow? _mainWindow;

    // Virtual-key code for 'M' - kept as a low-level diagnostic hotkey
    private const uint VkM = 0x4D;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "MedScribeAI"
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Launch MedScribeAI", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Test Injection (Ctrl+Alt+M)", null, (_, _) => RunTestInjection());
        menu.Items.Add("Exit", null, (_, _) => { _mainWindow?.Close(); Shutdown(); });
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        _hotkeyManager = new Services.GlobalHotkeyManager();
        _hotkeyManager.RegisterHotkey(
            Services.GlobalHotkeyManager.MOD_CONTROL | Services.GlobalHotkeyManager.MOD_ALT,
            VkM,
            RunTestInjection);
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Closing += (_, args) =>
            {
                // Closing the window (X button) just hides it instead of
                // destroying it, so "Launch MedScribeAI" reopens the same
                // session instantly - the provider's in-progress work
                // (recorded conversation, draft HPI/ROS) isn't lost.
                args.Cancel = true;
                _mainWindow?.Hide();
            };
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void RunTestInjection()
    {
        var result = Services.EcwInjector.TryInject("Test injection from MedScribeOS");
        _trayIcon?.ShowBalloonTip(
            1500,
            "MedScribeOS",
            result.Message,
            result.Success ? WinForms.ToolTipIcon.Info : WinForms.ToolTipIcon.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeyManager?.Dispose();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}