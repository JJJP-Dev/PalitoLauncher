using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using PalitoLauncher.Core.Downloads;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Launch;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Util;

namespace PalitoLauncher.Core.Install;

/// <summary>
/// Instala, actualiza, repara, mueve y desinstala el juego.
///
/// Estructura en disco (InstallRoot = carpeta elegida, p.ej. D:\Juegos\Coop2D):
///   InstallRoot\game\                   ← versión actual (Coop2D.exe + palito_install.json)
///   InstallRoot\.launcher\              ← (oculta) área de trabajo
///       downloads\&lt;assetId&gt;-&lt;name&gt;     ← descargas (+ .part/.part.json reanudables)
///       staging\&lt;8 hex&gt;\                ← extracción temporal
///       ready\                          ← versión nueva verificada, a punto de entrar
///       old\&lt;yyyyMMddHHmmss&gt;\           ← versión anterior durante el intercambio
///       trash\&lt;8 hex&gt;\                  ← árboles pendientes de borrar (nunca se restaura desde aquí)
///       swap.json                       ← diario del intercambio {oldDir, newDir, tag, startedUtc}
///       operation.lock                  ← candado entre procesos (FileShare.None… ver abajo)
/// Nombres cortos a propósito (rutas profundas de Unity bajo MAX_PATH).
///
/// "Carpeta de juego válida" = palito_install.json se parsea Y su ExecutableName existe en ella.
/// "Contenido ajeno" = game\ no vacía, sin marcador válido, sin el exe y sin ningún *_Data\app.info → nunca se borra:
///   se renombra a InstallRoot\game.backup-yyyyMMddHHmmss (y se registra).
///
/// Borrados de árboles completos: primero se renombran a trash\&lt;8 hex&gt; y luego TryDeleteDirectory (si algo queda
/// bloqueado, se reintenta en el próximo Recover). Así nunca queda un juego "a medio borrar" en game\ u old\. La raíz
/// antigua tras mover entre volúmenes se renombra igual, a un hermano "&lt;raíz&gt;.deleting-&lt;8 hex&gt;", antes de borrarla.
///
/// Candado entre procesos: los métodos que modifican (InstallAsync, UninstallAsync, MoveAsync, ClearDownloads, Recover,
/// UpdateMarkerIdentity) abren .launcher\operation.lock con FileMode.OpenOrCreate, FileShare.None y
/// FileOptions.DeleteOnClose mientras duran (si el proceso muere, el SO lo libera). Si ya está bloqueado → LauncherException
/// ("Otra ventana del launcher está modificando el juego. Espera a que termine.") — salvo Recover, que simplemente no hace
/// nada (Log.Info). Además, dentro del proceso, un SemaphoreSlim(1,1) con Wait(0) → si ocupado, LauncherException("Ya hay
/// una operación en curso."). MoveAsync/UninstallAsync liberan el candado justo antes del rename/borrado final de la raíz.
///
/// Todos los métodos públicos (salvo los estáticos puros) registran en el Log y convierten errores esperables en
/// LauncherException con mensaje en español. Los guardados de Unity (%USERPROFILE%\AppData\LocalLow\...) no se tocan nunca.
/// </summary>
public sealed class GameInstaller
{
    public const string GameFolderName = "game";
    public const string WorkFolderName = ".launcher";

    /// <summary>
    /// Multiplicador del tamaño del asset para estimar el espacio necesario antes de descargar. Mismo modelo que la
    /// comprobación tras descargar (archivo en disco + descomprimido × <see cref="UncompressedSpaceFactor"/>):
    /// 1 (archivo) + <see cref="AssumedCompressionRatio"/> × <see cref="UncompressedSpaceFactor"/> = 3.97 → 4.
    /// </summary>
    public const double RequiredSpaceFactor = 4.0;

    /// <summary>Relación descomprimido/comprimido que cubre la estimación previa (la build real ronda 2.5).</summary>
    internal const double AssumedCompressionRatio = 2.7;

    /// <summary>Margen sobre el tamaño descomprimido real al recomprobar el espacio tras descargar.</summary>
    internal const double UncompressedSpaceFactor = 1.1;

    // Nombres dentro de .launcher (cortos a propósito).
    private const string DownloadsFolderName = "downloads";
    private const string StagingFolderName = "staging";
    private const string ReadyFolderName = "ready";
    private const string OldFolderName = "old";
    private const string TrashFolderName = "trash";
    private const string SwapJournalFileName = "swap.json";
    private const string LockFileName = "operation.lock";
    private const string BackupFolderPrefix = "game.backup-";
    private const string UnityAppInfoFileName = "app.info";
    private const string DeletingSuffix = ".deleting-";

    // Igual que ArchiveExtractor.FindContentRoot: el exe se busca hasta 3 carpetas por debajo de lo extraído.
    private const int ContentRootMaxDepth = 3;
    private const string MacOsJunkFolder = "__MACOSX";

    // HRESULT: distinto dispositivo (rename entre volúmenes), antivirus, archivo en uso.
    private const int HResultNotSameDevice = unchecked((int)0x80070011);
    private const int HResultVirusInfected = unchecked((int)0x800700E1);
    private const int HResultVirusDeleted = unchecked((int)0x800700E2);
    private const int HResultSharingViolation = unchecked((int)0x80070020);
    private const int HResultLockViolation = unchecked((int)0x80070021);
    private const int HResultDiskFull = unchecked((int)0x80070070);
    private const int HResultHandleDiskFull = unchecked((int)0x80070027);

    private static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinGameExitPoll = TimeSpan.FromMilliseconds(10);

    private const string BusyMessage = "Ya hay una operación en curso.";
    private const string LockedMessage = "Otra ventana del launcher está modificando el juego. Espera a que termine.";
    private const string SwapFailedMessage =
        "No se pudo completar la actualización. Cierra y vuelve a abrir el launcher para restaurar el juego.";

    private static readonly JsonSerializerOptions MarkerJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly FileDownloader _downloader;
    private readonly string _executableName;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public GameInstaller(FileDownloader downloader, string executableName)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        _downloader = downloader;
        _executableName = executableName.Trim();
    }

    /// <summary>Espera tras extraer antes de verificar (deja actuar al antivirus). Tests la ponen a cero.</summary>
    public TimeSpan PostExtractDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Intervalo de sondeo mientras se espera a que el juego se cierre (fase WaitingForGameExit).</summary>
    public TimeSpan GameExitPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>¿Hay un proceso del juego abierto desde (gameDir, exe)? Por defecto GameProcessManager.IsRunningFrom; tests lo sustituyen.</summary>
    internal Func<string, string, bool> IsGameRunningFrom { get; set; } = GameProcessManager.IsRunningFrom;

    /// <summary>¿Mismo volumen? (decide rename o copia al mover). Por defecto FileSystemUtil.IsSameVolume; tests lo sustituyen.</summary>
    internal Func<string, string, bool> IsSameVolume { get; set; } = FileSystemUtil.IsSameVolume;

    /// <summary>Rename de la raíz completa al mover en el mismo volumen. Por defecto MoveDirectoryWithRetry; tests lo sustituyen.</summary>
    internal Action<string, string> MoveRootDirectory { get; set; } = (from, to) => FileSystemUtil.MoveDirectoryWithRetry(from, to);

    /// <summary>Espacio libre en la unidad de una ruta. Por defecto FileSystemUtil.GetAvailableFreeSpace; tests lo sustituyen.</summary>
    internal Func<string, long> GetFreeSpace { get; set; } = FileSystemUtil.GetAvailableFreeSpace;

    public static string GetGameDir(string installRoot) => Path.Combine(installRoot, GameFolderName);
    public static string GetWorkDir(string installRoot) => Path.Combine(installRoot, WorkFolderName);

    /// <summary>Espacio estimado para instalar esa release: Asset.Size × RequiredSpaceFactor.</summary>
    public static long EstimateRequiredBytes(GameRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var estimate = Math.Ceiling(Math.Max(0, release.Asset.Size) * RequiredSpaceFactor);
        return estimate >= long.MaxValue ? long.MaxValue : (long)estimate;
    }

    /// <summary>
    /// Convierte la carpeta elegida por el usuario en InstallRoot (Path.GetFullPath, sin separador final):
    /// - si la carpeta misma contiene palito_install.json, o se llama ".launcher", o se llama "game" y tiene un hermano
    ///   ".launcher" → su carpeta padre (el usuario eligió una carpeta de dentro de una instalación);
    /// - si ya contiene una instalación del launcher (game\palito_install.json o .launcher\) → tal cual;
    /// - si su nombre ya es LauncherConfig.InstallFolderName (sin distinguir mayúsculas) → tal cual;
    /// - si no, se le añade "\Coop2D".
    /// </summary>
    public static string ResolveInstallRoot(string pickedFolder)
    {
        ArgumentNullException.ThrowIfNull(pickedFolder);
        string full;
        try
        {
            if (string.IsNullOrWhiteSpace(pickedFolder) || !Path.IsPathFullyQualified(pickedFolder.Trim()))
                throw new LauncherException("La carpeta elegida no es válida.");
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pickedFolder.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            throw new LauncherException("La carpeta elegida no es válida.", ex);
        }

        var name = Path.GetFileName(full);
        var parent = Path.GetDirectoryName(full);

        if (parent != null)
        {
            var insideInstallation =
                File.Exists(Path.Combine(full, InstalledGameInfo.MarkerFileName))
                || name.Equals(WorkFolderName, StringComparison.OrdinalIgnoreCase)
                || (name.Equals(GameFolderName, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(Path.Combine(parent, WorkFolderName)));
            if (insideInstallation)
                return Path.TrimEndingDirectorySeparator(parent);
        }

        if (File.Exists(Path.Combine(full, GameFolderName, InstalledGameInfo.MarkerFileName))
            || Directory.Exists(Path.Combine(full, WorkFolderName)))
            return full;

        if (name.Equals(LauncherConfig.InstallFolderName, StringComparison.OrdinalIgnoreCase))
            return full;

        return Path.Combine(full, LauncherConfig.InstallFolderName);
    }

    /// <summary>
    /// Estado actual en disco. Unidad no lista → DriveMissing. game\ inexistente o vacía → NotInstalled. Carpeta válida →
    /// Installed. Exe presente sin marcador válido, o marcador sin exe → Broken. Contenido ajeno → Broken +
    /// HasForeignContent. HasPendingDownload si hay algún .part en downloads. Nunca lanza.
    /// </summary>
    public InstallStatus GetStatus(string installRoot)
    {
        string root;
        try
        {
            root = NormalizeRoot(installRoot);
        }
        catch (Exception ex)
        {
            Log.Warn($"GetStatus: carpeta de instalación no válida ('{installRoot}').", ex);
            return new InstallStatus { State = InstallState.NotInstalled, InstallRoot = installRoot ?? "" };
        }

        var gameDir = GetGameDir(root);
        try
        {
            if (!FileSystemUtil.IsDriveReady(root))
                return new InstallStatus { State = InstallState.DriveMissing, InstallRoot = root, GameDir = gameDir };

            var pending = HasPendingDownload(root);
            if (!Directory.Exists(gameDir) || IsDirectoryEmpty(gameDir))
            {
                return new InstallStatus
                {
                    State = InstallState.NotInstalled, InstallRoot = root, GameDir = gameDir, HasPendingDownload = pending,
                };
            }

            var info = TryReadMarker(gameDir);
            var exePath = Path.Combine(gameDir, info?.ExecutableName ?? _executableName);
            var exeExists = File.Exists(exePath);
            if (info != null && exeExists)
            {
                return new InstallStatus
                {
                    State = InstallState.Installed, Info = info, InstallRoot = root, GameDir = gameDir,
                    ExecutablePath = exePath, HasPendingDownload = pending,
                };
            }

            var foreign = info == null && IsForeignContent(gameDir);
            return new InstallStatus
            {
                State = InstallState.Broken, Info = info, InstallRoot = root, GameDir = gameDir,
                ExecutablePath = exeExists ? exePath : null, HasForeignContent = foreign, HasPendingDownload = pending,
            };
        }
        catch (Exception ex)
        {
            Log.Error($"GetStatus: error inesperado leyendo {root}.", ex);
            return new InstallStatus { State = InstallState.Broken, InstallRoot = root, GameDir = gameDir };
        }
    }

    /// <summary>
    /// Recuperación tras un cierre inesperado (llamar al arrancar, antes de GetStatus). Si la unidad no está lista, no hace nada.
    /// Si game\ falta o no es válida:
    ///   1. si existe swap.json y su newDir (ready\) es válido → se promueve a game\;
    ///   2. si no, si su oldDir es válido → se restaura a game\;
    ///   3. sin diario, se restaura la carpeta válida más reciente de old\.
    ///   (Un game\ inválido se aparta a trash\ antes; si es contenido ajeno, a game.backup-….)
    /// Después: si game\ es válida, borra old\ (vía trash). Siempre: borra staging\ y ready\ huérfanos, vacía trash\ y borra
    /// swap.json si ya no hace falta. Conserva downloads\ (los .part y los archivos completos). Borra *.tmp viejos (&gt; 1 h)
    /// de .launcher. Nunca lanza.
    /// </summary>
    public void Recover(string installRoot)
    {
        try
        {
            var root = NormalizeRoot(installRoot);
            if (!FileSystemUtil.IsDriveReady(root))
            {
                Log.Info($"Recover: la unidad de {root} no está lista; no se hace nada.");
                return;
            }

            var work = GetWorkDir(root);
            if (!Directory.Exists(work))
                return; // sin área de trabajo no hay nada que recuperar

            if (!_operationGate.Wait(0))
            {
                Log.Info("Recover: hay una operación en curso en este launcher; no se hace nada.");
                return;
            }

            try
            {
                using var opLock = TryAcquireLock(work, out var busy, out var lockError);
                if (opLock == null)
                {
                    if (busy)
                        Log.Info("Recover: otra ventana del launcher está modificando el juego; no se hace nada.");
                    else
                        Log.Warn($"Recover: no se pudo abrir el candado en {work}; no se hace nada.", lockError);
                    return;
                }

                RecoverCore(root, work);
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Recover falló en '{installRoot}'.", ex);
        }
    }

    /// <summary>
    /// Instala/actualiza/reinstala <paramref name="release"/> en <paramref name="installRoot"/>:
    /// 1. Preparing: unidad lista; FileSystemUtil.CheckInstallLocation(installRoot, Environment.ProcessPath) no bloqueada;
    ///    escribible. Crea .launcher (oculta) y toma el candado. Limpia lo que el propio launcher dejó (staging\, ready\,
    ///    trash\ y, en downloads\, todo lo que no sea de este asset) y DESPUÉS comprueba el espacio libre ≥
    ///    EstimateRequiredBytes menos lo ya descargado de este asset (si no → LauncherException "Necesitas al menos 250.3 MB
    ///    libres en C:\ (tienes 120.0 MB)."), siempre antes de descargar nada.
    /// 2. Downloading/Verifying: FileDownloader a downloads\&lt;assetId&gt;-&lt;assetName&gt; con ExpectedSize, ExpectedSha256 y
    ///    ResumeKey "asset:&lt;id&gt;:&lt;sha256|size&gt;". Mapea RetryScheduled a Status "Conexión perdida. Reintentando en 4 s
    ///    (intento 3 de 8)…" y VerificationStarted a la fase Verifying. RemoteAssetChangedException se propaga tal cual.
    ///    Tras descargar, recomprueba espacio con el tamaño descomprimido real × UncompressedSpaceFactor (mismo modelo que
    ///    RequiredSpaceFactor, así que una build con relación de compresión ≤ AssumedCompressionRatio nunca falla aquí si
    ///    pasó el paso 1).
    /// 3. Extracting: ArchiveExtractor a staging\&lt;8 hex&gt;. Raíz de contenido = carpeta del exe configurado menos profundo
    ///    según la LISTA de archivos extraídos (no según el disco: el antivirus puede haberlo borrado ya); si el archivo no lo
    ///    traía → FindContentRoot y ejecutable = el único .exe de esa raíz que no sea UnityCrashHandler*; si no hay →
    ///    LauncherException("La build descargada no contiene Coop2D.exe."). Espera PostExtractDelay y verifica que TODOS los
    ///    archivos extraídos siguen existiendo (bajo la raíz de contenido) con su tamaño y que el exe existe; si no →
    ///    LauncherException del antivirus ("El antivirus bloqueó o borró archivos del juego…"). Escribe palito_install.json
    ///    (atómico) AL FINAL. Renombra la raíz de contenido a ready\ (si falla, abortar sin tocar game\).
    /// 4. WaitingForGameExit: si GameProcessManager.IsRunningFrom(game\) → reporta "Cierra Coop2D para terminar la
    ///    actualización…" y sondea cada GameExitPollInterval (cancelable).
    /// 5. Installing (ignora la cancelación): escribe swap.json; si game\ existe: contenido ajeno → game.backup-…; si no →
    ///    MoveDirectoryWithRetry(game, old\&lt;ts&gt;). Luego MoveDirectoryWithRetry(ready, game). Si falla → devuelve old a game;
    ///    si también falla, deja swap.json y lanza LauncherException("No se pudo completar la actualización. Cierra y vuelve a
    ///    abrir el launcher para restaurar el juego."). Comprueba que el exe existe en game\ (si no → mensaje del antivirus;
    ///    el próximo Recover restaura la versión anterior porque old\ se conserva en ese caso). Borra swap.json.
    /// 6. CleaningUp: old\ y staging\ vía trash\; borra el archivo descargado salvo KeepDownloadedArchive.
    /// Ante CUALQUIER excepción en 1–4 se borra staging\/ready\ en un finally (vía trash) pero se conserva downloads\
    /// (el archivo completo y verificado sirve para reintentar sin volver a descargar). Cancelación en 1–4 →
    /// OperationCanceledException con game\ intacto. Devuelve el marcador escrito.
    /// </summary>
    public async Task<InstalledGameInfo> InstallAsync(string installRoot, GameRelease release, InstallOptions options,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        options ??= new InstallOptions();
        var root = NormalizeRootOrThrow(installRoot);
        if (!_operationGate.Wait(0))
            throw new LauncherException(BusyMessage);

        try
        {
            Log.Info($"Instalando {release.VersionDisplay} (release {release.Release.Id}, asset {release.Asset.Id} " +
                $"'{release.Asset.Name}', {release.Asset.Size} bytes) en {root}.");
            return await Task.Run(() => InstallCoreAsync(root, release, options, progress, ct), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var translated = TranslateFailure(InstallerOperation.Install, root, ex);
            if (ReferenceEquals(translated, ex))
                throw;
            throw translated;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Si la versión instalada tiene el mismo contenido que <paramref name="release"/> (mismo SHA-256) pero distinta
    /// identidad (release/asset recreados en GitHub), reescribe ReleaseId, AssetId, AssetName y Tag en el marcador sin
    /// descargar nada. Devuelve true si lo actualizó. Nunca lanza (false si no aplica o falla).
    /// </summary>
    public bool UpdateMarkerIdentity(string installRoot, GameRelease release)
    {
        try
        {
            if (release == null)
                return false;
            var root = NormalizeRoot(installRoot);
            if (!FileSystemUtil.IsDriveReady(root))
                return false;

            var gameDir = GetGameDir(root);
            var current = TryReadValidMarker(gameDir);
            if (current == null || !IsSameContentDifferentIdentity(current, release))
                return false;

            if (!_operationGate.Wait(0))
            {
                Log.Info("UpdateMarkerIdentity: hay una operación en curso; se deja para más tarde.");
                return false;
            }

            try
            {
                var work = GetWorkDir(root);
                EnsureWorkDir(work);
                using var opLock = TryAcquireLock(work, out _, out var lockError);
                if (opLock == null)
                {
                    Log.Info("UpdateMarkerIdentity: el candado está ocupado; se deja para más tarde." +
                        (lockError != null ? " " + lockError.Message : ""));
                    return false;
                }

                // Releer bajo el candado (otra ventana pudo cambiarlo).
                current = TryReadValidMarker(gameDir);
                if (current == null || !IsSameContentDifferentIdentity(current, release))
                    return false;

                var oldIdentity = $"tag '{current.Tag}', release {current.ReleaseId}, asset {current.AssetId} '{current.AssetName}'";
                current.ReleaseId = release.Release.Id;
                current.AssetId = release.Asset.Id;
                current.AssetName = release.Asset.Name;
                current.Tag = release.Release.TagName;
                if (release.Version != null)
                {
                    // Mismo contenido = misma build: la versión también pasa a ser la de la release actual.
                    current.Version = release.Version.ToString();
                    current.VersionDisplay = release.VersionDisplay;
                }

                WriteMarker(gameDir, current);
                Log.Info($"Marcador actualizado sin descargar (mismo SHA-256): {oldIdentity} → tag '{current.Tag}', " +
                    $"release {current.ReleaseId}, asset {current.AssetId} '{current.AssetName}'.");
                return true;
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"UpdateMarkerIdentity falló en '{installRoot}'.", ex);
            return false;
        }
    }

    /// <summary>Bytes ya descargados (reanudables) del asset de esa release en esa instalación; 0 si nada.</summary>
    public long GetResumableBytes(string installRoot, GameRelease release)
    {
        try
        {
            if (release == null)
                return 0;
            var root = NormalizeRoot(installRoot);
            if (!FileSystemUtil.IsDriveReady(root))
                return 0;
            return Math.Max(0, FileDownloader.GetResumableBytes(BuildDownloadRequest(root, release, 0)));
        }
        catch (Exception ex)
        {
            Log.Warn($"GetResumableBytes falló en '{installRoot}'.", ex);
            return 0;
        }
    }

    /// <summary>Borra cualquier descarga parcial/caché de .launcher\downloads (no lanza; si hay operación en curso no hace nada).</summary>
    public void ClearDownloads(string installRoot)
    {
        try
        {
            var root = NormalizeRoot(installRoot);
            if (!FileSystemUtil.IsDriveReady(root))
                return;
            var work = GetWorkDir(root);
            var downloads = Path.Combine(work, DownloadsFolderName);
            if (!Directory.Exists(downloads))
                return;

            if (!_operationGate.Wait(0))
            {
                Log.Info("ClearDownloads: hay una operación en curso; no se borra nada.");
                return;
            }

            try
            {
                using var opLock = TryAcquireLock(work, out _, out var lockError);
                if (opLock == null)
                {
                    Log.Info("ClearDownloads: otra ventana del launcher está modificando el juego; no se borra nada." +
                        (lockError != null ? " " + lockError.Message : ""));
                    return;
                }

                if (FileSystemUtil.TryDeleteDirectory(downloads))
                    Log.Info($"Descargas pendientes borradas ({downloads}).");
                else
                    Log.Warn($"No se pudieron borrar todas las descargas de {downloads}.");
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"ClearDownloads falló en '{installRoot}'.", ex);
        }
    }

    /// <summary>
    /// Desinstala: juego cerrado (si no → LauncherException "Cierra Coop2D antes de desinstalar."); renombra game\ a
    /// .launcher\trash\… (si ese rename falla → LauncherException sin haber borrado nada); contenido ajeno se deja en su
    /// sitio; luego borra .launcher y la InstallRoot si queda vacía. No toca los guardados (LocalLow).
    /// </summary>
    public async Task UninstallAsync(string installRoot, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var root = NormalizeRootOrThrow(installRoot);
        if (!_operationGate.Wait(0))
            throw new LauncherException(BusyMessage);

        try
        {
            Log.Info($"Desinstalando el juego de {root}.");
            await Task.Run(() => UninstallCore(root, progress, ct), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var translated = TranslateFailure(InstallerOperation.Uninstall, root, ex);
            if (ReferenceEquals(translated, ex))
                throw;
            throw translated;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Mueve la instalación completa a <paramref name="newInstallRoot"/> (ya resuelta con ResolveInstallRoot):
    /// valida destino (CheckInstallLocation no bloqueada; no dentro del origen; debe no existir o estar vacío), juego
    /// cerrado, espacio libre. Mismo volumen → MoveDirectoryWithRetry (si Windows responde ERROR_NOT_SAME_DEVICE, p.ej.
    /// destino bajo un punto de montaje o una unión a otro disco, se usa la copia). Distinto volumen → CopyDirectoryAsync a
    /// "&lt;destino&gt;.moving", verificar tamaño total, renombrar a destino y luego borrar el origen (best effort; se
    /// renombra antes a "&lt;origen&gt;.deleting-&lt;8 hex&gt;" y, si ese rename falla, el origen se deja entero).
    /// Si se cancela/falla durante la copia, borra la copia parcial y deja el origen intacto.
    /// Equivale a la otra sobrecarga con commitNewRoot = null (el llamador guarda la nueva ruta después).
    /// </summary>
    public Task MoveAsync(string fromInstallRoot, string newInstallRoot, IProgress<InstallProgress>? progress, CancellationToken ct)
        => MoveAsync(fromInstallRoot, newInstallRoot, commitNewRoot: null, progress, ct);

    /// <summary>
    /// Igual que la otra sobrecarga, pero espera a <paramref name="commitNewRoot"/>(destino) en cuanto la instalación ya está
    /// completa en el destino y ANTES de tocar el origen (la UI guarda ahí settings.InstallRoot):
    /// - mismo volumen: tras el rename origen → destino;
    /// - distinto volumen: tras el rename "&lt;destino&gt;.moving" → destino y antes de borrar el origen.
    /// El callback se invoca en el SynchronizationContext de quien llamó a MoveAsync (el hilo de la UI si se llamó desde
    /// él; sin contexto, en un hilo del pool): no bloquees ese hilo esperando a MoveAsync. Debe lanzar si NO pudo guardar
    /// (y en ese caso no dejar nada guardado). Si lanza, se deshace el movimiento (mismo volumen: se renombra de vuelta
    /// al origen; distinto volumen: se borra la copia y el origen queda intacto) y su excepción se propaga. Solo después
    /// de un callback correcto se borra el origen: primero se renombra a "&lt;origen&gt;.deleting-&lt;8 hex&gt;" (mismo
    /// volumen) para que nunca quede un juego a medio borrar en la ruta antigua; si ese rename falla, se borra en su sitio
    /// (best effort; la ruta guardada ya es el destino).
    /// </summary>
    public async Task MoveAsync(string fromInstallRoot, string newInstallRoot, Func<string, Task>? commitNewRoot,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var from = NormalizeRootOrThrow(fromInstallRoot);
        var to = NormalizeRootOrThrow(newInstallRoot);
        if (!_operationGate.Wait(0))
            throw new LauncherException(BusyMessage);

        try
        {
            Log.Info($"Moviendo la instalación de {from} a {to}.");
            var commit = commitNewRoot == null ? null : BindToCallerContext(commitNewRoot, SynchronizationContext.Current);
            await Task.Run(() => MoveCoreAsync(from, to, commit, progress, ct), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var translated = TranslateFailure(InstallerOperation.Move, from, ex, to);
            if (ReferenceEquals(translated, ex))
                throw;
            throw translated;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Carpeta de datos de Unity (guardados y Player.log): lee game\Coop2D_Data\app.info (o cualquier *_Data\app.info):
    /// línea 1 = compañía, línea 2 = producto → %USERPROFILE%\AppData\LocalLow\&lt;compañía&gt;\&lt;producto&gt;.
    /// Devuelve la ruta aunque aún no exista; null si no hay app.info. Nunca lanza.
    /// </summary>
    public string? GetUnityPersistentDataDir(string installRoot)
    {
        try
        {
            var gameDir = GetGameDir(NormalizeRoot(installRoot));
            if (!Directory.Exists(gameDir))
                return null;

            var exe = TryReadMarker(gameDir)?.ExecutableName ?? _executableName;
            var preferred = Path.Combine(gameDir, Path.GetFileNameWithoutExtension(exe) + "_Data", UnityAppInfoFileName);
            var appInfo = File.Exists(preferred)
                ? preferred
                : Directory.EnumerateDirectories(gameDir, "*_Data")
                    .Select(d => Path.Combine(d, UnityAppInfoFileName))
                    .FirstOrDefault(File.Exists);
            if (appInfo == null)
                return null;

            if (new FileInfo(appInfo).Length > 64 * 1024)
                return null; // app.info real: dos líneas cortas

            var lines = File.ReadAllLines(appInfo);
            var company = lines.Length > 0 ? lines[0].Trim() : "";
            var product = lines.Length > 1 ? lines[1].Trim() : "";
            if (!IsSafeFolderName(company) || !IsSafeFolderName(product))
            {
                Log.Warn($"app.info con contenido inesperado en {appInfo}.");
                return null;
            }

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile))
                return null;
            return Path.Combine(profile, "AppData", "LocalLow", company, product);
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo leer la carpeta de datos de Unity en '{installRoot}'.", ex);
            return null;
        }
    }

    // ───────────────────────────── Instalación ─────────────────────────────

    private async Task<InstalledGameInfo> InstallCoreAsync(string root, GameRelease release, InstallOptions options,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var reporter = new ProgressReporter(progress);
        var asset = release.Asset;

        // 1. Preparing
        reporter.Report(InstallPhase.Preparing, null, "Preparando…");
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(asset.DownloadUrl)
            || !Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var downloadUri)
            || (downloadUri.Scheme != Uri.UriSchemeHttps && downloadUri.Scheme != Uri.UriSchemeHttp))
            throw new LauncherException("Esta versión no tiene un archivo descargable válido en GitHub.");

        EnsureDriveReady(root);
        var location = FileSystemUtil.CheckInstallLocation(root, Environment.ProcessPath);
        if (location.IsBlocked)
            throw new LauncherException(location.Reason ?? "No se puede instalar el juego en esa carpeta. Elige otra.");
        if (location.IsWarning)
            Log.Warn($"Aviso sobre la carpeta de instalación {root}: {location.Reason}");
        if (!FileSystemUtil.IsDirectoryWritable(root))
            throw new LauncherException($"No se puede escribir en {root}. Elige otra carpeta.");

        var request = BuildDownloadRequest(root, release, options.MaxBytesPerSecond);
        var archivePath = request.DestinationPath;

        var work = GetWorkDir(root);
        EnsureWorkDir(work);
        using var opLock = AcquireLockOrThrow(work);

        // Un intercambio interrumpido (cierre inesperado) se resuelve antes de nada.
        if (File.Exists(Path.Combine(work, SwapJournalFileName)))
        {
            Log.Warn("Hay un intercambio de versiones sin terminar; se recupera antes de instalar.");
            RecoverCore(root, work);
        }

        var stagingRoot = Path.Combine(work, StagingFolderName);
        var readyDir = Path.Combine(work, ReadyFolderName);
        DeleteTree(work, stagingRoot, allowInPlace: true);
        DeleteTree(work, readyDir, allowInPlace: true);
        EmptyTrash(work);
        PruneDownloads(Path.GetDirectoryName(archivePath)!, Path.GetFileName(archivePath));

        // El espacio se mide después de limpiar lo que dejó el propio launcher (descargas viejas, restos), antes de descargar.
        EnsureFreeSpace(root, Math.Max(0, EstimateRequiredBytes(release) - GetAlreadyDownloadedBytes(request, asset.Size)));

        var gameDir = GetGameDir(root);
        InstalledGameInfo info;
        var swapStarted = false;
        try
        {
            // 2. Downloading / Verifying
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            await DownloadAsync(request, asset, reporter, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // 3. Extracting
            reporter.Report(InstallPhase.Extracting, 0, "Extrayendo…");
            long uncompressed;
            try
            {
                uncompressed = ArchiveExtractor.GetUncompressedSize(archivePath);
            }
            catch (LauncherException) when (request.ExpectedSha256 == null)
            {
                // Sin hash no sabemos si el archivo es el bueno: se borra para que el reintento lo descargue de nuevo.
                FileSystemUtil.TryDeleteFile(archivePath);
                throw;
            }
            EnsureFreeSpace(root, (long)Math.Ceiling(uncompressed * UncompressedSpaceFactor));

            var stagingDir = Path.Combine(stagingRoot, RandomHex8());
            var extractProgress = new InlineProgress<ExtractProgress>(p => reporter.Report(InstallPhase.Extracting,
                p.Fraction, $"Extrayendo… {p.EntriesDone} de {p.EntriesTotal} archivos"));
            var files = await ArchiveExtractor.ExtractAsync(archivePath, stagingDir, extractProgress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Raíz según lo que traía el archivo: si el antivirus ya borró el exe, la verificación lo dirá (mensaje del antivirus).
            var contentRoot = FindContentRootFromFiles(stagingDir, files);
            string? exeName = contentRoot == null ? null : _executableName;
            if (contentRoot == null)
            {
                contentRoot = ArchiveExtractor.FindContentRoot(stagingDir, _executableName);
                exeName = contentRoot == null ? null : ResolveExecutableName(stagingDir, contentRoot, files);
            }
            if (contentRoot == null || exeName == null)
                throw new LauncherException($"La build descargada no contiene {_executableName}.");
            if (!exeName.Equals(_executableName, StringComparison.OrdinalIgnoreCase))
                Log.Warn($"La build no trae {_executableName}; se usa {exeName} como ejecutable.");

            if (PostExtractDelay > TimeSpan.Zero)
                await Task.Delay(PostExtractDelay, ct).ConfigureAwait(false);
            VerifyExtractedFiles(stagingDir, contentRoot, files, exeName, root);

            info = CreateMarker(release, exeName);
            WriteMarker(contentRoot, info);
            FileSystemUtil.MoveDirectoryWithRetry(contentRoot, readyDir);

            // 4. WaitingForGameExit
            if (Directory.Exists(gameDir))
            {
                var runningExe = TryReadMarker(gameDir)?.ExecutableName ?? _executableName;
                await WaitForGameExitAsync(gameDir, runningExe, reporter, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            swapStarted = true;
        }
        finally
        {
            if (!swapStarted)
            {
                // game\ no se ha tocado; la descarga (completa o parcial) se conserva para reintentar.
                DeleteTree(work, stagingRoot, allowInPlace: true);
                DeleteTree(work, readyDir, allowInPlace: true);
                EmptyTrash(work);
            }
        }

        // 5. Installing (ya no se atiende la cancelación)
        reporter.Report(InstallPhase.Installing, null, "Instalando…");
        SwapIn(root, work, gameDir, readyDir, info, release);

        // 6. CleaningUp
        reporter.Report(InstallPhase.CleaningUp, null, "Limpiando…");
        CleanupAfterInstall(work, stagingRoot, archivePath, options.KeepDownloadedArchive);

        Log.Info($"Instalado {info.VersionDisplay} en {gameDir}.");
        return info;
    }

    private async Task DownloadAsync(DownloadRequest request, ReleaseAsset asset, ProgressReporter reporter, CancellationToken ct)
    {
        var tracker = new DownloadStatusTracker(reporter, asset.Size);
        tracker.ReportStart(GetAlreadyDownloadedBytes(request, asset.Size));

        EventHandler<DownloadRetryInfo> onRetry = (_, retry) => tracker.OnRetry(retry);
        EventHandler onVerify = (_, _) => tracker.OnVerificationStarted();
        _downloader.RetryScheduled += onRetry;
        _downloader.VerificationStarted += onVerify;
        try
        {
            await _downloader.DownloadAsync(request, new InlineProgress<DownloadProgress>(tracker.OnProgress), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _downloader.RetryScheduled -= onRetry;
            _downloader.VerificationStarted -= onVerify;
        }
    }

    private void VerifyExtractedFiles(string stagingDir, string contentRoot, IReadOnlyList<ExtractedFile> files,
        string exeName, string root)
    {
        var contentPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot)) + Path.DirectorySeparatorChar;
        var missing = new List<string>();
        foreach (var file in files)
        {
            string full;
            try { full = Path.GetFullPath(Path.Combine(stagingDir, file.RelativePath)); }
            catch (Exception) { missing.Add(file.RelativePath); continue; }
            if (!full.StartsWith(contentPrefix, StringComparison.OrdinalIgnoreCase))
                continue; // fuera de la raíz de contenido (p.ej. __MACOSX): no se instala

            try
            {
                var fileInfo = new FileInfo(full);
                if (!fileInfo.Exists || fileInfo.Length != file.Length)
                    missing.Add(file.RelativePath);
            }
            catch (Exception)
            {
                missing.Add(file.RelativePath);
            }
        }

        if (!File.Exists(Path.Combine(contentRoot, exeName)))
            missing.Insert(0, exeName);

        if (missing.Count == 0)
            return;

        Log.Warn($"Tras extraer faltan o cambiaron {missing.Count} archivo(s): {string.Join(", ", missing.Take(10))}");
        throw new LauncherException(AntivirusMessage(Path.GetFileName(missing[0]), root));
    }

    private async Task WaitForGameExitAsync(string gameDir, string exeName, ProgressReporter reporter, CancellationToken ct)
    {
        var waiting = false;
        while (CheckGameRunning(gameDir, exeName))
        {
            if (!waiting)
            {
                waiting = true;
                Log.Info($"{LauncherConfig.GameDisplayName} está abierto desde {gameDir}: se espera a que se cierre.");
            }
            reporter.Report(InstallPhase.WaitingForGameExit, null,
                $"Cierra {LauncherConfig.GameDisplayName} para terminar la actualización…");
            var delay = GameExitPollInterval < MinGameExitPoll ? MinGameExitPoll : GameExitPollInterval;
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        if (waiting)
            Log.Info($"{LauncherConfig.GameDisplayName} se cerró; se continúa con la actualización.");
    }

    // Paso 5: intercambio con diario. Si algo falla antes de tocar game\, se deshace todo; si falla después, se restaura old\.
    private void SwapIn(string root, string work, string gameDir, string readyDir, InstalledGameInfo info, GameRelease release)
    {
        var journalPath = Path.Combine(work, SwapJournalFileName);
        var gameExists = Directory.Exists(gameDir);
        var gameEmpty = gameExists && IsDirectoryEmpty(gameDir);
        var foreign = gameExists && !gameEmpty && IsForeignContent(gameDir);
        string? oldDir = null;
        if (gameExists && !gameEmpty && !foreign)
            oldDir = UniquePath(Path.Combine(work, OldFolderName,
                DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)));

        try
        {
            WriteJournal(journalPath, new SwapJournal
            {
                OldDir = oldDir == null ? null : Path.GetRelativePath(work, oldDir),
                NewDir = ReadyFolderName,
                Tag = release.Release.TagName,
                StartedUtc = DateTimeOffset.UtcNow,
            });

            if (gameEmpty)
                Directory.Delete(gameDir);
            else if (foreign)
                MoveForeignToBackup(root, gameDir);
            else if (oldDir != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(oldDir)!);
                FileSystemUtil.MoveDirectoryWithRetry(gameDir, oldDir);
            }
        }
        catch (Exception ex)
        {
            // game\ sigue intacto (un rename fallido no mueve nada).
            Log.Error("No se pudo apartar la versión actual; se cancela la actualización.", ex);
            FileSystemUtil.TryDeleteFile(journalPath);
            DeleteTree(work, readyDir, allowInPlace: true);
            EmptyTrash(work);
            throw;
        }

        try
        {
            FileSystemUtil.MoveDirectoryWithRetry(readyDir, gameDir);
        }
        catch (Exception moveError)
        {
            Log.Error("No se pudo colocar la versión nueva en game\\.", moveError);
            if (oldDir != null)
            {
                try
                {
                    FileSystemUtil.MoveDirectoryWithRetry(oldDir, gameDir);
                    Log.Info("Se restauró la versión anterior.");
                }
                catch (Exception rollbackError)
                {
                    // Se conservan swap.json, ready\ y old\: el próximo Recover lo arregla.
                    Log.Error("Tampoco se pudo restaurar la versión anterior; queda pendiente para Recover.", rollbackError);
                    throw new LauncherException(SwapFailedMessage, moveError);
                }
            }

            FileSystemUtil.TryDeleteFile(journalPath);
            DeleteTree(work, readyDir, allowInPlace: true);
            EmptyTrash(work);
            throw;
        }

        if (!File.Exists(Path.Combine(gameDir, info.ExecutableName)))
        {
            Log.Error($"Tras el intercambio falta {info.ExecutableName} en {gameDir} (¿antivirus?). Se intenta restaurar la versión anterior.");
            // old\ y swap.json siguen ahí: Recover restaura la versión anterior (ahora mismo, y si no puede, en el próximo arranque).
            try { RecoverCore(root, work); }
            catch (Exception ex) { Log.Error("La restauración inmediata falló; se reintentará al arrancar.", ex); }
            throw new LauncherException(AntivirusMessage(info.ExecutableName, root));
        }

        if (!FileSystemUtil.TryDeleteFile(journalPath))
            Log.Warn("No se pudo borrar swap.json; Recover lo borrará más tarde.");
    }

    private static void CleanupAfterInstall(string work, string stagingRoot, string archivePath, bool keepArchive)
    {
        try
        {
            foreach (var dir in GetOldDirsNewestFirst(work))
                DeleteTree(work, dir, allowInPlace: false);
            DeleteDirectoryIfEmpty(Path.Combine(work, OldFolderName));
            DeleteTree(work, stagingRoot, allowInPlace: true);
            EmptyTrash(work);
            if (!keepArchive)
            {
                FileSystemUtil.TryDeleteFile(archivePath);
                FileDownloader.DeletePartial(archivePath);
            }
            FileSystemUtil.CleanupStaleTempFiles(work, StaleTempAge);
        }
        catch (Exception ex)
        {
            Log.Warn("La limpieza tras instalar no se completó; se reintentará en el próximo arranque.", ex);
        }
    }

    // ───────────────────────────── Recuperación ─────────────────────────────

    // Requiere el candado y el semáforo.
    private void RecoverCore(string root, string work)
    {
        var gameDir = GetGameDir(root);
        var journalPath = Path.Combine(work, SwapJournalFileName);
        var readyDir = Path.Combine(work, ReadyFolderName);
        var journal = TryReadJournal(journalPath);
        var journalNew = ResolveJournalPath(work, journal?.NewDir);
        var journalOld = ResolveJournalPath(work, journal?.OldDir);

        var gameValid = IsValidGameDir(gameDir);
        if (!gameValid)
        {
            // Candidatos por orden: ready\ del diario, old\ del diario y, como último recurso, el old\ válido más reciente.
            var candidates = new List<string>();
            if (journalNew != null) candidates.Add(journalNew);
            if (journalOld != null) candidates.Add(journalOld);
            candidates.AddRange(GetOldDirsNewestFirst(work));
            var candidate = candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(IsValidGameDir);

            if (candidate != null)
            {
                if (ClearInvalidGameDir(root, work, gameDir))
                {
                    try
                    {
                        FileSystemUtil.MoveDirectoryWithRetry(candidate, gameDir, 5, 300);
                        gameValid = true;
                        Log.Warn($"Recuperación: se restauró {Path.GetRelativePath(work, candidate)} como versión del juego.");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Recuperación: no se pudo restaurar {candidate}; se reintentará en el próximo arranque.", ex);
                    }
                }
            }
            else if (Directory.Exists(gameDir) && !IsDirectoryEmpty(gameDir))
            {
                Log.Info("Recuperación: game\\ no es válida y no hay ninguna versión anterior que restaurar.");
            }
        }

        if (gameValid)
        {
            foreach (var dir in GetOldDirsNewestFirst(work))
                DeleteTree(work, dir, allowInPlace: false);
            DeleteDirectoryIfEmpty(Path.Combine(work, OldFolderName));
            DeleteTree(work, readyDir, allowInPlace: true);
            if (File.Exists(journalPath) && !FileSystemUtil.TryDeleteFile(journalPath))
                Log.Warn("Recuperación: no se pudo borrar swap.json.");
        }
        else
        {
            // Sin juego válido: se conservan old\ y un ready\ válido para el próximo intento.
            var stillNeeded = (journalNew != null && Directory.Exists(journalNew))
                || (journalOld != null && Directory.Exists(journalOld));
            if (!stillNeeded && File.Exists(journalPath))
                FileSystemUtil.TryDeleteFile(journalPath);
            var readyNeeded = stillNeeded && journalNew != null
                && string.Equals(journalNew, readyDir, StringComparison.OrdinalIgnoreCase) && IsValidGameDir(readyDir);
            if (!readyNeeded)
                DeleteTree(work, readyDir, allowInPlace: true);
        }

        DeleteTree(work, Path.Combine(work, StagingFolderName), allowInPlace: true);
        EmptyTrash(work);
        FileSystemUtil.CleanupStaleTempFiles(work, StaleTempAge);
        var downloads = Path.Combine(work, DownloadsFolderName);
        if (Directory.Exists(downloads))
            FileSystemUtil.CleanupStaleTempFiles(downloads, StaleTempAge);
    }

    // Aparta un game\ no válido (vacío → se borra; ajeno → game.backup-…; resto → trash). True si game\ ya no existe.
    private bool ClearInvalidGameDir(string root, string work, string gameDir)
    {
        if (!Directory.Exists(gameDir))
            return true;
        try
        {
            if (IsDirectoryEmpty(gameDir))
            {
                Directory.Delete(gameDir);
                return true;
            }
            if (IsForeignContent(gameDir))
            {
                MoveForeignToBackup(root, gameDir);
                return !Directory.Exists(gameDir);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo apartar {gameDir}.", ex);
            return false;
        }
        return MoveToTrash(work, gameDir, attempts: 5) && !Directory.Exists(gameDir);
    }

    // ───────────────────────────── Desinstalar / mover ─────────────────────────────

    private void UninstallCore(string root, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var reporter = new ProgressReporter(progress);
        reporter.Report(InstallPhase.Uninstalling, null, "Desinstalando…");
        EnsureDriveReady(root);
        ct.ThrowIfCancellationRequested();

        if (!Directory.Exists(root))
        {
            Log.Info($"No hay nada que desinstalar en {root}.");
            return;
        }

        var gameDir = GetGameDir(root);
        var work = GetWorkDir(root);
        if (Directory.Exists(gameDir))
        {
            var exe = TryReadMarker(gameDir)?.ExecutableName ?? _executableName;
            if (CheckGameRunning(gameDir, exe))
                throw new LauncherException($"Cierra {LauncherConfig.GameDisplayName} antes de desinstalar.");
        }

        EnsureWorkDir(work);
        using (AcquireLockOrThrow(work))
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(gameDir))
            {
                if (IsDirectoryEmpty(gameDir))
                {
                    Directory.Delete(gameDir);
                }
                else if (IsForeignContent(gameDir))
                {
                    Log.Warn($"{gameDir} tiene contenido que no es del launcher: se deja en su sitio.");
                }
                else
                {
                    var trashPath = NewTrashPath(work);
                    Directory.CreateDirectory(Path.GetDirectoryName(trashPath)!);
                    try
                    {
                        FileSystemUtil.MoveDirectoryWithRetry(gameDir, trashPath);
                    }
                    catch (LauncherException ex)
                    {
                        throw new LauncherException(
                            $"No se pudo desinstalar: hay archivos de {LauncherConfig.GameDisplayName} en uso. Cierra el juego " +
                            "o cualquier ventana que tenga abierta esa carpeta y vuelve a intentarlo.", ex);
                    }
                }
            }
        } // el candado se libera justo antes del borrado final

        if (!FileSystemUtil.TryDeleteDirectory(work))
            Log.Warn($"Quedaron restos en {work}; se podrán borrar más tarde.");
        DeleteDirectoryIfEmpty(root);
        Log.Info($"Juego desinstalado de {root}.");
    }

    private async Task MoveCoreAsync(string from, string to, Func<string, Task>? commitNewRoot,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var reporter = new ProgressReporter(progress);
        reporter.Report(InstallPhase.Moving, null, "Moviendo…");
        ct.ThrowIfCancellationRequested();

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            throw new LauncherException("La carpeta de destino es la misma que la actual.");
        EnsureDriveReady(from);
        EnsureDriveReady(to);
        if (!Directory.Exists(from))
            throw new LauncherException($"No se encuentra la instalación actual en {from}.");

        var location = FileSystemUtil.CheckInstallLocation(to, Environment.ProcessPath);
        if (location.IsBlocked)
            throw new LauncherException(location.Reason ?? "No se puede mover el juego a esa carpeta. Elige otra.");
        if (location.IsWarning)
            Log.Warn($"Aviso sobre la carpeta de destino {to}: {location.Reason}");
        if (FileSystemUtil.IsSubPathOf(to, from))
            throw new LauncherException("No se puede mover el juego a una carpeta que está dentro de la instalación actual.");
        if (File.Exists(to) || (Directory.Exists(to) && !IsDirectoryEmpty(to)))
            throw new LauncherException("La carpeta de destino no está vacía. Elige una carpeta vacía o nueva.");

        var gameDir = GetGameDir(from);
        if (Directory.Exists(gameDir))
        {
            var exe = TryReadMarker(gameDir)?.ExecutableName ?? _executableName;
            if (CheckGameRunning(gameDir, exe))
                throw new LauncherException($"Cierra {LauncherConfig.GameDisplayName} antes de mover el juego.");
        }

        var work = GetWorkDir(from);
        EnsureWorkDir(work);
        var opLock = AcquireLockOrThrow(work);
        try
        {
            // Lo que no hace falta llevarse.
            DeleteTree(work, Path.Combine(work, StagingFolderName), allowInPlace: true);
            EmptyTrash(work);

            var parent = Path.GetDirectoryName(to);
            if (parent != null)
                Directory.CreateDirectory(parent);

            if (IsSameVolume(from, to))
            {
                if (Directory.Exists(to))
                    Directory.Delete(to); // vacía (comprobado arriba): Directory.Move exige que no exista
                opLock.Dispose(); // el candado vive dentro de la carpeta que se renombra
                if (TryRenameRoot(from, to))
                {
                    Log.Info($"Instalación movida a {to} (mismo volumen).");
                    reporter.Report(InstallPhase.Moving, 1, "Moviendo… 100 %");
                    if (commitNewRoot != null)
                        await CommitOrUndoRenameAsync(from, to, commitNewRoot).ConfigureAwait(false);
                    return;
                }
                // Misma letra pero otro volumen (punto de montaje o unión): se copia como entre unidades.
                opLock = AcquireLockOrThrow(work);
            }

            var totalBytes = FileSystemUtil.GetDirectorySize(from);
            EnsureFreeSpace(to, totalBytes);

            var temp = to + ".moving";
            if ((Directory.Exists(temp) && !FileSystemUtil.TryDeleteDirectory(temp)) || File.Exists(temp))
                throw new LauncherException($"No se puede usar la carpeta temporal {temp}. Bórrala y vuelve a intentarlo.");

            try
            {
                var copyProgress = new InlineProgress<(long Done, long Total)>(p =>
                {
                    double? fraction = p.Total > 0 ? Math.Clamp((double)p.Done / p.Total, 0, 1) : null;
                    var percent = (int)Math.Floor((fraction ?? 0) * 100);
                    reporter.Report(InstallPhase.Moving, fraction, $"Moviendo… {percent} %");
                });
                await FileSystemUtil.CopyDirectoryAsync(from, temp, copyProgress, ct).ConfigureAwait(false);

                var copiedBytes = FileSystemUtil.GetDirectorySize(temp);
                var sourceBytes = FileSystemUtil.GetDirectorySize(from);
                if (copiedBytes != sourceBytes)
                    throw new LauncherException(
                        $"La copia no coincide con el original ({FileSystemUtil.FormatBytes(copiedBytes)} de " +
                        $"{FileSystemUtil.FormatBytes(sourceBytes)}). No se ha movido nada; vuelve a intentarlo.");

                if (Directory.Exists(to))
                    Directory.Delete(to);
                FileSystemUtil.MoveDirectoryWithRetry(temp, to);
            }
            catch
            {
                if (!FileSystemUtil.TryDeleteDirectory(temp))
                    Log.Warn($"No se pudo borrar la copia parcial {temp}.");
                throw;
            }

            reporter.Report(InstallPhase.Moving, 1, "Moviendo… 100 %");
            if (commitNewRoot != null)
            {
                // La nueva ruta se guarda ANTES de tocar el origen: si no se puede, se deshace la copia.
                try
                {
                    await commitNewRoot(to).ConfigureAwait(false);
                }
                catch (Exception commitError)
                {
                    Log.Error($"No se pudo guardar la nueva carpeta {to}; se borra la copia y el juego sigue en {from}.", commitError);
                    if (!DeleteRootTree(to, allowInPlace: true))
                        Log.Warn($"No se pudo borrar la copia {to}.");
                    throw;
                }
            }

            opLock.Dispose(); // justo antes de borrar el origen
            // Sin callback la ruta antigua puede seguir guardada: si no se puede apartar, se deja entera.
            if (!DeleteRootTree(from, allowInPlace: commitNewRoot != null))
                Log.Warn($"La instalación se copió a {to}, pero quedaron restos en {from}.");
            Log.Info($"Instalación movida a {to} (copiando entre unidades).");
        }
        finally
        {
            opLock.Dispose();
        }
    }

    /// <summary>Rename de la raíz en el mismo volumen. False si en realidad es otro volumen (ERROR_NOT_SAME_DEVICE): toca copiar.</summary>
    private bool TryRenameRoot(string from, string to)
    {
        try
        {
            MoveRootDirectory(from, to);
            return true;
        }
        catch (Exception ex) when (IsNotSameDevice(ex))
        {
            Log.Info($"{to} está en otro volumen aunque tenga la misma letra (punto de montaje o unión); se copiará.");
            return false;
        }
    }

    /// <summary>¿Algún nivel de la excepción es ERROR_NOT_SAME_DEVICE (0x80070011)? Tal cual o envuelta (p.ej. en LauncherException).</summary>
    internal static bool IsNotSameDevice(Exception? ex)
    {
        for (var depth = 0; ex != null && depth < 8; ex = ex.InnerException, depth++)
        {
            if (ex.HResult == HResultNotSameDevice)
                return true;
        }
        return false;
    }

    /// <summary>Mismo volumen: guarda la nueva ruta; si el callback lanza, devuelve la instalación al origen y relanza.</summary>
    private static async Task CommitOrUndoRenameAsync(string from, string to, Func<string, Task> commitNewRoot)
    {
        try
        {
            await commitNewRoot(to).ConfigureAwait(false);
        }
        catch (Exception commitError)
        {
            Log.Error($"No se pudo guardar la nueva carpeta {to}; se devuelve la instalación a {from}.", commitError);
            try
            {
                FileSystemUtil.MoveDirectoryWithRetry(to, from, 5, 300);
                Log.Info($"Instalación devuelta a {from}.");
            }
            catch (Exception undoError)
            {
                Log.Error($"Tampoco se pudo devolver la instalación a {from}; queda en {to}.", undoError);
                throw new LauncherException(
                    $"El juego quedó en {to}, pero no se pudo guardar el cambio. Pulsa INSTALAR y elige esa carpeta para " +
                    "seguir usándolo sin descargarlo otra vez.", commitError);
            }
            throw;
        }
    }

    /// <summary>
    /// Borra una raíz de instalación entera. Primero la renombra a un hermano "&lt;raíz&gt;.deleting-&lt;8 hex&gt;" (mismo
    /// volumen): la ruta original queda libre de golpe y nunca a medio borrar. Si ese rename falla: allowInPlace → se borra
    /// en su sitio (best effort); si no, se deja entera. Una raíz de unidad nunca se borra. True si la ruta ya no existe.
    /// </summary>
    private static bool DeleteRootTree(string dir, bool allowInPlace)
    {
        if (!Directory.Exists(dir))
            return true;
        if (Path.GetDirectoryName(dir) == null)
        {
            Log.Warn($"{dir} es la raíz de una unidad: no se borra.");
            return false;
        }

        var doomed = UniquePath(dir + DeletingSuffix + RandomHex8());
        try
        {
            FileSystemUtil.MoveDirectoryWithRetry(dir, doomed, 5, 300);
        }
        catch (Exception ex)
        {
            if (!allowInPlace)
            {
                Log.Warn($"No se pudo apartar {dir} para borrarlo; se deja entero.", ex);
                return false;
            }
            Log.Warn($"No se pudo apartar {dir} para borrarlo; se borra en su sitio.", ex);
            doomed = dir;
        }

        if (!FileSystemUtil.TryDeleteDirectory(doomed))
            Log.Warn($"Quedaron restos en {doomed}; se pueden borrar a mano.");
        return !Directory.Exists(dir);
    }

    /// <summary>
    /// Envuelve el callback para que se ejecute en <paramref name="context"/> (como el código que sigue a un await del
    /// llamador). Sin contexto se usa tal cual.
    /// </summary>
    private static Func<string, Task> BindToCallerContext(Func<string, Task> callback, SynchronizationContext? context)
    {
        if (context == null)
            return callback;
        return newRoot =>
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Post(async _ =>
            {
                try
                {
                    await callback(newRoot);
                    done.TrySetResult();
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            }, null);
            return done.Task;
        };
    }

    // ───────────────────────────── Marcador, diario y validaciones ─────────────────────────────

    private static InstalledGameInfo CreateMarker(GameRelease release, string exeName) => new()
    {
        SchemaVersion = 1,
        Tag = release.Release.TagName,
        ReleaseId = release.Release.Id,
        Version = release.Version?.ToString(),
        VersionDisplay = release.VersionDisplay,
        AssetName = release.Asset.Name,
        AssetId = release.Asset.Id,
        AssetSize = release.Asset.Size,
        Sha256 = NormalizeSha(release.Asset.Sha256),
        ExecutableName = exeName,
        InstalledAtUtc = DateTimeOffset.UtcNow,
    };

    private static void WriteMarker(string gameDir, InstalledGameInfo info) =>
        FileSystemUtil.WriteAllTextAtomic(Path.Combine(gameDir, InstalledGameInfo.MarkerFileName),
            JsonSerializer.Serialize(info, MarkerJsonOptions));

    /// <summary>Marcador parseado (con ExecutableName seguro) o null. No comprueba que el exe exista.</summary>
    private static InstalledGameInfo? TryReadMarker(string gameDir)
    {
        var path = Path.Combine(gameDir, InstalledGameInfo.MarkerFileName);
        try
        {
            if (!File.Exists(path))
                return null;
            var info = JsonSerializer.Deserialize<InstalledGameInfo>(ReadAllTextShared(path), MarkerJsonOptions);
            if (info == null || !IsSafeRelativeFile(info.ExecutableName))
            {
                Log.Warn($"Marcador no válido en {path}.");
                return null;
            }
            info.ExecutableName = info.ExecutableName.Trim();
            return info;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warn($"No se pudo leer el marcador {path}.", ex);
            return null;
        }
    }

    private static InstalledGameInfo? TryReadValidMarker(string gameDir)
    {
        if (!Directory.Exists(gameDir))
            return null;
        var info = TryReadMarker(gameDir);
        return info != null && File.Exists(Path.Combine(gameDir, info.ExecutableName)) ? info : null;
    }

    private static bool IsValidGameDir(string dir) => TryReadValidMarker(dir) != null;

    private static bool IsSameContentDifferentIdentity(InstalledGameInfo installed, GameRelease release)
    {
        var installedSha = NormalizeSha(installed.Sha256);
        var releaseSha = NormalizeSha(release.Asset.Sha256);
        if (installedSha == null || releaseSha == null || !string.Equals(installedSha, releaseSha, StringComparison.Ordinal))
            return false;
        return installed.AssetId != release.Asset.Id
            || installed.ReleaseId != release.Release.Id
            || !string.Equals(installed.AssetName, release.Asset.Name, StringComparison.Ordinal)
            || !string.Equals(installed.Tag, release.Release.TagName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Contenido ajeno: game\ no vacía, sin marcador que se parsee, sin el exe configurado y sin ningún *_Data\app.info.
    /// Ante cualquier duda (error al leer) se considera ajeno: así nunca se borra.
    /// </summary>
    private bool IsForeignContent(string gameDir)
    {
        try
        {
            if (!Directory.Exists(gameDir) || IsDirectoryEmpty(gameDir))
                return false;
            if (TryReadMarker(gameDir) != null)
                return false;
            if (File.Exists(Path.Combine(gameDir, _executableName)))
                return false;
            foreach (var dataDir in Directory.EnumerateDirectories(gameDir, "*_Data"))
            {
                if (File.Exists(Path.Combine(dataDir, UnityAppInfoFileName)))
                    return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo examinar {gameDir}; se trata como contenido ajeno (no se borrará).", ex);
            return true;
        }
    }

    /// <summary>
    /// Carpeta (bajo <paramref name="stagingDir"/>) del exe configurado menos profundo según la lista de archivos extraídos
    /// (hasta ContentRootMaxDepth, sin __MACOSX). Null si el archivo no traía el exe. No mira el disco a propósito.
    /// </summary>
    private string? FindContentRootFromFiles(string stagingDir, IReadOnlyList<ExtractedFile> files)
    {
        string? best = null;
        var bestDepth = int.MaxValue;
        foreach (var file in files)
        {
            if (string.IsNullOrEmpty(file.RelativePath)
                || !Path.GetFileName(file.RelativePath).Equals(_executableName, StringComparison.OrdinalIgnoreCase))
                continue;
            var dir = Path.GetDirectoryName(file.RelativePath) ?? "";
            var segments = dir.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > ContentRootMaxDepth
                || segments.Any(s => s.Equals(MacOsJunkFolder, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (segments.Length < bestDepth
                || (segments.Length == bestDepth && StringComparer.OrdinalIgnoreCase.Compare(dir, best) < 0))
            {
                best = dir;
                bestDepth = segments.Length;
            }
        }
        if (best == null)
            return null;

        var stagingFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDir));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(stagingFull, best)));
        return candidate.Equals(stagingFull, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(stagingFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    /// <summary>
    /// Build sin el exe configurado: el único .exe (que no sea UnityCrashHandler*) directamente en la raíz de contenido,
    /// contando lo que había en el archivo aunque el antivirus ya lo haya borrado (la verificación lo detecta). Null si no
    /// hay exactamente uno.
    /// </summary>
    private string? ResolveExecutableName(string stagingDir, string contentRoot, IReadOnlyList<ExtractedFile> files)
    {
        if (File.Exists(Path.Combine(contentRoot, _executableName)))
            return _executableName;

        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
        var fromArchive = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var full = Path.GetFullPath(Path.Combine(stagingDir, file.RelativePath));
                if (string.Equals(Path.GetDirectoryName(full), rootFull, StringComparison.OrdinalIgnoreCase))
                    fromArchive.Add(Path.GetFileName(full));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ruta rara: no cuenta como candidato.
            }
        }

        var candidates = Directory.EnumerateFiles(contentRoot)
            .Select(f => Path.GetFileName(f))
            .Concat(fromArchive)
            .Where(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !n.StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private sealed class SwapJournal
    {
        public string? OldDir { get; set; }
        public string? NewDir { get; set; }
        public string? Tag { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
    }

    private static void WriteJournal(string journalPath, SwapJournal journal) =>
        FileSystemUtil.WriteAllTextAtomic(journalPath, JsonSerializer.Serialize(journal, JournalJsonOptions));

    private static SwapJournal? TryReadJournal(string journalPath)
    {
        try
        {
            if (!File.Exists(journalPath))
                return null;
            return JsonSerializer.Deserialize<SwapJournal>(ReadAllTextShared(journalPath), JournalJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warn($"swap.json ilegible ({journalPath}); se ignora.", ex);
            return null;
        }
    }

    /// <summary>
    /// Ruta del diario → ruta real dentro de .launcher. Acepta relativas a .launcher y absolutas; una absoluta de otra
    /// ubicación (instalación movida, letra de unidad cambiada) se reubica por la parte tras "\.launcher\".
    /// Cualquier cosa fuera de .launcher se ignora.
    /// </summary>
    private static string? ResolveJournalPath(string work, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;
        try
        {
            var workFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(work));
            var prefix = workFull + Path.DirectorySeparatorChar;
            string candidate;
            if (Path.IsPathRooted(stored))
            {
                candidate = Path.GetFullPath(stored);
                if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var marker = Path.DirectorySeparatorChar + WorkFolderName + Path.DirectorySeparatorChar;
                    var index = candidate.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (index < 0)
                        return null;
                    candidate = Path.GetFullPath(Path.Combine(workFull, candidate[(index + marker.Length)..]));
                }
            }
            else
            {
                candidate = Path.GetFullPath(Path.Combine(workFull, stored));
            }

            candidate = Path.TrimEndingDirectorySeparator(candidate);
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"swap.json apunta fuera de .launcher ('{stored}'); se ignora.");
                return null;
            }
            return candidate;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Log.Warn($"Ruta no válida en swap.json ('{stored}').", ex);
            return null;
        }
    }

    // ───────────────────────────── Utilidades ─────────────────────────────

    private DownloadRequest BuildDownloadRequest(string root, GameRelease release, long maxBytesPerSecond)
    {
        var asset = release.Asset;
        var sha = NormalizeSha(asset.Sha256);
        var fileName = asset.Id.ToString(CultureInfo.InvariantCulture) + "-" + SanitizeFileName(asset.Name);
        return new DownloadRequest
        {
            Url = asset.DownloadUrl,
            DestinationPath = Path.Combine(GetWorkDir(root), DownloadsFolderName, fileName),
            ExpectedSize = asset.Size > 0 ? asset.Size : null,
            ExpectedSha256 = sha,
            MaxBytesPerSecond = Math.Max(0, maxBytesPerSecond),
            ResumeKey = "asset:" + asset.Id.ToString(CultureInfo.InvariantCulture) + ":" +
                (sha != null ? "sha256:" + sha : "size:" + asset.Size.ToString(CultureInfo.InvariantCulture)),
        };
    }

    private static long GetAlreadyDownloadedBytes(DownloadRequest request, long assetSize)
    {
        try
        {
            if (assetSize > 0 && File.Exists(request.DestinationPath) && new FileInfo(request.DestinationPath).Length == assetSize)
                return assetSize;
            return Math.Max(0, FileDownloader.GetResumableBytes(request));
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo comprobar lo ya descargado.", ex);
            return 0;
        }
    }

    // Borra de downloads\ lo que no pertenezca al asset actual (archivo, .part, .part.json y sus .tmp).
    private static void PruneDownloads(string downloadsDir, string keepFileName)
    {
        try
        {
            if (!Directory.Exists(downloadsDir))
                return;
            foreach (var file in Directory.EnumerateFiles(downloadsDir))
            {
                var name = Path.GetFileName(file);
                if (name.Equals(keepFileName, StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(keepFileName + ".", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (FileSystemUtil.TryDeleteFile(file))
                    Log.Info($"Descarga antigua borrada: {name}");
            }
            foreach (var dir in Directory.EnumerateDirectories(downloadsDir))
                FileSystemUtil.TryDeleteDirectory(dir);
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudieron limpiar las descargas antiguas de {downloadsDir}.", ex);
        }
    }

    private static bool HasPendingDownload(string root)
    {
        try
        {
            var downloads = Path.Combine(GetWorkDir(root), DownloadsFolderName);
            return Directory.Exists(downloads)
                && Directory.EnumerateFiles(downloads).Any(f => f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo comprobar si hay descargas pendientes.", ex);
            return false;
        }
    }

    private bool CheckGameRunning(string gameDir, string exeName)
    {
        try
        {
            return IsGameRunningFrom(gameDir, exeName);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo comprobar si el juego está abierto; se asume que no.", ex);
            return false;
        }
    }

    private static void EnsureDriveReady(string path)
    {
        if (!FileSystemUtil.IsDriveReady(path))
            throw new LauncherException(DriveMissingMessage(path));
    }

    private static string DriveMissingMessage(string path) =>
        $"No se encuentra la unidad {DriveLabel(path)}. Conéctala o elige otra carpeta en Ajustes.";

    private void EnsureFreeSpace(string path, long requiredBytes)
    {
        if (requiredBytes <= 0)
            return;
        var free = GetFreeSpace(path);
        if (free < requiredBytes)
            throw new LauncherException(
                $"Necesitas al menos {FileSystemUtil.FormatBytes(requiredBytes)} libres en {DriveLabel(path)} " +
                $"(tienes {FileSystemUtil.FormatBytes(free)}).");
    }

    private static string DriveLabel(string path)
    {
        try { return Path.GetPathRoot(path) is { Length: > 0 } r ? r : path; }
        catch (ArgumentException) { return path; }
    }

    // Crea .launcher y la marca como oculta (best effort).
    private static void EnsureWorkDir(string work)
    {
        var dir = Directory.CreateDirectory(work);
        try
        {
            if ((dir.Attributes & FileAttributes.Hidden) == 0)
                dir.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception ex)
        {
            Log.Debug($"No se pudo ocultar {work}: {ex.Message}");
        }
    }

    private static FileStream AcquireLockOrThrow(string work)
    {
        var stream = TryAcquireLock(work, out var busy, out var error);
        if (stream != null)
            return stream;
        if (busy)
            throw new LauncherException(LockedMessage, error);
        throw new LauncherException($"No se puede escribir en {work}. Comprueba los permisos de la carpeta.", error);
    }

    /// <summary>Abre operation.lock en exclusiva (se borra solo al cerrarse). Null si está ocupado (busy) o falla.</summary>
    private static FileStream? TryAcquireLock(string work, out bool busy, out Exception? error)
    {
        var path = Path.Combine(work, LockFileName);
        busy = false;
        error = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Directory.CreateDirectory(work);
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                busy = true;
                error = ex;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Acceso denegado puede ser un borrado pendiente del candado anterior: se reintenta.
                busy = false;
                error = ex;
            }
            if (attempt < 3)
                Thread.Sleep(100);
        }
        return null;
    }

    private static bool IsSharingViolation(IOException ex) =>
        ex.HResult == unchecked((int)0x80070020) || ex.HResult == unchecked((int)0x80070021);

    private static string NewTrashPath(string work) => Path.Combine(work, TrashFolderName, RandomHex8());

    // Mueve un árbol a trash\ y lo intenta borrar. False si no se pudo mover (sigue donde estaba).
    private static bool MoveToTrash(string work, string dir, int attempts = 3)
    {
        if (!Directory.Exists(dir))
            return true;
        var destination = NewTrashPath(work);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            FileSystemUtil.MoveDirectoryWithRetry(dir, destination, attempts, 300);
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo mover {dir} a la papelera interna.", ex);
            return false;
        }
        if (!FileSystemUtil.TryDeleteDirectory(destination))
            Log.Info($"Quedan archivos bloqueados en {destination}; se borrarán más tarde.");
        return true;
    }

    // Borra un árbol vía trash\. allowInPlace: si el rename falla, se borra en su sitio (solo para lo que nunca se restaura).
    private static void DeleteTree(string work, string dir, bool allowInPlace)
    {
        try
        {
            if (!Directory.Exists(dir))
                return;
            if (MoveToTrash(work, dir))
                return;
            if (allowInPlace && !FileSystemUtil.TryDeleteDirectory(dir))
                Log.Warn($"No se pudo borrar {dir}; se reintentará más tarde.");
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo borrar {dir}.", ex);
        }
    }

    private static void EmptyTrash(string work)
    {
        var trash = Path.Combine(work, TrashFolderName);
        try
        {
            if (!Directory.Exists(trash))
                return;
            foreach (var dir in Directory.EnumerateDirectories(trash).ToList())
                FileSystemUtil.TryDeleteDirectory(dir);
            foreach (var file in Directory.EnumerateFiles(trash).ToList())
                FileSystemUtil.TryDeleteFile(file);
            DeleteDirectoryIfEmpty(trash);
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo vaciar {trash}.", ex);
        }
    }

    /// <summary>Carpetas de old\ de la más reciente a la más antigua (por el nombre yyyyMMddHHmmss[-n]).</summary>
    private static List<string> GetOldDirsNewestFirst(string work)
    {
        var oldRoot = Path.Combine(work, OldFolderName);
        try
        {
            if (!Directory.Exists(oldRoot))
                return new List<string>();
            return Directory.EnumerateDirectories(oldRoot)
                .OrderByDescending(d => OldDirSortKey(Path.GetFileName(d)), StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo listar {oldRoot}.", ex);
            return new List<string>();
        }
    }

    // "20261019134501-2" → "20261019134501-0002"; nombres raros → "" (los más antiguos).
    private static string OldDirSortKey(string name)
    {
        var parts = name.Split('-', 2);
        if (parts[0].Length != 14 || !parts[0].All(char.IsAsciiDigit))
            return "";
        var suffix = parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return parts[0] + "-" + suffix.ToString("D4", CultureInfo.InvariantCulture);
    }

    private static string MoveForeignToBackup(string root, string gameDir)
    {
        var destination = UniquePath(Path.Combine(root,
            BackupFolderPrefix + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)));
        FileSystemUtil.MoveDirectoryWithRetry(gameDir, destination);
        Log.Warn($"{gameDir} tenía archivos que no son del launcher: se apartaron a {destination} (no se borran).");
        return destination;
    }

    private static string UniquePath(string path)
    {
        var candidate = path;
        for (var i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
            candidate = path + "-" + i.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    private static bool IsDirectoryEmpty(string dir) => !Directory.EnumerateFileSystemEntries(dir).Any();

    private static void DeleteDirectoryIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && IsDirectoryEmpty(dir))
                Directory.Delete(dir);
        }
        catch (Exception ex)
        {
            Log.Debug($"No se pudo borrar la carpeta vacía {dir}: {ex.Message}");
        }
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string RandomHex8() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    private static string? NormalizeSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
            return null;
        var value = sha.Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            value = value["sha256:".Length..];
        return value.ToLowerInvariant();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (string.IsNullOrWhiteSpace(name) ? "build" : name.Trim())
            .Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        var safe = new string(chars).Trim('.', ' ');
        if (safe.Length == 0)
            safe = "build";
        if (safe.Length > 80)
        {
            var extension = Path.GetExtension(safe);
            safe = safe[..(80 - Math.Min(extension.Length, 20))] + extension[..Math.Min(extension.Length, 20)];
        }
        return safe;
    }

    private static bool IsSafeRelativeFile(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return false;
        var value = relative.Trim();
        if (Path.IsPathRooted(value) || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return false;
        var segments = value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.All(s => s.Length > 0 && s != "." && s != ".." && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    private static bool IsSafeFolderName(string name) =>
        name.Length > 0 && name != "." && name != ".." && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string AntivirusMessage(string fileName, string root) =>
        $"El antivirus bloqueó o borró archivos del juego ({fileName}). Añade una exclusión para {root} en tu " +
        "antivirus y vuelve a intentarlo.";

    /// <summary>Normaliza una ruta absoluta (sin separador final). Lanza ArgumentException si no es válida.</summary>
    private static string NormalizeRoot(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Path.IsPathFullyQualified(installRoot.Trim()))
            throw new ArgumentException($"Ruta de instalación no válida: '{installRoot}'.", nameof(installRoot));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot.Trim()));
    }

    private static string NormalizeRootOrThrow(string installRoot)
    {
        try
        {
            return NormalizeRoot(installRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            throw new LauncherException("La carpeta de instalación no es válida. Elige otra en Ajustes.", ex);
        }
    }

    /// <summary>Operación que falló (elige el texto del error para el usuario).</summary>
    internal enum InstallerOperation { Install, Uninstall, Move }

    /// <summary>
    /// Registra el fallo y lo convierte en LauncherException si es un error de disco esperable, con un texto en español
    /// propio de la operación (el texto de .NET va solo al log). Cancelaciones y LauncherException pasan tal cual; lo
    /// inesperado también (la UI muestra el mensaje genérico). <paramref name="root"/>: InstallRoot (al mover, el origen);
    /// <paramref name="destination"/>: destino al mover (solo para detectar una unidad desconectada).
    /// </summary>
    internal static Exception TranslateFailure(InstallerOperation operation, string root, Exception ex, string? destination = null)
    {
        var action = operation switch
        {
            InstallerOperation.Install => "instalar el juego",
            InstallerOperation.Uninstall => "desinstalar el juego",
            _ => "mover el juego",
        };
        switch (ex)
        {
            case OperationCanceledException:
                Log.Info($"Operación cancelada ({action}).");
                return ex;
            case LauncherException:
                Log.Warn($"No se pudo {action}: {ex.Message}", ex.InnerException);
                return ex;
            case IOException { HResult: HResultDiskFull or HResultHandleDiskFull }:
                Log.Error($"Disco lleno al {action}.", ex);
                return new LauncherException("No hay espacio suficiente en el disco. Libera espacio y vuelve a intentarlo.", ex);
            case IOException { HResult: HResultVirusInfected or HResultVirusDeleted }:
                Log.Error($"El antivirus bloqueó un archivo al {action}.", ex);
                return new LauncherException(operation == InstallerOperation.Move
                    ? "El antivirus bloqueó archivos del juego. Añade una exclusión para la carpeta del juego en tu antivirus y vuelve a intentarlo."
                    : $"El antivirus bloqueó archivos del juego. Añade una exclusión para {root} en tu antivirus y vuelve a intentarlo.", ex);
            case IOException or UnauthorizedAccessException when FindMissingDrive(root, destination) is { } missing:
                Log.Error($"La unidad de {missing} dejó de estar disponible al {action}.", ex);
                return new LauncherException(DriveMissingMessage(missing), ex);
            case UnauthorizedAccessException:
            case IOException { HResult: HResultSharingViolation or HResultLockViolation }:
                Log.Error($"Windows no dejó modificar archivos al {action}.", ex);
                return new LauncherException(InUseMessage(operation, root), ex);
            case IOException:
                Log.Error($"Error de disco al {action}.", ex);
                return new LauncherException($"Error de disco al {action}. Vuelve a intentarlo; si se repite, pulsa «Crear informe».", ex);
            default:
                Log.Error($"Error inesperado al {action}.", ex);
                return ex;
        }
    }

    // Acceso denegado o archivo en uso: el consejo depende de la operación (al mover no se culpa a una carpeta concreta).
    private static string InUseMessage(InstallerOperation operation, string root) => operation switch
    {
        InstallerOperation.Install =>
            $"Windows no deja modificar archivos en {root}. Cierra el juego y cualquier ventana de esa carpeta y vuelve a " +
            "intentarlo; si se repite, elige otra carpeta.",
        InstallerOperation.Uninstall =>
            $"Windows no deja modificar {root}. Cierra el juego y cualquier ventana de esa carpeta y vuelve a intentarlo.",
        _ => "Windows no deja mover el juego. Cierra el juego y cualquier ventana de la carpeta actual o de la de destino " +
            "y vuelve a intentarlo.",
    };

    private static string? FindMissingDrive(string root, string? destination)
    {
        if (!FileSystemUtil.IsDriveReady(root))
            return root;
        return destination != null && !FileSystemUtil.IsDriveReady(destination) ? destination : null;
    }

    // ───────────────────────────── Progreso ─────────────────────────────

    /// <summary>Envía el progreso al llamador sin dejar que un fallo del receptor rompa la operación.</summary>
    private sealed class ProgressReporter(IProgress<InstallProgress>? target)
    {
        private int _failureLogged;

        public void Report(InstallPhase phase, double? fraction, string status, DownloadProgress? download = null) =>
            Report(new InstallProgress(phase, fraction, status, download));

        public void Report(InstallProgress value)
        {
            if (target == null)
                return;
            try
            {
                target.Report(value);
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
                    Log.Warn("Error al notificar el progreso.", ex);
            }
        }
    }

    /// <summary>IProgress síncrono (Progress&lt;T&gt; reordenaría los avisos en el pool de hilos).</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    /// <summary>Traduce progreso/reintentos/verificación del descargador a textos para la UI.</summary>
    private sealed class DownloadStatusTracker(ProgressReporter reporter, long assetSize)
    {
        private readonly object _sync = new();
        private DownloadProgress? _last;
        private DownloadRetryInfo? _retry;
        private long _bytesAtRetry;
        private DateTime _retryAtUtc;
        private bool _verifying;
        private long _bytesAtVerification;

        public void ReportStart(long resumableBytes)
        {
            double? fraction = assetSize > 0 ? Math.Clamp((double)resumableBytes / assetSize, 0, 1) : null;
            var status = resumableBytes > 0 && assetSize > 0
                ? $"Descargando… {FileSystemUtil.FormatBytes(resumableBytes)} de {FileSystemUtil.FormatBytes(assetSize)}"
                : "Descargando…";
            reporter.Report(InstallPhase.Downloading, fraction, status);
        }

        public void OnProgress(DownloadProgress p)
        {
            string status;
            lock (_sync)
            {
                if (_verifying)
                {
                    // El aviso final llega tras verificar (se ignora); si los bytes bajan, la verificación falló y
                    // el descargador volvió a empezar desde cero.
                    if (p.BytesReceived >= _bytesAtVerification)
                        return;
                    _verifying = false;
                }
                _last = p;
                if (_retry is { } retry && p.BytesReceived <= _bytesAtRetry)
                {
                    status = RetryStatus(retry);
                }
                else
                {
                    _retry = null;
                    status = FormatDownloadStatus(p);
                }
            }
            reporter.Report(new InstallProgress(InstallPhase.Downloading, p.Fraction, status, p));
        }

        public void OnRetry(DownloadRetryInfo retry)
        {
            DownloadProgress? last;
            string status;
            lock (_sync)
            {
                _retry = retry;
                _bytesAtRetry = _last?.BytesReceived ?? 0;
                _retryAtUtc = DateTime.UtcNow + retry.Delay;
                last = _last;
                status = RetryStatus(retry);
            }
            Log.Info($"Descarga: reintento {retry.Attempt} de {retry.MaxAttempts} en {retry.Delay.TotalSeconds:0.#} s ({retry.Reason}).");
            reporter.Report(new InstallProgress(InstallPhase.Downloading, last?.Fraction, status, last));
        }

        public void OnVerificationStarted()
        {
            lock (_sync)
            {
                _verifying = true;
                _bytesAtVerification = Math.Max(_last?.BytesReceived ?? 0, assetSize);
            }
            reporter.Report(InstallPhase.Verifying, null, "Verificando archivo…");
        }

        private string RetryStatus(DownloadRetryInfo retry)
        {
            // El motivo ya viene en español desde FileDownloader ("Conexión perdida", "No se pudo escribir en el disco"…).
            var reason = string.IsNullOrWhiteSpace(retry.Reason) ? "Conexión perdida" : retry.Reason.TrimEnd('.', ' ');
            var remaining = _retryAtUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.FromMilliseconds(500) && retry.Delay > TimeSpan.Zero)
                return $"{reason}. Reconectando… (intento {retry.Attempt} de {retry.MaxAttempts})";
            var seconds = (int)Math.Ceiling(Math.Max(0, (remaining > retry.Delay ? retry.Delay : remaining).TotalSeconds));
            return $"{reason}. Reintentando en {seconds} s (intento {retry.Attempt} de {retry.MaxAttempts})…";
        }

        private static string FormatDownloadStatus(DownloadProgress p)
        {
            var text = p.TotalBytes is > 0
                ? $"Descargando… {FileSystemUtil.FormatBytes(p.BytesReceived)} de {FileSystemUtil.FormatBytes(p.TotalBytes.Value)}"
                : $"Descargando… {FileSystemUtil.FormatBytes(p.BytesReceived)}";
            if (p.BytesPerSecond > 0)
                text += " · " + FileSystemUtil.FormatSpeed(p.BytesPerSecond);
            if (p.Eta is { } eta && p.BytesPerSecond > 0)
                text += " · " + FileSystemUtil.FormatDuration(eta) + " restantes";
            return text;
        }
    }
}
