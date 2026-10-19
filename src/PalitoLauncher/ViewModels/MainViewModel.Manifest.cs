using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using PalitoLauncher.Core;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Remote;
using PalitoLauncher.Core.State;
using PalitoLauncher.Core.Versioning;
using PalitoLauncher.Helpers;
using PalitoLauncher.Services;

namespace PalitoLauncher.ViewModels;

/// <summary>Aviso de una sesión de prueba a punto de empezar: la ventana muestra la notificación de Windows.</summary>
public sealed record PlaytestReminder(string Title, string Message);

// launcher.json (repo del juego): anuncios, builds con problemas, enlaces de comunidad y sesiones de prueba. Y «Qué ha
// cambiado desde tu versión» en la tarjeta de novedades de Inicio. Todo es opcional: sin launcher.json no se ve nada nuevo.
public sealed partial class MainViewModel
{
    /// <summary>Cada cuánto se recalculan anuncios (por si uno empieza o acaba), la cuenta atrás y el aviso de las sesiones.</summary>
    private static readonly TimeSpan ManifestClockInterval = TimeSpan.FromSeconds(30);

    /// <summary>Ids recordados como mucho (anuncios cerrados / sesiones avisadas); se olvidan los más viejos.</summary>
    private const int MaxRememberedIds = 200;

    private const string BlockedAnnouncementId = "palito-blocked-build";

    private readonly ManifestProvider? _manifestProvider;
    private readonly DispatcherTimer _manifestTimer;
    private readonly DispatcherTimer _manifestClockTimer;
    private readonly HashSet<string> _manifestErrorsLogged = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blockedPlayConfirmed = new(StringComparer.OrdinalIgnoreCase);

    private LauncherManifest _manifest = new();
    private bool _manifestLoaded;
    private bool _manifestLoading;
    private string? _manifestSummary;
    private string _announcementsKey = "";
    private PlaytestStatus? _playtestStatus;
    private Action? _playtestVersionAction;

    // «Qué ha cambiado desde tu versión» (caché por referencia de la instalación y de la lista de versiones).
    private InstalledGameInfo? _sinceInfo;
    private IReadOnlyList<GameRelease>? _sinceReleases;
    private IReadOnlyList<GameRelease> _sinceResult = Array.Empty<GameRelease>();
    private IReadOnlyList<GameRelease>? _notesSectionsSource;
    private string? _notesSectionsKey;
    private string _communityLinksKey = "";
    private string _blockedBuildsKey = "";

    private bool _hasAnnouncements;
    private bool _hasCommunityLinks;
    private bool _hasPlaytest;
    private bool _isMultiVersionNotes;

    // ───────────── Propiedades para las vistas ─────────────

    /// <summary>Anuncios apilados arriba del contenido: el aviso fijo de build con problemas y hasta 3 de launcher.json.</summary>
    public ObservableCollection<AnnouncementViewModel> Announcements { get; } = new();

    public bool HasAnnouncements { get => _hasAnnouncements; private set => SetProperty(ref _hasAnnouncements, value); }

    /// <summary>Enlaces de comunidad (botones en Inicio y entradas del menú ⋯).</summary>
    public ObservableCollection<CommunityLinkViewModel> CommunityLinks { get; } = new();

    public bool HasCommunityLinks { get => _hasCommunityLinks; private set => SetProperty(ref _hasCommunityLinks, value); }

    /// <summary>Tarjeta «Próxima sesión de prueba».</summary>
    public PlaytestCardViewModel Playtest { get; }

    public bool HasPlaytest { get => _hasPlaytest; private set => SetProperty(ref _hasPlaytest, value); }

    /// <summary>La tarjeta de novedades muestra las notas de varias versiones («Qué ha cambiado desde tu versión»).</summary>
    public bool IsMultiVersionNotes { get => _isMultiVersionNotes; private set => SetProperty(ref _isMultiVersionNotes, value); }

    /// <summary>Notas de cada versión desde la instalada (de la más nueva a la más vieja).</summary>
    public ObservableCollection<ReleaseNotesSectionViewModel> ReleaseNotesSections { get; } = new();

    /// <summary>Hay que avisar de una sesión de prueba (la ventana muestra la notificación de Windows y parpadea).</summary>
    public event EventHandler<PlaytestReminder>? PlaytestReminderRequested;

    /// <summary>Reloj (UTC) de anuncios y sesiones de prueba. Los tests lo fijan.</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;

    /// <summary>Elige dónde guardar el .ics (nombre propuesto, carpeta inicial). Los tests lo sustituyen.</summary>
    internal Func<string, string?, string?> PickIcsPath { get; set; } = static (fileName, folder) =>
        DialogService.PickSaveFile("Añadir la sesión al calendario", fileName, folder, "Calendario (*.ics)|*.ics", ".ics");

    /// <summary>Muestra el archivo guardado en el Explorador. Los tests lo sustituyen.</summary>
    internal Action<string> RevealFile { get; set; } = static path => ShellService.RevealInExplorer(path);

    // ───────────── Carga ─────────────

    /// <summary>Nunca impide abrir el launcher: si no se puede crear, simplemente no hay anuncios ni sesiones.</summary>
    private static ManifestProvider? CreateManifestProvider(GitHubClient github)
    {
        try
        {
            return new ManifestProvider(github, AppPaths.AppDataDir);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo preparar la lectura de launcher.json.", ex);
            return null;
        }
    }

    /// <summary>Tras arrancar: primera lectura (en segundo plano) y temporizadores.</summary>
    private void StartManifest()
    {
        if (_disposed) return;
        if (_manifestProvider == null) _manifestFirstAttempt.TrySetResult(); // nunca habrá launcher.json: no se espera
        _manifestTimer.Start();
        _manifestClockTimer.Start();
        RunFlow(RefreshManifestAsync, "launcher.json", showErrors: false);
    }

    /// <summary>
    /// Lee launcher.json (raw.githubusercontent.com con caché: no gasta cupo de la API). Nunca muestra diálogos: los
    /// problemas van al log y se sigue con lo último bueno (o nada).
    /// </summary>
    private async Task RefreshManifestAsync()
    {
        var provider = _manifestProvider;
        if (provider == null || _manifestLoading || _disposed) return;
        _manifestLoading = true;
        try
        {
            var token = _lifetimeCts.Token;
            // Todo en el pool (incluida la lectura del override local): la UI nunca espera al disco ni a la red.
            var manifest = await Task.Run(() => provider.GetManifestAsync(token), token);
            if (_disposed) return;
            ApplyManifest(manifest);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Cerrando.
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo leer launcher.json; se mantiene lo anterior.", ex);
        }
        finally
        {
            _manifestLoading = false;
            _manifestFirstAttempt.TrySetResult(); // la bandeja de reportes ya sabe qué webhook usar
        }
    }

    /// <summary>Aplica un manifiesto (ya saneado por el ManifestProvider) y recalcula todo lo que depende de él.</summary>
    internal void ApplyManifest(LauncherManifest? manifest)
    {
        if (_disposed) return;
        _manifest = manifest ?? new LauncherManifest();
        _manifestLoaded = true;

        var summary = ManifestDiagnosticText();
        if (!string.Equals(summary, _manifestSummary, StringComparison.Ordinal))
        {
            _manifestSummary = summary;
            Log.Info($"launcher.json: {summary}.");
        }

        RebuildCommunityLinks();

        // La lista de Versiones solo se rehace si cambiaron las builds con problemas (sus badges); si no, basta con
        // recalcular anuncios y sesión (al releer cada 15 min no se pierde el foco ni la posición en Versiones).
        var blockedKey = string.Join("\u001E", (_manifest.BlockedBuilds ?? new List<BlockedBuild>()).Select(b => $"{b?.Match}|{b?.Reason}"));
        if (!string.Equals(blockedKey, _blockedBuildsKey, StringComparison.Ordinal))
        {
            _blockedBuildsKey = blockedKey;
            RebuildReleaseItems(); // badges «Con problemas» y, al final, anuncios y sesión (RefreshManifestPresentation)
        }
        else
        {
            RefreshManifestPresentation();
        }

        CheckPlaytestReminder();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Anuncios (incluido el de build con problemas) y tarjeta de la sesión. Barato; nunca lanza.</summary>
    private void RefreshManifestPresentation()
    {
        if (_disposed) return;
        try
        {
            RebuildAnnouncements();
            UpdatePlaytest();
        }
        catch (Exception ex)
        {
            LogManifestError("mostrar los datos de launcher.json", ex);
        }
        // Misiones: la versión instalada pudo cambiar (aviso de versión) o el conjunto empezar o terminar.
        try
        {
            if (_missionSet != null) RefreshMissionsCard();
        }
        catch (Exception ex)
        {
            LogManifestError("mostrar los datos de launcher.json", ex);
        }
    }

    /// <summary>Cada 30 s: anuncios que empiezan o acaban, cuenta atrás y aviso de la sesión.</summary>
    internal void OnManifestClockTick()
    {
        if (_disposed) return;
        RefreshManifestPresentation();
        CheckPlaytestReminder();
        OnMissionsClockTick();
    }

    /// <summary>Un mismo fallo de la lógica del manifiesto se registra una vez (el reloj lo repetiría cada 30 s).</summary>
    private void LogManifestError(string what, Exception ex)
    {
        if (_manifestErrorsLogged.Add(what)) Log.Error($"Error al {what} (launcher.json).", ex);
    }

    // ───────────── Anuncios ─────────────

    private void RebuildAnnouncements()
    {
        var list = new List<AnnouncementViewModel>();

        var blocked = BlockedBuildAnnouncement();
        if (blocked != null) list.Add(blocked);

        foreach (var announcement in ActiveAnnouncementsSafe())
        {
            var id = announcement.Id;
            var url = announcement.Url;
            bool hasLink = CommunityLinkViewModel.IsHttps(url);
            bool dismissible = announcement.Dismissible && announcement.Level != AnnouncementLevel.Critical;
            list.Add(new AnnouncementViewModel(
                id,
                announcement.Level switch
                {
                    AnnouncementLevel.Critical => NoticeKind.Error,
                    AnnouncementLevel.Warning => NoticeKind.Warning,
                    _ => NoticeKind.Info,
                },
                announcement.Title,
                announcement.Message,
                hasLink ? (string.IsNullOrWhiteSpace(announcement.LinkText) ? "Más información" : announcement.LinkText) : null,
                hasLink ? () => OpenExternalUrl(url) : null,
                dismissible ? () => DismissAnnouncement(id) : null));
        }

        var key = string.Join("\u001E", list.Select(a => a.Key));
        if (string.Equals(key, _announcementsKey, StringComparison.Ordinal)) return;
        _announcementsKey = key;

        Announcements.Clear();
        foreach (var item in list) Announcements.Add(item);
        HasAnnouncements = Announcements.Count > 0;
    }

    private IReadOnlyList<Announcement> ActiveAnnouncementsSafe()
    {
        if (_manifest.Announcements is not { Count: > 0 }) return Array.Empty<Announcement>();
        try
        {
            var dismissed = (IReadOnlyCollection<string>?)_settings.DismissedAnnouncementIds ?? Array.Empty<string>();
            return ManifestLogic.GetActiveAnnouncements(_manifest, UtcNow(), dismissed);
        }
        catch (Exception ex)
        {
            LogManifestError("calcular los anuncios activos", ex);
            return Array.Empty<Announcement>();
        }
    }

    /// <summary>«✕» de un anuncio: no se vuelve a mostrar (los críticos no se pueden cerrar).</summary>
    internal void DismissAnnouncement(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var announcement = _manifest.Announcements?.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
        if (announcement == null || announcement.Level == AnnouncementLevel.Critical || !announcement.Dismissible) return;

        RememberId(_settings.DismissedAnnouncementIds ??= new List<string>(), id);
        _ = SaveSettingsAsync();
        Log.Info($"Anuncio «{id}» cerrado por el usuario.");
        RebuildAnnouncements();
    }

    /// <summary>
    /// Añade el id al final (el más reciente) sin repetir (sin distinguir mayúsculas, como ManifestLogic) y olvida los más
    /// viejos por encima del máximo.
    /// </summary>
    private static void RememberId(List<string> ids, string id)
    {
        ids.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        ids.Add(id);
        if (ids.Count > MaxRememberedIds) ids.RemoveRange(0, ids.Count - MaxRememberedIds);
    }

    private void OpenExternalUrl(string? url)
    {
        if (!CommunityLinkViewModel.IsHttps(url) || !ShellService.OpenUrl(url)) ShowToast("No se pudo abrir el navegador.");
    }

    // ───────────── Builds con problemas ─────────────

    /// <summary>La versión instalada (si está instalada) aparece en BlockedBuilds.</summary>
    private BlockedBuild? FindInstalledBlockSafe()
    {
        var info = _installStatus?.State == InstallState.Installed ? _installStatus.Info : null;
        return info == null ? null : FindBlockSafe(info);
    }

    private BlockedBuild? FindBlockSafe(InstalledGameInfo info)
    {
        if (_manifest.BlockedBuilds is not { Count: > 0 }) return null;
        try
        {
            return ManifestLogic.FindBlock(_manifest, info);
        }
        catch (Exception ex)
        {
            LogManifestError("comprobar si la versión instalada tiene problemas", ex);
            return null;
        }
    }

    private BlockedBuild? FindBlockSafe(GameRelease release)
    {
        if (_manifest.BlockedBuilds is not { Count: > 0 }) return null;
        try
        {
            return ManifestLogic.FindBlock(_manifest, release);
        }
        catch (Exception ex)
        {
            LogManifestError("comprobar si una versión tiene problemas", ex);
            return null;
        }
    }

    /// <summary>
    /// Aviso crítico fijo «Tu versión (X) tiene un problema: motivo» con «Actualizar» (a la versión más nueva sin
    /// problemas) o «Ver versiones». No cambia el estado ni la auto-actualización: solo avisa.
    /// </summary>
    private AnnouncementViewModel? BlockedBuildAnnouncement()
    {
        var info = _installStatus?.State == InstallState.Installed ? _installStatus.Info : null;
        if (info == null) return null;
        var block = FindBlockSafe(info);
        if (block == null) return null;

        var reason = string.IsNullOrWhiteSpace(block.Reason) ? "tiene un fallo grave conocido." : block.Reason.Trim();
        var title = $"Tu versión ({DisplayOf(info)}) tiene un problema: {reason}";
        var target = NewerUnblockedRelease(info);
        if (target != null)
        {
            var release = target;
            return new AnnouncementViewModel(BlockedAnnouncementId, NoticeKind.Error, title,
                $"Actualiza a {release.VersionDisplay} para evitarlo.", "Actualizar", () => InstallOrUpdateTo(release, "Actualizar desde el aviso"),
                dismiss: null);
        }
        return new AnnouncementViewModel(BlockedAnnouncementId, NoticeKind.Error, title,
            "En Versiones puedes instalar otra versión.", "Ver versiones", () => Navigate(AppPage.Versions), dismiss: null);
    }

    /// <summary>La versión más nueva que la instalada y sin problemas conocidos (la primera de la lista), o null.</summary>
    private GameRelease? NewerUnblockedRelease(InstalledGameInfo info)
    {
        var releases = _gameReleases;
        if (releases is not { Count: > 0 }) return null;
        GameVersion? installed;
        try
        {
            installed = info.ParsedVersion;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo leer la versión instalada.", ex);
            return null;
        }
        if (installed == null) return null;

        foreach (var release in releases)
        {
            if (release.Version == null || release.Version <= installed) continue;
            if (FindBlockSafe(release) == null) return release;
        }
        return null;
    }

    /// <summary>
    /// Instala <paramref name="release"/>: si es la última y hay algo instalado más viejo, como ACTUALIZAR; si no, como
    /// «Instalar esta versión» de Versiones (con su diálogo de confirmación).
    /// </summary>
    private void InstallOrUpdateTo(GameRelease release, string context)
    {
        var info = _installStatus?.State == InstallState.Installed ? _installStatus.Info : null;
        var latest = Latest;
        bool isUpdateToLatest = info != null && latest != null && latest.Asset.Id == release.Asset.Id
                                && GetUpdateKindSafe(info, latest) != UpdateKind.None;
        if (isUpdateToLatest)
            RunFlow(() => StartUpdateAsync(automatic: false), context, showErrors: true, guardPrimary: true);
        else
            _ = InstallSpecificReleaseAsync(release);
    }

    /// <summary>
    /// Antes de abrir el juego: si la versión instalada tiene un problema conocido, pregunta «¿Jugar igualmente?»
    /// (una vez por sesión del launcher y build). True = se puede jugar.
    /// </summary>
    private async Task<bool> ConfirmPlayBlockedBuildAsync(InstalledGameInfo? info)
    {
        if (info == null) return true;
        var block = FindBlockSafe(info);
        if (block == null) return true;

        var key = $"{info.Tag}|{info.AssetId}|{block.Match}";
        if (_blockedPlayConfirmed.Contains(key)) return true;

        var reason = string.IsNullOrWhiteSpace(block.Reason) ? "un fallo grave." : block.Reason.Trim();
        var message = $"Esta versión tiene un problema conocido: {reason}";
        if (NewerUnblockedRelease(info) is { } newer) message += $"\n\nTe recomendamos actualizar a {newer.VersionDisplay}.";
        message += "\n\n¿Jugar igualmente?";

        var answer = await Dialogs.ShowAsync("Versión con problemas", message, "Jugar igualmente", "Cancelar", isDanger: true);
        if (answer != DialogResult.Primary)
        {
            Log.Info($"El usuario no abrió la versión con problemas ({info.Tag}).");
            return false;
        }
        _blockedPlayConfirmed.Add(key);
        Log.Info($"El usuario abre igualmente la versión con problemas ({info.Tag}).");
        return true;
    }

    // ───────────── Enlaces de comunidad ─────────────

    private void RebuildCommunityLinks()
    {
        var links = (_manifest.Links ?? new List<CommunityLink>())
            .Where(l => l != null && !string.IsNullOrWhiteSpace(l.Label) && CommunityLinkViewModel.IsHttps(l.Url))
            .ToList();

        // Al releer launcher.json cada 15 min no se rehacen los botones (ni se pierde el foco) si nada cambió.
        var key = string.Join("\u001E", links.Select(l => $"{l.Label}|{l.Icon}|{l.Url}"));
        if (string.Equals(key, _communityLinksKey, StringComparison.Ordinal) && CommunityLinks.Count == links.Count) return;
        _communityLinksKey = key;

        CommunityLinks.Clear();
        foreach (var link in links) CommunityLinks.Add(new CommunityLinkViewModel(link.Label, link.Icon, link.Url, OpenExternalUrl));
        HasCommunityLinks = CommunityLinks.Count > 0;
    }

    // ───────────── Sesiones de prueba ─────────────

    private bool CanRunPlaytestVersionAction() => _playtestVersionAction != null && _initialized && !IsBusy && !_primaryActionRunning;

    private void RunPlaytestVersionAction() => _playtestVersionAction?.Invoke();

    /// <summary>Recalcula la tarjeta (cuenta atrás, estado de la versión…) con la hora actual.</summary>
    private void UpdatePlaytest()
    {
        PlaytestStatus? status = null;
        if (_manifest.Playtests is { Count: > 0 })
        {
            try
            {
                status = ManifestLogic.GetNextPlaytest(_manifest, UtcNow());
            }
            catch (Exception ex)
            {
                LogManifestError("calcular la próxima sesión de prueba", ex);
            }
        }

        _playtestStatus = status;
        if (status == null)
        {
            _playtestVersionAction = null;
            Playtest.Clear();
            HasPlaytest = false;
            return;
        }

        var playtest = status.Playtest;
        Playtest.Id = playtest.Id;
        Playtest.Title = playtest.Title;
        Playtest.IsOngoing = status.IsOngoing;
        Playtest.HeaderText = status.IsOngoing ? "Sesión de prueba en curso" : "Próxima sesión de prueba";
        Playtest.WhenText = UiFormat.SessionWhen(playtest.StartsUtc, status.EndsUtc);
        Playtest.CountdownText = status.IsOngoing
            ? $"En curso · termina a las {UiFormat.Time(status.EndsUtc)}"
            : StartsInText(status.UntilStart);
        Playtest.IsSoon = status.IsOngoing || status.UntilStart <= ManifestLogic.PlaytestReminderLead;
        Playtest.Description = playtest.Description;
        Playtest.HasDiscord = CommunityLinkViewModel.IsHttps(playtest.DiscordUrl);
        UpdatePlaytestVersion(playtest);
        Playtest.IsVisible = true;
        HasPlaytest = true;
    }

    /// <summary>"Empieza en 3 h 12 min" (con el texto de ManifestLogic.FormatCountdown).</summary>
    private string StartsInText(TimeSpan untilStart)
    {
        string countdown;
        try
        {
            countdown = ManifestLogic.FormatCountdown(untilStart);
        }
        catch (Exception ex)
        {
            LogManifestError("formatear la cuenta atrás", ex);
            countdown = "";
        }
        if (string.IsNullOrWhiteSpace(countdown)) return "Empieza pronto";
        return countdown.StartsWith("en ", StringComparison.OrdinalIgnoreCase) ? "Empieza " + countdown : countdown;
    }

    /// <summary>✓ «Tienes la versión correcta» o «Necesitas X» con «Actualizar» / «Instalar esa versión».</summary>
    private void UpdatePlaytestVersion(Playtest playtest)
    {
        _playtestVersionAction = null;
        Playtest.VersionActionText = null;
        Playtest.VersionHint = null;

        var required = playtest.RequiredVersion?.Trim();
        if (string.IsNullOrEmpty(required))
        {
            Playtest.HasVersionInfo = false;
            return;
        }

        var info = _installStatus?.State == InstallState.Installed ? _installStatus.Info : null;
        bool? installedOk = null;
        GameRelease? release = null;
        try
        {
            installedOk = ManifestLogic.IsRequiredBuildInstalled(playtest, info);
            release = ManifestLogic.FindRequiredRelease(playtest, _gameReleases ?? Array.Empty<GameRelease>());
        }
        catch (Exception ex)
        {
            LogManifestError("comprobar la versión que pide la sesión", ex);
        }

        Playtest.HasVersionInfo = true;
        if (installedOk == true)
        {
            Playtest.IsVersionOk = true;
            Playtest.VersionText = "Tienes la versión correcta";
            return;
        }

        Playtest.IsVersionOk = false;
        Playtest.VersionText = $"Necesitas {release?.VersionDisplay ?? required}";
        if (release == null)
        {
            Playtest.VersionHint = "Aún no aparece en tu lista de versiones. Si es una versión de prueba, actívalas en Ajustes › Actualizaciones.";
            return;
        }

        var latest = Latest;
        bool isUpdate = info != null && latest != null && latest.Asset.Id == release.Asset.Id && GetUpdateKindSafe(info, latest) != UpdateKind.None;
        Playtest.VersionActionText = isUpdate ? "Actualizar" : "Instalar esa versión";
        var target = release;
        _playtestVersionAction = () => InstallOrUpdateTo(target, "Versión de la sesión de prueba");
    }

    /// <summary>
    /// Si toca (ajuste activado, 15 min antes o recién empezada, y aún no avisada): notificación de Windows (la pide a la
    /// ventana), toast en la app y se recuerda que ya se avisó.
    /// </summary>
    internal void CheckPlaytestReminder()
    {
        var status = _playtestStatus;
        if (status == null || _disposed || !_settings.PlaytestRemindersEnabled) return;

        bool remind;
        try
        {
            remind = ManifestLogic.ShouldRemind(status, (IReadOnlyCollection<string>?)_settings.RemindedPlaytestIds ?? Array.Empty<string>());
        }
        catch (Exception ex)
        {
            LogManifestError("decidir si avisar de la sesión de prueba", ex);
            return;
        }
        if (!remind) return;

        var playtest = status.Playtest;
        RememberId(_settings.RemindedPlaytestIds ??= new List<string>(), playtest.Id);
        _ = SaveSettingsAsync();

        string title;
        string message;
        if (status.IsOngoing)
        {
            title = "¡La sesión de prueba ha empezado!";
            message = $"«{playtest.Title}» ya ha empezado (termina a las {UiFormat.Time(status.EndsUtc)}).";
        }
        else
        {
            title = $"Sesión de prueba {StartsInText(status.UntilStart).Replace("Empieza ", "", StringComparison.Ordinal)}";
            message = $"«{playtest.Title}» empieza a las {UiFormat.Time(playtest.StartsUtc)}.";
        }
        if (Playtest.HasVersionInfo && !Playtest.IsVersionOk) message += $" {Playtest.VersionText}: instálala desde el launcher.";

        Log.Info($"Aviso de la sesión de prueba «{playtest.Id}».");
        ShowToast(status.IsOngoing ? message : $"{title}. {message}");
        try
        {
            PlaytestReminderRequested?.Invoke(this, new PlaytestReminder(title, message));
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo mostrar el aviso de la sesión de prueba.", ex);
        }
    }

    /// <summary>«Añadir al calendario»: guarda un .ics (por defecto en Descargas) y lo muestra en el Explorador.</summary>
    private async Task AddPlaytestToCalendarAsync()
    {
        var playtest = _playtestStatus?.Playtest;
        if (playtest == null) return;

        string ics;
        try
        {
            ics = ManifestLogic.BuildIcs(playtest, Game, UtcNow());
        }
        catch (Exception ex)
        {
            LogManifestError("crear el archivo del calendario", ex);
            ShowToast("No se pudo crear el archivo del calendario.");
            return;
        }

        var path = PickIcsPath(SafeFileName($"{Game} - {playtest.Title}") + ".ics", DefaultDownloadsFolder());
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            await Task.Run(() => File.WriteAllText(path, ics, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo guardar el archivo del calendario en {path}.", ex);
            await ShowErrorAsync("No se pudo guardar el archivo. Prueba a guardarlo en otra carpeta (por ejemplo, Descargas).",
                "No se pudo guardar", offerReport: false);
            return;
        }

        Log.Info($"Sesión «{playtest.Id}» guardada para el calendario: {path}");
        ShowToast("Listo. Abre el archivo para añadir la sesión a tu calendario.");
        RevealFile(path);
    }

    private void JoinPlaytestDiscord() => OpenExternalUrl(_playtestStatus?.Playtest.DiscordUrl);

    /// <summary>Carpeta Descargas (o el Escritorio si no se puede saber).</summary>
    private static string? DefaultDownloadsFolder()
    {
        var downloads = NativeMethods.TryGetDownloadsFolder();
        if (!string.IsNullOrWhiteSpace(downloads)) return downloads;
        try
        {
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(fallback) ? fallback : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Nombre de archivo válido en Windows (sin \ / : * ? " &lt; &gt; | ni controles), máx. 100 caracteres.</summary>
    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 || char.IsControl(c) ? '-' : c);
        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length > 100) result = result[..100].TrimEnd('.', ' ');
        return result.Length > 0 ? result : LauncherConfig.GameDisplayName;
    }

    // ───────────── «Qué ha cambiado desde tu versión» ─────────────

    /// <summary>
    /// Con una actualización disponible, las versiones más nuevas que la instalada (de ManifestLogic.GetReleasesSince).
    /// Sin actualización, vacío. Se calcula solo cuando cambian la instalación o la lista (UpdateState se llama mucho).
    /// </summary>
    private IReadOnlyList<GameRelease> ReleasesSinceInstalled(InstalledGameInfo? info, GameRelease? latest)
    {
        var releases = _gameReleases;
        if (info == null || latest == null || releases is not { Count: > 0 }) return Array.Empty<GameRelease>();
        if (ReferenceEquals(info, _sinceInfo) && ReferenceEquals(releases, _sinceReleases)) return _sinceResult;

        IReadOnlyList<GameRelease> result = Array.Empty<GameRelease>();
        if (GetUpdateKindSafe(info, latest) != UpdateKind.None)
        {
            try
            {
                result = ManifestLogic.GetReleasesSince(info, releases);
            }
            catch (Exception ex)
            {
                LogManifestError("calcular las versiones nuevas desde la instalada", ex);
            }
        }

        _sinceInfo = info;
        _sinceReleases = releases;
        _sinceResult = result;
        return result;
    }

    /// <summary>Título y secciones de la tarjeta de novedades cuando te has saltado 2 o más versiones. Devuelve si aplica.</summary>
    private bool UpdateMultiVersionNotes(InstalledGameInfo? info, GameRelease? latest)
    {
        var since = ReleasesSinceInstalled(info, latest);
        if (info == null || since.Count < 2)
        {
            IsMultiVersionNotes = false;
            return false;
        }

        NotesTitle = $"Qué ha cambiado desde tu versión ({DisplayOf(info)} → {since[0].VersionDisplay})";

        // UpdateState se llama con cada tick de progreso: la misma lista (misma referencia, ver ReleasesSinceInstalled) no
        // se vuelve a comparar; una lista nueva solo regenera las notas si cambió algo visible.
        if (ReferenceEquals(since, _notesSectionsSource))
        {
            IsMultiVersionNotes = true;
            return true;
        }
        _notesSectionsSource = since;

        var key = string.Join("\u001E", since.Select(r =>
            $"{r.Release.Id}|{r.Asset.Id}|{r.VersionDisplay}|{r.Release.PublishedAt?.ToUnixTimeSeconds()}|{r.Release.Body}"));
        if (!string.Equals(key, _notesSectionsKey, StringComparison.Ordinal))
        {
            _notesSectionsKey = key;
            ReleaseNotesSections.Clear();
            for (int i = 0; i < since.Count; i++)
            {
                var release = since[i];
                ReleaseNotesSections.Add(new ReleaseNotesSectionViewModel(release.VersionDisplay,
                    UiFormat.Date(release.Release.PublishedAt ?? release.Asset.UpdatedAt),
                    MarkdownRenderer.Render(release.Release.Body), showSeparator: i > 0));
            }
        }

        IsMultiVersionNotes = true;
        return true;
    }

    // ───────────── Informe ─────────────

    /// <summary>"2 anuncios, 1 build con problemas, 3 enlaces, 1 sesión" (o "(no leído)").</summary>
    private string ManifestDiagnosticText()
    {
        if (!_manifestLoaded) return _manifestProvider == null ? "(no disponible)" : "(no leído)";
        var m = _manifest;
        return $"{m.Announcements?.Count ?? 0} anuncio(s), {m.BlockedBuilds?.Count ?? 0} build(s) con problemas, " +
               $"{m.Links?.Count ?? 0} enlace(s), {m.Playtests?.Count ?? 0} sesión(es), " +
               $"webhook de reportes: {(string.IsNullOrWhiteSpace(m.BugReportWebhookUrl) ? "no" : "sí")}";
    }
}
