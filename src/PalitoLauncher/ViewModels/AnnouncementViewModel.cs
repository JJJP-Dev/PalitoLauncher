using System.Windows.Input;

namespace PalitoLauncher.ViewModels;

/// <summary>
/// Un anuncio arriba del contenido (launcher.json o el aviso fijo de «tu versión tiene un problema»). Inmutable: si algo
/// cambia, el MainViewModel crea otro.
/// </summary>
public sealed class AnnouncementViewModel : ObservableObject
{
    // Glifos de Segoe Fluent Icons / Segoe MDL2 Assets (los mismos que el aviso de la barra de acción).
    private const string GlyphInfo = "";
    private const string GlyphWarning = "";
    private const string GlyphCritical = "";

    /// <param name="id">Id del anuncio (o un id interno para el aviso de build con problemas).</param>
    /// <param name="kind">Info (azul), Warning (ámbar) o Error (rojo, «crítico»).</param>
    /// <param name="action">Lo que hace el botón (abrir el enlace, actualizar…); null = sin botón.</param>
    /// <param name="dismiss">«✕»; null = no se puede cerrar (los críticos nunca).</param>
    public AnnouncementViewModel(string id, NoticeKind kind, string title, string? message, string? actionText, Action? action,
        Action? dismiss)
    {
        Id = id ?? "";
        Kind = kind;
        Title = title ?? "";
        Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        ActionText = action != null && !string.IsNullOrWhiteSpace(actionText) ? actionText.Trim() : null;
        IsDismissible = dismiss != null && kind != NoticeKind.Error;

        Glyph = kind switch
        {
            NoticeKind.Warning => GlyphWarning,
            NoticeKind.Error => GlyphCritical,
            _ => GlyphInfo,
        };

        var levelName = kind switch
        {
            NoticeKind.Warning => "Aviso",
            NoticeKind.Error => "Importante",
            _ => "Información",
        };
        AutomationName = Message != null ? $"{levelName}: {Title}. {Message}" : $"{levelName}: {Title}";

        ActionCommand = new RelayCommand(() => action?.Invoke(), () => action != null && ActionText != null);
        DismissCommand = new RelayCommand(() => dismiss?.Invoke(), () => IsDismissible);

        // Para no reconstruir la lista (ni mover el foco) si nada visible cambió.
        Key = string.Join("\u001F", Id, kind.ToString(), Title, Message ?? "", ActionText ?? "", IsDismissible ? "1" : "0");
    }

    public string Id { get; }
    public NoticeKind Kind { get; }
    public string Title { get; }
    public string? Message { get; }
    public bool HasMessage => Message != null;

    /// <summary>Texto del botón («Más información», «Actualizar», «Ver versiones»…), o null si no hay botón.</summary>
    public string? ActionText { get; }

    public bool HasAction => ActionText != null;
    public bool IsDismissible { get; }
    public string Glyph { get; }

    /// <summary>Lo que lee el lector de pantalla («Aviso: título. mensaje»).</summary>
    public string AutomationName { get; }

    public ICommand ActionCommand { get; }
    public ICommand DismissCommand { get; }

    internal string Key { get; }
}
