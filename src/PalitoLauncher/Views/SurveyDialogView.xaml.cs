using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PalitoLauncher.Helpers;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.Views;

/// <summary>
/// Vista de la encuesta. Lógica solo de vista: foco inicial en la primera respuesta, foco en «Seguir respondiendo» /
/// «Volver» al pedir confirmación, avisos al lector de pantalla (error de envío, «Enviando…», confirmación), alto máximo
/// (~80 % de la ventana, el resto se desplaza) y rueda del ratón que pasa a la lista cuando un cuadro de texto ya no
/// puede desplazarse más.
/// </summary>
public partial class SurveyDialogView : UserControl
{
    private const double MaxHeightRatio = 0.8;
    private const double MinMaxHeight = 320;

    private SurveyDialogViewModel? _dialog;
    private Window? _window;
    private IInputElement? _focusBeforeConfirm;

    public SurveyDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, _) => Attach();
        QuestionsScroller.PreviewMouseWheel += OnQuestionsPreviewMouseWheel;
    }

    private void Attach()
    {
        if (!IsLoaded) return;

        var window = Window.GetWindow(this);
        if (!ReferenceEquals(window, _window))
        {
            if (_window != null) _window.SizeChanged -= OnWindowSizeChanged;
            _window = window;
            if (_window != null) _window.SizeChanged += OnWindowSizeChanged;
            UpdateMaxHeight();
        }

        if (ReferenceEquals(_dialog, DataContext)) return;
        if (_dialog != null) _dialog.PropertyChanged -= OnDialogPropertyChanged;
        _dialog = DataContext as SurveyDialogViewModel;
        if (_dialog == null) return;

        _dialog.PropertyChanged += OnDialogPropertyChanged;
        QuestionsScroller.ScrollToTop();
        FocusFirstInput();
    }

    private void Detach()
    {
        if (_dialog != null) _dialog.PropertyChanged -= OnDialogPropertyChanged;
        _dialog = null;
        if (_window != null) _window.SizeChanged -= OnWindowSizeChanged;
        _window = null;
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => UpdateMaxHeight();

    /// <summary>La encuesta puede ser más alta que la ventana: como mucho ~80 % y el resto se desplaza.</summary>
    private void UpdateMaxHeight()
    {
        var height = _window?.ActualHeight ?? 0;
        DialogBorder.MaxHeight = height > 0 ? Math.Max(MinMaxHeight, height * MaxHeightRatio) : double.PositiveInfinity;
    }

    private void OnDialogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var dialog = _dialog;
        if (dialog == null) return;

        switch (e.PropertyName)
        {
            case nameof(SurveyDialogViewModel.IsConfirming):
                OnConfirmingChanged(dialog.IsConfirming);
                break;
            case nameof(SurveyDialogViewModel.ConfirmText) when dialog.IsConfirming:
                // De «¿Descartar…?» a «¿Salir sin enviarla?» sin cerrar la barra: también se anuncia.
                LiveRegion.Announce(ConfirmTextBlock);
                break;
            case nameof(SurveyDialogViewModel.HasError) when dialog.HasError:
                LiveRegion.Announce(ErrorText);
                break;
            case nameof(SurveyDialogViewModel.IsSending) when dialog.IsSending:
                LiveRegion.Announce(SendButton); // «Enviando…»
                break;
        }
    }

    private void OnConfirmingChanged(bool confirming)
    {
        if (confirming)
        {
            _focusBeforeConfirm = Keyboard.FocusedElement;
            FocusLater(() => KeepEditingButton.Focus());
            LiveRegion.Announce(ConfirmTextBlock);
        }
        else
        {
            var previous = _focusBeforeConfirm as UIElement;
            _focusBeforeConfirm = null;
            FocusLater(() =>
            {
                if (previous is { IsVisible: true, IsEnabled: true, Focusable: true } && IsAncestorOf(previous)) previous.Focus();
                else CancelButton.Focus();
            });
        }
    }

    /// <summary>Foco en la primera respuesta (la estrella elegida, la primera opción o el cuadro de texto).</summary>
    private void FocusFirstInput()
    {
        var dialog = _dialog;
        FocusLater(() =>
        {
            if (dialog == null || !ReferenceEquals(DataContext, dialog)) return;
            var first = FindFirstInput(QuestionsPanel);
            if (first == null || !first.Focus()) CancelButton.Focus();
        });
    }

    private void FocusLater(Action action)
        => Dispatcher.BeginInvoke(DispatcherPriority.Input, action);

    private static UIElement? FindFirstInput(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Focusable: true, IsEnabled: true } element
                && (child is TextBoxBase || (child is ButtonBase && KeyboardNavigation.GetIsTabStop(element))))
            {
                return element;
            }
            var found = FindFirstInput(child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Un cuadro de texto se "come" la rueda aunque no tenga nada que desplazar: si ya está en su tope en esa dirección
    /// (o no desborda), la rueda desplaza la lista de preguntas.
    /// </summary>
    private void OnQuestionsPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not { } box) return;

        bool canScrollBox = e.Delta > 0
            ? box.VerticalOffset > 0
            : box.VerticalOffset + box.ViewportHeight < box.ExtentHeight - 0.5;
        if (canScrollBox) return;

        e.Handled = true;
        QuestionsScroller.ScrollToVerticalOffset(QuestionsScroller.VerticalOffset - e.Delta * 0.5);
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        var current = start;
        while (current != null)
        {
            if (current is T match) return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
