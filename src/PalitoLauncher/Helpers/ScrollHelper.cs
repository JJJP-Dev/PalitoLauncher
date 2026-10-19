using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PalitoLauncher.Helpers;

/// <summary>
/// Reenvía la rueda del ratón al ScrollViewer padre. Necesario para los FlowDocumentScrollViewer de las notas, que se
/// "comen" la rueda aunque no tengan nada que desplazar (la página no se movería al pasar el ratón por encima).
/// </summary>
public static class ScrollHelper
{
    public static readonly DependencyProperty ForwardMouseWheelProperty = DependencyProperty.RegisterAttached(
        "ForwardMouseWheel", typeof(bool), typeof(ScrollHelper), new PropertyMetadata(false, OnForwardMouseWheelChanged));

    public static bool GetForwardMouseWheel(DependencyObject element) => (bool)element.GetValue(ForwardMouseWheelProperty);

    public static void SetForwardMouseWheel(DependencyObject element, bool value) => element.SetValue(ForwardMouseWheelProperty, value);

    private static void OnForwardMouseWheelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true) element.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject source) return;

        var parent = FindParentScrollViewer(source);
        if (parent == null) return;

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        });
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject start)
    {
        var current = GetParent(start);
        while (current != null)
        {
            if (current is ScrollViewer sv) return sv;
            current = GetParent(current);
        }
        return null;
    }

    private static DependencyObject? GetParent(DependencyObject element)
        => element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);
}
