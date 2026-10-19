using System.Windows.Input;
using PalitoLauncher.Core.Logging;

namespace PalitoLauncher.ViewModels;

/// <summary>
/// Punto común para los errores no controlados dentro de comandos: se registran y, si la App lo configuró, se muestra
/// el diálogo amable de error inesperado.
/// </summary>
public static class CommandErrors
{
    /// <summary>Lo asigna la App (muestra el mensaje genérico con «Crear informe»).</summary>
    public static Action<Exception>? Report { get; set; }

    internal static void Handle(Exception ex, string context)
    {
        Log.Error(context, ex);
        try
        {
            Report?.Invoke(ex);
        }
        catch (Exception inner)
        {
            Log.Error("Error al mostrar el aviso de error.", inner);
        }
    }
}

/// <summary>Comando síncrono. CanExecuteChanged se apoya en CommandManager.RequerySuggested.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
        ArgumentNullException.ThrowIfNull(execute);
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter)
    {
        try
        {
            return _canExecute?.Invoke(parameter) ?? true;
        }
        catch (Exception ex)
        {
            Log.Error("Error al evaluar si un comando se puede ejecutar.", ex);
            return false;
        }
    }

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        try
        {
            _execute(parameter);
        }
        catch (Exception ex)
        {
            CommandErrors.Handle(ex, "Error no controlado en un comando.");
        }
    }

    /// <summary>Pide a WPF que vuelva a consultar CanExecute de todos los comandos.</summary>
    public static void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}

/// <summary>
/// Comando asíncrono con bloqueo de reentrada: mientras se ejecuta, CanExecute devuelve false. Las excepciones se
/// capturan y registran (nunca escapan de Execute).
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
        ArgumentNullException.ThrowIfNull(execute);
    }

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool IsExecuting => _isExecuting;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter)
    {
        if (_isExecuting) return false;
        try
        {
            return _canExecute?.Invoke(parameter) ?? true;
        }
        catch (Exception ex)
        {
            Log.Error("Error al evaluar si un comando se puede ejecutar.", ex);
            return false;
        }
    }

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync(parameter);
        }
        catch (Exception ex)
        {
            // ExecuteAsync ya captura todo; esto es solo una red de seguridad.
            CommandErrors.Handle(ex, "Error no controlado en un comando asíncrono.");
        }
    }

    /// <summary>Ejecuta el comando si se puede. Nunca lanza.</summary>
    public async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        _isExecuting = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await _execute(parameter);
        }
        catch (OperationCanceledException)
        {
            Log.Info("Comando cancelado.");
        }
        catch (Exception ex)
        {
            CommandErrors.Handle(ex, "Error no controlado en un comando asíncrono.");
        }
        finally
        {
            _isExecuting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
