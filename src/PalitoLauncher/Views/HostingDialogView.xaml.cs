using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PalitoLauncher.Helpers;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.Views;

/// <summary>
/// Vista de «Hostear partida». Lógica solo de vista: foco inicial en «Copiar» de la IP en tu red (o en «Cerrar»), alto
/// máximo (~85 % de la ventana, el resto se desplaza) y avisos al lector de pantalla («Copiado…», error de la IP pública).
/// </summary>
public partial class HostingDialogView : UserControl
{
    private const double MaxHeightRatio = 0.85;
    private const double MinMaxHeight = 320;

    private HostingDialogViewModel? _dialog;
    private Window? _window;

    public HostingDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, _) => Attach();
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
        _dialog = DataContext as HostingDialogViewModel;
        if (_dialog == null) return;

        _dialog.PropertyChanged += OnDialogPropertyChanged;
        ContentScroller.ScrollToTop();
        var dialog = _dialog;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!ReferenceEquals(DataContext, dialog)) return;
            if (!(dialog.HasLanAddress && CopyLanButton.Focus())) CloseButton.Focus();
        }));
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
            case nameof(HostingDialogViewModel.CopyStatusText) when dialog.HasCopyStatus:
                LiveRegion.Announce(CopyStatusTextBlock);
                break;
            case nameof(HostingDialogViewModel.HasPublicIpError) when dialog.HasPublicIpError:
                LiveRegion.Announce(PublicIpErrorText);
                break;
        }
    }
}
