using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using PalitoLauncher.Core.Downloads;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Core.Versioning;

namespace PalitoLauncher.Core.Update;

/// <summary>
/// La versión nueva del launcher se descargó e instaló pero no llegó a arrancar (no señaló UpdateAlive). Es el ÚNICO
/// fallo de auto-actualización por el que la UI debe omitir esa versión; los errores de red/descarga no. El mensaje dice
/// si se restauró la versión anterior o si hay que descargarla de nuevo.
/// </summary>
public sealed class LauncherStartFailedException : LauncherException
{
    public LauncherStartFailedException(string userMessage)
        : base(userMessage)
    {
    }
}

public sealed class LauncherUpdateInfo
{
    public LauncherUpdateInfo(GameVersion version, ReleaseInfo release, ReleaseAsset asset)
    {
        Version = version;
        Release = release;
        Asset = asset;
    }

    public GameVersion Version { get; }
    public ReleaseInfo Release { get; }
    public ReleaseAsset Asset { get; }
    public string VersionText => Version.ToString();
}

/// <summary>
/// Auto-actualización del launcher desde las releases de LauncherConfig.LauncherRepoOwner/LauncherRepoName.
/// Técnica estándar en Windows: un .exe en ejecución no se puede sobrescribir pero sí renombrar.
///   1. Descarga el exe nuevo a "&lt;exe&gt;.new" (FileDownloader, verificando SHA-256 si GitHub lo da) y comprueba que
///      empieza por "MZ".
///   2. Renombra el exe actual a "&lt;exe&gt;.old" y "&lt;exe&gt;.new" a "&lt;exe&gt;" (FileSystemUtil.MoveFileWithRetry; si falla el
///      segundo paso, deshace el primero, también con reintentos).
///   3. Crea el EventWaitHandle con nombre <see cref="UpdateAliveEventName"/> y arranca el exe nuevo con
///      "--post-update &lt;pidActual&gt;". Espera hasta 45 s a que el nuevo proceso señale el evento (lo hace con
///      <see cref="SignalAlive"/> como PRIMERA instrucción de su arranque). Si el proceso muere o vence el tiempo: lo mata si
///      sigue vivo, renombra el exe nuevo a "&lt;exe&gt;.failed", devuelve "&lt;exe&gt;.old" a "&lt;exe&gt;" (si no se puede renombrar,
///      lo copia) y lanza <see cref="LauncherStartFailedException"/>: "La nueva versión del launcher no pudo iniciarse; se
///      restauró la anterior." o, si tampoco se pudo restaurar, "…y no se pudo recuperar la anterior. Descárgalo de nuevo
///      desde GitHub.".
///   4. Si señala → devuelve true y el llamador cierra la app. La versión nueva llama a CleanupOldVersions() cuando su
///      ventana principal ya se ha mostrado (no antes: mientras exista "&lt;exe&gt;.old" se puede volver atrás con
///      <see cref="TryRestorePreviousVersion(string)"/> si la ventana no llega a abrirse).
/// </summary>
public sealed class LauncherSelfUpdater
{
    public const string PostUpdateArgument = "--post-update";

    /// <summary>Evento con nombre que la versión nueva señala nada más arrancar.</summary>
    public const string UpdateAliveEventName = @"Local\PalitoLauncher.UpdateAlive";

    /// <summary>Nombre del ensamblado de la app WPF (para distinguir el exe publicado de hosts como testhost/dotnet).</summary>
    internal const string AppAssemblyName = "PalitoLauncher";

    internal const string CheckStateFileName = "launcher-update-check.json";

    private const string StartFailedMessage = "La nueva versión del launcher no pudo iniciarse; se restauró la anterior.";

    private const string RestoreFailedMessage =
        "La nueva versión del launcher no pudo iniciarse y no se pudo recuperar la anterior. Descárgalo de nuevo desde GitHub.";

    private static readonly TimeSpan DefaultAliveTimeout = TimeSpan.FromSeconds(45);

    private static readonly Lazy<GameVersion> s_currentVersion = new(ResolveCurrentVersion);

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    private readonly GitHubClient _github;
    private readonly FileDownloader _downloader;
    private readonly string _cacheDir;
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    public LauncherSelfUpdater(GitHubClient github, FileDownloader downloader, string cacheDir)
    {
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDir);
        _github = github;
        _downloader = downloader;
        _cacheDir = cacheDir;
    }

    /// <summary>Versión del launcher en ejecución (AssemblyInformationalVersion del ensamblado de entrada, sin "+metadata").</summary>
    public static GameVersion CurrentVersion => s_currentVersion.Value;

    /// <summary>Texto de la versión actual ("1.0.0").</summary>
    public static string CurrentVersionText => CurrentVersion.ToString();

    /// <summary>Ruta del exe en ejecución (Environment.ProcessPath).</summary>
    public static string? CurrentExePath => Environment.ProcessPath;

    /// <summary>Solo tests: versión "actual" con la que compara <see cref="CheckAsync"/> (null = <see cref="CurrentVersion"/>).</summary>
    internal GameVersion? CurrentVersionOverride { get; set; }

    /// <summary>Solo tests: reloj inyectable para el intervalo entre comprobaciones.</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Solo tests: tiempo máximo de espera a que la versión nueva confirme que vive.</summary>
    internal TimeSpan AliveTimeout { get; set; } = DefaultAliveTimeout;

    private GameVersion EffectiveCurrentVersion => CurrentVersionOverride ?? CurrentVersion;

    private string CheckStatePath => Path.Combine(_cacheDir, CheckStateFileName);

    /// <summary>Señala <see cref="UpdateAliveEventName"/> si existe (la instancia vieja está esperando). Nunca lanza.</summary>
    public static void SignalAlive()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(UpdateAliveEventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (Exception ex)
        {
            // Normalmente aún no hay log inicializado: va a Debug.
            Log.Warn("No se pudo señalar el evento de actualización.", ex);
        }
    }

    /// <summary>
    /// Borra junto al exe actual "&lt;exe&gt;.old", "&lt;exe&gt;.new", "&lt;exe&gt;.failed", "&lt;exe&gt;.new.part" y
    /// "&lt;exe&gt;.new.part.json" (en segundo plano, reintentando durante ~10 s porque el proceso viejo puede seguir cerrándose).
    /// Llamar cuando la ventana principal ya se ha mostrado: "&lt;exe&gt;.old" es la copia para volver atrás si la versión
    /// nueva no abre su ventana. Nunca lanza.
    /// </summary>
    public static void CleanupOldVersions()
    {
        try
        {
            var exe = CurrentExePath;
            if (string.IsNullOrEmpty(exe) || IsDotnetHost(exe))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await CleanupLeftoversAsync(exe, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn("Error limpiando versiones antiguas del launcher.", ex);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo programar la limpieza de versiones antiguas del launcher.", ex);
        }
    }

    /// <summary>
    /// False (con motivo en español) si no se puede auto-actualizar: ejecutándose con "dotnet" (desarrollo), el nombre del
    /// proceso no es un exe propio, o la carpeta del exe no es escribible (p.ej. Program Files).
    /// </summary>
    public static bool CanSelfUpdate(out string? reason)
    {
        try
        {
            var exe = CurrentExePath;
            if (string.IsNullOrEmpty(exe))
            {
                reason = "No se pudo determinar dónde está el launcher.";
                return false;
            }

            if (IsDotnetHost(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Modo desarrollo: el launcher se está ejecutando con dotnet.";
                return false;
            }

            if (!IsPublishedLauncherExe(exe))
            {
                reason = "Modo desarrollo: este no es el ejecutable publicado del launcher.";
                return false;
            }

            var dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir) || !FileSystemUtil.IsDirectoryWritable(dir))
            {
                reason = "No se puede escribir en la carpeta del launcher (¿está en Archivos de programa?). " +
                         "Descarga la nueva versión desde GitHub.";
                return false;
            }

            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Error comprobando si el launcher puede actualizarse solo.", ex);
            reason = "No se pudo comprobar si el launcher puede actualizarse solo.";
            return false;
        }
    }

    /// <summary>
    /// Consulta la última release del repo del launcher, como mucho una vez cada LauncherConfig.LauncherUpdateCheckInterval
    /// (marca de tiempo en &lt;cacheDir&gt;\launcher-update-check.json; <paramref name="force"/> la ignora, igual que la caché de
    /// GitHubClient: 404 cacheado y ventana de frescura) y nunca si
    /// github.RateLimitRemaining &lt;= LauncherConfig.RateLimitReserveForUser (salvo force). Devuelve info si la versión del
    /// tag es MAYOR que CurrentVersion y hay un asset que coincide con LauncherConfig.LauncherAssetPattern; si no, null.
    /// Cuando no consulta por el intervalo, devuelve el último resultado positivo guardado si sigue siendo mayor que la
    /// versión actual. Cualquier error → null (registrado). Solo lanza OperationCanceledException si se canceló <paramref name="ct"/>.
    /// </summary>
    public async Task<LauncherUpdateInfo?> CheckAsync(CancellationToken ct, bool force = false)
    {
        ct.ThrowIfCancellationRequested();
        await _checkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = EffectiveCurrentVersion;
            var now = UtcNow();
            var state = await Task.Run(LoadCheckState, ct).ConfigureAwait(false);

            if (!force)
            {
                // Una marca en el futuro (reloj cambiado) no bloquea las comprobaciones.
                if (state.LastCheckUtc is { } last && last <= now && now - last < LauncherConfig.LauncherUpdateCheckInterval)
                {
                    Log.Debug("Comprobación de versión del launcher omitida (intervalo).");
                    return FromSaved(state.LastResult, current);
                }

                var remaining = _github.RateLimitRemaining;
                if (_github.IsRateLimited || remaining is { } r && r <= LauncherConfig.RateLimitReserveForUser)
                {
                    Log.Info($"Comprobación de versión del launcher omitida: quedan {remaining?.ToString(CultureInfo.InvariantCulture) ?? "?"} consultas a GitHub.");
                    return FromSaved(state.LastResult, current);
                }
            }

            // Con force (botón "Buscar ahora") se ignora también la caché de GitHubClient (404 cacheado, frescura de 60 s):
            // si no, una release recién publicada no se veía hasta que caducaba esa caché.
            var latest = await _github.GetLatestReleaseAsync(LauncherConfig.LauncherRepoOwner, LauncherConfig.LauncherRepoName, ct,
                    bypassCache: force)
                .ConfigureAwait(false);

            if (latest == null)
            {
                // Sin releases, 404 (cacheado por GitHubClient) o error de red: no se guarda marca para reintentar en el
                // próximo arranque.
                Log.Info("No hay información de versiones del launcher.");
                return null;
            }

            var info = Evaluate(latest, current);
            state = new CheckState
            {
                LastCheckUtc = now,
                LastResult = info != null ? SavedUpdate.From(info) : null,
            };
            await Task.Run(() => SaveCheckState(state), CancellationToken.None).ConfigureAwait(false);

            if (info != null)
                Log.Info($"Hay una versión nueva del launcher: {info.VersionText} (actual {current}).");
            else
                Log.Info($"El launcher está al día ({current}; última publicada: {latest.TagName}).");

            return info;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Error comprobando si hay versión nueva del launcher.", ex);
            return null;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// <summary>
    /// Ejecuta los pasos 1–4. Devuelve true si el nuevo proceso arrancó y confirmó que vive (el llamador debe cerrar la app
    /// de inmediato). Si algo falla deja todo como estaba y lanza LauncherException; si lo que falló es el arranque de la
    /// versión nueva, lanza <see cref="LauncherStartFailedException"/> (con otro mensaje si no se pudo restaurar la anterior).
    /// </summary>
    public async Task<bool> DownloadAndApplyAsync(LauncherUpdateInfo info, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(info);
        // CanSelfUpdate prueba a escribir en disco: fuera del hilo de UI.
        var (canUpdate, reason) = await Task.Run(() => (CanSelfUpdate(out var r), r), ct).ConfigureAwait(false);
        if (!canUpdate)
            throw new LauncherException(reason ?? "El launcher no puede actualizarse solo.");

        return await DownloadAndApplyCoreAsync(info, CurrentExePath!, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Pasos 1–4 sobre un exe concreto (separado para poder probarlo sin ser el exe publicado).</summary>
    internal async Task<bool> DownloadAndApplyCoreAsync(LauncherUpdateInfo info, string exePath, IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var newPath = exePath + ".new";
        var oldPath = exePath + ".old";
        var failedPath = exePath + ".failed";
        Log.Info($"Actualizando el launcher a {info.VersionText} desde {info.Asset.DownloadUrl}");

        // 1. Descarga + comprobación "MZ".
        try
        {
            var request = new DownloadRequest
            {
                Url = info.Asset.DownloadUrl,
                DestinationPath = newPath,
                ExpectedSize = info.Asset.Size > 0 ? info.Asset.Size : null,
                ExpectedSha256 = info.Asset.Sha256,
                ResumeKey = $"launcher:{info.Asset.Id}:{info.Asset.Sha256 ?? info.Asset.Size.ToString(CultureInfo.InvariantCulture)}",
            };
            await _downloader.DownloadAsync(request, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LauncherException ex)
        {
            Log.Warn("Falló la descarga de la nueva versión del launcher.", ex);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("Error inesperado descargando la nueva versión del launcher.", ex);
            throw new LauncherException("No se pudo descargar la nueva versión del launcher. Vuelve a intentarlo más tarde.", ex);
        }

        ct.ThrowIfCancellationRequested();

        if (!await Task.Run(() => HasMzHeader(newPath), ct).ConfigureAwait(false))
        {
            Log.Error($"El archivo descargado no es un ejecutable: {newPath}");
            FileSystemUtil.TryDeleteFile(newPath);
            throw new LauncherException("El archivo descargado no es un ejecutable válido. Vuelve a intentarlo más tarde.");
        }

        // El evento se crea ANTES de tocar nada: si falla, todo sigue como estaba.
        EventWaitHandle aliveEvent;
        try
        {
            aliveEvent = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateAliveEventName, out var createdNew);
            if (!createdNew)
                aliveEvent.Reset();
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo crear el evento de confirmación de la actualización.", ex);
            throw new LauncherException("No se pudo preparar la actualización del launcher. Vuelve a intentarlo más tarde.", ex);
        }

        using var aliveEventScope = aliveEvent;

        // A partir de aquí no se atiende la cancelación: dejar el cambio a medias sería peor.
        // 2. Renombrados.
        await Task.Run(() => SwapExecutables(exePath, newPath, oldPath), CancellationToken.None).ConfigureAwait(false);

        // 3. Arranque de la versión nueva + confirmación.
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
            };
            psi.ArgumentList.Add(PostUpdateArgument);
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            try
            {
                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo arrancar la nueva versión del launcher.", ex);
                process = null;
            }

            var alive = process != null &&
                        await Task.Run(() => WaitForAliveSignal(aliveEvent, process, AliveTimeout), CancellationToken.None)
                            .ConfigureAwait(false);

            if (alive)
            {
                Log.Info($"La nueva versión del launcher ({info.VersionText}) arrancó correctamente (pid {process!.Id}).");
                return true;
            }

            Log.Error("La nueva versión del launcher no confirmó el arranque a tiempo; restaurando la anterior.");
            if (process != null)
                KillQuietly(process);

            var restored = await Task.Run(() => RestoreAfterFailedStart(exePath, oldPath, failedPath), CancellationToken.None)
                .ConfigureAwait(false);
            throw new LauncherStartFailedException(restored ? StartFailedMessage : RestoreFailedMessage);
        }
        finally
        {
            process?.Dispose();
        }
    }

    // ---------------------------------------------------------------- pasos internos (testeables)

    /// <summary>
    /// exe → exe.old y exe.new → exe. Si falla el segundo paso, devuelve exe.old a exe. LauncherException si no se pudo
    /// (dejando el exe original en su sitio siempre que sea posible).
    /// </summary>
    internal static void SwapExecutables(string exePath, string newPath, string oldPath, int attempts = 10, int delayMs = 500)
    {
        try
        {
            FileSystemUtil.MoveFileWithRetry(exePath, oldPath, overwrite: true, attempts, delayMs);
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo renombrar {exePath} a .old.", ex);
            throw new LauncherException("No se pudo reemplazar el launcher. Vuelve a intentarlo más tarde.", ex);
        }

        try
        {
            FileSystemUtil.MoveFileWithRetry(newPath, exePath, overwrite: true, attempts, delayMs);
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo colocar la nueva versión en {exePath}; deshaciendo.", ex);
            try
            {
                FileSystemUtil.MoveFileWithRetry(oldPath, exePath, overwrite: true, attempts, delayMs);
            }
            catch (Exception rollbackEx)
            {
                Log.Error("No se pudo deshacer el renombrado del launcher.", rollbackEx);
                throw new LauncherException(
                    "No se pudo reemplazar el launcher ni restaurar el anterior. Descárgalo de nuevo desde GitHub.", ex);
            }

            throw new LauncherException("No se pudo reemplazar el launcher. Vuelve a intentarlo más tarde.", ex);
        }
    }

    /// <summary>
    /// exe (nuevo, fallido) → exe.failed y exe.old → exe; si ese renombrado falla, copia exe.old sobre exe. Devuelve true solo
    /// si la versión anterior quedó en exe. Registra los fallos y nunca lanza.
    /// </summary>
    internal static bool RestoreAfterFailedStart(string exePath, string oldPath, string failedPath, int attempts = 10, int delayMs = 500)
    {
        try
        {
            if (!File.Exists(oldPath))
            {
                Log.Error($"No existe la copia de la versión anterior ({oldPath}); no se puede restaurar.");
                return false;
            }

            if (File.Exists(exePath))
            {
                try
                {
                    FileSystemUtil.MoveFileWithRetry(exePath, failedPath, overwrite: true, attempts, delayMs);
                }
                catch (Exception ex)
                {
                    Log.Error($"No se pudo apartar la versión fallida a {failedPath}.", ex);
                }
            }

            try
            {
                FileSystemUtil.MoveFileWithRetry(oldPath, exePath, overwrite: true, attempts, delayMs);
                Log.Info("Versión anterior del launcher restaurada.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"No se pudo restaurar {oldPath} a {exePath}; se intenta copiando.", ex);
            }

            // Plan B: copiar (exe.old se queda; se borrará en la próxima limpieza).
            try
            {
                File.Copy(oldPath, exePath, overwrite: true);
                Log.Info("Versión anterior del launcher restaurada (copiada).");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Tampoco se pudo copiar {oldPath} a {exePath}.", ex);
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Error inesperado restaurando la versión anterior del launcher.", ex);
            return false;
        }
    }

    /// <summary>
    /// Vuelta atrás desde la versión nueva: para un arranque "--post-update" cuya ventana principal no pudo abrirse. Si existe
    /// "&lt;exePath&gt;.old", aparta <paramref name="exePath"/> (el exe en ejecución, que se puede renombrar) a
    /// "&lt;exePath&gt;.failed" y devuelve "&lt;exePath&gt;.old" a <paramref name="exePath"/> (copiando si no se puede renombrar).
    /// True solo si la versión anterior quedó en <paramref name="exePath"/>; el llamador debe soltar la instancia única,
    /// arrancarla SIN "--post-update" y cerrarse. Sin "&lt;exePath&gt;.old" o con dotnet como host → false sin tocar nada.
    /// Nunca lanza.
    /// </summary>
    public static bool TryRestorePreviousVersion(string? exePath) => TryRestorePreviousVersion(exePath, 10, 500);

    internal static bool TryRestorePreviousVersion(string? exePath, int attempts, int delayMs)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || IsDotnetHost(exePath))
                return false;

            var oldPath = exePath + ".old";
            if (!File.Exists(oldPath))
            {
                Log.Warn($"No hay copia de la versión anterior del launcher ({oldPath}); no se puede volver atrás.");
                return false;
            }

            Log.Warn($"La versión nueva del launcher no pudo abrir su ventana; volviendo a la anterior ({oldPath}).");
            return RestoreAfterFailedStart(exePath, oldPath, exePath + ".failed", attempts, delayMs);
        }
        catch (Exception ex)
        {
            Log.Error("Error inesperado volviendo a la versión anterior del launcher.", ex);
            return false;
        }
    }

    /// <summary>Espera a que se señale el evento; false si el proceso muere antes o vence el tiempo.</summary>
    internal static bool WaitForAliveSignal(WaitHandle aliveEvent, Process process, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (aliveEvent.WaitOne(250))
                return true;

            bool exited;
            try
            {
                exited = process.HasExited;
            }
            catch (Exception)
            {
                exited = false;
            }

            if (exited)
            {
                // Pudo señalar justo antes de salir.
                var signaled = aliveEvent.WaitOne(0);
                if (!signaled)
                {
                    int? code = null;
                    try
                    {
                        code = process.ExitCode;
                    }
                    catch (Exception)
                    {
                        // ignorado: solo informativo
                    }

                    Log.Warn($"La nueva versión del launcher terminó sin confirmar el arranque (código {code?.ToString(CultureInfo.InvariantCulture) ?? "?"}).");
                }

                return signaled;
            }

            if (sw.Elapsed >= timeout)
                return aliveEvent.WaitOne(0);
        }
    }

    /// <summary>Borra los restos de actualizaciones junto a <paramref name="exePath"/>, reintentando hasta <paramref name="timeout"/>.</summary>
    internal static async Task CleanupLeftoversAsync(string exePath, TimeSpan timeout, TimeSpan interval, CancellationToken ct)
    {
        var targets = GetLeftoverPaths(exePath);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var pending = 0;
            foreach (var path in targets)
            {
                if (!File.Exists(path))
                    continue;
                if (!FileSystemUtil.TryDeleteFile(path))
                    pending++;
                else
                    Log.Info($"Borrado resto de actualización: {path}");
            }

            if (pending == 0)
                return;

            if (sw.Elapsed >= timeout)
            {
                Log.Warn($"No se pudieron borrar {pending} restos de actualización junto a {exePath}; se reintentará en el próximo arranque.");
                return;
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    internal static string[] GetLeftoverPaths(string exePath) => new[]
    {
        exePath + ".old",
        exePath + ".new",
        exePath + ".failed",
        exePath + ".new.part",
        exePath + ".new.part.json",
    };

    /// <summary>Devuelve la info si la release es mayor que <paramref name="current"/> y tiene el asset exacto del launcher.</summary>
    internal static LauncherUpdateInfo? Evaluate(ReleaseInfo release, GameVersion current)
    {
        if (release.IsDraft)
            return null;

        if (!GameVersion.TryParse(release.TagName, out var version) && !GameVersion.TryParse(release.Name, out version))
        {
            Log.Warn($"No se pudo leer la versión de la release del launcher '{release.TagName}'.");
            return null;
        }

        if (version <= current)
            return null;

        var asset = release.Assets.FirstOrDefault(a =>
            !string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(a.DownloadUrl) &&
            Regex.IsMatch(a.Name, LauncherConfig.LauncherAssetPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        if (asset == null)
        {
            Log.Warn($"La release del launcher {release.TagName} no tiene el archivo PalitoLauncher.exe.");
            return null;
        }

        return new LauncherUpdateInfo(version, release, asset);
    }

    internal static bool IsDotnetHost(string exePath) =>
        string.Equals(Path.GetFileName(exePath), "dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetFileName(exePath), "dotnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ¿Es el exe publicado (archivo único) de la app? Descarta hosts ajenos (testhost…) y builds de desarrollo
    /// "exe + dll", que no se pueden reemplazar copiando solo el exe.
    /// </summary>
    internal static bool IsPublishedLauncherExe(string exePath)
    {
        var entryName = Assembly.GetEntryAssembly()?.GetName().Name;
        if (!string.Equals(entryName, AppAssemblyName, StringComparison.OrdinalIgnoreCase))
            return false;

        return !File.Exists(Path.ChangeExtension(exePath, ".dll"));
    }

    private static GameVersion ResolveCurrentVersion()
    {
        try
        {
            // El ensamblado de entrada solo si es la app (en tests/hosts ajenos se usa el del Core, que comparte versión).
            var entry = Assembly.GetEntryAssembly();
            var assembly = entry != null && string.Equals(entry.GetName().Name, AppAssemblyName, StringComparison.OrdinalIgnoreCase)
                ? entry
                : typeof(LauncherSelfUpdater).Assembly;

            var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(text))
                text = assembly.GetName().Version?.ToString();

            if (!string.IsNullOrWhiteSpace(text))
            {
                var plus = text.IndexOf('+');
                if (plus >= 0)
                    text = text[..plus];
                if (GameVersion.TryParse(text, out var v))
                    return v;
            }

            Log.Warn($"No se pudo leer la versión del launcher ('{text}').");
        }
        catch (Exception ex)
        {
            Log.Warn("Error leyendo la versión del launcher.", ex);
        }

        return new GameVersion(0, 0, 0);
    }

    private static bool HasMzHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[2];
            return fs.ReadAtLeast(header, 2, throwOnEndOfStream: false) == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo leer {path}.", ex);
            return false;
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo cerrar la nueva versión fallida del launcher.", ex);
        }
    }

    private static LauncherUpdateInfo? FromSaved(SavedUpdate? saved, GameVersion current)
    {
        try
        {
            var info = saved?.ToInfo();
            return info != null && info.Version > current ? info : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Resultado guardado de la comprobación del launcher ilegible.", ex);
            return null;
        }
    }

    private CheckState LoadCheckState()
    {
        var path = CheckStatePath;
        try
        {
            if (!File.Exists(path))
                return new CheckState();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CheckState>(json, s_json) ?? new CheckState();
        }
        catch (Exception ex)
        {
            Log.Warn($"Estado de comprobación del launcher corrupto; se ignora ({path}).", ex);
            FileSystemUtil.TryDeleteFile(path);
            return new CheckState();
        }
    }

    private void SaveCheckState(CheckState state)
    {
        try
        {
            FileSystemUtil.WriteAllTextAtomic(CheckStatePath, JsonSerializer.Serialize(state, s_json));
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo guardar el estado de comprobación del launcher.", ex);
        }
    }

    // ---------------------------------------------------------------- persistencia

    private sealed class CheckState
    {
        public DateTimeOffset? LastCheckUtc { get; set; }
        public SavedUpdate? LastResult { get; set; }
    }

    private sealed class SavedUpdate
    {
        public string Version { get; set; } = "";
        public long ReleaseId { get; set; }
        public string TagName { get; set; } = "";
        public string? Name { get; set; }
        public string? Body { get; set; }
        public bool IsPrerelease { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public string HtmlUrl { get; set; } = "";
        public long AssetId { get; set; }
        public string AssetName { get; set; } = "";
        public long AssetSize { get; set; }
        public string AssetUrl { get; set; } = "";
        public string? AssetContentType { get; set; }
        public string? AssetSha256 { get; set; }
        public DateTimeOffset? AssetUpdatedAt { get; set; }

        public static SavedUpdate From(LauncherUpdateInfo info) => new()
        {
            Version = info.Version.ToString(),
            ReleaseId = info.Release.Id,
            TagName = info.Release.TagName,
            Name = info.Release.Name,
            Body = info.Release.Body,
            IsPrerelease = info.Release.IsPrerelease,
            PublishedAt = info.Release.PublishedAt,
            HtmlUrl = info.Release.HtmlUrl,
            AssetId = info.Asset.Id,
            AssetName = info.Asset.Name,
            AssetSize = info.Asset.Size,
            AssetUrl = info.Asset.DownloadUrl,
            AssetContentType = info.Asset.ContentType,
            AssetSha256 = info.Asset.Sha256,
            AssetUpdatedAt = info.Asset.UpdatedAt,
        };

        public LauncherUpdateInfo? ToInfo()
        {
            if (!GameVersion.TryParse(Version, out var version) || string.IsNullOrEmpty(AssetUrl) || string.IsNullOrEmpty(AssetName))
                return null;

            var asset = new ReleaseAsset
            {
                Id = AssetId,
                Name = AssetName,
                Size = AssetSize,
                DownloadUrl = AssetUrl,
                ContentType = AssetContentType,
                Sha256 = AssetSha256,
                UpdatedAt = AssetUpdatedAt,
            };
            var release = new ReleaseInfo
            {
                Id = ReleaseId,
                TagName = TagName,
                Name = Name,
                Body = Body,
                IsPrerelease = IsPrerelease,
                PublishedAt = PublishedAt,
                HtmlUrl = HtmlUrl,
                Assets = new[] { asset },
            };
            return new LauncherUpdateInfo(version, release, asset);
        }
    }
}
