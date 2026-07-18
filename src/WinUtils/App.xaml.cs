using System.Windows;

namespace WinOsUtils;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "WinOS Utils", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}
