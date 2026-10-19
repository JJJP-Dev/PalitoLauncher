using System.Windows.Documents;
using System.Windows.Input;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Helpers;
using PalitoLauncher.Services;

namespace PalitoLauncher.ViewModels;

/// <summary>Una release jugable en la página "Versiones".</summary>
public sealed class ReleaseItemViewModel : ObservableObject
{
    private bool _isExpanded;
    private FlowDocument? _notesDocument;

    /// <param name="blockedReason">Si launcher.json la marca como build con problemas, el motivo (badge «Con problemas»).</param>
    public ReleaseItemViewModel(GameRelease release, bool isInstalled, bool isLatest,
        Func<GameRelease, Task> install, Func<bool> canInstall, string? blockedReason = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(canInstall);

        Release = release;
        IsInstalled = isInstalled;
        IsLatest = isLatest;
        BlockedReason = blockedReason == null ? null : string.IsNullOrWhiteSpace(blockedReason) ? "Tiene un fallo grave conocido." : blockedReason.Trim();

        var info = release.Release;
        VersionText = string.IsNullOrWhiteSpace(release.VersionDisplay) ? info.TagName : release.VersionDisplay;

        var parts = new List<string>();
        var title = info.DisplayName;
        if (!string.IsNullOrWhiteSpace(title) && !string.Equals(title, VersionText, StringComparison.OrdinalIgnoreCase))
            parts.Add(title);
        var date = UiFormat.Date(info.PublishedAt ?? release.Asset.UpdatedAt);
        if (date.Length > 0) parts.Add(date);
        if (release.Asset.Size > 0) parts.Add(UiFormat.Bytes(release.Asset.Size));
        DetailsText = string.Join(" · ", parts);

        InstallCommand = new AsyncRelayCommand(() => install(Release), () => !IsInstalled && canInstall());
        OpenOnGitHubCommand = new RelayCommand(() => ShellService.OpenUrl(Release.Release.HtmlUrl),
            () => ShellService.IsWebUrl(Release.Release.HtmlUrl));
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public GameRelease Release { get; }

    /// <summary>"0.0.2 · Prototipo 2" (o el tag si no hay versión).</summary>
    public string VersionText { get; }

    /// <summary>"Prototype 2 Test · 19/10/2026 · 70.8 MB".</summary>
    public string DetailsText { get; }

    public bool IsInstalled { get; }
    public bool IsLatest { get; }

    /// <summary>Badge "De prueba": SOLO si GitHub la marca como pre-release (no por llamarse Prototipo).</summary>
    public bool IsPrerelease => Release.Release.IsPrerelease;

    public bool CanShowInstall => !IsInstalled;

    /// <summary>Badge rojo «Con problemas»: launcher.json avisa de un fallo grave en esta build.</summary>
    public bool IsBlocked => BlockedReason != null;

    /// <summary>Motivo del problema (para el jugador), o null.</summary>
    public string? BlockedReason { get; }

    /// <summary>"Problema conocido: se desconecta al entrar en la sala del jefe".</summary>
    public string? BlockedText => BlockedReason != null ? $"Problema conocido: {BlockedReason}" : null;

    /// <summary>Al expandir se generan las notas (perezoso).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (value && _notesDocument == null) NotesDocument = MarkdownRenderer.Render(Release.Release.Body);
        }
    }

    public FlowDocument? NotesDocument
    {
        get => _notesDocument;
        private set => SetProperty(ref _notesDocument, value);
    }

    public ICommand InstallCommand { get; }
    public ICommand OpenOnGitHubCommand { get; }
    public ICommand ToggleExpandedCommand { get; }
}
