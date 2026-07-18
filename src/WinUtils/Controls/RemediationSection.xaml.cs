using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using WinOsUtils.Services;

namespace WinOsUtils.Controls;

/// <summary>
/// The scan / apply / results block that used to be copy-pasted into eight separate pages.
/// Hosts drive it with delegates so no service class had to change shape.
/// </summary>
public partial class RemediationSection : UserControl
{
    private readonly ObservableCollection<RemediationCheck> _items = new();

    public RemediationSection()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _items;
    }

    /// <summary>Returns the current findings. Runs on a background thread.</summary>
    public Func<List<RemediationCheck>>? ScanFunc { get; set; }

    /// <summary>Applies the pending changes. Runs on a background thread, then triggers a re-scan.</summary>
    public Action? ApplyAction { get; set; }

    /// <summary>
    /// Optional gate for destructive actions. Runs on the UI thread before <see cref="ApplyAction"/>;
    /// returning false cancels. Lets a host put up a confirmation dialog.
    /// </summary>
    public Func<bool>? ConfirmApply { get; set; }

    /// <summary>Raised after every scan so a host can mirror the outcome into its ToolCard status line.</summary>
    public event EventHandler<string>? StatusChanged;

    private static DependencyProperty Register(string name, string fallback = "") =>
        DependencyProperty.Register(name, typeof(string), typeof(RemediationSection), new PropertyMetadata(fallback));

    public static readonly DependencyProperty DescriptionProperty = Register(nameof(Description));
    public static readonly DependencyProperty WarningTitleProperty = Register(nameof(WarningTitle));
    public static readonly DependencyProperty WarningBodyProperty = Register(nameof(WarningBody));
    public static readonly DependencyProperty ScanLabelProperty = Register(nameof(ScanLabel), "Scan");
    public static readonly DependencyProperty ApplyLabelProperty = Register(nameof(ApplyLabel), "Apply changes");
    public static readonly DependencyProperty CompliantTitleProperty = Register(nameof(CompliantTitle), "Everything is already configured");
    public static readonly DependencyProperty CompliantMessageProperty = Register(nameof(CompliantMessage), "Every checked item is already compliant.");
    public static readonly DependencyProperty AppliedMessageProperty = Register(nameof(AppliedMessage), "All changes applied and re-verified — nothing left to fix.");
    public static readonly DependencyProperty PendingMessageProperty = Register(nameof(PendingMessage), "Review the list below, then apply.");
    public static readonly DependencyProperty RemainingMessageProperty = Register(nameof(RemainingMessage), "Re-scan after a reboot if anything remains — some changes only finish on restart.");

    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string WarningTitle { get => (string)GetValue(WarningTitleProperty); set => SetValue(WarningTitleProperty, value); }
    public string WarningBody { get => (string)GetValue(WarningBodyProperty); set => SetValue(WarningBodyProperty, value); }
    public string ScanLabel { get => (string)GetValue(ScanLabelProperty); set => SetValue(ScanLabelProperty, value); }
    public string ApplyLabel { get => (string)GetValue(ApplyLabelProperty); set => SetValue(ApplyLabelProperty, value); }
    public string CompliantTitle { get => (string)GetValue(CompliantTitleProperty); set => SetValue(CompliantTitleProperty, value); }
    public string CompliantMessage { get => (string)GetValue(CompliantMessageProperty); set => SetValue(CompliantMessageProperty, value); }
    public string AppliedMessage { get => (string)GetValue(AppliedMessageProperty); set => SetValue(AppliedMessageProperty, value); }
    public string PendingMessage { get => (string)GetValue(PendingMessageProperty); set => SetValue(PendingMessageProperty, value); }
    public string RemainingMessage { get => (string)GetValue(RemainingMessageProperty); set => SetValue(RemainingMessageProperty, value); }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        WarningBar.IsOpen = !string.IsNullOrWhiteSpace(WarningTitle);
    }

    private async void OnScanClick(object sender, RoutedEventArgs e) => await RunScanAsync(false);

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (ApplyAction is null)
            return;

        if (ConfirmApply is not null && !ConfirmApply())
            return;

        SetBusy(true);
        await Task.Run(ApplyAction);
        await RunScanAsync(true);
    }

    /// <summary>Scans without requiring a click — lets a host refresh a section when its group is opened.</summary>
    public Task ScanAsync() => RunScanAsync(false);

    private async Task RunScanAsync(bool afterApply)
    {
        if (ScanFunc is null)
            return;

        SetBusy(true);

        List<RemediationCheck> results;
        try
        {
            results = await Task.Run(ScanFunc);
        }
        catch (Exception ex)
        {
            // A failed scan used to bubble to the app-wide crash handler and kill the window.
            SetSummary(InfoBarSeverity.Error, "Scan failed", ex.Message);
            SetBusy(false);
            return;
        }

        _items.Clear();
        foreach (var r in results)
            _items.Add(r);

        var need = results.Count(r => r.State == CheckState.NeedsChange);
        var errors = results.Count(r => r.State == CheckState.Error);

        if (need == 0 && errors == 0)
        {
            SetSummary(
                InfoBarSeverity.Success,
                CompliantTitle,
                afterApply ? AppliedMessage : CompliantMessage);
            StatusChanged?.Invoke(this, "Nothing to change");
        }
        else
        {
            SetSummary(
                errors > 0 ? InfoBarSeverity.Error : InfoBarSeverity.Warning,
                afterApply ? "Some items still need attention" : $"{need} item(s) need changes",
                afterApply ? RemainingMessage : PendingMessage);
            StatusChanged?.Invoke(this, errors > 0 ? $"{errors} error(s)" : $"{need} change(s) available");
        }

        ApplyButton.IsEnabled = need > 0;
        SetBusy(false);
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
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = !busy;
        if (busy)
            ApplyButton.IsEnabled = false;
    }
}
