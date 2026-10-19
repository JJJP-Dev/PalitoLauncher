using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.Views;

/// <summary>
/// Vista del diálogo "Instalar Coop2D". Foco inicial en Instalar; como al abrir aún se está validando la carpeta (botón
/// deshabilitado), se pone en "Cambiar…" y pasa a Instalar en cuanto termina la primera validación.
/// </summary>
public partial class InstallDialogView : UserControl
{
    private InstallDialogViewModel? _dialog;
    private bool _initialFocusDone;

    public InstallDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (!IsLoaded) return;
        if (ReferenceEquals(_dialog, DataContext)) return;

        Detach();
        _dialog = DataContext as InstallDialogViewModel;
        if (_dialog == null) return;

        _initialFocusDone = false;
        _dialog.PropertyChanged += OnDialogPropertyChanged;
        FocusLater(preferPrimary: true);
    }

    private void Detach()
    {
        if (_dialog != null) _dialog.PropertyChanged -= OnDialogPropertyChanged;
        _dialog = null;
    }

    private void OnDialogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(InstallDialogViewModel.IsValidating) || _initialFocusDone || _dialog?.IsValidating != false) return;
        // Solo si el usuario no ha movido el foco a otro sitio (sigue en "Cambiar…" o en ninguna parte del diálogo).
        if (Keyboard.FocusedElement == null || ReferenceEquals(Keyboard.FocusedElement, ChangeButton))
            FocusLater(preferPrimary: true);
    }

    private void FocusLater(bool preferPrimary)
    {
        var dialog = _dialog;
        // ContextIdle: después de que WPF reevalúe CanExecute (prioridad Background).
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (dialog == null || !ReferenceEquals(DataContext, dialog)) return;
            if (preferPrimary && PrimaryButton.IsEnabled && PrimaryButton.Focus())
            {
                _initialFocusDone = true;
                return;
            }
            if (!ChangeButton.IsKeyboardFocused) ChangeButton.Focus();
        }));
    }
}
