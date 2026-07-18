using System.Collections.ObjectModel;
using System.Windows.Controls;
using WinOsUtils.Services;

namespace WinOsUtils.Views;

public partial class PerformancePage : UserControl
{
    private readonly GamingOptimizer _optimizer = new();
    private readonly ObservableCollection<ProcessSummary> _processes = new();

    public PerformancePage()
    {
        InitializeComponent();
        ProcessList.ItemsSource = _processes;

        // The process list is context-only, so it piggybacks on the same scan pass.
        // ScanFunc runs on a background thread, hence the marshal back to the UI thread.
        GamingSection.ScanFunc = () =>
        {
            var procs = _optimizer.ScanProcesses();
            Dispatcher.Invoke(() =>
            {
                _processes.Clear();
                foreach (var p in procs)
                    _processes.Add(p);
            });
            return _optimizer.Scan();
        };
        GamingSection.ApplyAction = () => _optimizer.Apply();
        GamingSection.StatusChanged += (_, status) => GamingCard.Status = status;
    }
}
