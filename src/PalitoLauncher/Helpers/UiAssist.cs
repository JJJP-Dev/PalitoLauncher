using System.Windows;

namespace PalitoLauncher.Helpers;

/// <summary>Propiedades adjuntas para las plantillas del tema (radio de las esquinas de los botones).</summary>
public static class UiAssist
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(UiAssist), new FrameworkPropertyMetadata(new CornerRadius(6)));

    public static CornerRadius GetCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject element, CornerRadius value) => element.SetValue(CornerRadiusProperty, value);
}
