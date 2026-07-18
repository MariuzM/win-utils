using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WinUtils.Controls;

/// <summary>
/// Makes a ScrollViewer honour the *magnitude* of the mouse wheel delta.
///
/// WPF's ScrollViewer calls MouseWheelDown/Up once per event and ignores Delta entirely,
/// so a precision touchpad or Magic Mouse — which emits a stream of small deltas rather
/// than one 120 per notch — scrolls several times faster than every native Windows app.
/// A classic mouse (Delta=120) lands on exactly the same 48px it did before, so the
/// familiar feel is preserved.
/// </summary>
public static class WheelScroll
{
    /// <summary>WPF's per-line scroll amount; 3 lines x 16px = the usual 48px notch.</summary>
    private const double LineHeight = 16.0;

    public static readonly DependencyProperty ProportionalProperty =
        DependencyProperty.RegisterAttached(
            "Proportional",
            typeof(bool),
            typeof(WheelScroll),
            new PropertyMetadata(false, OnProportionalChanged));

    public static void SetProportional(DependencyObject o, bool value) =>
        o.SetValue(ProportionalProperty, value);

    public static bool GetProportional(DependencyObject o) =>
        (bool)o.GetValue(ProportionalProperty);

    private static void OnProportionalChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not ScrollViewer sv)
            return;

        if ((bool)e.NewValue)
            sv.PreviewMouseWheel += OnPreviewMouseWheel;
        else
            sv.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || e.Delta == 0)
            return;

        // Respect the user's "lines to scroll" setting; -1 means "one screen at a time".
        var lines = SystemParameters.WheelScrollLines;
        var step = lines < 0 ? sv.ViewportHeight : lines * LineHeight;

        sv.ScrollToVerticalOffset(sv.VerticalOffset - (e.Delta / 120.0 * step));
        e.Handled = true;
    }
}
