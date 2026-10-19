using System.Windows.Input;
using PalitoLauncher.Services;

namespace PalitoLauncher.ViewModels;

/// <summary>Diálogo de mensaje del overlay: título, texto y hasta tres botones.</summary>
public sealed class MessageDialogViewModel : ObservableObject, IOverlayDialog
{
    private readonly TaskCompletionSource<DialogResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _escapeChoosesSecondary;

    /// <param name="escapeChoosesSecondary">
    /// Esc = botón secundario (lo normal: «Cancelar»). False cuando el secundario es una acción («Reportar el problema»):
    /// entonces Esc cierra sin elegir nada (DialogResult.None).
    /// </param>
    public MessageDialogViewModel(string title, string message, string primaryText, string? secondaryText, bool isDanger,
        string? tertiaryText, bool escapeChoosesSecondary = true)
    {
        Title = title ?? "";
        Message = message ?? "";
        PrimaryText = string.IsNullOrWhiteSpace(primaryText) ? "Aceptar" : primaryText;
        SecondaryText = string.IsNullOrWhiteSpace(secondaryText) ? null : secondaryText;
        TertiaryText = string.IsNullOrWhiteSpace(tertiaryText) ? null : tertiaryText;
        // Sin botón secundario no hay opción "segura" a la que mover el foco.
        IsDanger = isDanger && SecondaryText != null;
        _escapeChoosesSecondary = escapeChoosesSecondary;

        PrimaryCommand = new RelayCommand(() => Close(DialogResult.Primary));
        SecondaryCommand = new RelayCommand(() => Close(DialogResult.Secondary));
        TertiaryCommand = new RelayCommand(() => Close(DialogResult.Tertiary));
    }

    public string Title { get; }
    public string Message { get; }
    public string PrimaryText { get; }
    public string? SecondaryText { get; }
    public string? TertiaryText { get; }
    public bool IsDanger { get; }
    public bool HasSecondary => SecondaryText != null;
    public bool HasTertiary => TertiaryText != null;

    public ICommand PrimaryCommand { get; }
    public ICommand SecondaryCommand { get; }
    public ICommand TertiaryCommand { get; }

    public Task<DialogResult> Completion => _completion.Task;

    public void Cancel() => Close(HasSecondary && _escapeChoosesSecondary ? DialogResult.Secondary : DialogResult.None);

    public void Accept()
    {
        if (!IsDanger) Close(DialogResult.Primary);
    }

    private void Close(DialogResult result) => _completion.TrySetResult(result);
}
