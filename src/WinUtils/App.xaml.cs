using System.Windows;

namespace WinUtils;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "WinUtils", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}
