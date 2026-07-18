using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using WinUtils.Services;

namespace WinUtils.Views;

public partial class SystemPage : UserControl
{
    public SystemPage()
    {
        InitializeComponent();
        LoadStatus();
    }

    private void LoadStatus()
    {
        try
        {
            var status = AutoLogonService.GetStatus();

            AutoLogonCard.Status = status.Enabled
                ? $"On — signs in as {Describe(status.Domain, status.UserName)}"
                : "Off — Windows asks for your password";

            if (string.IsNullOrWhiteSpace(UserNameBox.Text))
            {
                UserNameBox.Text = status.Enabled && !string.IsNullOrWhiteSpace(status.UserName)
                    ? status.UserName
                    : Environment.UserName;
            }

            if (string.IsNullOrWhiteSpace(DomainBox.Text))
            {
                DomainBox.Text = !string.IsNullOrWhiteSpace(status.Domain)
                    ? status.Domain
                    : Environment.MachineName;
            }
        }
        catch (Exception e)
        {
            AutoLogonCard.Status = "Couldn't read the current setting";
            ShowResult(InfoBarSeverity.Error, "Couldn't read status", e.Message);
        }
    }

    private void OnEnableClick(object sender, RoutedEventArgs e)
    {
        var user = UserNameBox.Text.Trim();
        var domain = DomainBox.Text.Trim();
        var password = PasswordInputBox.Password;

        if (string.IsNullOrWhiteSpace(user))
        {
            ShowResult(InfoBarSeverity.Error, "User name required", "Enter the account to sign in automatically.");
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            ShowResult(
                InfoBarSeverity.Error,
                "Password required",
                "The password is stored as an encrypted LSA secret so Windows can sign in for you.");
            return;
        }

        try
        {
            AutoLogonService.Enable(user, domain, password);
            PasswordInputBox.Password = string.Empty;
            LoadStatus();
            ShowResult(
                InfoBarSeverity.Success,
                "Automatic sign-in enabled",
                $"This PC will sign in as {Describe(domain, user)} on next restart.");
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, "Couldn't enable auto sign-in", ex.Message);
        }
    }

    private void OnDisableClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AutoLogonService.Disable();
            PasswordInputBox.Password = string.Empty;
            LoadStatus();
            ShowResult(
                InfoBarSeverity.Success,
                "Automatic sign-in turned off",
                "Windows will ask for your password at startup again.");
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, "Couldn't turn off auto sign-in", ex.Message);
        }
    }

    private static string Describe(string domain, string user) =>
        string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";

    private void ShowResult(InfoBarSeverity severity, string title, string message)
    {
        ResultBar.Severity = severity;
        ResultBar.Title = title;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }
}
