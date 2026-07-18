using System.Windows.Controls;
using WinOsUtils.Services;

namespace WinOsUtils.Views;

public partial class PersonalizationPage : UserControl
{
    private readonly StartMenuTweaker _startMenu = new();

    public PersonalizationPage()
    {
        InitializeComponent();

        StartMenuSection.ScanFunc = () => _startMenu.Scan();
        StartMenuSection.ApplyAction = () => _startMenu.Apply();
        StartMenuSection.StatusChanged += (_, status) => StartMenuCard.Status = status;
    }
}
