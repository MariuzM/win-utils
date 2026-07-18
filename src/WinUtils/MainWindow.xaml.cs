using System.Windows;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;
using WinUtils.Views;

namespace WinUtils;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        // Without a provider the NavigationView would construct a fresh page on every
        // navigation, throwing away scan results when you switch groups and come back.
        RootNavigation.SetPageProviderService(new SingletonPageProvider());

        Loaded += (_, _) =>
        {
            RootNavigation.Navigate(typeof(SystemPage));
            HidePaneSeparators();
        };
    }

    /// <summary>
    /// WPF-UI's NavigationView draws a 1px rule above the menu (PART_TopSeparator) and
    /// another between the menu and the footer items (PART_FooterSeparator). With no
    /// pane header and no footer items they just read as stray hairlines, so collapse both.
    /// </summary>
    private void HidePaneSeparators()
    {
        RootNavigation.ApplyTemplate();

        foreach (var part in new[] { "PART_TopSeparator", "PART_FooterSeparator" })
        {
            if (RootNavigation.Template?.FindName(part, RootNavigation) is FrameworkElement separator)
                separator.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Hands back one cached instance per page type, matching the eager-singleton
    /// behaviour the old hardcoded sidebar had.
    /// </summary>
    private sealed class SingletonPageProvider : INavigationViewPageProvider
    {
        private readonly Dictionary<Type, object> _pages = new();

        public object? GetPage(Type pageType)
        {
            if (_pages.TryGetValue(pageType, out var existing))
                return existing;

            if (Activator.CreateInstance(pageType) is not { } created)
                return null;

            _pages[pageType] = created;
            return created;
        }
    }
}
