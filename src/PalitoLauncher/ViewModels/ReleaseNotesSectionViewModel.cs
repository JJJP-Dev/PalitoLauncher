using System.Windows.Documents;

namespace PalitoLauncher.ViewModels;

/// <summary>Notas de una versión dentro de «Qué ha cambiado desde tu versión» (tarjeta de novedades de Inicio).</summary>
public sealed class ReleaseNotesSectionViewModel : ObservableObject
{
    public ReleaseNotesSectionViewModel(string title, string? dateText, FlowDocument document, bool showSeparator)
    {
        Title = title ?? "";
        DateText = string.IsNullOrWhiteSpace(dateText) ? null : dateText;
        Document = document ?? throw new ArgumentNullException(nameof(document));
        ShowSeparator = showSeparator;
    }

    /// <summary>"0.0.4 · Prototipo 3".</summary>
    public string Title { get; }

    /// <summary>"19/10/2026" o null.</summary>
    public string? DateText { get; }

    public bool HasDate => DateText != null;
    public FlowDocument Document { get; }

    /// <summary>Línea encima (todas menos la primera).</summary>
    public bool ShowSeparator { get; }
}
