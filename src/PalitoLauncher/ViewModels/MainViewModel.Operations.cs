using System.Globalization;
using System.IO;
using System.Windows.Input;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Diagnostics;
using PalitoLauncher.Core.Downloads;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Launch;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Settings;
using PalitoLauncher.Core.State;
using PalitoLauncher.Core.Update;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Helpers;
using PalitoLauncher.Services;

namespace PalitoLauncher.ViewModels;

// Acciones del usuario y operaciones largas (instalar, actualizar, reparar, mover, desinstalar, jugar, informes…).
public sealed partial class MainViewModel
{
    private bool _suppressNextCrashNotice;

    // ───────────── Ejecución segura de flujos ─────────────

    /// <summary>Lanza un flujo async sin esperarlo; nunca deja escapar excepciones (se registran y, si procede, se muestran).</summary>
    internal void RunFlow(Func<Task> flow, string context, bool showErrors = true, bool guardPrimary = false)
        => _ = RunFlowCoreAsync(flow, context, showErrors, guardPrimary);

    private void RunAction(Func<Task>? action, string context)
    {
        if (action != null) RunFlow(action, context, showErrors: true, guardPrimary: true);
    }

    /// <summary>Envoltorio para comandos async: los errores esperables se muestran con su mensaje.</summary>
    private Task Guarded(Func<Task> flow, string context) => RunFlowCoreAsync(flow, context, showErrors: true, guardPrimary: false);

    private async Task RunFlowCoreAsync(Func<Task> flow, string context, bool showErrors, bool guardPrimary)
    {
        if (guardPrimary)
        {
            if (_primaryActionRunning) return;
            _primaryActionRunning = true;
            UpdateState();
        }

        try
        {
            await flow();
        }
        catch (OperationCanceledException)
        {
            Log.Info($"{context}: cancelado.");
        }
        catch (Exception ex)
        {
            if (showErrors)
            {
                await HandleExceptionAsync(ex, context, automatic: false);
            }
            else if (ex is LauncherException)
            {
                Log.Warn($"{context}: {ex.Message}", ex);
            }
            else
            {
                Log.Error($"{context}: error inesperado.", ex);
            }
        }
        finally
        {
            if (guardPrimary)
            {
                _primaryActionRunning = false;
                UpdateState();
            }
        }
    }

    // ───────────── Botón principal ─────────────

    private void ExecutePrimary()
    {
        var state = State;
        if (state == LauncherState.Busy)
        {
            PauseOrCancel();
            return;
        }

        Func<Task>? flow = state switch
        {
            LauncherState.DriveMissing => RetryAfterDriveMissingAsync,
            LauncherState.LoadFailed => RetryLoadAsync,
            LauncherState.NotInstalled => HasPendingDownloadForLatest() ? ContinueFirstInstallAsync : StartFirstInstallAsync,
            LauncherState.UpdateAvailable => () => StartUpdateAsync(automatic: false),
            LauncherState.Broken => () => RepairAsync(confirm: false),
            LauncherState.Ready => PlayAsync,
            _ => null,
        };

        if (flow != null) RunFlow(flow, $"Botón principal ({state})", showErrors: true, guardPrimary: true);
    }

    private bool HasPendingDownloadForLatest()
        => _installRoot != null && Latest != null && _installStatus?.HasPendingDownload == true && _pendingResumeBytes > 0;

    private async Task RetryAfterDriveMissingAsync()
    {
        await RefreshInstallStatusAsync(recover: true);
        if (_installStatus?.State == InstallState.DriveMissing)
        {
            ShowToast($"La unidad {DriveOf(_installRoot)} sigue sin estar disponible. Conéctala o elige otra carpeta.");
            return;
        }
        if (_gameReleases == null) await RefreshReleasesAsync(bypassFreshness: false, _lifetimeCts.Token);
        await EvaluateAutoUpdateAsync();
    }

    /// <summary>
    /// Salida del estado DriveMissing: abre el diálogo de instalación (proponiendo la carpeta por defecto, porque la unidad
    /// guardada no está) e instala la última versión ahí. La carpeta nueva se guarda en los ajustes ANTES de instalar.
    /// Nunca se instala en otro sitio sin que el usuario lo elija.
    /// </summary>
    private async Task ChooseAnotherFolderAsync()
    {
        if (IsBusy)
        {
            ShowToast(BusyMessage);
            return;
        }

        if (Latest == null) await RefreshReleasesAsync(bypassFreshness: false, _lifetimeCts.Token);
        var target = Latest;
        if (target == null)
        {
            await ShowErrorAsync(
                "Para instalar el juego hace falta la lista de versiones de GitHub y ahora no se puede obtener. Revisa tu conexión y vuelve a intentarlo.",
                "No se puede instalar ahora", offerReport: false);
            return;
        }

        var result = await ShowInstallDialogAsync(target, preferDefault: true);
        if (result == null) return;
        await BeginInstallToRootAsync(result, target, OperationKind.Install);
    }

    /// <summary>
    /// Salida del estado DriveMissing: deja de usar la carpeta guardada (no borra nada) y vuelve a mirar como en el
    /// arranque (la carpeta por defecto si ahí hay juego; si no, "No instalado" → INSTALAR).
    /// </summary>
    private async Task ForgetInstallFolderAsync()
    {
        var root = _installRoot;
        if (root == null || _installStatus?.State != InstallState.DriveMissing) return;
        if (IsBusy)
        {
            ShowToast(BusyMessage);
            return;
        }

        var answer = await Dialogs.ShowAsync("Olvidar esta carpeta",
            $"El launcher dejará de buscar {Game} en {root}. No se borra nada: si vuelves a conectar la unidad, podrás elegir esa carpeta otra vez al instalar.",
            "Olvidar", "Cancelar");
        if (answer != DialogResult.Primary) return;
        if (IsBusy || !string.Equals(_installRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            ShowToast(BusyMessage);
            return;
        }

        _settings.InstallRoot = null;
        await SaveSettingsAsync();
        _installRoot = await Task.Run(ResolveInitialInstallRoot);
        Log.Info($"El usuario olvidó la carpeta {root}. Carpeta ahora: {_installRoot ?? "(ninguna)"}.");
        await RefreshInstallStatusAsync(recover: true);

        ShowToast(_installRoot != null
            ? $"Se usará la instalación de {_installRoot}."
            : $"Listo. Pulsa INSTALAR para elegir dónde instalar {Game}.");
        await EvaluateAutoUpdateAsync();
    }

    private async Task RetryLoadAsync()
    {
        bool ok = await RefreshReleasesAsync(bypassFreshness: false, _lifetimeCts.Token);
        if (!ok) ShowToast("Sigue sin poder obtenerse la lista de versiones.");
        await EvaluateAutoUpdateAsync();
    }

    private void PauseOrCancel()
    {
        var cts = _operationCts;
        if (cts == null || _cancelRequested) return;
        if (_operationKind != OperationKind.LauncherUpdate && _currentProgress is { CanCancel: false }) return;
        CancelCurrentOperation(_currentProgress?.Phase == InstallPhase.Downloading ? "Pausa pedida por el usuario" : "Cancelación pedida por el usuario");
    }

    /// <summary>Cancela la operación en curso (la descarga queda en pausa). No espera. Nunca lanza.</summary>
    public void CancelCurrentOperation(string reason)
    {
        var cts = _operationCts;
        if (cts == null) return;
        Log.Info($"Cancelando la operación en curso: {reason}.");
        _cancelRequested = true;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ya terminó.
        }
        catch (Exception ex)
        {
            Log.Warn("Error al cancelar la operación.", ex);
        }
        UpdateState();
    }

    // ───────────── Instalar / actualizar / reparar ─────────────

    private async Task StartFirstInstallAsync()
    {
        var latest = Latest;
        if (latest == null) return;
        var result = await ShowInstallDialogAsync(latest);
        if (result == null) return;
        await BeginInstallToRootAsync(result, latest, OperationKind.Install);
    }

    private Task ContinueFirstInstallAsync()
    {
        var latest = Latest;
        var root = _installRoot;
        // Es la última: al terminar bien, la auto-actualización queda sin pausa (como al instalar desde el diálogo).
        if (latest != null && root != null)
            StartInstallOperation(root, latest, OperationKind.Install, automatic: false, onInstalled: () => ApplyAutoUpdatePause(null));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Diálogo de ubicación. Propone la carpeta guardada si su unidad está lista (salvo <paramref name="preferDefault"/>,
    /// p.ej. al salir de DriveMissing) y si no AppPaths.DefaultInstallRoot.
    /// </summary>
    private async Task<InstallDialogResult?> ShowInstallDialogAsync(GameRelease target, bool preferDefault = false)
    {
        var proposed = AppPaths.DefaultInstallRoot;
        var saved = _settings.InstallRoot;
        if (!preferDefault && !string.IsNullOrWhiteSpace(saved) && await Task.Run(() => FileSystemUtil.IsDriveReady(saved)))
            proposed = saved;

        var dialog = new InstallDialogViewModel(target, proposed, root => _installer.GetStatus(root), showShortcutOption: true);
        dialog.StartValidation();
        return await Dialogs.ShowDialogAsync(dialog, dialog.Completion);
    }

    /// <summary>
    /// Guarda la carpeta elegida ANTES de instalar. Si ya contiene exactamente la build pedida, la adopta sin descargar;
    /// si contiene otra versión, instala encima la pedida. La pausa de auto-update (instalar una que no es la última) se
    /// aplica solo cuando la instalación termina bien.
    /// </summary>
    private async Task BeginInstallToRootAsync(InstallDialogResult result, GameRelease target, OperationKind kind)
    {
        if (IsBusy)
        {
            ShowToast(BusyMessage);
            return;
        }

        var root = result.InstallRoot;
        var latest = Latest;
        string? pauseTag = latest == null || target.Asset.Id == latest.Asset.Id ? null : latest.Release.TagName;
        _settings.InstallRoot = root;
        await SaveSettingsAsync();
        _installRoot = root;
        Log.Info($"Carpeta de instalación elegida: {root}");

        if (result.CreateDesktopShortcut) await CreateDesktopShortcutCoreAsync(interactive: false);

        if (result.AdoptExisting)
        {
            await RefreshInstallStatusAsync(recover: true);
            if (_installStatus?.State == InstallState.Installed && InstallDialogViewModel.IsSameBuild(_installStatus.Info, target))
            {
                Log.Info($"Se adopta la instalación existente en {root}.");
                ApplyAutoUpdatePause(pauseTag);
                ShowToast($"Se encontró {Game} ya instalado en esa carpeta: se usará esa instalación.");
                await EvaluateAutoUpdateAsync();
                return;
            }
            Log.Info("La instalación de la carpeta elegida ya no es la build pedida: se instala.");
        }

        if (IsBusy)
        {
            ShowToast(BusyMessage);
            return;
        }
        StartInstallOperation(root, target, kind, automatic: false, onInstalled: () => ApplyAutoUpdatePause(pauseTag));
    }

    /// <summary>
    /// Tras instalar con éxito una versión desde Versiones (o la primera vez): si no era la última, se guarda el tag de la
    /// última para que la auto-actualización no la reemplace; si era la última, se quita la pausa.
    /// </summary>
    private void ApplyAutoUpdatePause(string? latestTagToPause)
    {
        if (string.Equals(_settings.AutoUpdatePausedForTag, latestTagToPause, StringComparison.Ordinal)) return;
        _settings.AutoUpdatePausedForTag = latestTagToPause;
        _ = SaveSettingsAsync();
        Log.Info(latestTagToPause != null
            ? $"Actualización automática en pausa hasta que salga una versión más nueva que {latestTagToPause}."
            : "Actualización automática reanudada.");
    }

    /// <summary>Actualiza a la última (manual o automática).</summary>
    private async Task StartUpdateAsync(bool automatic)
    {
        var latest = Latest;
        var root = _installRoot;
        if (latest == null || root == null) return;
        if (IsBusy)
        {
            if (!automatic) ShowToast(BusyMessage);
            return;
        }

        if (!automatic)
        {
            if (!await EnsureGameClosedAsync(GameInstaller.GetGameDir(root))) return;
            _autoUpdateSuppressedAssetId = null;
            if (IsBusy)
            {
                ShowToast(BusyMessage);
                return;
            }
        }

        if (_settings.AutoUpdatePausedForTag != null)
        {
            _settings.AutoUpdatePausedForTag = null;
            _ = SaveSettingsAsync();
        }
        StartInstallOperation(root, latest, OperationKind.Update, automatic);
    }

    /// <summary>Reinstala SelectRepairTarget. Sin releases, refresca; si sigue sin datos, error y sigue Broken.</summary>
    private async Task RepairAsync(bool confirm)
    {
        var root = _installRoot;
        if (root == null) return;
        if (IsBusy)
        {
            ShowToast(BusyMessage);
            return;
        }

        if (_gameReleases is not { Count: > 0 })
            await RefreshReleasesAsync(bypassFreshness: true, _lifetimeCts.Token);

        GameRelease? target = null;
        if (_gameReleases is { Count: > 0 } releases)
        {
            try
            {
                target = LauncherStateLogic.SelectRepairTarget(_installStatus?.Info, releases);
            }
            catch (Exception ex)
            {
                Log.Error("Error al elegir la versión a reparar.", ex);
                target = releases[0];
            }
        }

        if (target == null)
        {
            await ShowErrorAsync(_fetchErrorMessage ?? "No se pudo obtener la lista de versiones de GitHub. Revisa tu conexión y vuelve a intentarlo.",
                "No se puede reparar ahora", offerReport: false);
            return;
        }

        if (confirm)
        {
            var answer = await Dialogs.ShowAsync($"Reparar {Game}",
                $"Se volverá a instalar {target.VersionDisplay}. Tus partidas guardadas se conservan.", "Reparar", "Cancelar");
            if (answer != DialogResult.Primary) return;
        }

        if (!await EnsureGameClosedAsync(GameInstaller.GetGameDir(root))) return;
        if (IsBusy)
        {
            ShowToast(BusyMessage);
            return;
        }
        StartInstallOperation(root, target, OperationKind.Repair, automatic: false);
    }

    /// <summary>"Instalar esta versión" desde la página Versiones.</summary>
    private Task InstallSpecificReleaseAsync(GameRelease release)
        => RunFlowCoreAsync(() => InstallSpecificReleaseCoreAsync(release), "Instalar otra versión", showErrors: true, guardPrimary: true);

    private async Task InstallSpecificReleaseCoreAsync(GameRelease release)
    {
        if (IsBusy)
        {
            ShowToast(BusyMessage);
            return;
        }

        var latest = Latest;
        bool isLatest = latest != null && latest.Asset.Id == release.Asset.Id;
        var status = _installStatus;

        if (status?.State == InstallState.DriveMissing)
        {
            // Se ofrece instalar en otra carpeta (solo si el usuario lo elige; nunca en otro sitio por su cuenta).
            var choice = await Dialogs.ShowAsync("Unidad no disponible",
                $"No se encuentra la unidad {DriveOf(_installRoot)}, donde estaba instalado {Game}. ¿Quieres instalar {release.VersionDisplay} en otra carpeta?",
                "Elegir carpeta…", "Cancelar");
            if (choice != DialogResult.Primary) return;

            var picked = await ShowInstallDialogAsync(release, preferDefault: true);
            if (picked == null) return;
            await BeginInstallToRootAsync(picked, release, isLatest ? OperationKind.Install : OperationKind.InstallOther);
            return;
        }

        // Sin instalación: mismo diálogo de ubicación que INSTALAR.
        if (_installRoot == null || status == null || status.State == InstallState.NotInstalled)
        {
            var result = await ShowInstallDialogAsync(release);
            if (result == null) return;
            await BeginInstallToRootAsync(result, release, isLatest ? OperationKind.Install : OperationKind.InstallOther);
            return;
        }

        var root = _installRoot;
        var info = status.Info;
        var message = info != null
            ? $"Se reemplazará la versión instalada ({DisplayOf(info)}) por {release.VersionDisplay}. Tus partidas guardadas se conservan."
            : $"Se instalará {release.VersionDisplay}. Tus partidas guardadas se conservan.";

        bool isOlder = false;
        try
        {
            isOlder = info?.ParsedVersion is { } installedVersion && release.Version is { } releaseVersion && releaseVersion < installedVersion;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudieron comparar las versiones.", ex);
        }

        if (isOlder)
            message += "\n\nOjo: es más antigua que la versión instalada. Para jugar en cooperativo todos tenéis que usar la misma versión.";
        if (!isLatest)
            message += "\n\nMientras uses esta versión no se actualizará sola (hasta que salga otra más nueva).";

        var answer = await Dialogs.ShowAsync($"Instalar {release.VersionDisplay}", message, "Instalar", "Cancelar");
        if (answer != DialogResult.Primary) return;

        if (!await EnsureGameClosedAsync(GameInstaller.GetGameDir(root))) return;
        if (IsBusy || _installRoot != root)
        {
            ShowToast(BusyMessage);
            return;
        }

        // La pausa de auto-update se guarda solo si esta instalación termina bien (cancelar o fallar no la cambia).
        string? pauseTag = isLatest ? null : latest?.Release.TagName;
        StartInstallOperation(root, release, isLatest ? OperationKind.Update : OperationKind.InstallOther, automatic: false,
            onInstalled: () => ApplyAutoUpdatePause(pauseTag));
    }

    private void StartInstallOperation(string root, GameRelease release, OperationKind kind, bool automatic, Action? onInstalled = null)
        => StartOperation(kind, automatic, release.Asset.Id, ct => InstallBodyAsync(root, release, kind, onInstalled, ct));

    /// <summary>
    /// Instala con el GameInstaller y, si termina bien, ejecuta <paramref name="onInstalled"/> (hilo de UI). Si la build
    /// cambió en GitHub (RemoteAssetChangedException o DownloadVerificationException, detectadas por tipo), refresca las
    /// releases una vez y reintenta solo si la build original ya no está publicada igual y hay otra de la misma versión
    /// (ver <see cref="FindReplacement"/>); si no, muestra el error.
    /// </summary>
    private async Task InstallBodyAsync(string root, GameRelease release, OperationKind kind, Action? onInstalled, CancellationToken ct)
    {
        var progress = new Progress<InstallProgress>(OnInstallProgress);
        var options = new InstallOptions
        {
            MaxBytesPerSecond = Math.Max(0, _settings.MaxDownloadSpeedKBps) * 1024L,
            KeepDownloadedArchive = _settings.KeepDownloadedArchives,
        };

        InstalledGameInfo info;
        try
        {
            info = await _installer.InstallAsync(root, release, options, progress, ct);
        }
        catch (Exception ex) when (IsRemoteAssetChange(ex) && !ct.IsCancellationRequested)
        {
            Log.Warn($"La build cambió en GitHub durante la instalación: {ex.Message}");
            _currentProgress = new InstallProgress(InstallPhase.Preparing, null, "La build cambió en GitHub. Buscando la nueva…");
            UpdateState();

            await RefreshReleasesAsync(bypassFreshness: true, ct);
            var replacement = FindReplacement(release);
            if (replacement == null)
            {
                Log.Warn("No hay una build nueva de la misma versión: se muestra el error.");
                throw;
            }

            Log.Info($"Reintentando con la build nueva {replacement.Asset.Name} (asset {replacement.Asset.Id}).");
            release = replacement;
            info = await _installer.InstallAsync(root, release, options, progress, ct);
        }

        onInstalled?.Invoke();
        OnInstallSucceeded(kind, info);
    }

    /// <summary>
    /// ¿El fallo indica que lo descargado no coincide con lo publicado (build cambiada en GitHub o datos caducados)?
    /// Por tipo, nunca por el texto del mensaje (también si viene envuelta en otra excepción).
    /// </summary>
    private static bool IsRemoteAssetChange(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is RemoteAssetChangedException or DownloadVerificationException) return true;
        }
        return false;
    }

    /// <summary>
    /// Build de reemplazo tras un fallo de verificación de <paramref name="old"/>, con la lista ya refrescada. Solo hay
    /// reemplazo si la build cambió de verdad en GitHub: si el asset original sigue publicado con el mismo tamaño y
    /// SHA-256, el fallo es local (red, antivirus…) y se devuelve null para mostrar el error. Si no, se busca otra build
    /// de la misma versión (o mismo tag si no hay versión): primero la de la misma release; si no, una publicada o
    /// actualizada DESPUÉS de la original (nunca una más antigua de la misma versión).
    /// </summary>
    private GameRelease? FindReplacement(GameRelease old)
    {
        var releases = _gameReleases;
        if (releases == null) return null;

        // El asset original sigue publicado igual → la build no cambió.
        if (releases.Any(r => r.Asset.Id == old.Asset.Id && r.Asset.Size == old.Asset.Size && SameSha(r.Asset, old.Asset)))
            return null;

        var candidates = releases.Where(r =>
        {
            bool sameVersion = old.Version != null
                ? r.Version != null && r.Version == old.Version
                : string.Equals(r.Release.TagName, old.Release.TagName, StringComparison.Ordinal);
            // Mismo zip re-subido con otro id: volvería a fallar igual.
            bool identical = r.Asset.Size == old.Asset.Size && !string.IsNullOrWhiteSpace(old.Asset.Sha256) && SameSha(r.Asset, old.Asset);
            return sameVersion && !identical;
        }).ToList();

        var sameRelease = candidates.FirstOrDefault(r => r.Release.Id == old.Release.Id);
        if (sameRelease != null) return sameRelease;

        var oldDate = LatestDate(old);
        if (oldDate == null) return null; // sin fechas no se puede saber cuál es más nueva
        return candidates.FirstOrDefault(r => LatestDate(r) is { } d && d > oldDate.Value);
    }

    private static bool SameSha(ReleaseAsset a, ReleaseAsset b)
        => string.Equals(a.Sha256?.Trim() ?? "", b.Sha256?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? LatestDate(GameRelease r)
    {
        var a = r.Asset.UpdatedAt;
        var b = r.Release.PublishedAt;
        if (a == null) return b;
        if (b == null) return a;
        return a.Value >= b.Value ? a : b;
    }

    private void OnInstallProgress(InstallProgress progress)
    {
        // Progress<T> llega en diferido: ignorar avisos que lleguen después de terminar.
        if (!IsBusy || _operationKind == OperationKind.LauncherUpdate) return;
        _currentProgress = progress;
        UpdateState();
    }

    private void OnInstallSucceeded(OperationKind kind, InstalledGameInfo info)
    {
        _playWithoutUpdate = false;
        _autoOperationError = null;
        var version = DisplayOf(info);
        var message = kind switch
        {
            OperationKind.Update => $"{Game} actualizado a {version}",
            OperationKind.Repair => $"{Game} reparado ({version})",
            OperationKind.InstallOther => $"{Game} {version} instalado",
            _ => $"{Game} {version} instalado. ¡A jugar!",
        };
        Log.Info(message);
        ShowToast(message);
    }

    // ───────────── Operación en curso (una sola) ─────────────

    private bool StartOperation(OperationKind kind, bool automatic, long? targetAssetId, Func<CancellationToken, Task> body)
    {
        if (IsBusy || _disposed)
        {
            if (!automatic && !_disposed) ShowToast(BusyMessage);
            return false;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _operationCts = cts;
        _operationKind = kind;
        _cancelRequested = false;
        _currentProgress = new InstallProgress(InstallPhase.Preparing, null, "Preparando…");
        _launcherDownloadProgress = null;
        if (!automatic) _autoOperationError = null;
        IsBusy = true;
        Log.Info($"Operación {kind} iniciada{(automatic ? " (automática)" : "")}.");
        UpdateState();

        _operationTask = RunOperationCoreAsync(kind, automatic, targetAssetId, body, cts);
        return true;
    }

    private async Task RunOperationCoreAsync(OperationKind kind, bool automatic, long? targetAssetId,
        Func<CancellationToken, Task> body, CancellationTokenSource cts)
    {
        Exception? failure = null;
        bool cancelled = false;
        try
        {
            await body(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        var lastPhase = _currentProgress?.Phase;
        _operationCts = null;
        _operationKind = OperationKind.None;
        _currentProgress = null;
        _launcherDownloadProgress = null;
        _cancelRequested = false;
        IsBusy = false;
        cts.Dispose();

        // La auto-actualización no relanza sola (en esta sesión) una operación automática que falló o se pausó, ni nada
        // que el usuario haya pausado/cancelado: evita bucles y respeta la pausa. Se levanta al pulsar ACTUALIZAR/CONTINUAR.
        if (targetAssetId != null && (automatic || cancelled))
            _autoUpdateSuppressedAssetId = automatic ? targetAssetId : Latest?.Asset.Id ?? targetAssetId;

        if (cancelled)
        {
            bool paused = lastPhase == InstallPhase.Downloading && kind != OperationKind.LauncherUpdate;
            Log.Info($"Operación {kind} {(paused ? "en pausa" : "cancelada")}.");
            if (!_disposed) ShowToast(paused ? BuildPausedToast(kind, targetAssetId) : "Operación cancelada.");
        }
        else if (failure == null)
        {
            Log.Info($"Operación {kind} terminada.");
        }

        if (!_disposed)
        {
            try
            {
                await RefreshInstallStatusAsync(recover: false);
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo comprobar la instalación tras la operación.", ex);
            }
        }
        UpdateState();

        if (_closeWhenOperationEnds && !_disposed)
        {
            _closeWhenOperationEnds = false;
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (failure != null) await HandleExceptionAsync(failure, $"Operación {kind}", automatic);
        else if (!cancelled) await EvaluateAutoUpdateAsync();
    }

    /// <summary>
    /// Texto al pausar una descarga. "Pulsa CONTINUAR" solo si ese botón va a aparecer (instalar/actualizar la última);
    /// en los demás casos se dice cómo seguir de verdad.
    /// </summary>
    private string BuildPausedToast(OperationKind kind, long? targetAssetId)
    {
        bool offersContinue = kind is OperationKind.Install or OperationKind.Update
                              && targetAssetId != null && targetAssetId == Latest?.Asset.Id;
        if (offersContinue) return "Descarga en pausa. Pulsa CONTINUAR cuando quieras.";
        return kind == OperationKind.Repair
            ? "Descarga en pausa. Vuelve a pulsar «Reparar» para continuar."
            : "Descarga en pausa. Vuelve a pulsar «Instalar esta versión» en Versiones para continuar.";
    }

    // ───────────── Jugar ─────────────

    private async Task PlayAsync()
    {
        if (State != LauncherState.Ready) return;

        if (!_playWithoutUpdate && NeedsPrePlayCheck())
        {
            _statusOverride = "Comprobando versión…";
            UpdateState();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                timeout.CancelAfter(PrePlayCheckTimeout);
                try
                {
                    await RefreshReleasesAsync(bypassFreshness: false, timeout.Token);
                }
                catch (OperationCanceledException) when (!_lifetimeCts.IsCancellationRequested)
                {
                    Log.Info("Comprobación previa sin respuesta en 4 s: se abre el juego igualmente.");
                }
            }
            finally
            {
                _statusOverride = null;
                UpdateState();
            }

            if (State == LauncherState.UpdateAvailable)
            {
                ShowToast("Hay una versión nueva: actualiza para jugar con tus amigos");
                return;
            }
            if (State != LauncherState.Ready) return;
        }

        await LaunchGameAsync();
    }

    private bool NeedsPrePlayCheck()
        => _lastSuccessfulFetchUtc is not { } last || DateTimeOffset.UtcNow - last > LauncherConfig.PrePlayCheckMaxAge;

    private async Task PlayWithoutUpdateAsync()
    {
        if (State != LauncherState.UpdateAvailable) return;
        if (!_coopWarningShown)
        {
            var answer = await Dialogs.ShowAsync("Jugar sin actualizar",
                "Para jugar en cooperativo todos tenéis que usar la misma versión. Si tus amigos ya actualizaron, no podréis jugar juntos.\n\n¿Jugar igualmente con la versión instalada?",
                "Jugar igualmente", "Cancelar");
            if (answer != DialogResult.Primary) return;
            _coopWarningShown = true;
        }
        if (State != LauncherState.UpdateAvailable) return;
        _playWithoutUpdate = true;
        await LaunchGameAsync();
    }

    private async Task LaunchGameAsync()
    {
        var status = _installStatus;
        if (status?.State != InstallState.Installed || string.IsNullOrEmpty(status.GameDir) || IsGameRunning) return;

        var exe = GameExecutableName;
        var arguments = _settings.LaunchArguments;
        try
        {
            await Task.Run(() => _process.Launch(status.GameDir, exe, arguments));
        }
        catch (LauncherException)
        {
            await RefreshInstallStatusAsync(recover: false); // p.ej. falta el exe → Broken
            throw;
        }

        Log.Info($"Juego lanzado ({(status.Info != null ? DisplayOf(status.Info) : exe)}).");
        _crashNotice = null;
        IsGameRunning = IsProcessRunningSafe();
        UpdateState();
        if (!IsGameRunning) return; // se cerró al instante (el aviso llega por GameExited)

        switch (_settings.AfterLaunch)
        {
            case AfterLaunchBehavior.Minimize:
                _minimizedByUs = true;
                MinimizeRequested?.Invoke(this, EventArgs.Empty);
                break;
            case AfterLaunchBehavior.Close:
                Log.Info("Se cierra el launcher tras abrir el juego (según los ajustes).");
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private bool IsProcessRunningSafe()
    {
        try
        {
            return _process.IsRunning;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo comprobar si el juego sigue abierto.", ex);
            return true;
        }
    }

    private async Task ForceCloseGameAsync()
    {
        var answer = await Dialogs.ShowAsync("Forzar cierre",
            $"¿Cerrar {Game} a la fuerza? Se perderá lo que no hayas guardado.", "Forzar cierre", "Cancelar", isDanger: true);
        if (answer != DialogResult.Primary) return;

        var gameDir = !string.IsNullOrEmpty(_installStatus?.GameDir)
            ? _installStatus!.GameDir
            : _installRoot != null ? GameInstaller.GetGameDir(_installRoot) : null;
        if (string.IsNullOrEmpty(gameDir)) return;

        await KillGameAsync(gameDir, GameExecutableName);
        Log.Info("Juego cerrado a la fuerza por el usuario.");
    }

    /// <summary>
    /// Cierra el juego a la fuerza. Solo marca "no avisar de cierre inesperado" si va a morir el proceso SEGUIDO (el único
    /// que dispara GameExited, que es quien consume la marca): matar uno no seguido (abierto desde el Explorador) no debe
    /// ocultar el aviso de un cierre real posterior. Si Kill falla y el seguido sigue vivo, se quita la marca.
    /// </summary>
    private async Task KillGameAsync(string gameDir, string exe)
    {
        bool killsTracked = IsTrackedGameAlive();
        if (killsTracked) _suppressNextCrashNotice = true;
        try
        {
            await Task.Run(() => _process.Kill(gameDir, exe));
        }
        catch
        {
            if (killsTracked && IsTrackedGameAlive()) _suppressNextCrashNotice = false;
            throw;
        }
    }

    /// <summary>¿Sigue vivo el proceso que sigue el GameProcessManager? False si no se puede saber.</summary>
    private bool IsTrackedGameAlive()
    {
        try
        {
            return _process.IsRunning;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo comprobar si el juego seguido sigue abierto.", ex);
            return false;
        }
    }

    /// <summary>
    /// Antes de instalar/reparar/desinstalar/mover: si el juego está abierto desde esa carpeta, pregunta
    /// [Forzar cierre y continuar] [Cancelar]. True si se puede continuar.
    /// </summary>
    private async Task<bool> EnsureGameClosedAsync(string gameDir)
    {
        var exe = GameExecutableName;
        bool running = IsGameRunning || await Task.Run(() => GameProcessManager.IsRunningFrom(gameDir, exe));
        if (!running) return true;

        var answer = await Dialogs.ShowAsync($"{Game} está abierto",
            "Para continuar hay que cerrar el juego. Si estás en una partida, guarda y sal antes.",
            "Forzar cierre y continuar", "Cancelar", isDanger: true);
        if (answer != DialogResult.Primary) return false;

        await KillGameAsync(gameDir, exe);
        Log.Info("Juego cerrado a la fuerza para continuar con una operación.");
        return true;
    }

    // ───────────── Desinstalar / mover / descargas ─────────────

    private async Task UninstallAsync()
    {
        var root = _installRoot;
        if (root == null) return;
        if (IsBusy)
        {
            ShowToast(BusyMessage);
            return;
        }

        var gameDir = GameInstaller.GetGameDir(root);
        long size = await Task.Run(() => Directory.Exists(gameDir) ? FileSystemUtil.GetDirectorySize(gameDir) : 0L);
        var sizeText = size > 0 ? $" ({UiFormat.Bytes(size)})" : "";

        var answer = await Dialogs.ShowAsync($"Desinstalar {Game}",
            $"Se borrará {Game} de {root}{sizeText}. Tus partidas guardadas se conservan.", "Desinstalar", "Cancelar", isDanger: true);
        if (answer != DialogResult.Primary) return;

        if (!await EnsureGameClosedAsync(gameDir)) return;
        if (IsBusy || _installRoot != root)
        {
            ShowToast(BusyMessage);
            return;
        }

        StartOperation(OperationKind.Uninstall, automatic: false, null, async ct =>
        {
            var progress = new Progress<InstallProgress>(OnInstallProgress);
            await _installer.UninstallAsync(root, progress, ct);

            _settings.InstallRoot = null;
            await SaveSettingsAsync();
            _installRoot = null;
            _installStatus = new InstallStatus { State = InstallState.NotInstalled };
            _pendingResumeBytes = 0;
            Log.Info($"{Game} desinstalado de {root}.");
            ShowToast($"{Game} se ha desinstalado. Tus partidas guardadas se conservan.");
        });
    }

    private async Task MoveInstallAsync()
    {
        var root = _installRoot;
        if (root == null) return;
        if (IsBusy)
        {
            ShowToast(BusyMessage);
            return;
        }

        string? parent = null;
        try { parent = Path.GetDirectoryName(root); }
        catch (Exception ex) { Log.Debug($"Ruta de instalación rara ({root}): {ex.Message}"); }

        var picked = DialogService.PickFolder($"Elige la nueva carpeta para {Game}", parent);
        if (picked == null) return;

        var check = await Task.Run(() => ValidateMoveTarget(root, picked));
        if (check.Error != null || check.Resolved == null)
        {
            await ShowErrorAsync(check.Error ?? "No se puede mover ahí.", "No se puede mover ahí", offerReport: false);
            return;
        }

        var resolved = check.Resolved;
        var message = $"{Game} se moverá de:\n{root}\n\na:\n{resolved}";
        if (check.Warning != null) message += $"\n\n{check.Warning}";
        var answer = await Dialogs.ShowAsync($"Mover {Game}", message, "Mover", "Cancelar");
        if (answer != DialogResult.Primary) return;

        if (!await EnsureGameClosedAsync(GameInstaller.GetGameDir(root))) return;
        if (IsBusy || _installRoot != root)
        {
            ShowToast(BusyMessage);
            return;
        }

        StartOperation(OperationKind.Move, automatic: false, null, async ct =>
        {
            var progress = new Progress<InstallProgress>(OnInstallProgress);
            bool committed = false;

            // El instalador llama a esto cuando el juego ya está completo en el destino y ANTES de borrar el origen:
            // la carpeta nueva queda guardada en los ajustes antes de que el origen desaparezca. Si no se puede
            // guardar, lanza y el instalador no borra el origen.
            Task Commit(string newRoot)
            {
                async Task CommitOnUiAsync()
                {
                    await CommitMovedRootAsync(root, newRoot, canAbort: true);
                    committed = true;
                }
                return _dispatcher.CheckAccess() ? CommitOnUiAsync() : _dispatcher.InvokeAsync<Task>(CommitOnUiAsync).Task.Unwrap();
            }

            await _installer.MoveAsync(root, resolved, Commit, progress, ct);

            // Por si el instalador terminó sin llamar al callback: el juego ya está en el destino, se guarda igualmente.
            if (!committed) await CommitMovedRootAsync(root, resolved, canAbort: false);

            Log.Info($"{Game} movido de {root} a {_installRoot}.");
            ShowToast($"{Game} se movió a {_installRoot}");
        });
    }

    /// <summary>
    /// Guarda la carpeta nueva tras mover (hilo de UI). Con <paramref name="canAbort"/>, si no se puede guardar deja los
    /// ajustes como estaban y lanza LauncherException (el instalador no borra el origen); sin él solo lo registra.
    /// </summary>
    private async Task CommitMovedRootAsync(string oldRoot, string newRoot, bool canAbort)
    {
        _settings.InstallRoot = newRoot;
        bool saved = await SaveSettingsCheckedAsync();
        if (!saved)
        {
            if (canAbort)
            {
                _settings.InstallRoot = oldRoot;
                _ = SaveSettingsAsync();
                Log.Error($"No se pudo guardar la carpeta nueva ({newRoot}) tras mover; se pide no borrar el origen.");
                throw new LauncherException("No se pudo guardar la nueva carpeta en los ajustes, así que no se ha terminado de mover el juego. Vuelve a intentarlo.");
            }
            Log.Error($"No se pudo guardar la carpeta nueva ({newRoot}) en los ajustes tras mover el juego.");
        }
        _installRoot = newRoot;
    }

    private sealed record MoveCheck(string? Resolved, string? Error, string? Warning);

    /// <summary>Misma validación que el diálogo de instalación, aplicada al destino de "Mover…". Hilo de fondo.</summary>
    private static MoveCheck ValidateMoveTarget(string from, string picked)
    {
        string resolved;
        try
        {
            resolved = GameInstaller.ResolveInstallRoot(picked);
        }
        catch (LauncherException ex)
        {
            return new MoveCheck(null, ex.UserMessage, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Destino no válido para mover: {picked}", ex);
            return new MoveCheck(null, "La carpeta elegida no es válida.", null);
        }

        try
        {
            static string Normalize(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(Normalize(resolved), Normalize(from), StringComparison.OrdinalIgnoreCase))
                return new MoveCheck(null, $"{Game} ya está en esa carpeta.", null);
            if (FileSystemUtil.IsSubPathOf(resolved, from))
                return new MoveCheck(null, "No se puede mover el juego a una carpeta que está dentro de la instalación actual.", null);

            var location = FileSystemUtil.CheckInstallLocation(resolved, LauncherInstallation.GetPreferredLauncherExePath());
            if (location.IsBlocked)
                return new MoveCheck(null, location.Reason ?? "No se puede instalar en esa carpeta.", null);

            if (Directory.Exists(resolved) && Directory.EnumerateFileSystemEntries(resolved).Any())
                return new MoveCheck(null, "La carpeta de destino no está vacía. Elige una carpeta vacía o nueva.", null);

            if (!FileSystemUtil.IsSameVolume(from, resolved))
            {
                long need = FileSystemUtil.GetDirectorySize(from);
                long free = FileSystemUtil.GetAvailableFreeSpace(resolved);
                if (free != long.MaxValue && free < need + need / 20)
                {
                    return new MoveCheck(null,
                        $"Necesitas al menos {UiFormat.Bytes(need)} libres en {DriveOf(resolved)} (tienes {UiFormat.Bytes(free)}).", null);
                }
            }

            return new MoveCheck(resolved, null, location.IsWarning ? location.Reason : null);
        }
        catch (Exception ex)
        {
            Log.Error("Error al validar el destino de la instalación.", ex);
            return new MoveCheck(null, "No se pudo comprobar la carpeta de destino.", null);
        }
    }

    private async Task ClearDownloadsAsync(bool confirm)
    {
        var root = _installRoot;
        if (root == null) return;
        if (IsBusy)
        {
            ShowToast(BusyMessage);
            return;
        }

        if (confirm)
        {
            var answer = await Dialogs.ShowAsync("Borrar descargas pendientes",
                "Se borrará lo descargado hasta ahora. La próxima descarga empezará de cero.", "Borrar", "Cancelar", isDanger: true);
            if (answer != DialogResult.Primary) return;
            if (IsBusy || _installRoot != root)
            {
                ShowToast(BusyMessage);
                return;
            }
        }

        await Task.Run(() => _installer.ClearDownloads(root));
        _pendingResumeBytes = 0;
        Log.Info("Descargas pendientes borradas por el usuario.");
        await RefreshInstallStatusAsync(recover: false);
        ShowToast("Descargas pendientes borradas.");
    }

    // ───────────── Carpetas y enlaces ─────────────

    private void OpenGameFolder()
    {
        var root = _installRoot;
        if (root == null) return;
        if (!ShellService.OpenFolder(GameInstaller.GetGameDir(root)) && !ShellService.OpenFolder(root))
            ShowToast("No se pudo abrir la carpeta del juego.");
    }

    private void OpenInstallFolder()
    {
        var root = _installRoot;
        if (root == null) return;
        if (!ShellService.OpenFolder(root)) ShowToast("No se pudo abrir la carpeta de instalación.");
    }

    private Task OpenSavesFolderAsync() => Guarded(async () =>
    {
        var root = _installRoot;
        if (root == null) return;
        var dir = await Task.Run(() => _installer.GetUnityPersistentDataDir(root));
        if (string.IsNullOrEmpty(dir))
        {
            ShowToast("No se encontró la carpeta de guardados del juego.");
            return;
        }
        if (!Directory.Exists(dir))
        {
            ShowToast($"Aún no hay guardados ni logs: abre {Game} al menos una vez.");
            return;
        }
        if (!ShellService.OpenFolder(dir)) ShowToast("No se pudo abrir la carpeta de guardados.");
    }, "Abrir guardados");

    private void OpenLogsFolder()
    {
        string? dir = null;
        try
        {
            dir = Log.LogsDirectory ?? AppPaths.LogsDir;
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo preparar la carpeta de logs.", ex);
        }
        if (!ShellService.OpenFolder(dir)) ShowToast("No se pudo abrir la carpeta de logs.");
    }

    private Task OpenGameReleasesPageAsync()
    {
        if (!ShellService.OpenUrl(LauncherConfig.GameReleasesPageUrl)) ShowToast("No se pudo abrir el navegador.");
        return Task.CompletedTask;
    }

    // ───────────── Buscar actualizaciones ─────────────

    private Task CheckForUpdatesAsync(bool includeLauncher) => Guarded(async () =>
    {
        if (_isFetching) return;

        if (_lastSuccessfulFetchUtc is { } last && DateTimeOffset.UtcNow - last < LauncherConfig.ApiFreshnessWindow)
        {
            ShowToast("Comprobado hace un momento. " + BuildUpdateSummary());
            return;
        }

        bool ok = await RefreshReleasesAsync(bypassFreshness: false, _lifetimeCts.Token);
        if (!ok)
        {
            await ShowErrorAsync(_fetchErrorMessage ?? "No se pudo conectar con GitHub.", "No se pudo comprobar", offerReport: false);
            return;
        }

        ShowToast(_lastFetchResult?.IsStale == true
            ? (_lastFetchResult.Warning ?? "Sin conexión con GitHub. Mostrando datos guardados.")
            : BuildUpdateSummary());

        await EvaluateAutoUpdateAsync();
        if (includeLauncher && _settings.CheckLauncherUpdates) await CheckLauncherUpdateAsync(force: true);
    }, "Buscar actualizaciones");

    private string BuildUpdateSummary() => State switch
    {
        LauncherState.UpdateAvailable => "Hay una versión nueva disponible.",
        LauncherState.Ready or LauncherState.GameRunning => "Ya tienes la última versión.",
        LauncherState.NotInstalled when Latest != null => $"Última versión: {Latest.VersionDisplay}.",
        LauncherState.NoReleases => "Aún no hay builds publicadas en GitHub.",
        _ => "Lista de versiones actualizada.",
    };

    // ───────────── Auto-actualización del launcher ─────────────

    /// <summary>
    /// [Actualizar] del banner. Si la descarga no coincide con lo publicado (DownloadVerificationException o
    /// RemoteAssetChangedException) vuelve a consultar GitHub (force) y reintenta una vez si la build cambió. La versión
    /// solo se omite (SkippedLauncherVersion) si la nueva no llegó a arrancar (LauncherStartFailedException); cualquier
    /// otro fallo muestra el mensaje y deja el banner para reintentar.
    /// </summary>
    private async Task UpdateLauncherAsync()
    {
        var info = _launcherUpdate;
        if (info == null) return;
        if (IsBusy)
        {
            ShowToast(BusyMessage);
            return;
        }

        bool canSelfUpdate;
        string? reason = null;
        try
        {
            (canSelfUpdate, reason) = await Task.Run(() =>
            {
                bool ok = LauncherSelfUpdater.CanSelfUpdate(out var why);
                return (ok, why);
            });
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo comprobar si el launcher puede auto-actualizarse.", ex);
            canSelfUpdate = false;
        }

        if (!canSelfUpdate)
        {
            Log.Info($"Auto-actualización no disponible ({reason}); se abre la página de releases del launcher.");
            ShellService.OpenUrl(LauncherConfig.LauncherReleasesPageUrl);
            ShowToast(string.IsNullOrWhiteSpace(reason)
                ? "Descarga la versión nueva desde GitHub."
                : $"Descarga la versión nueva desde GitHub ({reason}).");
            return;
        }

        if (IsBusy || !ReferenceEquals(info, _launcherUpdate))
        {
            if (IsBusy) ShowToast(BusyMessage);
            return;
        }

        StartOperation(OperationKind.LauncherUpdate, automatic: false, null, async ct =>
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (_operationKind != OperationKind.LauncherUpdate) return;
                _launcherDownloadProgress = p;
                UpdateState();
            });

            var target = info;
            bool restarted;
            try
            {
                try
                {
                    restarted = await _selfUpdater.DownloadAndApplyAsync(target, progress, ct);
                }
                catch (Exception ex) when (IsRemoteAssetChange(ex) && !ct.IsCancellationRequested)
                {
                    // Datos del asset caducados (p.ej. re-subido con el mismo tag): se consulta GitHub otra vez y, si la
                    // build publicada cambió, se reintenta UNA vez con los datos nuevos.
                    Log.Warn($"La descarga del launcher v{target.VersionText} no coincide con lo publicado; se vuelve a consultar GitHub.", ex);
                    _launcherDownloadProgress = null;
                    UpdateState();

                    var fresh = await _selfUpdater.CheckAsync(ct, force: true);
                    if (fresh == null || IsSameLauncherAsset(fresh, target)) throw;

                    target = fresh;
                    if (ReferenceEquals(_launcherUpdate, info))
                    {
                        _launcherUpdate = fresh;
                        LauncherUpdateText = $"Nueva versión del launcher: v{fresh.VersionText}";
                    }
                    restarted = await _selfUpdater.DownloadAndApplyAsync(target, progress, ct);
                }
            }
            catch (LauncherStartFailedException ex)
            {
                // Solo se deja de ofrecer la versión si NO llegó a arrancar; los fallos de red o descarga no la omiten
                // (el banner sigue ahí para reintentar).
                Log.Warn($"La versión nueva del launcher (v{target.VersionText}) no arrancó; se omite esa versión.", ex);
                _settings.SkippedLauncherVersion = target.VersionText;
                _ = SaveSettingsAsync();
                _launcherUpdate = null;
                IsLauncherUpdateVisible = false;
                throw;
            }

            if (restarted)
            {
                Log.Info("La versión nueva del launcher arrancó: se cierra esta.");
                ShutdownRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Log.Warn("La actualización del launcher no se completó.");
            }
        });
    }

    /// <summary>Mismo exe publicado (mismo asset, tamaño y SHA-256): reintentar volvería a fallar igual.</summary>
    private static bool IsSameLauncherAsset(LauncherUpdateInfo a, LauncherUpdateInfo b)
        => a.Asset.Id == b.Asset.Id && a.Asset.Size == b.Asset.Size && SameSha(a.Asset, b.Asset);

    private void SkipLauncherUpdate()
    {
        var info = _launcherUpdate;
        if (info == null) return;
        _settings.SkippedLauncherVersion = info.VersionText;
        _ = SaveSettingsAsync();
        _launcherUpdate = null;
        IsLauncherUpdateVisible = false;
        Log.Info($"El usuario omitió la versión v{info.VersionText} del launcher.");
        ShowToast($"No se volverá a avisar de la versión v{info.VersionText}.");
        UpdateState();
    }

    // ───────────── Acceso directo e informe ─────────────

    private Task CreateDesktopShortcutAsync() => Guarded(() => CreateDesktopShortcutCoreAsync(interactive: true), "Crear acceso directo");

    private async Task<bool> CreateDesktopShortcutCoreAsync(bool interactive)
    {
        try
        {
            await Task.Run(LauncherInstallation.CreateDesktopShortcut);
            Log.Info("Acceso directo del escritorio creado.");
            if (interactive) ShowToast("Acceso directo creado en el escritorio.");
            return true;
        }
        catch (LauncherException ex)
        {
            Log.Warn($"No se pudo crear el acceso directo: {ex.Message}", ex);
            if (interactive) await ShowErrorAsync(ex.UserMessage, "No se pudo crear el acceso directo", offerReport: false);
            else ShowToast(ex.UserMessage);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Error inesperado al crear el acceso directo.", ex);
            if (interactive) await ShowErrorAsync(GenericErrorMessage);
            else ShowToast("No se pudo crear el acceso directo en el escritorio.");
            return false;
        }
    }

    /// <summary>Crea el zip para el desarrollador en el Escritorio y lo muestra en el Explorador. Nunca lanza.</summary>
    internal async Task CreateReportAsync()
    {
        if (_reportRunning) return;
        _reportRunning = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            ShowToast("Creando informe…");
            var root = _installRoot;
            string? unityDir = null;
            if (root != null)
            {
                try
                {
                    unityDir = await Task.Run(() => _installer.GetUnityPersistentDataDir(root));
                }
                catch (Exception ex)
                {
                    Log.Warn("No se pudo localizar la carpeta de datos del juego para el informe.", ex);
                }
            }

            var context = new DiagnosticContext
            {
                Summary = BuildDiagnosticSummary(),
                InstallRoot = root,
                UnityDataDir = unityDir,
            };

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            bool onDesktop = !string.IsNullOrEmpty(desktop) && Directory.Exists(desktop);
            var outputDir = onDesktop ? desktop : AppPaths.AppDataDir;

            var zip = await DiagnosticReport.CreateAsync(context, outputDir, _lifetimeCts.Token);
            Log.Info($"Informe creado: {zip}");
            ShellService.RevealInExplorer(zip);
            ShowToast(onDesktop
                ? "Informe creado en el Escritorio. Envíaselo al desarrollador."
                : $"Informe creado en {outputDir}. Envíaselo al desarrollador.");
        }
        catch (OperationCanceledException)
        {
            // Cerrando.
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex, "Crear informe", automatic: false, offerReport: false);
        }
        finally
        {
            _reportRunning = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private Dictionary<string, string?> BuildDiagnosticSummary()
    {
        var inv = CultureInfo.InvariantCulture;
        var info = _installStatus?.Info;
        var latest = Latest;
        return new Dictionary<string, string?>
        {
            ["Estado"] = State.ToString(),
            ["Operación en curso"] = IsBusy ? $"{_operationKind} ({_currentProgress?.Phase})" : "ninguna",
            ["Carpeta de instalación"] = _installRoot ?? "(sin elegir)",
            ["Estado en disco"] = _installStatus?.State.ToString(),
            ["Contenido ajeno"] = _installStatus?.HasForeignContent.ToString(),
            ["Descarga pendiente"] = _installStatus?.HasPendingDownload == true
                ? $"sí ({_pendingResumeBytes.ToString(inv)} bytes de la última)"
                : "no",
            ["Versión instalada"] = info != null
                ? $"{DisplayOf(info)} (tag {info.Tag}, asset {info.AssetId.ToString(inv)} {info.AssetName}, sha256 {info.Sha256 ?? "-"})"
                : "(ninguna)",
            ["Última versión"] = latest != null
                ? $"{latest.VersionDisplay} (tag {latest.Release.TagName}, asset {latest.Asset.Id.ToString(inv)} {latest.Asset.Name}, {latest.Asset.Size.ToString(inv)} bytes)"
                : "(desconocida)",
            ["Releases jugables"] = _gameReleases?.Count.ToString(inv) ?? "(sin datos)",
            ["Origen de los datos"] = _lastFetchResult?.Source.ToString() ?? "(sin datos)",
            ["Datos obtenidos (UTC)"] = _lastFetchResult?.FetchedAt?.ToString("u", inv),
            ["Aviso de GitHub"] = _lastFetchResult?.Warning ?? _fetchErrorMessage,
            ["Última consulta correcta (UTC)"] = _lastSuccessfulFetchUtc?.ToString("u", inv),
            ["Consultas a GitHub restantes"] = RateLimitRemainingSafe()?.ToString(inv) ?? "(desconocido)",
            ["Juego abierto"] = IsGameRunning ? "sí" : "no",
            ["Actualizar automáticamente"] = _settings.AutoDownloadUpdates ? "sí" : "no",
            ["Incluir versiones de prueba"] = _settings.IncludePrereleases ? "sí" : "no",
            ["Auto-actualización pausada para"] = _settings.AutoUpdatePausedForTag,
            ["Límite de descarga (KB/s)"] = _settings.MaxDownloadSpeedKBps.ToString(inv),
            ["Conservar descargas"] = _settings.KeepDownloadedArchives ? "sí" : "no",
            ["Al abrir el juego"] = _settings.AfterLaunch.ToString(),
            ["Argumentos de lanzamiento"] = _settings.LaunchArguments,
            ["Aviso en pantalla"] = SecondaryNoticeText,
            ["Texto de estado"] = StatusText,
        };
    }

    // ───────────── Errores ─────────────

    /// <summary>
    /// LauncherException → su mensaje; cualquier otra → Log.Error + mensaje genérico. Las operaciones automáticas van al
    /// aviso secundario; las del usuario, a un diálogo con «Crear informe». Nunca lanza.
    /// </summary>
    private async Task HandleExceptionAsync(Exception ex, string context, bool automatic, bool offerReport = true)
    {
        try
        {
            string message;
            if (ex is LauncherException launcherException)
            {
                message = launcherException.UserMessage;
                Log.Warn($"{context}: {message}", ex);
            }
            else
            {
                message = GenericErrorMessage;
                Log.Error($"{context}: error inesperado.", ex);
            }

            if (automatic || _disposed)
            {
                _autoOperationError = message;
                UpdateState();
                return;
            }

            await ShowErrorAsync(message, offerReport: offerReport);
        }
        catch (Exception inner)
        {
            Log.Error("Error al mostrar un error.", inner);
        }
    }

    private async Task ShowErrorAsync(string message, string title = "No se pudo completar", bool offerReport = true)
    {
        var result = await Dialogs.ShowAsync(title, message, "Aceptar", null, false, offerReport ? "Crear informe" : null);
        if (result == DialogResult.Tertiary) await CreateReportAsync();
    }

    /// <summary>Diálogo amable para errores no controlados (lo usa la App). Nunca lanza.</summary>
    internal async Task ShowUnexpectedErrorAsync()
    {
        try
        {
            await ShowErrorAsync(GenericErrorMessage, "Algo salió mal");
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo mostrar el diálogo de error inesperado.", ex);
        }
    }

    // ───────────── Cierre de la ventana ─────────────

    /// <summary>Qué hacer si el usuario intenta cerrar la ventana ahora.</summary>
    public CloseDecision GetCloseDecision()
    {
        if (!IsBusy) return CloseDecision.Allow;
        if (_operationKind == OperationKind.LauncherUpdate)
            return (_launcherDownloadProgress?.Fraction ?? 0) >= 1 ? CloseDecision.MustWait : CloseDecision.Confirm;

        var progress = _currentProgress;
        if (progress is { Phase: InstallPhase.Installing or InstallPhase.CleaningUp or InstallPhase.Uninstalling })
            return CloseDecision.MustWait;
        return progress is { CanCancel: false } ? CloseDecision.MustWait : CloseDecision.Confirm;
    }

    /// <summary>Fase no interrumpible: avisa y cierra la ventana en cuanto termine la operación.</summary>
    public void CloseWhenOperationEnds()
    {
        if (!IsBusy)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        _closeWhenOperationEnds = true;
        Log.Info("Cierre pedido durante una fase no interrumpible: se cerrará al terminar.");
        ShowToast("Espera un momento: se está terminando la operación. El launcher se cerrará solo al acabar.");
    }

    /// <summary>
    /// Pregunta si cerrar durante una operación cancelable. Si se confirma, cancela (la descarga queda en pausa), espera
    /// a que termine (máx. 30 s) y devuelve true para cerrar.
    /// </summary>
    public async Task<bool> ConfirmCloseDuringOperationAsync()
    {
        bool downloading = _operationKind == OperationKind.LauncherUpdate || _currentProgress?.Phase == InstallPhase.Downloading;
        var message = _operationKind == OperationKind.Move
            ? "Se está moviendo el juego. Si cierras ahora se cancelará y el juego se quedará donde estaba."
            : downloading
                ? "Hay una descarga en curso. Si cierras ahora quedará en pausa y podrás continuarla la próxima vez."
                : "Hay una operación en curso. Si cierras ahora se cancelará.";

        var answer = await Dialogs.ShowAsync("¿Cerrar el launcher?", message, "Cerrar", "Seguir");
        if (answer != DialogResult.Primary) return false;
        if (!IsBusy) return true;

        if (GetCloseDecision() == CloseDecision.MustWait)
        {
            CloseWhenOperationEnds();
            return false;
        }

        var task = _operationTask;
        CancelCurrentOperation("Cierre de la ventana");
        if (task != null)
        {
            var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (finished != task) Log.Warn("La operación no terminó a tiempo tras cancelarla; se cierra igualmente.");
        }
        return true;
    }
}
