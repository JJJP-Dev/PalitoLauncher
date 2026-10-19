using System.IO;
using System.Windows.Media.Imaging;
using PalitoLauncher.Core;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Settings;
using PalitoLauncher.Core.State;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Core.Versioning;

namespace PalitoLauncher.ViewModels;

// Arranque, consulta de releases, estado del disco, comprobaciones periódicas y eventos del juego.
public sealed partial class MainViewModel
{
    private static readonly TimeSpan PlayGateDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PrePlayCheckTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Carga ajustes, mira el disco, consulta GitHub y decide el estado. Nunca lanza y nunca deja la pantalla en
    /// "CARGANDO…" (cualquier fallo deja un estado utilizable y lo registra).
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            await InitializeCoreAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Error inesperado al inicializar el launcher.", ex);
            _autoOperationError ??= GenericErrorMessage;
        }
        finally
        {
            _installStatus ??= new InstallStatus { State = InstallState.NotInstalled, InstallRoot = _installRoot ?? "" };
            _initialFetchDone = true;
            _playGateOpen = true;
            _initialized = true;
            RebuildReleaseItems();
            UpdateState();
            if (!_disposed) _periodicTimer.Start();
        }

        // En segundo plano: fondo remoto y versión nueva del launcher.
        RunFlow(LoadBackgroundAsync, "Fondo remoto", showErrors: false);
        if (_settings.CheckLauncherUpdates)
            RunFlow(() => CheckLauncherUpdateAsync(force: false), "Buscar actualización del launcher", showErrors: false);

        RunFlow(EvaluateAutoUpdateAsync, "Actualización automática", showErrors: false);
    }

    private async Task InitializeCoreAsync()
    {
        // 1. Ajustes.
        try
        {
            _settings = await Task.Run(() => _settingsStore.Load());
        }
        catch (Exception ex)
        {
            Log.Error("No se pudieron cargar los ajustes; se usan los valores por defecto.", ex);
            _settings = new LauncherSettings();
        }
        _settingsLoaded = true;
        Settings.Reload();

        // 2. Disco: carpeta de instalación, recuperación, estado y juego abierto.
        try
        {
            _installRoot = await Task.Run(ResolveInitialInstallRoot);
            Log.Info(_installRoot != null ? $"Carpeta de instalación: {_installRoot}" : "Aún no hay carpeta de instalación.");
            await RefreshInstallStatusAsync(recover: true);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo comprobar la instalación.", ex);
            // Con carpeta conocida pero ilegible se ofrece REPARAR (el instalador nunca borra contenido ajeno).
            _installStatus = new InstallStatus
            {
                State = _installRoot != null ? InstallState.Broken : InstallState.NotInstalled,
                InstallRoot = _installRoot ?? "",
                GameDir = _installRoot != null ? GameInstaller.GetGameDir(_installRoot) : "",
            };
            _autoOperationError = "No se pudo comprobar la instalación del juego. " + GenericErrorMessage;
        }
        Settings.RefreshInstallInfo(_installRoot, _installStatus);
        UpdateState();

        // 3. GitHub. JUGAR se habilita como muy tarde a los 5 s aunque GitHub no responda.
        RunFlow(OpenPlayGateLaterAsync, "Habilitar JUGAR", showErrors: false);
        try
        {
            await RefreshReleasesAsync(bypassFreshness: false, _lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Cerrando.
        }
    }

    private async Task OpenPlayGateLaterAsync()
    {
        try
        {
            await Task.Delay(PlayGateDelay, _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_playGateOpen) return;
        _playGateOpen = true;
        UpdateState();
    }

    /// <summary>InstallRoot = ajustes ?? (DefaultInstallRoot si ahí hay instalación o una descarga a medias). Hilo de fondo.</summary>
    private string? ResolveInitialInstallRoot()
    {
        if (!string.IsNullOrWhiteSpace(_settings.InstallRoot)) return _settings.InstallRoot;

        var defaultRoot = AppPaths.DefaultInstallRoot;
        try
        {
            if (!Directory.Exists(defaultRoot)) return null;
            var status = _installer.GetStatus(defaultRoot);
            if (status.State != InstallState.NotInstalled || status.HasPendingDownload)
            {
                Log.Info($"Se encontró una instalación en la carpeta por defecto: {defaultRoot}");
                return defaultRoot;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo comprobar la carpeta de instalación por defecto.", ex);
        }
        return null;
    }

    /// <summary>
    /// Vuelve a mirar el disco. Con <paramref name="recover"/> (arranque/reintento) llama antes a Recover y se engancha
    /// al juego si está abierto. Unidad no lista → DriveMissing sin tocar nada. Lanza solo errores inesperados.
    /// </summary>
    private async Task RefreshInstallStatusAsync(bool recover)
    {
        var root = _installRoot;
        if (root == null)
        {
            _installStatus = new InstallStatus { State = InstallState.NotInstalled };
            _pendingResumeBytes = 0;
            Settings.RefreshInstallInfo(null, _installStatus);
            RebuildReleaseItems();
            UpdateState();
            return;
        }

        var latest = Latest;
        bool canRecover = recover && !IsBusy;
        var (status, resumable) = await Task.Run(() =>
        {
            if (!FileSystemUtil.IsDriveReady(root))
            {
                Log.Warn($"La unidad de {root} no está disponible.");
                return (new InstallStatus { State = InstallState.DriveMissing, InstallRoot = root, GameDir = GameInstaller.GetGameDir(root) }, 0L);
            }

            if (canRecover) _installer.Recover(root);
            var s = _installer.GetStatus(root);
            long bytes = 0;
            if (s.HasPendingDownload && latest != null)
            {
                try
                {
                    bytes = _installer.GetResumableBytes(root, latest);
                }
                catch (Exception ex)
                {
                    Log.Warn("No se pudo leer el progreso de la descarga pendiente.", ex);
                }
            }
            return (s, bytes);
        });

        if (!string.Equals(root, _installRoot, StringComparison.OrdinalIgnoreCase)) return; // cambió mientras tanto

        _installStatus = status;
        _pendingResumeBytes = resumable;

        if (recover && status.State == InstallState.Installed && !IsGameRunning && !string.IsNullOrEmpty(status.GameDir))
        {
            var exe = GameExecutableName;
            bool attached = await Task.Run(() => _process.TryAttachToRunning(status.GameDir, exe));
            if (attached)
            {
                Log.Info("El juego ya estaba abierto: se sigue su proceso.");
                IsGameRunning = true;
            }
        }

        Settings.RefreshInstallInfo(_installRoot, _installStatus);
        RebuildReleaseItems();
        UpdateState();
    }

    /// <summary>
    /// Consulta las releases (con la caché/frescura del GitHubClient). Los errores no lanzan: quedan en el aviso
    /// secundario (y en LoadFailed si no hay datos). Devuelve true si se obtuvieron datos (de red o de caché).
    /// Solo lanza OperationCanceledException si se cancela <paramref name="ct"/>.
    /// </summary>
    private async Task<bool> RefreshReleasesAsync(bool bypassFreshness, CancellationToken ct)
    {
        _isFetching = true;
        UpdateState();

        ReleaseFetchResult result;
        try
        {
            result = await _github.GetReleasesAsync(LauncherConfig.GameRepoOwner, LauncherConfig.GameRepoName, ct, bypassFreshness);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _isFetching = false;
            UpdateState();
            throw;
        }
        catch (GitHubRateLimitException ex)
        {
            Log.Warn($"Límite de GitHub sin caché: {ex.Message}");
            return FetchFailed(ex.UserMessage, isRateLimit: true);
        }
        catch (LauncherException ex)
        {
            Log.Warn($"No se pudo consultar GitHub: {ex.Message}");
            return FetchFailed(ex.UserMessage, isRateLimit: false);
        }
        catch (Exception ex)
        {
            Log.Error("Error inesperado al consultar las releases.", ex);
            return FetchFailed("No se pudo consultar GitHub. " + GenericErrorMessage, isRateLimit: false);
        }

        _isFetching = false;
        await ApplyFetchResultAsync(result); // recalcula la lista y el estado
        return true;
    }

    private bool FetchFailed(string message, bool isRateLimit)
    {
        _isFetching = false;
        SetFetchError(message, isRateLimit);
        RebuildReleaseItems();
        UpdateState();
        return false;
    }

    private void SetFetchError(string message, bool isRateLimit)
    {
        _fetchErrorMessage = message;
        _fetchErrorIsRateLimit = isRateLimit;
        // Se conservan los datos que ya hubiera en memoria; solo es "fallo" si no hay nada que mostrar.
        _fetchFailed = _gameReleases == null;
    }

    private async Task ApplyFetchResultAsync(ReleaseFetchResult result)
    {
        _lastFetchResult = result;
        _fetchFailed = false;
        _fetchErrorMessage = null;
        _fetchErrorIsRateLimit = false;
        if (!result.IsStale) _lastSuccessfulFetchUtc = DateTimeOffset.UtcNow;

        _gameReleases = SelectGameReleases(result.Releases);
        Log.Info($"Releases: {result.Releases.Count} en GitHub, {_gameReleases.Count} jugables (origen {result.Source}).");

        await AfterReleasesChangedAsync();
    }

    private IReadOnlyList<GameRelease> SelectGameReleases(IReadOnlyList<ReleaseInfo> releases)
    {
        try
        {
            return ReleaseSelector.GetGameReleases(releases, LauncherConfig.GameAssetPattern, _settings.IncludePrereleases);
        }
        catch (Exception ex)
        {
            Log.Error("Error al seleccionar las builds del juego.", ex);
            return Array.Empty<GameRelease>();
        }
    }

    /// <summary>Tras cambiar la lista de releases: identidad del marcador, pausa de auto-update y bytes reanudables.</summary>
    private async Task AfterReleasesChangedAsync()
    {
        var latest = Latest;
        var root = _installRoot;
        var info = _installStatus?.Info;

        // Release/asset recreados con el mismo contenido → solo se reescribe el marcador.
        if (root != null && info != null && latest != null && !IsBusy
            && _installStatus?.State == InstallState.Installed && IsSameContentDifferentIdentitySafe(info, latest))
        {
            try
            {
                bool updated = await Task.Run(() => _installer.UpdateMarkerIdentity(root, latest));
                if (updated)
                {
                    Log.Info($"Marcador actualizado a la release recreada {latest.Release.TagName} (mismo contenido).");
                    await RefreshInstallStatusAsync(recover: false);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error al actualizar la identidad del marcador.", ex);
            }
        }

        // La pausa de auto-update se borra SOLO cuando aparece una versión aún más nueva que aquella. Si la release pausada
        // no está en la lista (filtrada por "versiones de prueba", borrada o en borrador), la pausa se conserva: no
        // estorba (solo bloquea cuando la última es justo esa) y evita auto-instalar la versión de la que el usuario se bajó.
        var paused = _settings.AutoUpdatePausedForTag;
        if (paused != null && latest != null && !string.Equals(latest.Release.TagName, paused, StringComparison.Ordinal))
        {
            var pausedRelease = FindReleaseByTagSafe(paused);
            if (pausedRelease != null && IsNewerThan(latest, pausedRelease))
            {
                Log.Info($"Hay una versión más nueva que {paused}: se reanuda la actualización automática.");
                _settings.AutoUpdatePausedForTag = null;
                _ = SaveSettingsAsync();
            }
        }

        // Bytes ya descargados de la última (para "CONTINUAR (45 %)").
        if (root != null && latest != null && _installStatus?.HasPendingDownload == true && !IsBusy)
        {
            try
            {
                _pendingResumeBytes = await Task.Run(() => _installer.GetResumableBytes(root, latest));
            }
            catch (Exception ex)
            {
                Log.Warn("No se pudo leer el progreso de la descarga pendiente.", ex);
                _pendingResumeBytes = 0;
            }
        }
        else if (_installStatus?.HasPendingDownload != true)
        {
            _pendingResumeBytes = 0;
        }

        RebuildReleaseItems();
        UpdateState();
    }

    /// <summary>
    /// Busca la release jugable con ese tag: primero en la lista visible y, si no está, en todas las descargadas
    /// (incluidas las de prueba, para que activar/desactivar "versiones de prueba" no cambie la decisión). Nunca lanza.
    /// </summary>
    private GameRelease? FindReleaseByTagSafe(string tag)
    {
        try
        {
            var visible = _gameReleases?.FirstOrDefault(r => string.Equals(r.Release.TagName, tag, StringComparison.Ordinal));
            if (visible != null || _lastFetchResult == null) return visible;
            return ReleaseSelector.GetGameReleases(_lastFetchResult.Releases, LauncherConfig.GameAssetPattern, includePrereleases: true)
                .FirstOrDefault(r => string.Equals(r.Release.TagName, tag, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo buscar la release en pausa.", ex);
            return null;
        }
    }

    /// <summary>¿<paramref name="candidate"/> es más nueva que <paramref name="other"/>? Por versión; sin versión, por fecha.</summary>
    private static bool IsNewerThan(GameRelease candidate, GameRelease other)
    {
        if (candidate.Version != null && other.Version != null)
            return GameVersion.Compare(candidate.Version, other.Version) > 0;

        var a = candidate.Asset.UpdatedAt ?? candidate.Release.PublishedAt;
        var b = other.Asset.UpdatedAt ?? other.Release.PublishedAt;
        return a != null && b != null && a.Value > b.Value;
    }

    private static bool IsSameContentDifferentIdentitySafe(InstalledGameInfo info, GameRelease latest)
    {
        try
        {
            return LauncherStateLogic.IsSameContentDifferentIdentity(info, latest);
        }
        catch (Exception ex)
        {
            Log.Error("Error al comparar la identidad de la release.", ex);
            return false;
        }
    }

    /// <summary>Al cambiar "Incluir versiones de prueba": recalcula la lista con los datos ya descargados.</summary>
    private async Task ReselectReleasesAsync()
    {
        if (_lastFetchResult == null)
        {
            UpdateState();
            return;
        }
        _gameReleases = SelectGameReleases(_lastFetchResult.Releases);
        _lastAttentionAssetId = null;
        await AfterReleasesChangedAsync();
        await EvaluateAutoUpdateAsync();
    }

    /// <summary>Arranca sola la actualización si toca (sin diálogos: los errores van al aviso secundario).</summary>
    private async Task EvaluateAutoUpdateAsync()
    {
        if (!_initialized || _disposed || IsBusy || _primaryActionRunning) return;
        var latest = Latest;
        if (latest == null || latest.Asset.Id == _autoUpdateSuppressedAssetId) return;

        bool should;
        try
        {
            should = LauncherStateLogic.ShouldAutoUpdate(State, _settings.AutoDownloadUpdates, IsGameRunning, latest,
                _settings.AutoUpdatePausedForTag);
        }
        catch (Exception ex)
        {
            Log.Error("Error al decidir la actualización automática.", ex);
            return;
        }
        if (!should) return;

        Log.Info($"Actualización automática a {latest.VersionDisplay} (asset {latest.Asset.Id}).");
        await StartUpdateAsync(automatic: true);
    }

    /// <summary>Comprobación cada LauncherConfig.PeriodicCheckInterval (solo si no molesta y queda cupo de GitHub).</summary>
    private async Task PeriodicCheckAsync()
    {
        if (!_initialized || _disposed || IsBusy || IsOverlayOpen || IsGameRunning || _isFetching || _primaryActionRunning)
        {
            Log.Debug("Comprobación periódica omitida (launcher ocupado).");
            return;
        }

        var remaining = RateLimitRemainingSafe();
        if (remaining is int r && r <= LauncherConfig.RateLimitReserveForUser)
        {
            Log.Info($"Comprobación periódica omitida: quedan {r} consultas a GitHub.");
            return;
        }

        await RefreshReleasesAsync(bypassFreshness: false, _lifetimeCts.Token);
        await EvaluateAutoUpdateAsync();
        if (_settings.CheckLauncherUpdates) await CheckLauncherUpdateAsync(force: false);
    }

    /// <summary>Fondo remoto (raw.githubusercontent.com, sin gastar cupo de la API). Si falla, queda el degradado.</summary>
    private async Task LoadBackgroundAsync()
    {
        string? path;
        try
        {
            path = await _github.GetCachedFileAsync(LauncherConfig.RemoteBackgroundUrl, "background.png", _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo obtener el fondo remoto.", ex);
            return;
        }
        if (string.IsNullOrEmpty(path)) return;

        var image = await Task.Run(() => LoadFrozenBitmap(path));
        if (image != null) BackgroundImage = image;
    }

    private static BitmapImage? LoadFrozenBitmap(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Warn("El fondo descargado no se pudo cargar; se borra la caché y se usa el degradado.", ex);
            FileSystemUtil.TryDeleteFile(path);
            return null;
        }
    }

    /// <summary>
    /// Busca versión nueva del launcher (respeta el intervalo salvo <paramref name="force"/>). Si mientras tanto el usuario
    /// desactivó "Buscar actualizaciones del launcher", el resultado se descarta (no aparece el banner).
    /// </summary>
    private async Task CheckLauncherUpdateAsync(bool force)
    {
        if (_disposed || (!force && !_settings.CheckLauncherUpdates)) return;
        try
        {
            var info = await _selfUpdater.CheckAsync(_lifetimeCts.Token, force);
            // Se vuelve a mirar tras la espera: el ajuste pudo cambiar durante la consulta.
            if (info == null || _disposed || !_settings.CheckLauncherUpdates) return;

            if (!force && string.Equals(info.VersionText, _settings.SkippedLauncherVersion, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"Versión del launcher {info.VersionText} omitida por el usuario.");
                return;
            }

            _launcherUpdate = info;
            LauncherUpdateText = $"Nueva versión del launcher: v{info.VersionText}";
            IsLauncherUpdateVisible = true;
            UpdateState();
        }
        catch (OperationCanceledException)
        {
            // Cerrando.
        }
        catch (Exception ex)
        {
            Log.Error("Error al buscar una versión nueva del launcher.", ex);
        }
    }

    // ───────────── Eventos del proceso del juego (llegan desde hilos del pool) ─────────────

    private void OnGameStarted(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_disposed) return;
                // El evento llega en diferido: se mira el proceso real por si ya se cerró.
                IsGameRunning = IsProcessRunningSafe();
                UpdateState();
            }
            catch (Exception ex)
            {
                Log.Error("Error al procesar el arranque del juego.", ex);
            }
        }));
    }

    private void OnGameExited(object? sender, int exitCode)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            RunFlow(() => HandleGameExitedAsync(exitCode), "Cierre del juego", showErrors: false);
        }));
    }

    private async Task HandleGameExitedAsync(int exitCode)
    {
        Log.Info($"El juego se cerró (código {exitCode}).");
        IsGameRunning = false;

        if (_minimizedByUs)
        {
            _minimizedByUs = false;
            RestoreRequested?.Invoke(this, EventArgs.Empty);
        }

        bool forcedByUs = _suppressNextCrashNotice;
        _suppressNextCrashNotice = false;
        if (exitCode != 0 && exitCode != -1 && !forcedByUs)
        {
            _crashNotice = $"{Game} se cerró de forma inesperada (código {exitCode}).";
            Log.Warn(_crashNotice);
        }

        UpdateState();
        if (!IsBusy) await RefreshInstallStatusAsync(recover: false);
        await EvaluateAutoUpdateAsync();
    }
}
