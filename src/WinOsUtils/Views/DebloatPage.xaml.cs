using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using WinOsUtils.Services;

// Wpf.Ui.Controls ships its own MessageBox; keep the familiar Win32 dialogs.
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace WinOsUtils.Views;

public partial class DebloatPage : UserControl
{
    private readonly OneDriveRemover _oneDrive = new();
    private readonly CopilotRemover _copilot = new();
    private readonly BrowserManager _browsers = new();

    public DebloatPage()
    {
        InitializeComponent();

        OneDriveSection.ScanFunc = () => _oneDrive.Scan();
        OneDriveSection.ApplyAction = () => _oneDrive.Apply();
        OneDriveSection.StatusChanged += (_, status) => OneDriveCard.Status = status;

        CopilotSection.ScanFunc = () => _copilot.Scan();
        CopilotSection.ApplyAction = () => _copilot.Apply();
        CopilotSection.StatusChanged += (_, status) => CopilotCard.Status = status;

        EdgeLeftoverSection.ScanFunc = () => new EdgeLeftoverScanner().Scan();
        EdgeLeftoverSection.ApplyAction = () => new EdgeLeftoverScanner().Clean();
        EdgeLeftoverSection.ConfirmApply = ConfirmLeftoverClean;

        LoadEdgeStatus();
    }

    private void LoadEdgeStatus()
    {
        var installed = _browsers.IsEdgeInstalled();
        var version = _browsers.EdgeVersion();

        EdgeDetailText.Text = installed
            ? version is not null ? $"Installed — v{version}" : "Installed"
            : "Not installed.";
        EdgeCard.Status = installed ? "Installed" : "Not installed";
        RemoveEdgeButton.IsEnabled = installed;
    }

    private static bool ConfirmLeftoverClean() =>
        MessageBox.Show(
            "Remove the Microsoft Edge leftovers found by the scan?\n\n"
                + "This deletes Edge's leftover folders, registry keys, scheduled tasks, services and shortcuts. "
                + "The WebView2 runtime and the updater it depends on are kept.",
            "Clean up Edge leftovers",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private async void OnRemoveEdgeClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            Window.GetWindow(this),
            "Uninstall Microsoft Edge and block it from silently reinstalling?\n\n"
                + "The WebView2 runtime is kept, so apps that embed it keep working. Make sure another browser is installed first.",
            "Remove Microsoft Edge",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        SetEdgeBusy(true);
        var result = await Task.Run(() => _browsers.RemoveEdge());
        LoadEdgeStatus();

        EdgeResultBar.Severity = result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        EdgeResultBar.Title = result.Title;
        EdgeResultBar.Message = result.Message;
        EdgeResultBar.IsOpen = true;

        SetEdgeBusy(false);
    }

    private void SetEdgeBusy(bool busy)
    {
        EdgeBusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RemoveEdgeButton.IsEnabled = !busy && _browsers.IsEdgeInstalled();
    }
}
