using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Util;

namespace PalitoLauncher.Core.Downloads;

/// <summary>
/// Progreso de descarga. Se reporta como máximo ~10 veces por segundo y AL MENOS una vez por segundo aunque no lleguen
/// bytes (en ese caso BytesPerSecond decae hacia 0 y Eta = null), para que la UI distinga "reconectando" de "colgado".
/// </summary>
public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes, double BytesPerSecond, TimeSpan? Eta)
{
    /// <summary>0..1, o null si no se conoce el total.</summary>
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1) : null;
}

/// <summary>Aviso de reintento programado (para mostrar "Conexión perdida. Reintentando en 4 s (intento 3 de 8)…").</summary>
public readonly record struct DownloadRetryInfo(int Attempt, int MaxAttempts, TimeSpan Delay, string Reason);

/// <summary>
/// El archivo remoto ya no es el esperado (el desarrollador re-subió la build: cambió tamaño, ETag o Last-Modified de la CDN).
/// No tiene sentido reintentar con los mismos metadatos: hay que volver a consultar las releases.
/// </summary>
public sealed class RemoteAssetChangedException : LauncherException
{
    public RemoteAssetChangedException()
        : base("La build cambió en GitHub mientras se descargaba. Vuelve a buscar actualizaciones.")
    {
    }
}

/// <summary>
/// El archivo se descargó completo dos veces y su tamaño o SHA-256 no coincide con lo que anuncia GitHub.
/// La UI la usa (por tipo, no por texto) para refrescar las releases y reintentar si la build cambió.
/// </summary>
public sealed class DownloadVerificationException : LauncherException
{
    public DownloadVerificationException(string userMessage)
        : base(userMessage)
    {
    }
}

public sealed class DownloadRequest
{
    /// <summary>
    /// URL de descarga ORIGINAL (p.ej. browser_download_url). Cada intento (inicial, reintento, reanudación) pide esta URL y
    /// deja que HttpClient siga la redirección: la URL firmada de la CDN caduca y NUNCA se guarda ni se reutiliza.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>Ruta final del archivo. Mientras se descarga se usa "&lt;DestinationPath&gt;.part" + "&lt;DestinationPath&gt;.part.json" (metadatos).</summary>
    public required string DestinationPath { get; init; }

    /// <summary>Tamaño esperado en bytes (si se conoce). Se verifica al terminar.</summary>
    public long? ExpectedSize { get; init; }

    /// <summary>SHA-256 esperado en hex (cualquier capitalización). Si es null solo se verifica el tamaño.</summary>
    public string? ExpectedSha256 { get; init; }

    /// <summary>Límite de velocidad en bytes/s. 0 = sin límite.</summary>
    public long MaxBytesPerSecond { get; init; }

    /// <summary>
    /// Identidad del contenido (p.ej. "asset:654684182:sha256:..."). Un .part existente solo se reanuda si su
    /// metadato tiene la misma clave. Si es null se usa Url + ExpectedSize.
    /// </summary>
    public string? ResumeKey { get; init; }
}

/// <summary>
/// Descargador robusto. Metadatos en .part.json = {url (la original), resumeKey, expectedSize, expectedSha256,
/// cdnETag, cdnLastModified, createdAtUtc}. Un .part.json ilegible/corrupto → el .part no es reanudable (se borra y se
/// empieza de 0, sin lanzar).
///
/// Antes de pedir nada:
/// - Si DestinationPath ya existe y coincide en tamaño (y hash si se da) → no descarga (caché).
/// - Si el .part mide exactamente ExpectedSize → directo a verificación. Si mide más → se borra.
///
/// Respuestas (cada intento pide <see cref="DownloadRequest.Url"/>; con .part reanudable añade "Range: bytes=N-"):
/// - 206: válido solo si Content-Range empieza en N y su total == ExpectedSize (si se conoce). Si los validadores de la CDN
///   (ETag / Last-Modified) difieren de los guardados → borrar .part y empezar sin Range. Si el total difiere de
///   ExpectedSize → <see cref="RemoteAssetChangedException"/> (sin descargar el cuerpo).
/// - 200: válido solo si Content-Type no es text/html y Content-Length falta o == ExpectedSize (si difiere →
///   RemoteAssetChangedException). Solo entonces se trunca el .part y se escribe desde 0. Se guardan ETag/Last-Modified.
/// - 416: si Content-Range "bytes */T" con T == largo local == ExpectedSize → verificar; si no → borrar .part y reiniciar.
/// - 200 text/html (portal cautivo, proxy, antivirus) → error transitorio que NO toca el .part. Si se agotan los reintentos:
///   LauncherException("La conexión devolvió una página web en lugar del archivo (¿wifi que requiere iniciar sesión, proxy o antivirus?)").
/// - 401/403/404 cuyo host final no es github.com/api.github.com (firma de CDN caducada) → transitorio UNA vez por racha (se
///   vuelve a pedir Url; un intento que avanza rearma ese reintento); un segundo seguido, y para github.com 404/403 →
///   LauncherException inmediata ("El archivo ya no está disponible en GitHub.").
/// - 5xx, 408, 429, errores de red y timeouts → transitorios.
/// - Si los bytes recibidos superarían ExpectedSize → abortar el intento (transitorio) y borrar .part.
/// - HttpRequestError.SecureConnectionError → como mucho 1 reintento: 2 fallos TLS CONSECUTIVOS (sin progreso entre medias)
///   → LauncherException sobre fecha/hora/antivirus/wifi. NameResolutionError → cuenta como intento sin progreso con el
///   mensaje "Sin conexión a Internet".
///
/// Reintentos: se cuentan solo los intentos CONSECUTIVOS sin progreso (un intento que avanza ≥ 1 MB reinicia el contador y el
/// backoff). Máximo 8 intentos sin progreso; backoff 1, 2, 4, 8, 16, 30, 30 s (× RetryDelayScale). Antes de cada espera se
/// dispara <see cref="RetryScheduled"/>. Al agotarse: conserva el .part y lanza LauncherException("No se pudo completar la
/// descarga. Tu progreso se guardó: pulsa CONTINUAR cuando vuelva la conexión.") (o el mensaje de disco si el último fallo
/// fue de disco).
///
/// Unidad y disco (no gastan los reintentos de red): antes de empezar y antes de cada intento se comprueba
/// FileSystemUtil.IsDriveReady; si la unidad no está (USB desconectado) → LauncherException("No se encuentra la unidad X.
/// Conéctala y vuelve a intentarlo: lo descargado se conserva.") sin pedir nada a la red. Un error de disco no clasificado
/// se reintenta como mucho UNA vez seguida; después → LauncherException("No se pudo guardar la descarga en el disco…").
///
/// Estancamiento: un CancellationTokenSource por intento, enlazado a ct, armado con CancelAfter(StallTimeout) ANTES de
/// SendAsync(ResponseHeadersRead), rearmado tras cada lectura con datos y pausado mientras el limitador de velocidad espera.
/// Si salta sin que ct esté cancelado → intento transitorio.
///
/// Verificación: al completar, flush, (evento VerificationStarted) tamaño + SHA-256 leyendo el archivo. Solo un tamaño o
/// SHA-256 distinto cuenta como fallo: borra el parcial y reintenta UNA vez desde cero; si vuelve a fallar →
/// <see cref="DownloadVerificationException"/>("El archivo descargado está dañado (no coincide la suma de verificación)…").
/// Si el .part desaparece o no se deja leer por el antivirus (UnauthorizedAccessException, HResult 0x800700E1/0x800700E2)
/// → LauncherException("El antivirus (u otro programa) bloqueó o borró el archivo descargado. Añade una exclusión para
/// &lt;carpeta&gt;…") SIN borrar ni volver a descargar; otros errores de lectura (archivo en uso tras 3 intentos, etc.) →
/// LauncherException("No se pudo leer el archivo descargado…") conservando el .part. Al terminar: mueve .part →
/// DestinationPath (sobrescribiendo) y borra .part.json. El DestinationPath ya existente se comprueba igual: antivirus →
/// esa misma LauncherException; otros errores de lectura → se descarga de nuevo.
///
/// Escritura: FileStream (FileShare.Read, buffer 81920, Asynchronous|SequentialScan). IOException de disco lleno
/// (HResult 0x80070070 / 0x80070027) → LauncherException("No hay espacio suficiente en el disco…");
/// UnauthorizedAccessException → LauncherException("Sin permisos para escribir en …"); HResult 0x800700E1/0x800700E2 →
/// mensaje del antivirus; unidad no lista → mensaje de unidad.
/// Cancelación: deja el .part para reanudar y lanza OperationCanceledException.
/// Progreso: velocidad con media exponencial (~2 s), ETA a partir de ella; reportes ≤ 10/s, ≥ 1/s y uno final.
/// Límite de velocidad: token bucket simple tras cada bloque.
/// Todo el trabajo pesado ocurre fuera del hilo de UI.
/// </summary>
public sealed class FileDownloader
{
    /// <summary>Máximo de intentos consecutivos sin progreso.</summary>
    internal const int MaxAttemptsWithoutProgress = 8;

    /// <summary>Un intento que avanza al menos esto reinicia el contador de intentos y el backoff.</summary>
    internal const long ProgressResetBytes = 1024 * 1024;

    /// <summary>Esperas entre intentos (antes de multiplicar por RetryDelayScale).</summary>
    internal static readonly TimeSpan[] BackoffSchedule =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
    ];

    internal const string GiveUpMessage =
        "No se pudo completar la descarga. Tu progreso se guardó: pulsa CONTINUAR cuando vuelva la conexión.";
    internal const string NoInternetMessage =
        "Sin conexión a Internet. Tu progreso se guardó: pulsa CONTINUAR cuando vuelva la conexión.";
    internal const string PortalMessage =
        "La conexión devolvió una página web en lugar del archivo (¿wifi que requiere iniciar sesión, proxy o antivirus?)";
    internal const string NotAvailableMessage = "El archivo ya no está disponible en GitHub.";
    internal const string CorruptedMessage =
        "El archivo descargado está dañado (no coincide la suma de verificación). Vuelve a intentarlo; si se repite, avisa al desarrollador.";
    internal const string TlsMessage =
        "No se pudo establecer una conexión segura con GitHub. Comprueba que la fecha y la hora del PC son correctas; " +
        "si lo son, puede que un antivirus o la red wifi estén bloqueando la conexión.";
    internal const string ReadErrorMessage =
        "No se pudo leer el archivo descargado (puede que otro programa, como el antivirus, lo esté usando). " +
        "Vuelve a intentarlo: lo descargado se conserva.";

    internal static string AntivirusMessage(string folder) =>
        $"El antivirus (u otro programa) bloqueó o borró el archivo descargado. Añade una exclusión para {folder} en tu " +
        "antivirus y vuelve a intentarlo.";

    internal static string DriveMissingMessage(string drive) =>
        $"No se encuentra la unidad {drive}. Conéctala y vuelve a intentarlo: lo descargado se conserva.";

    internal static string DiskErrorMessage(string drive) =>
        $"No se pudo guardar la descarga en el disco ({drive}). Comprueba que la unidad sigue conectada y vuelve a intentarlo: " +
        "lo descargado se conserva.";

    /// <summary>Errores de disco no clasificados seguidos que se toleran (1 reintento) antes de rendirse.</summary>
    internal const int MaxConsecutiveLocalFileFailures = 2;

    private const int BufferSize = 81920;
    private const int MaxConsecutiveRestarts = 3;
    private const int HashOpenAttempts = 3;
    private static readonly TimeSpan HashRetryDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions MetadataJsonOptions = new() { WriteIndented = true };

    private readonly HttpClient _http;

    public FileDownloader(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <summary>Tiempo sin recibir datos (o sin cabeceras) antes de abortar un intento. Configurable para tests.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Multiplicador de las esperas entre reintentos (tests lo ponen a 0).</summary>
    public double RetryDelayScale { get; set; } = 1.0;

    /// <summary>Se dispara justo antes de empezar a calcular el hash (para que la UI muestre "Verificando...").</summary>
    public event EventHandler? VerificationStarted;

    /// <summary>Se dispara antes de esperar para reintentar.</summary>
    public event EventHandler<DownloadRetryInfo>? RetryScheduled;

    public Task DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expectedSha256 = ValidateRequest(request);
        // Todo en el pool de hilos: ni el hash del archivo existente ni la E/S tocan el hilo de UI.
        return Task.Run(() => DownloadCoreAsync(request, expectedSha256, progress, ct), ct);
    }

    /// <summary>Bytes ya descargados reanudables para esta petición (0 si no hay .part, .part.json corrupto o distinta ResumeKey).</summary>
    public static long GetResumableBytes(DownloadRequest request)
    {
        try
        {
            if (request is null || string.IsNullOrWhiteSpace(request.DestinationPath))
            {
                return 0;
            }

            var destination = Path.GetFullPath(request.DestinationPath);
            var partPath = destination + ".part";
            if (!File.Exists(partPath))
            {
                return 0;
            }

            var metadata = TryReadMetadata(destination + ".part.json", logProblems: false);
            if (metadata is null || !string.Equals(metadata.ResumeKey, GetEffectiveResumeKey(request), StringComparison.Ordinal))
            {
                return 0;
            }

            var length = new FileInfo(partPath).Length;
            return request.ExpectedSize is long expected && length > expected ? 0 : length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Borra .part y .part.json de ese destino (no lanza).</summary>
    public static void DeletePartial(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return;
        }
        try
        {
            var destination = Path.GetFullPath(destinationPath);
            FileSystemUtil.TryDeleteFile(destination + ".part");
            FileSystemUtil.TryDeleteFile(destination + ".part.json");
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo borrar la descarga parcial de {destinationPath}.", ex);
        }
    }

    /// <summary>SHA-256 de un archivo en hex minúsculas (FileShare.ReadWrite).</summary>
    public static Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return Task.Run(async () =>
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return Convert.ToHexStringLower(hash);
        }, ct);
    }

    // ───────────────────────── Flujo principal ─────────────────────────

    private async Task DownloadCoreAsync(DownloadRequest request, string? expectedSha256, IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var session = new Session(request, expectedSha256, new ProgressTracker(progress));
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = progress is null ? Task.CompletedTask : RunHeartbeatAsync(session.Tracker, heartbeatCts.Token);
        try
        {
            await RunAsync(session, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Info($"Descarga cancelada (se conserva lo descargado): {session.Destination}");
            throw;
        }
        catch (Exception ex) when (ex is not (LauncherException or OperationCanceledException))
        {
            Log.Error($"Error inesperado al descargar {request.Url}", ex);
            throw;
        }
        finally
        {
            await heartbeatCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Esperado al parar el latido.
            }
        }
    }

    private async Task RunAsync(Session s, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureDriveReady(s);
        EnsureDownloadDirectory(s);

        if (await IsExistingDestinationValidAsync(s, ct).ConfigureAwait(false))
        {
            DeletePartial(s.Destination);
            var length = new FileInfo(s.Destination).Length;
            s.Tracker.ReportFinal(length);
            Log.Info($"Ya estaba descargado y verificado: {s.Destination}");
            return;
        }

        var partLength = PrepareResumeState(s);
        var partComplete = s.ExpectedSize is long size && partLength == size && File.Exists(s.PartPath);
        s.Tracker.Reset(partLength, s.KnownTotal);
        s.Tracker.Paused = false;
        Log.Info(partLength > 0
            ? $"Descargando {s.Request.Url} → {s.Destination} (reanudando desde {FileSystemUtil.FormatBytes(partLength)})"
            : $"Descargando {s.Request.Url} → {s.Destination}");

        var attemptInStreak = 1;
        var consecutiveRestarts = 0;
        var verificationRetried = false;
        var cdnAuthRetried = false;
        var tlsFailures = 0;
        var localFileFailures = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!partComplete)
            {
                // Unidad desconectada (USB): no tiene sentido pedir nada a la red.
                EnsureDriveReady(s);
                var outcome = await AttemptAsync(s, ct).ConfigureAwait(false);

                if (outcome.Result == AttemptResult.Restart)
                {
                    consecutiveRestarts++;
                    if (consecutiveRestarts <= MaxConsecutiveRestarts)
                    {
                        continue;
                    }
                    // Un servidor que obliga a reiniciar una y otra vez se trata como fallo normal (con espera y límite).
                    outcome = outcome with { Result = AttemptResult.Transient, Failure = FailureKind.Protocol };
                }

                if (outcome.Result == AttemptResult.Completed)
                {
                    partComplete = true;
                }
                else
                {
                    if (outcome.Advanced > 0)
                    {
                        consecutiveRestarts = 0;
                    }

                    Log.Warn($"Intento de descarga fallido ({outcome.Failure}): {outcome.Reason}" +
                             (outcome.Advanced > 0 ? $" tras avanzar {FileSystemUtil.FormatBytes(outcome.Advanced)}" : ""),
                        outcome.Error);

                    // Solo cuentan los fallos SEGUIDOS de cada tipo: un intento con progreso (o de otro tipo) los reinicia.
                    if (outcome.Failure != FailureKind.Tls || outcome.Advanced > 0)
                    {
                        tlsFailures = 0;
                    }
                    if (outcome.Advanced > 0)
                    {
                        cdnAuthRetried = false;
                    }
                    if (outcome.Failure != FailureKind.LocalFile || outcome.Advanced >= ProgressResetBytes)
                    {
                        localFileFailures = 0;
                    }

                    if (outcome.Failure == FailureKind.Tls && ++tlsFailures > 1)
                    {
                        throw new LauncherException(TlsMessage, outcome.Error);
                    }
                    if (outcome.Failure == FailureKind.CdnAuth)
                    {
                        if (cdnAuthRetried)
                        {
                            throw new LauncherException(NotAvailableMessage, outcome.Error);
                        }
                        cdnAuthRetried = true;
                    }
                    if (outcome.Failure == FailureKind.LocalFile && ++localFileFailures >= MaxConsecutiveLocalFileFailures)
                    {
                        // Un disco que falla dos veces seguidas no se arregla esperando a la red.
                        throw CreateGiveUpException(s, outcome);
                    }

                    if (outcome.Advanced >= ProgressResetBytes)
                    {
                        attemptInStreak = 1;
                    }
                    if (attemptInStreak >= MaxAttemptsWithoutProgress)
                    {
                        throw CreateGiveUpException(s, outcome);
                    }

                    var delay = GetRetryDelay(attemptInStreak, outcome.RetryAfter);
                    attemptInStreak++;
                    RaiseRetryScheduled(new DownloadRetryInfo(attemptInStreak, MaxAttemptsWithoutProgress, delay, outcome.Reason));
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                    continue;
                }
            }

            if (await VerifyAndFinishAsync(s, ct).ConfigureAwait(false))
            {
                return;
            }

            DeletePartial(s.Destination);
            if (verificationRetried)
            {
                throw new DownloadVerificationException(CorruptedMessage);
            }

            Log.Warn($"La verificación falló; se descarga de nuevo desde cero: {s.Destination}");
            verificationRetried = true;
            ResetPartialState(s, deleteFiles: false);
            partComplete = false;
            attemptInStreak = 1;
            consecutiveRestarts = 0;
            tlsFailures = 0;
            cdnAuthRetried = false;
            localFileFailures = 0;
        }
    }

    private TimeSpan GetRetryDelay(int attemptInStreak, TimeSpan? retryAfter)
    {
        var baseDelay = BackoffSchedule[Math.Clamp(attemptInStreak - 1, 0, BackoffSchedule.Length - 1)];
        if (retryAfter is { } serverDelay && serverDelay > baseDelay)
        {
            baseDelay = serverDelay > MaxRetryAfter ? MaxRetryAfter : serverDelay;
        }

        var scale = double.IsFinite(RetryDelayScale) ? Math.Max(0, RetryDelayScale) : 1.0;
        return TimeSpan.FromMilliseconds(Math.Round(baseDelay.TotalMilliseconds * scale));
    }

    private static LauncherException CreateGiveUpException(Session s, AttemptOutcome outcome) => outcome.Failure switch
    {
        FailureKind.Html => new LauncherException(PortalMessage, outcome.Error),
        FailureKind.NoInternet => new LauncherException(NoInternetMessage, outcome.Error),
        FailureKind.LocalFile => FileSystemUtil.IsDriveReady(s.Destination)
            ? new LauncherException(DiskErrorMessage(DriveOf(s)), outcome.Error)
            : new LauncherException(DriveMissingMessage(DriveOf(s)), outcome.Error),
        _ => new LauncherException(GiveUpMessage, outcome.Error),
    };

    // ───────────────────────── Un intento ─────────────────────────

    private async Task<AttemptOutcome> AttemptAsync(Session s, CancellationToken ct)
    {
        var offset = File.Exists(s.PartPath) ? new FileInfo(s.PartPath).Length : 0;
        var state = new AttemptState { StartOffset = offset, Position = offset };
        s.Tracker.Reset(offset, s.KnownTotal);

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stall = GetStallTimeout();
        // Armado ANTES de enviar: cubre conexión + cabeceras.
        attemptCts.CancelAfter(stall);

        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, s.Request.Url);
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
            }
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token).ConfigureAwait(false);
            var finalHost = (response.RequestMessage?.RequestUri ?? request.RequestUri)?.Host ?? "";
            var status = (int)response.StatusCode;

            switch (response.StatusCode)
            {
                case HttpStatusCode.PartialContent:
                    return await HandlePartialContentAsync(s, response, offset, state, attemptCts, stall, ct).ConfigureAwait(false);

                case HttpStatusCode.OK:
                    return await HandleOkAsync(s, response, offset, state, attemptCts, stall, ct).ConfigureAwait(false);

                case HttpStatusCode.RequestedRangeNotSatisfiable:
                    return HandleRangeNotSatisfiable(s, response, offset);

                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone:
                    if (IsGitHubHost(finalHost))
                    {
                        Log.Warn($"GitHub respondió {status} para {s.Request.Url}");
                        throw new LauncherException(NotAvailableMessage);
                    }
                    return AttemptOutcome.Transient(FailureKind.CdnAuth, $"El servidor de descargas respondió con el código {status}");

                case HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests:
                    return AttemptOutcome.Transient(FailureKind.Server, $"El servidor está saturado (código {status})",
                        retryAfter: GetRetryAfter(response));

                default:
                    if (status >= 500)
                    {
                        return AttemptOutcome.Transient(FailureKind.Server, $"El servidor respondió con un error (código {status})",
                            retryAfter: GetRetryAfter(response));
                    }
                    if (status is >= 400 and < 500)
                    {
                        Log.Warn($"Respuesta {status} no recuperable para {s.Request.Url}");
                        throw new LauncherException($"No se pudo descargar el archivo (error {status}).");
                    }
                    return AttemptOutcome.Transient(FailureKind.Protocol, $"Respuesta inesperada del servidor (código {status})");
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (LocalFileException ex)
        {
            return AttemptOutcome.Transient(FailureKind.LocalFile, "No se pudo escribir en el disco", state.Advanced, error: ex.InnerException);
        }
        catch (OperationCanceledException ex) when (attemptCts.IsCancellationRequested)
        {
            return AttemptOutcome.Transient(FailureKind.Stall, "La conexión se quedó parada", state.Advanced, error: ex);
        }
        catch (OperationCanceledException ex)
        {
            // Timeout propio de HttpClient (HttpClient.Timeout) u otro corte sin cancelación del usuario.
            return AttemptOutcome.Transient(FailureKind.Network, "Conexión perdida", state.Advanced, error: ex);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return AttemptOutcome.Transient(FailureKind.Tls, "No se pudo establecer una conexión segura", state.Advanced, error: ex);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.NameResolutionError)
        {
            return AttemptOutcome.Transient(FailureKind.NoInternet, "Sin conexión a Internet", state.Advanced, error: ex);
        }
        catch (HttpRequestException ex)
        {
            return AttemptOutcome.Transient(FailureKind.Network, "Conexión perdida", state.Advanced, error: ex);
        }
        catch (IOException ex)
        {
            // Errores de lectura de la respuesta (los de escritura en disco llegan como LocalFileException/LauncherException).
            return AttemptOutcome.Transient(FailureKind.Network, "Conexión perdida", state.Advanced, error: ex);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private async Task<AttemptOutcome> HandlePartialContentAsync(Session s, HttpResponseMessage response, long offset,
        AttemptState state, CancellationTokenSource attemptCts, TimeSpan stall, CancellationToken ct)
    {
        var range = response.Content.Headers.ContentRange;
        if (range is null || !range.HasRange || range.From != offset
            || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"Respuesta 206 con Content-Range inesperado ({range?.ToString() ?? "ninguno"}; pedido desde {offset}); se empieza de cero.");
            ResetPartialState(s, deleteFiles: true);
            return AttemptOutcome.Restart();
        }

        if (range.HasLength && s.ExpectedSize is long expected && range.Length != expected)
        {
            Log.Warn($"El tamaño remoto cambió: esperado {expected}, la CDN dice {range.Length}.");
            throw new RemoteAssetChangedException();
        }

        var etag = response.Headers.ETag?.ToString();
        var lastModified = response.Content.Headers.LastModified;
        if (offset > 0 && ValidatorsChanged(s.Metadata, etag, lastModified))
        {
            Log.Warn($"El archivo de la CDN cambió (ETag/Last-Modified distintos: {s.Metadata.CdnETag} → {etag}); se descarga desde cero.");
            ResetPartialState(s, deleteFiles: true);
            return AttemptOutcome.Restart();
        }

        if (s.ExpectedSize is null && range.HasLength)
        {
            s.KnownTotal = range.Length;
        }

        // Validadores aprendidos ahora (si antes no los había).
        var changed = false;
        if (s.Metadata.CdnETag is null && etag is not null)
        {
            s.Metadata.CdnETag = etag;
            changed = true;
        }
        if (s.Metadata.CdnLastModified is null && lastModified is not null)
        {
            s.Metadata.CdnLastModified = lastModified;
            changed = true;
        }
        if (changed || offset == 0)
        {
            SaveMetadata(s);
        }

        return await ReceiveBodyAsync(s, response, offset, state, attemptCts, stall, ct).ConfigureAwait(false);
    }

    private async Task<AttemptOutcome> HandleOkAsync(Session s, HttpResponseMessage response, long offset,
        AttemptState state, CancellationTokenSource attemptCts, TimeSpan stall, CancellationToken ct)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
        {
            // Portal cautivo / proxy / antivirus: no se toca el .part.
            return AttemptOutcome.Transient(FailureKind.Html, "La conexión devolvió una página web en lugar del archivo");
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is long length && s.ExpectedSize is long expected && length != expected)
        {
            Log.Warn($"El tamaño remoto cambió: esperado {expected}, Content-Length {length}.");
            throw new RemoteAssetChangedException();
        }

        if (offset > 0)
        {
            Log.Info("El servidor ignoró la petición de rango (200): se descarga desde cero.");
        }

        s.KnownTotal = s.ExpectedSize ?? contentLength;
        s.Metadata = CreateMetadata(s);
        s.Metadata.CdnETag = response.Headers.ETag?.ToString();
        s.Metadata.CdnLastModified = response.Content.Headers.LastModified;
        // Metadatos ANTES de truncar: así el .part siempre tiene su .part.json.
        SaveMetadata(s);
        s.Tracker.Reset(0, s.KnownTotal);

        return await ReceiveBodyAsync(s, response, 0, state, attemptCts, stall, ct).ConfigureAwait(false);
    }

    private static AttemptOutcome HandleRangeNotSatisfiable(Session s, HttpResponseMessage response, long offset)
    {
        var range = response.Content.Headers.ContentRange;
        if (offset > 0 && range is { HasLength: true } && range.Length == offset && (s.ExpectedSize is null || s.ExpectedSize == offset))
        {
            Log.Info("El servidor indica que la descarga ya estaba completa (416); se verifica.");
            s.KnownTotal ??= offset;
            return AttemptOutcome.Completed();
        }

        if (offset == 0)
        {
            return AttemptOutcome.Transient(FailureKind.Protocol, "Respuesta inesperada del servidor (código 416)");
        }

        Log.Warn($"Respuesta 416 incoherente ({range?.ToString() ?? "sin Content-Range"}; local {offset}); se empieza de cero.");
        ResetPartialState(s, deleteFiles: true);
        return AttemptOutcome.Restart();
    }

    private async Task<AttemptOutcome> ReceiveBodyAsync(Session s, HttpResponseMessage response, long startPosition,
        AttemptState state, CancellationTokenSource attemptCts, TimeSpan stall, CancellationToken ct)
    {
        var limiter = s.Request.MaxBytesPerSecond > 0 ? new RateLimiter(s.Request.MaxBytesPerSecond) : null;
        var chunkSize = limiter is null
            ? BufferSize
            : (int)Math.Clamp(s.Request.MaxBytesPerSecond / 10, 4096, BufferSize);
        var buffer = new byte[BufferSize];
        var position = startPosition;
        var overflow = false;
        state.Position = startPosition;

        var file = OpenPartFile(s, startPosition);
        await using (file.ConfigureAwait(false))
        {
            var body = await response.Content.ReadAsStreamAsync(attemptCts.Token).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                while (true)
                {
                    var read = await body.ReadAsync(buffer.AsMemory(0, chunkSize), attemptCts.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    attemptCts.CancelAfter(stall);

                    if (s.KnownTotal is long total && position + read > total)
                    {
                        overflow = true;
                        break;
                    }

                    await WritePartAsync(s, file, buffer, read, ct).ConfigureAwait(false);
                    position += read;
                    state.Position = position;
                    s.Tracker.OnBytes(position);

                    if (limiter is not null)
                    {
                        var wait = limiter.Consume(read);
                        if (wait > TimeSpan.Zero)
                        {
                            // El tiempo que espera el limitador no cuenta como estancamiento.
                            attemptCts.CancelAfter(Timeout.InfiniteTimeSpan);
                            await Task.Delay(wait, ct).ConfigureAwait(false);
                            attemptCts.CancelAfter(stall);
                        }
                    }
                }
            }

            if (!overflow)
            {
                await FlushPartAsync(s, file, ct).ConfigureAwait(false);
            }
        }

        if (overflow)
        {
            Log.Warn($"Se recibieron más bytes de los esperados ({s.KnownTotal}); se descarta el parcial.");
            ResetPartialState(s, deleteFiles: true);
            // Lo recibido se descarta: no cuenta como progreso.
            return AttemptOutcome.Transient(FailureKind.Protocol, "El servidor envió más datos de los esperados");
        }

        if (s.KnownTotal is long expectedTotal && position < expectedTotal)
        {
            return AttemptOutcome.Transient(FailureKind.Network, "Conexión perdida", state.Advanced);
        }

        s.KnownTotal ??= position;
        return AttemptOutcome.Completed();
    }

    // ───────────────────────── Verificación ─────────────────────────

    private async Task<bool> IsExistingDestinationValidAsync(Session s, CancellationToken ct)
    {
        if (!File.Exists(s.Destination))
        {
            return false;
        }
        // Sin tamaño ni hash no hay forma de saber si es el archivo correcto.
        if (s.ExpectedSize is null && s.ExpectedSha256 is null)
        {
            return false;
        }

        try
        {
            var length = new FileInfo(s.Destination).Length;
            if (s.ExpectedSize is long expected && length != expected)
            {
                Log.Info($"El archivo existente no coincide en tamaño ({length} ≠ {expected}); se descarga de nuevo.");
                FileSystemUtil.TryDeleteFile(s.Destination);
                return false;
            }

            if (s.ExpectedSha256 is not null)
            {
                RaiseVerificationStarted();
                var actual = await ComputeSha256WithRetryAsync(s.Destination, ct).ConfigureAwait(false);
                if (!string.Equals(actual, s.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info("El archivo existente no coincide en SHA-256; se descarga de nuevo.");
                    FileSystemUtil.TryDeleteFile(s.Destination);
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!FileSystemUtil.IsDriveReady(s.Destination))
            {
                throw new LauncherException(DriveMissingMessage(DriveOf(s)), ex);
            }
            if (ex is UnauthorizedAccessException || FileSystemUtil.IsAntivirusBlock(ex))
            {
                // Volver a descargarlo no sirve: el antivirus bloquearía también el nuevo.
                Log.Warn($"El antivirus (u otro programa) bloquea el archivo ya descargado {s.Destination}.", ex);
                throw new LauncherException(AntivirusMessage(ExclusionFolderOf(s)), ex);
            }
            Log.Warn($"No se pudo comprobar el archivo existente {s.Destination}; se descarga de nuevo.", ex);
            return false;
        }
    }

    private async Task<bool> VerifyAndFinishAsync(Session s, CancellationToken ct)
    {
        s.Tracker.Paused = true;
        try
        {
            RaiseVerificationStarted();

            long length;
            string? actual = null;
            try
            {
                if (!File.Exists(s.PartPath))
                {
                    // Existía al terminar el intento: casi siempre lo ha puesto en cuarentena el antivirus.
                    throw new FileNotFoundException("Falta el archivo parcial al verificar.", s.PartPath);
                }

                length = new FileInfo(s.PartPath).Length;
                if (s.ExpectedSize is long expected && length != expected)
                {
                    Log.Warn($"Tamaño incorrecto tras descargar: {length} ≠ {expected}");
                    return false;
                }

                if (s.ExpectedSha256 is not null)
                {
                    actual = await ComputeSha256WithRetryAsync(s.PartPath, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No es un fallo de la suma: no se borra ni se vuelve a descargar.
                throw MapVerificationReadError(s, ex);
            }

            if (actual is not null && !string.Equals(actual, s.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"SHA-256 incorrecto: esperado {s.ExpectedSha256}, obtenido {actual}");
                return false;
            }

            FileSystemUtil.MoveFileWithRetry(s.PartPath, s.Destination, overwrite: true, attempts: 10, delayMs: 300);
            FileSystemUtil.TryDeleteFile(s.MetadataPath);
            s.Tracker.ReportFinal(length);
            Log.Info($"Descarga completada y verificada: {s.Destination} ({FileSystemUtil.FormatBytes(length)})");
            return true;
        }
        finally
        {
            s.Tracker.Paused = false;
        }
    }

    /// <summary>
    /// SHA-256 del archivo reintentando (3 veces, 400 ms) si otro proceso lo tiene abierto en exclusiva: un antivirus suele
    /// escanear el archivo justo al cerrarlo. Los demás errores se propagan.
    /// </summary>
    private static async Task<string> ComputeSha256WithRetryAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ComputeSha256Async(path, ct).ConfigureAwait(false);
            }
            catch (IOException ex) when (FileSystemUtil.IsSharingViolation(ex) && attempt < HashOpenAttempts)
            {
                Log.Debug($"Archivo en uso al verificar {path} (intento {attempt}); se reintenta.");
                await Task.Delay(HashRetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Error leyendo el archivo descargado para verificarlo → mensaje de unidad, de antivirus o de lectura (nunca "dañado").</summary>
    private static LauncherException MapVerificationReadError(Session s, Exception ex)
    {
        if (!FileSystemUtil.IsDriveReady(s.Destination))
        {
            Log.Warn($"La unidad de la descarga ya no está disponible al verificar: {s.PartPath}", ex);
            return new LauncherException(DriveMissingMessage(DriveOf(s)), ex);
        }
        if (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException || FileSystemUtil.IsAntivirusBlock(ex))
        {
            Log.Warn($"El archivo descargado desapareció o está bloqueado (¿antivirus?): {s.PartPath}", ex);
            return new LauncherException(AntivirusMessage(ExclusionFolderOf(s)), ex);
        }
        Log.Warn($"No se pudo leer el archivo descargado para verificarlo: {s.PartPath}", ex);
        return new LauncherException(ReadErrorMessage, ex);
    }

    private static void EnsureDriveReady(Session s)
    {
        if (!FileSystemUtil.IsDriveReady(s.Destination))
        {
            Log.Warn($"La unidad de la descarga no está disponible: {s.Destination}");
            throw new LauncherException(DriveMissingMessage(DriveOf(s)));
        }
    }

    /// <summary>Unidad de la descarga para los mensajes ("E:\").</summary>
    private static string DriveOf(Session s)
    {
        try
        {
            return Path.GetPathRoot(s.Destination) is { Length: > 0 } root ? root : s.Destination;
        }
        catch (ArgumentException)
        {
            return s.Destination;
        }
    }

    /// <summary>Carpeta a excluir en el antivirus: la carpeta de instalación si la descarga está en su ".launcher".</summary>
    private static string ExclusionFolderOf(Session s) => FileSystemUtil.GetAntivirusExclusionFolder(s.Destination, isDirectory: false);

    // ───────────────────────── Estado del .part ─────────────────────────

    private static string? ValidateRequest(DownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url)
            || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException($"URL de descarga no válida: {request.Url}", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            throw new ArgumentException("Falta la ruta de destino.", nameof(request));
        }
        if (request.ExpectedSize is < 0)
        {
            throw new ArgumentException("ExpectedSize no puede ser negativo.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ExpectedSha256))
        {
            return null;
        }
        var sha = request.ExpectedSha256.Trim();
        if (sha.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            sha = sha["sha256:".Length..];
        }
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
        {
            throw new ArgumentException($"SHA-256 esperado no válido: {request.ExpectedSha256}", nameof(request));
        }
        return sha.ToLowerInvariant();
    }

    private static string GetEffectiveResumeKey(DownloadRequest request)
        => !string.IsNullOrWhiteSpace(request.ResumeKey)
            ? request.ResumeKey!
            : $"{request.Url}|{request.ExpectedSize?.ToString(CultureInfo.InvariantCulture) ?? "?"}";

    private static void EnsureDownloadDirectory(Session s)
    {
        var directory = Path.GetDirectoryName(s.Destination);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw MapFileError(s, ex) ?? new LauncherException($"No se pudo crear la carpeta de descargas ({directory}).", ex);
        }
    }

    /// <summary>Carga o descarta el .part existente. Devuelve los bytes reanudables.</summary>
    private static long PrepareResumeState(Session s)
    {
        var partExists = File.Exists(s.PartPath);
        var metadata = TryReadMetadata(s.MetadataPath, logProblems: partExists);

        if (!partExists)
        {
            if (File.Exists(s.MetadataPath))
            {
                FileSystemUtil.TryDeleteFile(s.MetadataPath);
            }
            s.Metadata = CreateMetadata(s);
            return 0;
        }

        if (metadata is null || !string.Equals(metadata.ResumeKey, s.ResumeKey, StringComparison.Ordinal))
        {
            Log.Info($"Hay una descarga parcial que no corresponde a este archivo; se empieza de cero: {s.PartPath}");
            ResetPartialState(s, deleteFiles: true);
            return 0;
        }

        var length = new FileInfo(s.PartPath).Length;
        if (s.ExpectedSize is long expected && length > expected)
        {
            Log.Warn($"La descarga parcial es más grande de lo esperado ({length} > {expected}); se empieza de cero.");
            ResetPartialState(s, deleteFiles: true);
            return 0;
        }

        s.Metadata = metadata;
        return length;
    }

    private static void ResetPartialState(Session s, bool deleteFiles)
    {
        if (deleteFiles)
        {
            DeletePartial(s.Destination);
        }
        s.Metadata = CreateMetadata(s);
        s.KnownTotal = s.ExpectedSize;
        s.Tracker.Reset(0, s.KnownTotal);
    }

    private static PartMetadata CreateMetadata(Session s) => new()
    {
        Url = s.Request.Url,
        ResumeKey = s.ResumeKey,
        ExpectedSize = s.ExpectedSize,
        ExpectedSha256 = s.ExpectedSha256,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static PartMetadata? TryReadMetadata(string metadataPath, bool logProblems)
    {
        try
        {
            if (!File.Exists(metadataPath))
            {
                return null;
            }
            var json = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize<PartMetadata>(json);
            if (metadata is null || string.IsNullOrEmpty(metadata.ResumeKey))
            {
                if (logProblems)
                {
                    Log.Warn($"Metadatos de descarga vacíos o incompletos: {metadataPath}");
                }
                return null;
            }
            return metadata;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (logProblems)
            {
                Log.Warn($"Metadatos de descarga ilegibles: {metadataPath}", ex);
            }
            return null;
        }
    }

    private static void SaveMetadata(Session s)
    {
        try
        {
            FileSystemUtil.WriteAllTextAtomic(s.MetadataPath, JsonSerializer.Serialize(s.Metadata, MetadataJsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw (Exception?)MapFileError(s, ex) ?? new LocalFileException(ex);
        }
    }

    private static bool ValidatorsChanged(PartMetadata metadata, string? etag, DateTimeOffset? lastModified)
    {
        // Un proxy puede convertir la ETag en débil (W/"x"): solo cuenta el valor.
        if (metadata.CdnETag is not null && etag is not null
            && !string.Equals(StripWeakPrefix(metadata.CdnETag), StripWeakPrefix(etag), StringComparison.Ordinal))
        {
            return true;
        }
        return metadata.CdnLastModified is { } stored && lastModified is { } current && stored != current;
    }

    private static string StripWeakPrefix(string etag)
        => etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? etag[2..] : etag;

    private static FileStream OpenPartFile(Session s, long startPosition)
    {
        FileStream? file = null;
        try
        {
            file = new FileStream(s.PartPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length != startPosition)
            {
                file.SetLength(startPosition);
            }
            file.Position = startPosition;
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            file?.Dispose();
            throw (Exception?)MapFileError(s, ex) ?? new LocalFileException(ex);
        }
    }

    private static async Task WritePartAsync(Session s, FileStream file, byte[] buffer, int count, CancellationToken ct)
    {
        try
        {
            await file.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw (Exception?)MapFileError(s, ex) ?? new LocalFileException(ex);
        }
    }

    private static async Task FlushPartAsync(Session s, FileStream file, CancellationToken ct)
    {
        try
        {
            await file.FlushAsync(ct).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw (Exception?)MapFileError(s, ex) ?? new LocalFileException(ex);
        }
    }

    /// <summary>
    /// Unidad no lista / disco lleno / antivirus / sin permisos → LauncherException; otros errores de disco → null (el
    /// llamador decide).
    /// </summary>
    private static LauncherException? MapFileError(Session s, Exception ex)
    {
        var directory = Path.GetDirectoryName(s.Destination) ?? s.Destination;
        if (!FileSystemUtil.IsDriveReady(s.Destination))
        {
            Log.Warn($"La unidad de la descarga ya no está disponible ({directory}).", ex);
            return new LauncherException(DriveMissingMessage(DriveOf(s)), ex);
        }
        if (FileSystemUtil.IsAntivirusBlock(ex))
        {
            Log.Warn($"El antivirus bloqueó la escritura de la descarga en {directory}.", ex);
            return new LauncherException(AntivirusMessage(ExclusionFolderOf(s)), ex);
        }
        if (FileSystemUtil.IsDiskFull(ex))
        {
            Log.Warn($"Disco lleno al descargar en {directory}.", ex);
            return new LauncherException(
                $"No hay espacio suficiente en el disco ({Path.GetPathRoot(directory)}). Libera espacio y vuelve a intentarlo: lo descargado se conserva.", ex);
        }
        if (ex is UnauthorizedAccessException)
        {
            Log.Warn($"Sin permisos para escribir en {directory}.", ex);
            return new LauncherException($"Sin permisos para escribir en {directory}.", ex);
        }
        return null;
    }

    // ───────────────────────── Utilidades ─────────────────────────

    private TimeSpan GetStallTimeout()
    {
        var stall = StallTimeout;
        return stall <= TimeSpan.Zero || stall == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : stall;
    }

    private static bool IsGitHubHost(string host)
        => host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }
        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }
        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }
        return null;
    }

    private void RaiseVerificationStarted()
    {
        try
        {
            VerificationStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("Error en un suscriptor de VerificationStarted.", ex);
        }
    }

    private void RaiseRetryScheduled(DownloadRetryInfo info)
    {
        try
        {
            RetryScheduled?.Invoke(this, info);
        }
        catch (Exception ex)
        {
            Log.Error("Error en un suscriptor de RetryScheduled.", ex);
        }
    }

    private static async Task RunHeartbeatAsync(ProgressTracker tracker, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                tracker.Heartbeat();
            }
        }
        catch (OperationCanceledException)
        {
            // Fin normal.
        }
        catch (Exception ex)
        {
            Log.Error("Error en el latido de progreso de la descarga.", ex);
        }
    }

    // ───────────────────────── Tipos internos ─────────────────────────

    private enum AttemptResult { Completed, Restart, Transient }

    private enum FailureKind { None, Network, NoInternet, Stall, Html, Server, CdnAuth, Tls, Protocol, LocalFile }

    private readonly record struct AttemptOutcome(AttemptResult Result, FailureKind Failure, string Reason, long Advanced,
        TimeSpan? RetryAfter, Exception? Error)
    {
        public static AttemptOutcome Completed() => new(AttemptResult.Completed, FailureKind.None, "", 0, null, null);

        public static AttemptOutcome Restart() => new(AttemptResult.Restart, FailureKind.None, "Reinicio de la descarga", 0, null, null);

        public static AttemptOutcome Transient(FailureKind failure, string reason, long advanced = 0,
            TimeSpan? retryAfter = null, Exception? error = null)
            => new(AttemptResult.Transient, failure, reason, advanced, retryAfter, error);
    }

    private sealed class AttemptState
    {
        /// <summary>Largo del .part al empezar el intento.</summary>
        public long StartOffset;

        /// <summary>Posición actual de escritura en el .part.</summary>
        public long Position;

        /// <summary>Avance neto del intento (lo que cuenta como "progreso" para reiniciar el contador de reintentos).</summary>
        public long Advanced => Math.Max(0, Position - StartOffset);
    }

    /// <summary>Error de E/S local no clasificado (se trata como intento fallido; nunca sale de esta clase).</summary>
    private sealed class LocalFileException(Exception inner) : Exception(inner.Message, inner);

    private sealed class Session
    {
        public Session(DownloadRequest request, string? expectedSha256, ProgressTracker tracker)
        {
            Request = request;
            Destination = Path.GetFullPath(request.DestinationPath);
            PartPath = Destination + ".part";
            MetadataPath = Destination + ".part.json";
            ResumeKey = GetEffectiveResumeKey(request);
            ExpectedSize = request.ExpectedSize;
            ExpectedSha256 = expectedSha256;
            KnownTotal = request.ExpectedSize;
            Tracker = tracker;
            Metadata = new PartMetadata();
        }

        public DownloadRequest Request { get; }
        public string Destination { get; }
        public string PartPath { get; }
        public string MetadataPath { get; }
        public string ResumeKey { get; }
        public long? ExpectedSize { get; }
        public string? ExpectedSha256 { get; }
        public long? KnownTotal { get; set; }
        public PartMetadata Metadata { get; set; }
        public ProgressTracker Tracker { get; }
    }

    private sealed class PartMetadata
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("resumeKey")] public string? ResumeKey { get; set; }
        [JsonPropertyName("expectedSize")] public long? ExpectedSize { get; set; }
        [JsonPropertyName("expectedSha256")] public string? ExpectedSha256 { get; set; }
        [JsonPropertyName("cdnETag")] public string? CdnETag { get; set; }
        [JsonPropertyName("cdnLastModified")] public DateTimeOffset? CdnLastModified { get; set; }
        [JsonPropertyName("createdAtUtc")] public DateTimeOffset CreatedAtUtc { get; set; }
    }

    /// <summary>Token bucket: capacidad de ~0,2 s de tráfico, empieza vacío.</summary>
    private sealed class RateLimiter
    {
        private readonly double _rate;
        private readonly double _capacity;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _tokens;
        private TimeSpan _last;

        public RateLimiter(long bytesPerSecond)
        {
            _rate = bytesPerSecond;
            _capacity = Math.Max(bytesPerSecond * 0.2, 4096);
        }

        public TimeSpan Consume(int bytes)
        {
            var now = _clock.Elapsed;
            _tokens = Math.Min(_capacity, _tokens + (now - _last).TotalSeconds * _rate);
            _last = now;
            _tokens -= bytes;
            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _rate);
        }
    }

    /// <summary>Velocidad (media exponencial ~2 s), ETA y frecuencia de reportes (≤ 10/s por datos, ≥ 1/s por latido).</summary>
    private sealed class ProgressTracker
    {
        private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan HeartbeatThreshold = TimeSpan.FromMilliseconds(800);
        private const double SmoothingSeconds = 2.0;

        private readonly object _lock = new();
        private readonly IProgress<DownloadProgress>? _progress;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _received;
        private long? _total;
        private double _speed;
        private long _sampleBytes;
        private TimeSpan _sampleTime;
        private TimeSpan _lastReport = TimeSpan.FromSeconds(-10);
        private bool _bytesSinceReport;

        private volatile bool _paused = true;

        public ProgressTracker(IProgress<DownloadProgress>? progress) => _progress = progress;

        /// <summary>Mientras está en pausa (preparación, verificación) el latido no reporta. Empieza en pausa.</summary>
        public bool Paused
        {
            get => _paused;
            set => _paused = value;
        }

        /// <summary>Nuevo punto de partida (inicio de intento, reinicio desde 0). No genera picos de velocidad.</summary>
        public void Reset(long received, long? total)
        {
            lock (_lock)
            {
                _received = received;
                _sampleBytes = received;
                _sampleTime = _clock.Elapsed;
                _total = total;
            }
        }

        public void OnBytes(long received)
        {
            if (_progress is null)
            {
                return;
            }
            lock (_lock)
            {
                _received = received;
                _bytesSinceReport = true;
                var now = _clock.Elapsed;
                if (now - _lastReport >= MinInterval)
                {
                    ReportLocked(now, final: false);
                }
            }
        }

        public void Heartbeat()
        {
            if (_progress is null || Paused)
            {
                return;
            }
            lock (_lock)
            {
                var now = _clock.Elapsed;
                if (now - _lastReport >= HeartbeatThreshold)
                {
                    ReportLocked(now, final: false);
                }
            }
        }

        public void ReportFinal(long received)
        {
            if (_progress is null)
            {
                return;
            }
            lock (_lock)
            {
                _received = received;
                _total ??= received;
                _bytesSinceReport = true;
                ReportLocked(_clock.Elapsed, final: true);
            }
        }

        private void ReportLocked(TimeSpan now, bool final)
        {
            var dt = (now - _sampleTime).TotalSeconds;
            if (dt > 0.001)
            {
                var instant = Math.Max(0, _received - _sampleBytes) / dt;
                var alpha = 1 - Math.Exp(-dt / SmoothingSeconds);
                _speed += alpha * (instant - _speed);
                if (_speed < 1)
                {
                    _speed = 0;
                }
                _sampleBytes = _received;
                _sampleTime = now;
            }

            TimeSpan? eta = null;
            if (final)
            {
                eta = TimeSpan.Zero;
            }
            else if (_bytesSinceReport && _total is long total && _speed > 0 && total >= _received)
            {
                eta = TimeSpan.FromSeconds(Math.Min((total - _received) / _speed, TimeSpan.FromDays(7).TotalSeconds));
            }

            _bytesSinceReport = false;
            _lastReport = now;
            try
            {
                _progress?.Report(new DownloadProgress(_received, _total, _speed, eta));
            }
            catch (Exception ex)
            {
                Log.Error("Error al reportar el progreso de la descarga.", ex);
            }
        }
    }
}
