using System;
using System.Windows;
using System.Windows.Input;
using MedScribeOS.Services;

namespace MedScribeOS;

/// <summary>
/// Modal sign-in dialog shown by <see cref="App"/> before the main window is
/// allowed to open. A "true" DialogResult means <see cref="AuthService.CurrentUser"/>
/// is now set; any other result means the user backed out.
/// </summary>
public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        // SetBusy(true) sets a process-wide wait cursor; make sure it never
        // leaks past this window when we close straight after a successful
        // sign-in without calling SetBusy(false).
        Mouse.OverrideCursor = null;
        base.OnClosed(e);
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text;
        var password = PasswordBox.Password;

        HideError();
        SetBusy(true);

        try
        {
            await AuthService.LoginAsync(email, password);

            // Closes the ShowDialog() call in App.EnsureSignedIn with a
            // "true" result - its cue to open the main window.
            try { DialogResult = true; }
            catch (InvalidOperationException) { /* window already closed by the user */ }
        }
        catch (AuthException ex)
        {
            ShowError(ex.Message);
            SetBusy(false);
            PasswordBox.Clear();
            PasswordBox.Focus();
        }
        catch (Exception ex)
        {
            ShowError($"Something went wrong signing in: {ex.Message}");
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        EmailBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        SignInButton.IsEnabled = !busy;
        SignInButton.Content = busy ? "Signing in…" : "Sign In";
        StatusText.Text = busy ? $"Contacting sign-in server ({AuthService.OrgBaseUrl})…" : "";
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
