using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Net;
using PalitoLauncher.Core.Util;

namespace PalitoLauncher.Core.GitHub;

/// <summary>
/// Límite de consultas a GitHub alcanzado. <see cref="Exception.Message"/> ya incluye la hora local de restablecimiento.
/// </summary>
public sealed class GitHubRateLimitException : Util.LauncherException
{
    public GitHubRateLimitException(string userMessage, DateTimeOffset resetAtUtc)
        : base(userMessage)
    {
        ResetAtUtc = resetAtUtc;
    }

    public DateTimeOffset ResetAtUtc { get; }
}

/// <summary>
/// Cliente mínimo de la API REST de GitHub SIN autenticación: 60 peticiones/hora por IP, compartidas por todos los
/// equipos detrás de la misma IP, y las respuestas 304 TAMBIÉN cuentan (solo son gratis con token). Por eso:
/// - Caché por URL en &lt;cacheDir&gt;\api-&lt;sha1(url) hex 16&gt;.json = {url, status, etag, lastModified, fetchedAtUtc, body}
///   escrita de forma atómica. El ETag se guarda tal cual (incluido "W/") y se envía con TryAddWithoutValidation.
/// - Ventana de frescura: si la caché de esa URL tiene menos de LauncherConfig.ApiFreshnessWindow y se parsea bien, se
///   devuelve sin ir a la red (Source = NotModifiedCache), salvo <c>bypassFreshness</c>.
/// - Estado de rate limit global del cliente, actualizado con X-RateLimit-Remaining / X-RateLimit-Reset de CADA respuesta
///   (200, 304, 403, 429) y persistido en &lt;cacheDir&gt;\ratelimit.json. Si Remaining == 0 y aún no pasó el reset, NO se hace
///   ninguna petición a la API hasta esa hora (se devuelve caché con aviso o se lanza GitHubRateLimitException).
///   Clasificación: límite primario = 403/429 con X-RateLimit-Remaining "0" (reset = X-RateLimit-Reset); secundario =
///   403/429 con Retry-After (reset = ahora + Retry-After); otro 403/451 = "GitHub rechazó la consulta" (genérico, también
///   cae a caché). La hora mostrada = hora local actual + (X-RateLimit-Reset − cabecera Date del servidor), para no depender
///   de que el reloj del PC esté bien. Se registra X-RateLimit-Remaining en el log (Info) en cada respuesta.
///   El estado guardado (en memoria o leído de ratelimit.json) se valida con el reloj actual cada vez que se consulta: si el
///   reset queda a más de 2 h de ahora o el estado se guardó más de 2 min "en el futuro" (la hora de Windows retrocedió, o
///   ratelimit.json trae fechas imposibles), se descarta (Remaining y reset pasan a null) y se vuelve a preguntar a GitHub.
///   Así un cambio de hora nunca deja el launcher bloqueado más de 2 h.
/// - Peticiones concurrentes a la misma URL se fusionan (la segunda espera el resultado de la primera).
/// - If-None-Match solo se envía si el cuerpo en caché existe y se parsea; si llega un 304 y la caché no se puede leer, se
///   repite una vez sin If-None-Match. Caché corrupta → se borra y se ignora (nunca lanza por la caché).
/// - Respuesta 200 que no es JSON (portal cautivo, HTML, proxy) → error de red.
/// - Errores de red con HttpRequestException.HttpRequestError: NameResolutionError → "Sin conexión a Internet (no se
///   encuentra github.com)."; SecureConnectionError → "No se pudo establecer una conexión segura con GitHub. Revisa la
///   fecha y hora de Windows, el antivirus o si la red wifi requiere iniciar sesión."; resto → "No se pudo conectar con GitHub.".
/// Cabeceras: Accept: application/vnd.github+json, X-GitHub-Api-Version: 2022-11-28, User-Agent (HttpFactory).
/// Timeout por petición: 20 s (CTS enlazado; un timeout es error de red, no cancelación). Thread-safe.
/// </summary>
public sealed class GitHubClient
{
    private const string ApiBaseUrl = "https://api.github.com";
    private const string ApiAccept = "application/vnd.github+json";
    private const string ImageAccept = "image/png, image/jpeg, image/*;q=0.8";
    private const int MaxApiBodyBytes = 16 * 1024 * 1024;
    private const int MaxFileBytes = 20 * 1024 * 1024;

    private static readonly TimeSpan NotFoundCacheDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan MaxResetDelay = TimeSpan.FromHours(2);
    private static readonly TimeSpan DefaultRateWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultSecondaryWait = TimeSpan.FromSeconds(60);

    /// <summary>Margen para pequeños ajustes de hora (sincronización NTP) antes de dar el estado por no fiable.</summary>
    private static readonly TimeSpan ClockBackTolerance = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions CacheJsonOptions = new() { WriteIndented = false };

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly string _rateLimitFile;
    private readonly bool _addUserAgent;

    private readonly object _stateLock = new();
    private readonly object _persistLock = new();
    private readonly object _inflightLock = new();
    private readonly Dictionary<string, Task> _inflight = new(StringComparer.Ordinal);

    private int? _rateRemaining;
    private DateTimeOffset? _rateResetUtc;
    private DateTimeOffset? _rateUpdatedUtc;

    public GitHubClient(HttpClient http, string cacheDir)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDir);

        _http = http;
        _cacheDir = Path.GetFullPath(cacheDir);
        _rateLimitFile = Path.Combine(_cacheDir, "ratelimit.json");
        // Se mira una sola vez: leer las cabeceras por defecto desde varios hilos no es seguro.
        _addUserAgent = !http.DefaultRequestHeaders.Contains("User-Agent");

        try
        {
            Directory.CreateDirectory(_cacheDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"No se pudo crear la carpeta de caché de GitHub: {_cacheDir}", ex);
        }

        LoadRateLimitState();
    }

    /// <summary>Reloj inyectable (tests de frescura / reset).</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;

    /// <summary>Timeout de cada petición a la API (tests).</summary>
    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Timeout de la descarga de archivos de <see cref="GetCachedFileAsync"/> (hasta 20 MB).</summary>
    internal TimeSpan FileRequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Peticiones restantes según la última respuesta de la API (null si aún no se sabe, si ya pasó el reset o si el estado
    /// guardado no es fiable porque la hora de Windows retrocedió).
    /// </summary>
    public int? RateLimitRemaining
    {
        get
        {
            ReadRateState(out var remaining);
            return remaining;
        }
    }

    /// <summary>
    /// Momento (UTC, ya corregido con la cabecera Date) en que se restablece el cupo; null si no se sabe, si ya pasó o si no
    /// es creíble (nunca más de 2 h por delante del reloj actual).
    /// </summary>
    public DateTimeOffset? RateLimitResetUtc => ReadRateState(out _);

    /// <summary>
    /// True si ahora mismo estamos dentro de una ventana de rate limit (Remaining == 0 y el reset, creíble, no ha pasado).
    /// </summary>
    public bool IsRateLimited => ReadRateState(out var remaining) is not null && remaining == 0;

    /// <summary>
    /// GET /repos/{owner}/{repo}/releases?per_page=100.
    /// 200 → parsea, guarda caché y devuelve Source=Network. 304 o caché fresca → Source=NotModifiedCache.
    /// Error de red, timeout, rate limit, 5xx, otro 403 o JSON inválido →
    ///   si hay caché: Source=OfflineCache con Warning en español (p.ej. "Límite de consultas a GitHub alcanzado; se
    ///   restablece a las 14:32. Mostrando datos guardados." o "Sin conexión con GitHub. Mostrando datos guardados.");
    ///   si no hay caché: GitHubRateLimitException (rate limit) o LauncherException (resto) con mensaje en español.
    /// 404 → LauncherException ("No se encontró el repositorio … en GitHub (¿es privado o cambió de nombre?)"), pero si hay
    ///   caché se devuelve como OfflineCache con ese aviso (así un amigo con el juego instalado puede seguir jugando).
    /// OperationCanceledException solo si <paramref name="ct"/> se canceló.
    /// </summary>
    public Task<ReleaseFetchResult> GetReleasesAsync(string owner, string repo, CancellationToken ct, bool bypassFreshness = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        if (ct.IsCancellationRequested) return Task.FromCanceled<ReleaseFetchResult>(ct);

        owner = owner.Trim();
        repo = repo.Trim();
        var url = $"{ApiBaseUrl}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases?per_page=100";
        var key = (bypassFreshness ? "releases-bypass|" : "releases|") + url;
        return JoinAsync(key, () => FetchReleasesAsync(url, $"{owner}/{repo}", bypassFreshness), ct);
    }

    /// <summary>
    /// GET /repos/{owner}/{repo}/releases/latest con la misma caché. Un 404 (repo inexistente/privado o sin releases) se
    /// cachea 6 h: durante ese tiempo devuelve null sin preguntar. Cualquier error o rate limit → null (registrado).
    /// Pensado para la auto-actualización del launcher (best-effort). Solo lanza OperationCanceledException si se canceló <paramref name="ct"/>.
    /// </summary>
    public Task<ReleaseInfo?> GetLatestReleaseAsync(string owner, string repo, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<ReleaseInfo?>(ct);
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            Log.Warn("GetLatestReleaseAsync: falta el propietario o el nombre del repositorio.");
            return Task.FromResult<ReleaseInfo?>(null);
        }

        var url = $"{ApiBaseUrl}/repos/{Uri.EscapeDataString(owner.Trim())}/{Uri.EscapeDataString(repo.Trim())}/releases/latest";
        return JoinAsync("latest|" + url, () => FetchLatestAsync(url), ct);
    }

    /// <summary>
    /// Descarga un archivo pequeño de imagen (p.ej. fondo desde raw.githubusercontent.com, que no gasta cupo de la API) a
    /// &lt;cacheDir&gt;\&lt;cacheFileName&gt; con ETag/If-None-Match. Solo acepta 200 si Content-Type empieza por "image/" y los
    /// primeros bytes son PNG (89 50 4E 47) o JPEG (FF D8 FF); si no, conserva la caché anterior. 304 → ruta en caché;
    /// 404 → borra la caché y devuelve null; error de red → ruta en caché si existe, si no null. Máximo 20 MB. Escritura
    /// atómica. Nunca lanza (salvo OperationCanceledException si se canceló <paramref name="ct"/>).
    /// </summary>
    public Task<string?> GetCachedFileAsync(string url, string cacheFileName, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<string?>(ct);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            Log.Warn($"GetCachedFileAsync: URL no válida \"{url}\".");
            return Task.FromResult<string?>(null);
        }
        if (!IsPlainFileName(cacheFileName))
        {
            Log.Warn($"GetCachedFileAsync: nombre de archivo de caché no válido \"{cacheFileName}\".");
            return Task.FromResult<string?>(null);
        }

        var key = "file|" + cacheFileName.ToLowerInvariant() + "|" + url;
        return JoinAsync(key, () => FetchFileAsync(url, cacheFileName), ct);
    }

    /// <summary>
    /// Convierte el JSON de la API (array de releases) a modelos. Expuesto para tests. Tolerante a campos null/ausentes.
    /// Mapea: id, tag_name, name, body, draft, prerelease, published_at, html_url y assets[] (id, name, size,
    /// browser_download_url, content_type, updated_at, digest "sha256:HEX" → Sha256 en minúsculas).
    /// Descarta assets cuyo "state" exista y no sea "uploaded" (subida en curso/fallida) y assets con size 0.
    /// </summary>
    internal static IReadOnlyList<ReleaseInfo> ParseReleases(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(StripBom(json));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Se esperaba un array de releases.");

        var list = new List<ReleaseInfo>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object)
                list.Add(ParseReleaseElement(element));
        }
        return list.AsReadOnly();
    }

    /// <summary>Igual que <see cref="ParseReleases"/> pero para un único objeto release.</summary>
    internal static ReleaseInfo ParseRelease(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(StripBom(json));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Se esperaba un objeto release.");
        return ParseReleaseElement(doc.RootElement);
    }

    // ───────────────────────────── Releases ─────────────────────────────

    private async Task<ReleaseFetchResult> FetchReleasesAsync(string url, string repoDisplay, bool bypassFreshness)
    {
        var cache = ReadCache(url);
        IReadOnlyList<ReleaseInfo>? cached = null;
        if (cache is { Status: 200, Body: not null })
        {
            try
            {
                cached = ParseReleases(cache.Body);
            }
            catch (JsonException ex)
            {
                Log.Warn($"Caché de releases corrupta, se descarta: {url}", ex);
                DeleteCache(url);
            }
        }
        if (cached is null) cache = null;

        if (!bypassFreshness && cache is not null && cached is not null && IsFresh(cache.FetchedAtUtc))
        {
            return new ReleaseFetchResult { Releases = cached, Source = ReleaseFetchSource.NotModifiedCache, FetchedAt = cache.FetchedAtUtc };
        }

        try
        {
            var response = await SendApiAsync(url, cache?.ETag, cache?.LastModified).ConfigureAwait(false);
            switch (response.Kind)
            {
                case ApiResponseKind.Ok:
                {
                    IReadOnlyList<ReleaseInfo> releases;
                    try
                    {
                        releases = ParseReleases(response.Body!);
                    }
                    catch (JsonException ex)
                    {
                        throw FetchFailure.InvalidResponse(ex);
                    }

                    var fetchedAt = UtcNow();
                    WriteCache(new CacheEntry
                    {
                        Url = url, Status = 200, ETag = response.ETag, LastModified = response.LastModified,
                        FetchedAtUtc = fetchedAt, Body = response.Body,
                    });
                    return new ReleaseFetchResult { Releases = releases, Source = ReleaseFetchSource.Network, FetchedAt = fetchedAt };
                }

                case ApiResponseKind.NotModified when cache is not null && cached is not null:
                {
                    var fetchedAt = UtcNow();
                    TouchCache(cache, response, fetchedAt);
                    return new ReleaseFetchResult { Releases = cached, Source = ReleaseFetchSource.NotModifiedCache, FetchedAt = fetchedAt };
                }

                case ApiResponseKind.NotFound:
                    throw FetchFailure.NotFound(repoDisplay);

                default:
                    throw FetchFailure.InvalidResponse(null);
            }
        }
        catch (FetchFailure failure)
        {
            return OfflineOrThrow(failure, cache, cached, url);
        }
        catch (Exception ex)
        {
            Log.Error($"Error inesperado al consultar las releases de GitHub ({url}).", ex);
            return OfflineOrThrow(FetchFailure.Unexpected(ex), cache, cached, url);
        }
    }

    private static ReleaseFetchResult OfflineOrThrow(FetchFailure failure, CacheEntry? cache, IReadOnlyList<ReleaseInfo>? cached, string url)
    {
        if (cache is not null && cached is not null)
        {
            Log.Warn($"GitHub no disponible ({failure.Message}); se usan los datos guardados de {cache.FetchedAtUtc:u}: {url}",
                failure.InnerException);
            return new ReleaseFetchResult
            {
                Releases = cached, Source = ReleaseFetchSource.OfflineCache, FetchedAt = cache.FetchedAtUtc, Warning = failure.CacheWarning,
            };
        }

        Log.Warn($"No se pudieron obtener las releases y no hay caché: {failure.Message} ({url})", failure.InnerException);
        if (failure.Kind == FailureKind.RateLimit && failure.ResetUtc is { } reset)
            throw new GitHubRateLimitException(failure.Message, reset);
        throw new LauncherException(failure.Message, failure.InnerException);
    }

    // ───────────────────────────── Última release ─────────────────────────────

    private async Task<ReleaseInfo?> FetchLatestAsync(string url)
    {
        try
        {
            var cache = ReadCache(url);
            ReleaseInfo? cached = null;

            if (cache is { Status: 404 })
            {
                var age = UtcNow() - cache.FetchedAtUtc;
                if (age >= TimeSpan.Zero && age < NotFoundCacheDuration)
                    return null; // 404 reciente: no se vuelve a preguntar
                cache = null;
            }
            else if (cache is { Status: 200, Body: not null })
            {
                try
                {
                    cached = ParseRelease(cache.Body);
                }
                catch (JsonException ex)
                {
                    Log.Warn($"Caché de la última release corrupta, se descarta: {url}", ex);
                    DeleteCache(url);
                }
                if (cached is null) cache = null;
                else if (IsFresh(cache!.FetchedAtUtc)) return cached;
            }
            else
            {
                cache = null;
            }

            var response = await SendApiAsync(url, cache?.ETag, cache?.LastModified).ConfigureAwait(false);
            switch (response.Kind)
            {
                case ApiResponseKind.Ok:
                {
                    ReleaseInfo release;
                    try
                    {
                        release = ParseRelease(response.Body!);
                    }
                    catch (JsonException ex)
                    {
                        throw FetchFailure.InvalidResponse(ex);
                    }
                    WriteCache(new CacheEntry
                    {
                        Url = url, Status = 200, ETag = response.ETag, LastModified = response.LastModified,
                        FetchedAtUtc = UtcNow(), Body = response.Body,
                    });
                    return release;
                }

                case ApiResponseKind.NotModified when cache is not null && cached is not null:
                    TouchCache(cache, response, UtcNow());
                    return cached;

                case ApiResponseKind.NotFound:
                    Log.Info($"GitHub: 404 en {url} (sin releases o repositorio privado); no se vuelve a preguntar en 6 h.");
                    WriteCache(new CacheEntry { Url = url, Status = 404, FetchedAtUtc = UtcNow() });
                    return null;

                default:
                    throw FetchFailure.InvalidResponse(null);
            }
        }
        catch (FetchFailure failure)
        {
            Log.Warn($"No se pudo consultar la última release ({url}): {failure.Message}", failure.InnerException);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Error inesperado al consultar la última release ({url}).", ex);
            return null;
        }
    }

    // ───────────────────────────── Archivos (fondo) ─────────────────────────────

    private async Task<string?> FetchFileAsync(string url, string cacheFileName)
    {
        var path = Path.Combine(_cacheDir, cacheFileName);
        var metaPath = path + ".meta.json";
        try
        {
            var hasValidCache = IsValidCachedImage(path);
            var meta = hasValidCache ? ReadFileMeta(metaPath) : null;
            var sameUrl = meta is not null && string.Equals(meta.Url, url, StringComparison.Ordinal);
            var etag = sameUrl ? meta!.ETag : null;
            var lastModified = sameUrl ? meta!.LastModified : null;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var raw = await SendOnceAsync(url, isApi: false, ImageAccept, etag, lastModified, noCache: attempt > 0,
                    FileRequestTimeout, MaxFileBytes).ConfigureAwait(false);

                switch (raw.Status)
                {
                    case 200:
                        if (!IsImageContentType(raw.ContentType) || raw.Body is null || !HasImageSignature(raw.Body.AsSpan()))
                        {
                            Log.Warn($"El archivo descargado no es una imagen PNG/JPEG válida (Content-Type: {raw.ContentType ?? "?"}); " +
                                     $"se conserva la caché: {url}");
                            return IsValidCachedImage(path) ? path : null;
                        }
                        await WriteAllBytesAtomicAsync(path, raw.Body!).ConfigureAwait(false);
                        WriteFileMeta(metaPath, new FileMeta
                        {
                            Url = url, ETag = raw.ETag, LastModified = raw.LastModified, FetchedAtUtc = UtcNow(),
                        });
                        return path;

                    case 304:
                        if (IsValidCachedImage(path)) return path;
                        // 304 sin caché legible: se repite una vez sin validadores.
                        etag = null;
                        lastModified = null;
                        continue;

                    case 404:
                    case 410:
                        Log.Info($"El archivo remoto ya no existe ({raw.Status}); se borra la caché: {url}");
                        FileSystemUtil.TryDeleteFile(path);
                        FileSystemUtil.TryDeleteFile(metaPath);
                        return null;

                    default:
                        Log.Warn($"No se pudo descargar {url} (código {raw.Status}).");
                        return IsValidCachedImage(path) ? path : null;
                }
            }

            Log.Warn($"Respuesta 304 inesperada sin caché para {url}.");
            return null;
        }
        catch (FetchFailure failure)
        {
            Log.Warn($"No se pudo descargar {url}: {failure.Message}", failure.InnerException);
            return IsValidCachedImage(path) ? path : null;
        }
        catch (Exception ex)
        {
            Log.Error($"Error inesperado al descargar {url}.", ex);
            return IsValidCachedImage(path) ? path : null;
        }
    }

    private static bool IsImageContentType(string? contentType)
        => contentType is not null && contentType.TrimStart().StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static bool HasImageSignature(ReadOnlySpan<byte> data)
        => (data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
           || (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF);

    /// <summary>True si el archivo existe y empieza por la firma PNG/JPEG. Si existe pero está dañado, lo borra.</summary>
    private static bool IsValidCachedImage(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            Span<byte> header = stackalloc byte[4];
            int read;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                read = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            }
            if (HasImageSignature(header[..read])) return true;

            Log.Warn($"Imagen en caché dañada, se borra: {path}");
            FileSystemUtil.TryDeleteFile(path);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"No se pudo leer la imagen en caché: {path}", ex);
            return false;
        }
    }

    private static bool IsPlainFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);
    }

    private static async Task WriteAllBytesAtomicAsync(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            var tmp = $"{path}.{RandomNumberGenerator.GetHexString(8, lowercase: true)}.tmp";
            try
            {
                // Siempre se ejecuta en el pool (Task.Run en JoinAsync): la escritura síncrona no bloquea la UI.
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(flushToDisk: true);
                }
                if (File.Exists(path))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileSystemUtil.TryDeleteFile(tmp);
                if (attempt >= attempts) throw;
                await Task.Delay(150).ConfigureAwait(false);
            }
            catch
            {
                FileSystemUtil.TryDeleteFile(tmp);
                throw;
            }
        }
    }

    // ───────────────────────────── HTTP ─────────────────────────────

    /// <summary>Petición a la API con rate limit, 304 sin caché → reintento, y clasificación de errores.</summary>
    private async Task<ApiResponse> SendApiAsync(string url, string? etag, string? lastModified)
    {
        var sentValidators = etag is not null || lastModified is not null;
        for (var attempt = 0; ; attempt++)
        {
            EnsureNotRateLimited(url);

            var raw = await SendOnceAsync(url, isApi: true, ApiAccept, etag, lastModified, noCache: attempt > 0,
                RequestTimeout, MaxApiBodyBytes).ConfigureAwait(false);

            switch (raw.Status)
            {
                case 200:
                {
                    var text = StripBom(new UTF8Encoding(false).GetString(raw.Body ?? Array.Empty<byte>()));
                    var trimmed = text.AsSpan().TrimStart();
                    var looksJson = trimmed.Length > 0 && (trimmed[0] == '[' || trimmed[0] == '{');
                    var isHtml = raw.ContentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
                    if (!looksJson || isHtml)
                    {
                        Log.Warn($"Respuesta 200 que no es JSON (Content-Type: {raw.ContentType ?? "?"}): {url}");
                        throw FetchFailure.InvalidResponse(null);
                    }
                    return new ApiResponse(ApiResponseKind.Ok, text, raw.ETag, raw.LastModified);
                }

                case 304:
                    if (sentValidators)
                        return new ApiResponse(ApiResponseKind.NotModified, null, raw.ETag, raw.LastModified);
                    if (attempt == 0)
                    {
                        Log.Warn($"304 sin caché utilizable; se repite sin If-None-Match: {url}");
                        continue;
                    }
                    throw FetchFailure.InvalidResponse(null);

                case 404:
                    return new ApiResponse(ApiResponseKind.NotFound, null, null, null);

                case 403 or 429 when raw.RateLimitResetUtc is { } reset:
                    throw FetchFailure.RateLimit(reset);

                case 403 or 451:
                    throw FetchFailure.Rejected(raw.Status);

                case >= 500:
                    throw FetchFailure.Server(raw.Status);

                default:
                    throw FetchFailure.Rejected(raw.Status);
            }
        }
    }

    private void EnsureNotRateLimited(string url)
    {
        if (ReadRateState(out var remaining) is { } until && remaining == 0)
        {
            // Hora en UTC: ToLocalTime podría desbordar con fechas extremas.
            Log.Info($"GitHub: límite de consultas agotado hasta {until:u}; no se pregunta: {url}");
            throw FetchFailure.RateLimit(until);
        }
    }

    private async Task<RawResponse> SendOnceAsync(string url, bool isApi, string accept, string? etag, string? lastModified,
        bool noCache, TimeSpan timeout, int maxBytes)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        if (isApi) request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (_addUserAgent) request.Headers.TryAddWithoutValidation("User-Agent", HttpFactory.UserAgent);
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        else if (lastModified is not null) request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
        if (noCache) request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;
            var raw = new RawResponse
            {
                Status = status,
                ETag = GetHeader(response.Headers, "ETag"),
                LastModified = GetHeader(response.Content.Headers, "Last-Modified"),
                ContentType = GetHeader(response.Content.Headers, "Content-Type"),
            };

            if (isApi)
            {
                var remaining = UpdateRateLimit(response, status, out var rateLimitReset);
                raw.RateLimitResetUtc = rateLimitReset;
                Log.Info($"GitHub GET {url} → {status} (X-RateLimit-Remaining={remaining?.ToString(CultureInfo.InvariantCulture) ?? "?"})");
            }
            else
            {
                Log.Debug($"GET {url} → {status}");
            }

            if (status == 200)
                raw.Body = await ReadBodyAsync(response.Content, maxBytes, timeoutCts.Token).ConfigureAwait(false);

            return raw;
        }
        catch (OperationCanceledException ex)
        {
            // Aquí no hay token externo: cualquier cancelación es el timeout de la petición.
            throw FetchFailure.Timeout(ex);
        }
        catch (HttpRequestException ex)
        {
            throw FetchFailure.Network(ex);
        }
        catch (IOException ex)
        {
            throw FetchFailure.Network(ex);
        }
        catch (InvalidDataException ex)
        {
            throw FetchFailure.InvalidResponse(ex);
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        var declared = content.Headers.ContentLength;
        if (declared > maxBytes)
            throw new InvalidDataException($"Respuesta demasiado grande ({declared} bytes).");

        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var streamScope = stream.ConfigureAwait(false);
        using var ms = declared is > 0 ? new MemoryStream((int)declared.Value) : new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > maxBytes)
                throw new InvalidDataException($"Respuesta demasiado grande (más de {maxBytes} bytes).");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static string? GetHeader(HttpHeaders headers, string name)
    {
        if (!headers.NonValidated.TryGetValues(name, out var values)) return null;
        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrEmpty(trimmed)) return trimmed;
        }
        return null;
    }

    // ───────────────────────────── Rate limit ─────────────────────────────

    /// <summary>
    /// Actualiza el estado con las cabeceras de la respuesta. Devuelve X-RateLimit-Remaining (o null) y, si la respuesta
    /// es un rechazo por límite (primario o secundario), la hora corregida en que se podrá volver a preguntar.
    /// </summary>
    private int? UpdateRateLimit(HttpResponseMessage response, int status, out DateTimeOffset? rateLimitedUntil)
    {
        rateLimitedUntil = null;
        var now = UtcNow();
        var serverDate = response.Headers.Date;

        int? remaining = int.TryParse(GetHeader(response.Headers, "X-RateLimit-Remaining"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var r) ? Math.Max(0, r) : null;
        DateTimeOffset? reset = long.TryParse(GetHeader(response.Headers, "X-RateLimit-Reset"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var epoch) ? CorrectServerTime(epoch, serverDate, now) : null;

        if (status is 403 or 429)
        {
            if (remaining == 0)
            {
                // Límite primario.
                reset ??= now + DefaultRateWindow;
                rateLimitedUntil = reset;
            }
            else if (response.Headers.RetryAfter is not null || status == 429)
            {
                // Límite secundario: se espera Retry-After (o 60 s si no lo indica).
                var wait = DefaultSecondaryWait;
                if (response.Headers.RetryAfter?.Delta is { } delta) wait = delta;
                else if (response.Headers.RetryAfter?.Date is { } date) wait = date - (serverDate ?? now);
                wait = Clamp(wait, TimeSpan.FromSeconds(1), MaxResetDelay);

                remaining = 0;
                reset = now + wait;
                rateLimitedUntil = reset;
            }
        }

        if (remaining is null) return null;

        lock (_stateLock)
        {
            _rateRemaining = remaining;
            _rateResetUtc = reset ?? now + DefaultRateWindow;
            _rateUpdatedUtc = now;
        }
        PersistRateLimit();
        return remaining;
    }

    /// <summary>
    /// Lee el estado de rate limit validado con el reloj actual. Devuelve el reset vigente (futuro y creíble) o null; en
    /// <paramref name="remaining"/> las peticiones restantes, o null si no hay reset vigente (ya no se sabe cuántas quedan).
    /// Si el estado no es fiable (ver <see cref="ValidateRateStateLocked"/>) lo olvida y lo registra en el log.
    /// </summary>
    private DateTimeOffset? ReadRateState(out int? remaining)
    {
        DateTimeOffset? reset;
        string? discarded;
        lock (_stateLock)
        {
            reset = ValidateRateStateLocked(UtcNow(), out discarded);
            remaining = reset is null ? null : _rateRemaining;
        }
        if (discarded is not null)
            Log.Warn($"GitHub: el estado del límite de consultas no es fiable (¿cambió la hora de Windows?); se descarta: {discarded}");
        return reset;
    }

    /// <summary>
    /// Con <see cref="_stateLock"/> tomado. Reset vigente o null. Un estado legítimo nunca tiene el reset a más de
    /// <see cref="MaxResetDelay"/> de ahora ni se guardó en el futuro; si pasa, el reloj retrocedió o ratelimit.json es
    /// absurdo: se borra el estado en memoria (Remaining/reset = null) y <paramref name="discarded"/> explica por qué.
    /// </summary>
    private DateTimeOffset? ValidateRateStateLocked(DateTimeOffset now, out string? discarded)
    {
        discarded = null;
        if (_rateResetUtc is not { } reset) return null;

        // Restas de DateTimeOffset: no desbordan aunque las fechas sean extremas.
        var resetAhead = reset - now;
        var updatedAhead = _rateUpdatedUtc is { } updated ? updated - now : TimeSpan.Zero;
        if (resetAhead > MaxResetDelay || updatedAhead > ClockBackTolerance)
        {
            discarded = string.Create(CultureInfo.InvariantCulture,
                $"reset {reset:u}, guardado {_rateUpdatedUtc:u}, ahora {now:u}");
            _rateRemaining = null;
            _rateResetUtc = null;
            _rateUpdatedUtc = null;
            return null;
        }

        return reset > now ? reset : null;
    }

    /// <summary>Hora local actual + (reset del servidor − Date del servidor), limitada a [0, 2 h].</summary>
    private static DateTimeOffset? CorrectServerTime(long epochSeconds, DateTimeOffset? serverDate, DateTimeOffset now)
    {
        if (epochSeconds <= 0 || epochSeconds > 253_402_300_799) return null;
        var serverReset = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
        var delta = serverReset - (serverDate ?? now);
        return now + Clamp(delta, TimeSpan.Zero, MaxResetDelay);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
        => value < min ? min : value > max ? max : value;

    /// <summary>
    /// Carga ratelimit.json. No se valida aquí contra el reloj: se hace en cada lectura (<see cref="ReadRateState"/>), con el
    /// reloj vigente, así un reset demasiado lejano o un "guardado" en el futuro se descartan antes de bloquear nada.
    /// </summary>
    private void LoadRateLimitState()
    {
        try
        {
            if (!File.Exists(_rateLimitFile)) return;
            var state = JsonSerializer.Deserialize<RateLimitState>(File.ReadAllText(_rateLimitFile, Encoding.UTF8), CacheJsonOptions);
            if (state?.Remaining is { } remaining && state.ResetUtc is { } reset)
            {
                lock (_stateLock)
                {
                    _rateRemaining = Math.Max(0, remaining);
                    _rateResetUtc = reset;
                    _rateUpdatedUtc = state.UpdatedUtc;
                }
            }
        }
        catch (JsonException ex)
        {
            Log.Warn("ratelimit.json corrupto, se descarta.", ex);
            FileSystemUtil.TryDeleteFile(_rateLimitFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("No se pudo leer ratelimit.json.", ex);
        }
    }

    private void PersistRateLimit()
    {
        try
        {
            lock (_persistLock)
            {
                RateLimitState state;
                lock (_stateLock)
                {
                    state = new RateLimitState
                    {
                        Remaining = _rateRemaining, ResetUtc = _rateResetUtc, UpdatedUtc = _rateUpdatedUtc ?? UtcNow(),
                    };
                }
                FileSystemUtil.WriteAllTextAtomic(_rateLimitFile, JsonSerializer.Serialize(state, CacheJsonOptions));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo guardar ratelimit.json.", ex);
        }
    }

    // ───────────────────────────── Caché ─────────────────────────────

    private bool IsFresh(DateTimeOffset fetchedAtUtc)
    {
        var age = UtcNow() - fetchedAtUtc;
        return age >= TimeSpan.Zero && age < LauncherConfig.ApiFreshnessWindow;
    }

    private string GetCachePath(string url)
    {
        var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(_cacheDir, $"api-{hash[..16]}.json");
    }

    private CacheEntry? ReadCache(string url)
    {
        var path = GetCachePath(url);
        try
        {
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path, Encoding.UTF8), CacheJsonOptions);
            if (entry is null || !string.Equals(entry.Url, url, StringComparison.Ordinal))
            {
                Log.Warn($"Caché de GitHub no válida, se borra: {path}");
                FileSystemUtil.TryDeleteFile(path);
                return null;
            }
            return entry;
        }
        catch (JsonException ex)
        {
            Log.Warn($"Caché de GitHub corrupta, se borra: {path}", ex);
            FileSystemUtil.TryDeleteFile(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"No se pudo leer la caché de GitHub: {path}", ex);
            return null;
        }
    }

    private void WriteCache(CacheEntry entry)
    {
        try
        {
            FileSystemUtil.WriteAllTextAtomic(GetCachePath(entry.Url), JsonSerializer.Serialize(entry, CacheJsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo guardar la caché de GitHub: {entry.Url}", ex);
        }
    }

    /// <summary>304: renueva la marca de tiempo (y el ETag si llegó uno nuevo) sin tocar el cuerpo.</summary>
    private void TouchCache(CacheEntry cache, ApiResponse response, DateTimeOffset fetchedAt)
    {
        WriteCache(new CacheEntry
        {
            Url = cache.Url,
            Status = cache.Status,
            ETag = response.ETag ?? cache.ETag,
            LastModified = response.LastModified ?? cache.LastModified,
            FetchedAtUtc = fetchedAt,
            Body = cache.Body,
        });
    }

    private void DeleteCache(string url) => FileSystemUtil.TryDeleteFile(GetCachePath(url));

    private static FileMeta? ReadFileMeta(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath)) return null;
            return JsonSerializer.Deserialize<FileMeta>(File.ReadAllText(metaPath, Encoding.UTF8), CacheJsonOptions);
        }
        catch (JsonException ex)
        {
            Log.Warn($"Metadatos de caché corruptos, se borran: {metaPath}", ex);
            FileSystemUtil.TryDeleteFile(metaPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"No se pudieron leer los metadatos de caché: {metaPath}", ex);
            return null;
        }
    }

    private static void WriteFileMeta(string metaPath, FileMeta meta)
    {
        try
        {
            FileSystemUtil.WriteAllTextAtomic(metaPath, JsonSerializer.Serialize(meta, CacheJsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudieron guardar los metadatos de caché: {metaPath}", ex);
        }
    }

    // ───────────────────────────── Fusión de peticiones ─────────────────────────────

    /// <summary>
    /// Si ya hay una operación en curso con esa clave se reutiliza. La operación compartida no depende del token de
    /// ningún llamador (tiene su propio timeout): cancelar solo deja de esperar ese llamador.
    /// </summary>
    private Task<T> JoinAsync<T>(string key, Func<Task<T>> operation, CancellationToken ct)
    {
        Task<T> task;
        lock (_inflightLock)
        {
            if (_inflight.TryGetValue(key, out var existing) && existing is Task<T> running)
            {
                task = running;
            }
            else
            {
                task = Task.Run(operation);
                _inflight[key] = task;
                _ = task.ContinueWith(t =>
                {
                    _ = t.Exception; // observada aunque todos los llamadores hayan cancelado
                    lock (_inflightLock)
                    {
                        if (_inflight.TryGetValue(key, out var current) && ReferenceEquals(current, t))
                            _inflight.Remove(key);
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    // ───────────────────────────── Parseo ─────────────────────────────

    private static string StripBom(string text) => text.Length > 0 && text[0] == '﻿' ? text[1..] : text;

    private static ReleaseInfo ParseReleaseElement(JsonElement element)
    {
        var assets = new List<ReleaseAsset>();
        if (element.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assetsElement.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;

                var state = GetString(a, "state");
                if (state is not null && !string.Equals(state, "uploaded", StringComparison.OrdinalIgnoreCase)) continue;

                var size = GetLong(a, "size") ?? 0;
                if (size <= 0) continue;

                assets.Add(new ReleaseAsset
                {
                    Id = GetLong(a, "id") ?? 0,
                    Name = GetString(a, "name") ?? "",
                    Size = size,
                    DownloadUrl = GetString(a, "browser_download_url") ?? "",
                    ContentType = GetString(a, "content_type"),
                    Sha256 = ParseSha256Digest(GetString(a, "digest")),
                    UpdatedAt = GetDate(a, "updated_at"),
                });
            }
        }

        return new ReleaseInfo
        {
            Id = GetLong(element, "id") ?? 0,
            TagName = GetString(element, "tag_name") ?? "",
            Name = GetString(element, "name"),
            Body = GetString(element, "body"),
            IsDraft = GetBool(element, "draft"),
            IsPrerelease = GetBool(element, "prerelease"),
            PublishedAt = GetDate(element, "published_at"),
            HtmlUrl = GetString(element, "html_url") ?? "",
            Assets = assets.AsReadOnly(),
        };
    }

    private static string? ParseSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        const string prefix = "sha256:";
        var value = digest.Trim();
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = value[prefix.Length..].Trim();
        if (hex.Length != 64) return null;
        foreach (var c in hex)
        {
            if (!char.IsAsciiHexDigit(c)) return null;
        }
        return hex.ToLowerInvariant();
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long? GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n)) return n;
        if (p.ValueKind == JsonValueKind.String
            && long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
        return null;
    }

    private static bool GetBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? GetDate(JsonElement element, string name)
    {
        var text = GetString(element, name);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value) ? value : null;
    }

    // ───────────────────────────── Tipos internos ─────────────────────────────

    private enum ApiResponseKind { Ok, NotModified, NotFound }

    private sealed record ApiResponse(ApiResponseKind Kind, string? Body, string? ETag, string? LastModified);

    private sealed class RawResponse
    {
        public int Status { get; init; }
        public string? ETag { get; init; }
        public string? LastModified { get; init; }
        public string? ContentType { get; init; }
        public byte[]? Body { get; set; }

        /// <summary>No null si la respuesta es un rechazo por límite de consultas.</summary>
        public DateTimeOffset? RateLimitResetUtc { get; set; }
    }

    private sealed class CacheEntry
    {
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("etag")] public string? ETag { get; set; }
        [JsonPropertyName("lastModified")] public string? LastModified { get; set; }
        [JsonPropertyName("fetchedAtUtc")] public DateTimeOffset FetchedAtUtc { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
    }

    private sealed class FileMeta
    {
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("etag")] public string? ETag { get; set; }
        [JsonPropertyName("lastModified")] public string? LastModified { get; set; }
        [JsonPropertyName("fetchedAtUtc")] public DateTimeOffset FetchedAtUtc { get; set; }
    }

    private sealed class RateLimitState
    {
        [JsonPropertyName("remaining")] public int? Remaining { get; set; }
        [JsonPropertyName("resetUtc")] public DateTimeOffset? ResetUtc { get; set; }
        [JsonPropertyName("updatedUtc")] public DateTimeOffset UpdatedUtc { get; set; }
    }

    private enum FailureKind { Network, Timeout, RateLimit, Rejected, Server, InvalidResponse, NotFound, Unexpected }

    /// <summary>Fallo esperado al consultar GitHub: mensaje sin caché (<see cref="Exception.Message"/>) y aviso con caché.</summary>
    private sealed class FetchFailure : Exception
    {
        private const string Saved = " Mostrando datos guardados.";

        private FetchFailure(FailureKind kind, string message, string cacheWarning, Exception? inner = null, DateTimeOffset? resetUtc = null)
            : base(message, inner)
        {
            Kind = kind;
            CacheWarning = cacheWarning;
            ResetUtc = resetUtc;
        }

        public FailureKind Kind { get; }
        public string CacheWarning { get; }
        public DateTimeOffset? ResetUtc { get; }

        public static FetchFailure Network(Exception ex)
        {
            var error = (ex as HttpRequestException)?.HttpRequestError ?? HttpRequestError.Unknown;
            return error switch
            {
                HttpRequestError.NameResolutionError => new FetchFailure(FailureKind.Network,
                    "Sin conexión a Internet (no se encuentra github.com).",
                    "Sin conexión a Internet." + Saved, ex),
                HttpRequestError.SecureConnectionError => new FetchFailure(FailureKind.Network,
                    "No se pudo establecer una conexión segura con GitHub. Revisa la fecha y hora de Windows, el antivirus " +
                    "o si la red wifi requiere iniciar sesión.",
                    "No se pudo establecer una conexión segura con GitHub." + Saved, ex),
                _ => new FetchFailure(FailureKind.Network, "No se pudo conectar con GitHub.", "Sin conexión con GitHub." + Saved, ex),
            };
        }

        public static FetchFailure Timeout(Exception ex) => new(FailureKind.Timeout,
            "No se pudo conectar con GitHub (tardó demasiado en responder).",
            "GitHub tarda demasiado en responder." + Saved, ex);

        public static FetchFailure RateLimit(DateTimeOffset resetUtc)
        {
            string time;
            try
            {
                time = resetUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Fecha extrema que no cabe en hora local: se muestra en UTC.
                time = resetUtc.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";
            }
            return new FetchFailure(FailureKind.RateLimit,
                $"Límite de consultas a GitHub alcanzado. Se restablece a las {time}; vuelve a intentarlo entonces.",
                $"Límite de consultas a GitHub alcanzado; se restablece a las {time}." + Saved, null, resetUtc);
        }

        public static FetchFailure Rejected(int status) => new(FailureKind.Rejected,
            $"GitHub rechazó la consulta (código {status}). Vuelve a intentarlo más tarde.",
            "GitHub rechazó la consulta." + Saved);

        public static FetchFailure Server(int status) => new(FailureKind.Server,
            $"GitHub no responde ahora mismo (código {status}). Vuelve a intentarlo en unos minutos.",
            "GitHub no responde ahora mismo." + Saved);

        public static FetchFailure InvalidResponse(Exception? ex) => new(FailureKind.InvalidResponse,
            "No se pudo conectar con GitHub: la respuesta no es válida. Si la red wifi requiere iniciar sesión, hazlo y " +
            "vuelve a intentarlo.",
            "Sin conexión con GitHub (respuesta no válida)." + Saved, ex);

        public static FetchFailure NotFound(string repo)
        {
            var message = $"No se encontró el repositorio {repo} en GitHub (¿es privado o cambió de nombre?).";
            return new FetchFailure(FailureKind.NotFound, message, message + Saved);
        }

        public static FetchFailure Unexpected(Exception ex) => new(FailureKind.Unexpected,
            "No se pudo consultar GitHub. Vuelve a intentarlo.",
            "No se pudo consultar GitHub." + Saved, ex);
    }
}
