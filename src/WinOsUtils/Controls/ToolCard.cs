using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace WinOsUtils.Controls;

/// <summary>
/// A always-visible tool container: icon, title, live status line, then its content.
/// Replaces the CardExpander that used to wrap each tool — nothing collapses now.
/// The template lives in App.xaml as an implicit style.
/// </summary>
public class ToolCard : ContentControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(SymbolRegular), typeof(ToolCard),
        new PropertyMetadata(SymbolRegular.Empty));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(ToolCard), new PropertyMetadata(""));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(string), typeof(ToolCard), new PropertyMetadata(""));

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Short outcome line under the title, e.g. "3 change(s) available".</summary>
    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }
}
