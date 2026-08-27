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

        // Gate everything behind sign-in. Backing out of the dialog quits.
        if (!EnsureSignedIn())
        {
            Shutdown();
            return;
        }

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "MedScribeAI"
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Launch MedScribeAI", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Test Injection (Ctrl+Alt+M)", null, (_, _) => RunTestInjection());
        menu.Items.Add("Sign out", null, (_, _) => SignOut());
        menu.Items.Add("Exit", null, (_, _) => { _mainWindow?.Close(); Shutdown(); });
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        // A hotkey clash with another app shouldn't crash startup - the rest
        // of the app works fine without it, so report it and carry on.
        try
        {
            _hotkeyManager = new Services.GlobalHotkeyManager();
            _hotkeyManager.RegisterHotkey(
                Services.GlobalHotkeyManager.MOD_CONTROL | Services.GlobalHotkeyManager.MOD_ALT,
                VkM,
                RunTestInjection);
        }
        catch (Exception ex)
        {
            Services.Notify.Warning(
                $"Couldn't register the Ctrl+Alt+M test-injection hotkey ({ex.Message}). " +
                "Everything else still works; use the tray menu's \"Test Injection\" instead.");
        }

        // Sign-in succeeded above - open the app straight away instead of
        // leaving the user to find it in the tray. Closing it (X) still just
        // hides it to the tray, so this only fires on a fresh launch.
        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Closing += HideInsteadOfClose;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    // Closing the window (X button) just hides it instead of destroying it, so
    // "Launch MedScribeAI" reopens the same session instantly - the provider's
    // in-progress work (recorded conversation, draft HPI/ROS) isn't lost.
    private void HideInsteadOfClose(object? sender, System.ComponentModel.CancelEventArgs args)
    {
        args.Cancel = true;
        _mainWindow?.Hide();
    }

    /// <summary>
    /// Shows the modal sign-in dialog unless a user is already signed in.
    /// Returns true once <see cref="Services.AuthService.CurrentUser"/> is set,
    /// false if the user closed the dialog without signing in.
    /// </summary>
    private bool EnsureSignedIn()
    {
        if (Services.AuthService.IsSignedIn)
            return true;

        return new LoginWindow().ShowDialog() == true;
    }

    internal void SignOut()
    {
        Services.AuthService.SignOut();
        _mainWindow?.Hide();

        // Re-prompt. If they don't sign back in, shut the app down.
        if (!EnsureSignedIn())
        {
            _mainWindow?.Close();
            Shutdown();
            return;
        }

        // Signed in as someone new - rebuild the main window so its header
        // and any per-user state reflect the new AuthService.CurrentUser.
        if (_mainWindow != null)
        {
            _mainWindow.Closing -= HideInsteadOfClose;
            _mainWindow.Close();
            _mainWindow = null;
        }
        ShowMainWindow();
    }

    private void RunTestInjection()
    {
        var result = Services.EcwInjector.TryInject("Test injection from MedScribeOS");

        // Balloon tip covers the case where the main window is hidden; the
        // toast covers the case where it's open and focused.
        _trayIcon?.ShowBalloonTip(
            1500,
            "MedScribeOS",
            result.Message,
            result.Success ? WinForms.ToolTipIcon.Info : WinForms.ToolTipIcon.Warning);

        if (result.Success)
            Services.Notify.Success($"Test injection: {result.Message}");
        else
            Services.Notify.Error($"Test injection failed: {result.Message}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeyManager?.Dispose();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}