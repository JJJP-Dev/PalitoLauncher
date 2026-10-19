using System.IO;
using System.Windows;
using Microsoft.Win32;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.Services;

/// <summary>Botón con el que se cerró un diálogo. None = cerrado con Esc sin botón secundario.</summary>
public enum DialogResult
{
    None,
    Primary,
    Secondary,
    Tertiary,
}

/// <summary>Diálogo que se muestra en el overlay de la ventana (no es una ventana aparte).</summary>
public interface IOverlayDialog
{
    /// <summary>Acción destructiva: el foco inicial va al botón secundario y Enter no acepta.</summary>
    bool IsDanger { get; }

    /// <summary>Esc: equivale al botón secundario.</summary>
    void Cancel();

    /// <summary>Enter (solo si no es IsDanger): equivale al botón primario si está habilitado.</summary>
    void Accept();
}

/// <summary>
/// Diálogos modales dentro de la ventana. Solo se muestra uno a la vez; los siguientes esperan su turno.
/// Usar siempre desde el hilo de UI. No abrir un diálogo desde dentro de otro (se esperarían mutuamente).
/// </summary>
public sealed class DialogService : ObservableObject
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IOverlayDialog? _currentDialog;

    /// <summary>Diálogo visible (null si no hay ninguno).</summary>
    public IOverlayDialog? CurrentDialog
    {
        get => _currentDialog;
        private set
        {
            if (SetProperty(ref _currentDialog, value))
                OnPropertyChanged(nameof(IsOpen));
        }
    }

    public bool IsOpen => _currentDialog != null;

    /// <summary>Muestra un mensaje con hasta tres botones y devuelve el pulsado.</summary>
    public Task<DialogResult> ShowAsync(string title, string message, string primaryText, string? secondaryText = null,
        bool isDanger = false, string? tertiaryText = null)
    {
        var dialog = new MessageDialogViewModel(title, message, primaryText, secondaryText, isDanger, tertiaryText);
        return ShowDialogAsync(dialog, dialog.Completion);
    }

    /// <summary>Muestra un diálogo propio (p.ej. el de instalación) hasta que <paramref name="completion"/> termine.</summary>
    public async Task<T> ShowDialogAsync<T>(IOverlayDialog dialog, Task<T> completion)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(completion);

        await _gate.WaitAsync();
        try
        {
            CurrentDialog = dialog;
            return await completion;
        }
        finally
        {
            CurrentDialog = null;
            _gate.Release();
        }
    }

    /// <summary>Selector de carpetas del sistema. Devuelve null si se cancela o falla. Nunca lanza.</summary>
    public static string? PickFolder(string title, string? initialPath)
    {
        try
        {
            var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
            var initial = NearestExistingDirectory(initialPath);
            if (initial != null) dialog.InitialDirectory = initial;

            var owner = Application.Current?.MainWindow;
            var ok = owner is { IsVisible: true } ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            return ok == true && !string.IsNullOrWhiteSpace(dialog.FolderName) ? dialog.FolderName : null;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo abrir el selector de carpetas.", ex);
            return null;
        }
    }

    private static string? NearestExistingDirectory(string? path)
    {
        try
        {
            var current = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim());
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current)) return current;
                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Ruta inicial del selector no válida ({path}): {ex.Message}");
        }
        return null;
    }
}
