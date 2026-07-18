using System.Windows;
using System.Windows.Controls;
using WinOsUtils.Services;

namespace WinOsUtils.Views;

public partial class PrivacyPage : UserControl
{
    private readonly TelemetryRemover _telemetry = new();

    public PrivacyPage()
    {
        InitializeComponent();

        TelemetrySection.ScanFunc = () => _telemetry.Scan();
        TelemetrySection.ApplyAction = () => _telemetry.Apply();
        TelemetrySection.StatusChanged += (_, status) => TelemetryCard.Status = status;

        RefreshSearch();
    }

    private void RefreshSearch()
    {
        var enabled = SearchService.IsWebEnabled();
        WebToggle.IsChecked = enabled;
        StateText.Text = enabled
            ? "On — Search sends queries to Bing and shows highlights."
            : "Off — no web/Bing results, no search highlights.";
        SearchCard.Status = enabled ? "Web results on" : "Web results off";
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        try
        {
            SearchService.SetWebEnabled(WebToggle.IsChecked == true);
        }
        catch
        {
        }

        RefreshSearch();
    }
}
