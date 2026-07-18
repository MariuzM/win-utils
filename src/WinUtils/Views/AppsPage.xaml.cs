using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using WinUtils.Services;
using Wpf.Ui.Controls;
// Wpf.Ui.Controls ships its own MessageBox; keep the familiar Win32 dialogs.
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace WinUtils.Views;

public partial class AppsPage : UserControl
{
    private readonly AppInventory _inventory = new();
    private readonly BrowserManager _browsers = new();
    private readonly ClaudeCodeInstaller _claude = new();
    private readonly ObservableCollection<InstalledApp> _items = new();

    private bool _busy;

    public AppsPage()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _items;

        ClaudeSection.ScanFunc = () => _claude.Scan();
        ClaudeSection.ApplyAction = () => _claude.Apply();
        ClaudeSection.StatusChanged += (_, status) => ClaudeCard.Status = status;

        LoadBrowserCaptions();
    }

    // ---- Installed apps -------------------------------------------------

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        SummaryBar.IsOpen = false;

        var apps = await Task.Run(() => _inventory.Scan());

        _items.Clear();
        foreach (var app in apps)
            _items.Add(app);

        SelectAllCheck.IsChecked = false;
        ListCard.Visibility = _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        SetSummary(
            _items.Count > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            $"{_items.Count} app(s) found",
            _items.Count > 0
                ? "Tick the apps to remove, then click Uninstall selected. Leftovers are removed automatically."
                : "No uninstallable apps were detected."
        );

        InventoryCard.Status = $"{_items.Count} app(s) found";

        SetBusy(false);
        UpdateSelection();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        var select = SelectAllCheck.IsChecked == true;
        foreach (var app in _items)
            app.IsSelected = select;
        UpdateSelection();
    }

    private void OnItemCheckClick(object sender, RoutedEventArgs e) => UpdateSelection();

    private async void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        var targets = _items.Where(a => a.IsSelected).ToList();
        if (targets.Count == 0)
            return;

        var leftoverTotal = targets.Sum(t => t.Leftovers.Count);
        var confirm = MessageBox.Show(
            Window.GetWindow(this),
            $"Uninstall {targets.Count} app(s) and remove {leftoverTotal} detected leftover location(s)?\n\n"
                + "This can't be undone. Apps are removed one by one; some may briefly show their own uninstaller.",
            "Uninstall selected apps",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning
        );
        if (confirm != MessageBoxResult.Yes)
            return;

        SetBusy(true);

        var results = await Task.Run(() =>
        {
            var list = new List<AppActionResult>();
            foreach (var app in targets)
                list.Add(_inventory.Uninstall(app));
            return list;
        });

        var ok = results.Count(r => r.Ok);
        var failed = results.Count - ok;

        // Refresh the list so removed apps disappear.
        var apps = await Task.Run(() => _inventory.Scan());
        _items.Clear();
        foreach (var app in apps)
            _items.Add(app);

        SelectAllCheck.IsChecked = false;
        ListCard.Visibility = _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (failed == 0)
        {
            SetSummary(
                InfoBarSeverity.Success,
                $"{ok} app(s) uninstalled",
                "Selected apps and their detected leftovers were removed."
            );
        }
        else
        {
            var stuck = string.Join(", ", results.Where(r => !r.Ok).Select(r => r.Name).Take(4));
            SetSummary(
                InfoBarSeverity.Warning,
                $"{ok} uninstalled, {failed} need attention",
                $"Couldn't fully remove: {stuck}. They may need user interaction or a reboot — re-scan afterwards."
            );
        }

        InventoryCard.Status = $"{_items.Count} app(s) found";

        SetBusy(false);
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var count = _items.Count(a => a.IsSelected);
        SelectedCountText.Text = _items.Count == 0 ? "" : $"{count} of {_items.Count} selected";
        UninstallButton.Content = count > 0 ? $"Uninstall selected ({count})" : "Uninstall selected";
        UninstallButton.IsEnabled = count > 0 && !_busy;
    }

    private void SetSummary(InfoBarSeverity severity, string title, string message)
    {
        SummaryBar.Severity = severity;
        SummaryBar.Title = title;
        SummaryBar.Message = message;
        SummaryBar.IsOpen = true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = !busy;
        SelectAllCheck.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy && _items.Count(a => a.IsSelected) > 0;
    }

    // ---- Install a browser ----------------------------------------------

    private void LoadBrowserCaptions()
    {
        ChromeCaption.Text = Caption(BrowserChoice.Chrome, "Google's browser — widest compatibility.");
        BraveCaption.Text = Caption(BrowserChoice.Brave, "Chromium-based, blocks ads and trackers by default.");
        FirefoxCaption.Text = Caption(BrowserChoice.Firefox, "Independent engine, strong privacy defaults.");
    }

    private string Caption(BrowserChoice choice, string blurb) =>
        _browsers.GetBrowserState(choice).Installed ? $"Installed — {blurb}" : blurb;

    private BrowserChoice SelectedChoice()
    {
        if (BraveOption.IsChecked == true)
            return BrowserChoice.Brave;
        if (FirefoxOption.IsChecked == true)
            return BrowserChoice.Firefox;
        return BrowserChoice.Chrome;
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        SetInstallBusy(true);

        var result = await Task.Run(() => _browsers.InstallBrowser(SelectedChoice()));
        LoadBrowserCaptions();

        InstallResultBar.Severity = result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        InstallResultBar.Title = result.Title;
        InstallResultBar.Message = result.Message;
        InstallResultBar.IsOpen = true;

        SetInstallBusy(false);
    }

    private void SetInstallBusy(bool busy)
    {
        InstallBusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = !busy;
    }
}
