using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Services;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher;

/// <summary>
/// Ventana principal sin borde del sistema (WindowChrome). Aquí solo hay lógica de vista: tamaño inicial, botones de
/// ventana, barra de tareas, foco/teclado del overlay de diálogos y confirmación al cerrar.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly Thickness MaximizedMargin = new(7);

    private readonly MainViewModel _vm;
    private bool _forceClose;
    private bool _closeFlowRunning;
    private IInputElement? _savedFocus;
    private bool _hasSavedFocus;

    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _vm;

        ApplyInitialSize();
        StateChanged += (_, _) => UpdateWindowStateVisuals();
        Activated += (_, _) => NativeMethods.StopFlashing(new WindowInteropHelper(this).Handle);
        PreviewKeyDown += OnPreviewKeyDown;

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.Dialogs.PropertyChanged += OnDialogsPropertyChanged;
        _vm.MinimizeRequested += OnMinimizeRequested;
        _vm.RestoreRequested += OnRestoreRequested;
        _vm.AttentionRequested += OnAttentionRequested;
        _vm.CloseRequested += OnCloseRequested;

        UpdateWindowStateVisuals();
        UpdateTaskbar();
    }

    /// <summary>Trae la ventana al frente (otra instancia pidió mostrarse).</summary>
    public void BringToFront()
    {
        try
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            // Truco habitual para ganar el primer plano.
            Topmost = true;
            Topmost = false;
            Focus();
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo traer la ventana al frente.", ex);
        }
    }

    /// <summary>1120×680 limitado al 95 % del área de trabajo (mínimo 900×540 si cabe), centrada.</summary>
    private void ApplyInitialSize()
    {
        var work = SystemParameters.WorkArea;
        double maxWidth = Math.Max(320, work.Width * 0.95);
        double maxHeight = Math.Max(240, work.Height * 0.95);
        MinWidth = Math.Min(900, maxWidth);
        MinHeight = Math.Min(540, maxHeight);
        Width = Math.Min(1120, maxWidth);
        Height = Math.Min(680, maxHeight);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void UpdateWindowStateVisuals()
    {
        bool maximized = WindowState == WindowState.Maximized;
        // Con WindowChrome la ventana maximizada se sale unos píxeles por cada lado: se compensa.
        RootBorder.Margin = maximized ? MaximizedMargin : new Thickness(0);
        RootBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);

        MaximizeButton.Content = maximized ? "" : "";
        var label = maximized ? "Restaurar" : "Maximizar";
        MaximizeButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, label);
    }

    // ───────────── Botones de la barra de título ─────────────

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ───────────── Menú ⋯ ─────────────

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not ContextMenu menu) return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    // ───────────── Eventos del ViewModel ─────────────

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.TaskbarProgressState) or nameof(MainViewModel.TaskbarProgressValue))
            UpdateTaskbar();
    }

    private void UpdateTaskbar()
    {
        try
        {
            TaskbarInfo.ProgressState = _vm.TaskbarProgressState;
            TaskbarInfo.ProgressValue = Math.Clamp(_vm.TaskbarProgressValue, 0, 1);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo actualizar el progreso de la barra de tareas.", ex);
        }
    }

    private void OnMinimizeRequested(object? sender, EventArgs e) => WindowState = WindowState.Minimized;

    private void OnRestoreRequested(object? sender, EventArgs e) => BringToFront();

    private void OnAttentionRequested(object? sender, EventArgs e)
    {
        if (!IsActive) NativeMethods.FlashTaskbar(new WindowInteropHelper(this).Handle);
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        _forceClose = true;
        Close();
    }

    // ───────────── Overlay de diálogos: foco y teclado ─────────────

    private void OnDialogsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DialogService.CurrentDialog)) return;

        if (_vm.Dialogs.CurrentDialog != null)
        {
            // Se guarda el foco solo al pasar de "sin diálogo" a "con diálogo" (los encadenados no lo pisan).
            if (!_hasSavedFocus)
            {
                _savedFocus = Keyboard.FocusedElement;
                _hasSavedFocus = true;
            }
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_vm.Dialogs.CurrentDialog != null) return; // se abrió otro diálogo justo después
            var target = _savedFocus as UIElement;
            _savedFocus = null;
            _hasSavedFocus = false;
            if (target is { IsVisible: true, IsEnabled: true, Focusable: true }) target.Focus();
            else PrimaryActionButton.Focus();
        }));
    }

    /// <summary>
    /// Esc = botón secundario; Enter = primario (solo si no es peligroso y el foco no está en un botón). Las repeticiones
    /// de tecla (Enter/Esc mantenidos) se ignoran para no aceptar o cancelar también el diálogo siguiente.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.Dialogs.CurrentDialog is not IOverlayDialog dialog) return;

        if (e.IsRepeat && e.Key is Key.Escape or Key.Enter)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            dialog.Cancel();
        }
        else if (e.Key == Key.Enter && !dialog.IsDanger)
        {
            // Un botón con el foco ya responde a Enter por sí mismo.
            if (Keyboard.FocusedElement is ButtonBase) return;
            e.Handled = true;
            dialog.Accept();
        }
    }

    // ───────────── Cierre ─────────────

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _forceClose || App.IsExiting) return;

        CloseDecision decision;
        try
        {
            decision = _vm.GetCloseDecision();
        }
        catch (Exception ex)
        {
            Log.Error("Error al decidir si se puede cerrar la ventana.", ex);
            return;
        }
        if (decision == CloseDecision.Allow) return;

        e.Cancel = true;
        if (_closeFlowRunning) return;
        _closeFlowRunning = true;
        _ = HandleCloseRequestAsync(decision);
    }

    private async Task HandleCloseRequestAsync(CloseDecision decision)
    {
        try
        {
            if (decision == CloseDecision.MustWait)
            {
                _vm.CloseWhenOperationEnds(); // el ViewModel pedirá cerrar al terminar
                return;
            }

            if (await _vm.ConfirmCloseDuringOperationAsync())
            {
                _forceClose = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Error al cerrar la ventana.", ex);
        }
        finally
        {
            _closeFlowRunning = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.Dialogs.PropertyChanged -= OnDialogsPropertyChanged;
        _vm.MinimizeRequested -= OnMinimizeRequested;
        _vm.RestoreRequested -= OnRestoreRequested;
        _vm.AttentionRequested -= OnAttentionRequested;
        _vm.CloseRequested -= OnCloseRequested;
        base.OnClosed(e);
    }
}
