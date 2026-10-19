using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PalitoLauncher.Helpers;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.Views;

/// <summary>
/// Vista de «Reportar un problema». Lógica solo de vista (como la encuesta): foco inicial en «¿Qué ha pasado?», foco en
/// «Seguir escribiendo» / «Volver» al pedir confirmación, avisos al lector de pantalla, alto máximo (~80 % de la ventana)
/// y rueda del ratón que pasa al formulario cuando un cuadro de texto ya no puede desplazarse más.
/// </summary>
public partial class BugReportDialogView : UserControl
{
    private const double MaxHeightRatio = 0.8;
    private const double MinMaxHeight = 320;

    private BugReportDialogViewModel? _dialog;
    private Window? _window;
    private IInputElement? _focusBeforeConfirm;

    public BugReportDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, _) => Attach();
        FormScroller.PreviewMouseWheel += OnFormPreviewMouseWheel;
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
        _dialog = DataContext as BugReportDialogViewModel;
        if (_dialog == null) return;

        _dialog.PropertyChanged += OnDialogPropertyChanged;
        FormScroller.ScrollToTop();
        var dialog = _dialog;
        FocusLater(() =>
        {
            if (ReferenceEquals(DataContext, dialog) && !DescriptionBox.Focus()) CancelButton.Focus();
        });
    }

    private void Detach()
    {
        if (_dialog != null) _dialog.PropertyChanged -= OnDialogPropertyChanged;
        _dialog = null;
        if (_window != null) _window.SizeChanged -= OnWindowSizeChanged;
        _window = null;
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => UpdateMaxHeight();

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
            case nameof(BugReportDialogViewModel.IsConfirming):
                OnConfirmingChanged(dialog.IsConfirming);
                break;
            case nameof(BugReportDialogViewModel.ConfirmText) when dialog.IsConfirming:
                LiveRegion.Announce(ConfirmTextBlock);
                break;
            case nameof(BugReportDialogViewModel.HasError) when dialog.HasError:
                LiveRegion.Announce(ErrorText);
                break;
            case nameof(BugReportDialogViewModel.IsSending) when dialog.IsSending:
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

    private void FocusLater(Action action)
        => Dispatcher.BeginInvoke(DispatcherPriority.Input, action);

    /// <summary>Un cuadro de texto en su tope (o sin desbordar) deja que la rueda desplace el formulario.</summary>
    private void OnFormPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not { } box) return;

        bool canScrollBox = e.Delta > 0
            ? box.VerticalOffset > 0
            : box.VerticalOffset + box.ViewportHeight < box.ExtentHeight - 0.5;
        if (canScrollBox) return;

        e.Handled = true;
        FormScroller.ScrollToVerticalOffset(FormScroller.VerticalOffset - e.Delta * 0.5);
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
