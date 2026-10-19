using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Net;
using PalitoLauncher.Core.Util;

namespace PalitoLauncher.Core.Feedback;

public enum BugCategory
{
    /// <summary>El juego se cerró o se colgó.</summary>
    Crash,

    /// <summary>Algo no funciona como debería.</summary>
    Bug,

    /// <summary>Conexión, lag, desconexiones.</summary>
    Network,

    /// <summary>Va lento, tirones.</summary>
    Performance,

    /// <summary>Gráficos, textos o interfaz.</summary>
    Visual,

    /// <summary>Problemas del propio launcher (instalar, actualizar…).</summary>
    Launcher,

    Other,
}

/// <summary>Un reporte de problema listo para enviar (se guarda tal cual en la bandeja de salida).</summary>
public sealed class BugReport
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public BugCategory Category { get; set; } = BugCategory.Bug;

    /// <summary>Qué pasó (10..2000 caracteres).</summary>
    public string Description { get; set; } = "";

    /// <summary>Cómo reproducirlo (opcional, máx. 1000).</summary>
    public string? Steps { get; set; }

    public string? Nickname { get; set; }
    public string? GameVersion { get; set; }
    public string? GameVersionDisplay { get; set; }
    public string LauncherVersion { get; set; } = "";
    public string? OsDescription { get; set; }

    /// <summary>Código de salida del juego si el reporte viene de un cierre inesperado.</summary>
    public int? ExitCode { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Nombres de los adjuntos ya copiados (y anonimizados) en la carpeta del reporte en la bandeja.</summary>
    public List<string> AttachmentNames { get; set; } = new();
}

/// <summary>Archivos que el usuario aceptó adjuntar (rutas de origen; cualquiera puede ser null o no existir).</summary>
public sealed class BugReportSources
{
    public string? PlayerLog { get; init; }
    public string? PlayerPrevLog { get; init; }
    public string? LauncherLog { get; init; }

    /// <summary>Pares clave/valor para summary.txt (estado del launcher, versiones, espacio…). Opcional.</summary>
    public IReadOnlyDictionary<string, string?>? Summary { get; init; }
}

/// <summary>
/// «Reportar un problema»: envía el reporte a un webhook de Discord (canal #bugs) con los registros adjuntos, usando una
/// bandeja de salida en disco para que nunca se pierda (misma filosofía que FeedbackService).
///
/// Bandeja: &lt;outboxDir&gt;\&lt;yyyyMMddHHmmss&gt;-&lt;id&gt;\ con report.json (BugReport, escritura atómica) y los adjuntos. Se crea
/// SIEMPRE antes de intentar enviar y se borra solo cuando Discord confirma (2xx).
///
/// Adjuntos (solo si se pasan fuentes): de cada log se copian como mucho los últimos 2 MB (empezando en un salto de línea),
/// leídos con FileShare.ReadWrite | FileShare.Delete, ANONIMIZADOS con <see cref="Redact"/>, como "Player.log",
/// "Player-prev.log" y "launcher.log"; más "summary.txt" (datos del reporte + Summary). Total ≤ 7 MB (si se pasa, se
/// recortan los logs más grandes). Los que no existan o no se puedan leer se anotan en summary.txt.
///
/// Envío: POST {webhook}?wait=true multipart/form-data con "payload_json" ({ username: "Coop2D · Reportes", embeds: [embed],
/// allowed_mentions: { parse: [] }, attachments: [...] }) y "files[i]". Embed: título «🐞 &lt;categoría en español&gt; ·
/// Coop2D 0.0.3 · Prototipo 2», descripción = Description (≤ 4096), campos «Cómo reproducirlo», «Jugador», «Partida» (si hay
/// ExitCode: «código X»), footer «Launcher 1.0.5 · Windows …», timestamp; color por categoría (Crash rojo, Network ámbar,
/// resto azul). Respuestas: 2xx enviado; 413 → reintenta UNA vez sin los logs (solo summary.txt) y lo indica en el embed; 429
/// → respeta retry_after y deja en la bandeja; 400 → mueve a &lt;outboxDir&gt;\failed\; 401/403/404, 5xx, red → bandeja.
/// Thread-safe (un envío a la vez).
/// </summary>
public sealed class BugReportService
{
    internal const string FailedFolderName = "failed";
    internal const string ReportFileName = "report.json";
    internal const string PlayerLogName = "Player.log";
    internal const string PlayerPrevLogName = "Player-prev.log";
    internal const string LauncherLogName = "launcher.log";
    internal const string SummaryFileName = "summary.txt";
    internal const string Username = LauncherConfig.GameDisplayName + " · Reportes";

    /// <summary>Prefijo de las carpetas en preparación (no encajan con el formato de la bandeja: nunca se envían a medias).</summary>
    internal const string StagingPrefix = ".tmp-";

    internal const int MinDescriptionChars = 10;
    internal const int MaxDescriptionChars = 2000;
    internal const int MaxStepsChars = 1000;
    private const int MaxNicknameChars = 100;

    // Límites de Discord para embeds y adjuntos.
    internal const int MaxEmbedTitleChars = 256;
    internal const int MaxEmbedDescriptionChars = 4096;
    internal const int MaxEmbedTotalChars = 6000;
    private const int MaxFieldValueChars = 1024;
    private const int MaxFooterChars = 256;
    internal const int MaxDiscordAttachments = 10;

    // Colores del embed (paleta del launcher).
    internal const int ColorCrash = 0xFF5C6C;
    internal const int ColorNetwork = 0xFFB627;
    internal const int ColorDefault = 0x5AA9FF;

    internal const int DefaultMaxLogTailBytes = 2 * 1024 * 1024;
    internal const int DefaultMaxTotalAttachmentBytes = 7 * 1024 * 1024;

    private const int MaxReportFileBytes = 256 * 1024;
    private const int MaxResponseBodyBytes = 64 * 1024;
    private const int MaxSummaryEntries = 100;
    private const int MaxSummaryKeyChars = 80;
    private const int MaxSummaryValueChars = 500;

    /// <summary>Margen para las notas que se añaden a summary.txt al recortar los logs por el límite total.</summary>
    private const int SummaryNotesSlackBytes = 1024;

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>Si Discord pide esperar menos que esto, se espera en el momento en lugar de dejarlo para más tarde.</summary>
    private static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly Regex OutboxFolderRegex = new(@"^(?<stamp>[0-9]{14})-(?<id>[A-Za-z0-9_-]{1,64})$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SafeIdRegex = new(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>Nombre de adjunto aceptable al leer report.json (nada de rutas: evita leer archivos fuera de la carpeta).</summary>
    private static readonly Regex SafeAttachmentNameRegex = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    internal static readonly JsonSerializerOptions OutboxJsonOptions = new()
    {
        // report.json legible si se abre a mano (tildes y ñ sin escapar; < > & siguen escapados).
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly HttpClient _http;
    private readonly string _outboxDir;
    private readonly string _failedDir;
    private readonly bool _addUserAgent;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly HashSet<string> _sentIds = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastReportUtc;
    private string? _lastReportId;
    private DateTimeOffset? _blockedUntilUtc;

    public BugReportService(HttpClient http, string outboxDir)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxDir);
        _http = http;
        _outboxDir = Path.GetFullPath(outboxDir);
        _failedDir = Path.Combine(_outboxDir, FailedFolderName);
        // Se mira una sola vez: leer las cabeceras por defecto desde varios hilos no es seguro.
        _addUserAgent = !http.DefaultRequestHeaders.Contains("User-Agent");
    }

    /// <summary>Tiempo mínimo entre dos reportes del usuario. Por defecto 60 s.</summary>
    public TimeSpan MinIntervalBetweenReports { get; set; } = TimeSpan.FromSeconds(60);

    internal Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;

    /// <summary>Timeout de cada POST a Discord (sube hasta 7 MB: con una conexión lenta tarda). Inyectable en tests.</summary>
    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Máximo que se copia de cada log (su final). Inyectable en tests.</summary>
    internal int MaxLogTailBytes { get; set; } = DefaultMaxLogTailBytes;

    /// <summary>Máximo de todos los adjuntos juntos (Discord admite 10 MB por mensaje sin mejoras del servidor). Inyectable en tests.</summary>
    internal int MaxTotalAttachmentBytes { get; set; } = DefaultMaxTotalAttachmentBytes;

    // Datos que se ocultan en los adjuntos (inyectables en tests).
    internal string? RedactUserName { get; set; } = SafeEnvironment(() => Environment.UserName);
    internal string? RedactUserProfile { get; set; } = SafeEnvironment(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    internal string? RedactMachineName { get; set; } = SafeEnvironment(() => Environment.MachineName);

    /// <summary>Carpeta de reportes que Discord rechazó o que estaban dañados.</summary>
    internal string FailedDir => _failedDir;

    /// <summary>Reportes pendientes en la bandeja (no cuenta failed\). Nunca lanza.</summary>
    public int PendingCount
    {
        get
        {
            try
            {
                return ListOutboxFolders().Count(path => !WasSent(GetIdFromFolderName(path)));
            }
            catch (Exception ex)
            {
                Log.Warn("No se pudo contar los reportes pendientes de enviar.", ex);
                return 0;
            }
        }
    }

    /// <summary>
    /// Guarda el reporte (y copia anonimizada de <paramref name="sources"/> si no es null) en la bandeja e intenta enviarlo.
    /// Webhook null/ inválido → Queued sin red. Otro reporte hace menos de MinIntervalBetweenReports →
    /// LauncherException("Espera un momento antes de enviar otro reporte."). Descripción con menos de 10 caracteres →
    /// LauncherException("Cuéntanos un poco más qué ha pasado."). Error de disco al guardar → LauncherException. Fallos de
    /// red/Discord → Queued (no lanza). OperationCanceledException si se canceló (el reporte queda en la bandeja).
    /// </summary>
    public Task<FeedbackSendOutcome> SubmitAsync(BugReport report, BugReportSources? sources, string? discordWebhookUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        // En el pool: copiar y anonimizar varios MB de logs no debe tocar el hilo de la UI.
        return Task.Run(() => SubmitCoreAsync(report, sources, discordWebhookUrl, ct));
    }

    /// <summary>Reintenta lo pendiente (más viejo primero, máx. <paramref name="maxItems"/>); para al primer 429/error de red. Devuelve enviados. Nunca lanza (salvo cancelación).</summary>
    public Task<int> FlushOutboxAsync(string? discordWebhookUrl, CancellationToken ct, int maxItems = 5)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<int>(ct);
        return Task.Run(() => FlushCoreAsync(discordWebhookUrl, ct, maxItems));
    }

    /// <summary>
    /// Anonimiza texto de logs: la ruta del perfil (<paramref name="userProfile"/>, con \ o /, sin distinguir mayúsculas)
    /// → "%USERPROFILE%"; el nombre de usuario (<paramref name="userName"/>, si tiene ≥ 3 caracteres, como palabra completa)
    /// → "&lt;usuario&gt;"; el nombre del equipo (<paramref name="machineName"/>, ≥ 3) → "&lt;equipo&gt;". Null/vacíos no se tocan. No lanza.
    /// </summary>
    internal static string Redact(string text, string? userName, string? userProfile, string? machineName)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        var profileSegments = GetProfileSegments(userProfile);
        var user = CleanRedactWord(userName);
        var machine = CleanRedactWord(machineName);
        if (profileSegments is null && user is null && machine is null) return text;

        try
        {
            // Una sola pasada con alternativas: lo ya sustituido no se vuelve a procesar (p.ej. un usuario llamado "equipo").
            // La ruta del perfil va primero para que gane a su propio nombre de usuario.
            var alternatives = new List<string>(3);
            if (profileSegments is not null)
            {
                // Separadores \ o / (también \\ de rutas escapadas en JSON); no debe seguir un carácter de nombre
                // ("C:\Users\Ana" no toca "C:\Users\Anabel").
                alternatives.Add("(?<p>" + string.Join(@"[\\/]+", profileSegments.Select(Regex.Escape)) + @")(?![\p{L}\p{N}_\-])");
            }
            if (user is not null) alternatives.Add(@"(?<u>(?<![\p{L}\p{N}_])" + Regex.Escape(user) + @"(?![\p{L}\p{N}_]))");
            if (machine is not null) alternatives.Add(@"(?<m>(?<![\p{L}\p{N}_])" + Regex.Escape(machine) + @"(?![\p{L}\p{N}_]))");

            var regex = new Regex(string.Join("|", alternatives),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            return regex.Replace(text, static match =>
                match.Groups["p"].Success ? "%USERPROFILE%" : match.Groups["u"].Success ? "<usuario>" : "<equipo>");
        }
        catch (Exception ex)
        {
            // Nunca se devuelve el texto sin anonimizar: plan B sin límites de palabra.
            Log.Warn("No se pudo anonimizar un registro con la expresión regular; se usa el reemplazo simple.", ex);
            return RedactSimple(text, user, profileSegments, machine);
        }
    }

    /// <summary>Nombre en español de la categoría ("Cierre del juego", "Fallo", "Red / conexión", "Rendimiento", "Gráficos o interfaz", "Launcher", "Otro").</summary>
    public static string CategoryDisplayName(BugCategory category) => category switch
    {
        BugCategory.Crash => "Cierre del juego",
        BugCategory.Bug => "Fallo",
        BugCategory.Network => "Red / conexión",
        BugCategory.Performance => "Rendimiento",
        BugCategory.Visual => "Gráficos o interfaz",
        BugCategory.Launcher => "Launcher",
        _ => "Otro",
    };

    /// <summary>
    /// Construye el JSON "payload_json" del mensaje de Discord (expuesto para tests; no hace E/S).
    /// <paramref name="attachmentNames"/> son los archivos que van en files[0..n] y <paramref name="logsOmitted"/> indica
    /// el reintento sin logs tras un 413.
    /// </summary>
    internal static string BuildDiscordPayload(BugReport report, IReadOnlyList<string> attachmentNames, bool logsOmitted)
    {
        ArgumentNullException.ThrowIfNull(report);
        attachmentNames ??= Array.Empty<string>();

        var category = Enum.IsDefined(report.Category) ? report.Category : BugCategory.Other;
        var version = FeedbackText.Clean(report.GameVersionDisplay, 120, multiline: false)
                      ?? FeedbackText.Clean(report.GameVersion, 120, multiline: false);
        var title = FeedbackText.Truncate(version is null
            ? $"🐞 {CategoryDisplayName(category)} · {LauncherConfig.GameDisplayName} · versión desconocida"
            : $"🐞 {CategoryDisplayName(category)} · {LauncherConfig.GameDisplayName} {version}", MaxEmbedTitleChars);

        var fields = new List<(string Name, string Value, bool Inline)>(4);
        var steps = FeedbackText.Clean(report.Steps, MaxFieldValueChars, multiline: true);
        if (steps is not null) fields.Add(("Cómo reproducirlo", steps, false));
        fields.Add(("Jugador", FeedbackText.Clean(report.Nickname, MaxNicknameChars, multiline: false) ?? "Anónimo", true));
        if (report.ExitCode is { } exitCode) fields.Add(("Partida", FormatExitCode(exitCode), true));
        fields.Add(("Registros", DescribeAttachments(attachmentNames, logsOmitted), false));

        var footer = FeedbackText.Truncate(BuildFooter(report), MaxFooterChars);

        // Límite total de Discord (título + descripción + campos + footer): lo que sobre se recorta de la descripción.
        var used = title.Length + footer.Length + fields.Sum(f => f.Name.Length + f.Value.Length);
        var description = FeedbackText.Clean(report.Description, MaxEmbedDescriptionChars, multiline: true) ?? "—";
        description = FeedbackText.Truncate(description, Math.Max(1, MaxEmbedTotalChars - used));

        var color = category switch
        {
            BugCategory.Crash => ColorCrash,
            BugCategory.Network => ColorNetwork,
            _ => ColorDefault,
        };

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("username", Username);
            writer.WriteStartObject("allowed_mentions");
            writer.WriteStartArray("parse");
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartArray("embeds");
            writer.WriteStartObject();
            writer.WriteString("title", title);
            writer.WriteString("description", description);
            writer.WriteNumber("color", color);
            writer.WriteStartArray("fields");
            foreach (var field in fields)
            {
                writer.WriteStartObject();
                writer.WriteString("name", field.Name);
                writer.WriteString("value", field.Value);
                writer.WriteBoolean("inline", field.Inline);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartObject("footer");
            writer.WriteString("text", footer);
            writer.WriteEndObject();
            // Fecha de cuando se escribió (aunque llegue más tarde desde la bandeja). Fechas absurdas se omiten.
            var created = report.CreatedAtUtc.UtcDateTime;
            if (created.Year is >= 2015 and <= 9000)
                writer.WriteString("timestamp", created.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
            writer.WriteEndArray();

            // Cada adjunto files[i] se declara con su id = i.
            writer.WriteStartArray("attachments");
            for (var i = 0; i < attachmentNames.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", i);
                writer.WriteString("filename", attachmentNames[i]);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // ───────────────────────────── Envío ─────────────────────────────

    private async Task<FeedbackSendOutcome> SubmitCoreAsync(BugReport report, BugReportSources? sources, string? discordWebhookUrl,
        CancellationToken ct)
    {
        // Validación antes que nada: un texto demasiado corto no gasta el anti-spam ni se guarda.
        var description = FeedbackText.Clean(report.Description, MaxDescriptionChars, multiline: true);
        if (description is null || description.Length < MinDescriptionChars)
            throw new LauncherException("Cuéntanos un poco más qué ha pasado.");
        NormalizeReport(report, description);

        var id = EnsureSafeId(report);
        if (WasSent(id))
        {
            Log.Info($"El reporte {id} ya se había enviado; no se repite.");
            return FeedbackSendOutcome.Sent;
        }

        // Anti doble clic / spam (reintentar el mismo reporte no cuenta: sustituye su carpeta).
        var now = UtcNow();
        DateTimeOffset? previousUtc;
        string? previousId;
        lock (_stateLock)
        {
            previousUtc = _lastReportUtc;
            previousId = _lastReportId;
            if (previousUtc is { } last && !string.Equals(previousId, id, StringComparison.OrdinalIgnoreCase))
            {
                var elapsed = now - last;
                if (elapsed >= TimeSpan.Zero && elapsed < MinIntervalBetweenReports)
                    throw new LauncherException("Espera un momento antes de enviar otro reporte.");
            }
            _lastReportUtc = now;
            _lastReportId = id;
        }

        string folder;
        try
        {
            folder = SaveToOutbox(report, id, sources);
        }
        catch
        {
            // No se guardó: el usuario puede reintentar enseguida.
            lock (_stateLock)
            {
                if (_lastReportUtc == now && string.Equals(_lastReportId, id, StringComparison.OrdinalIgnoreCase))
                {
                    _lastReportUtc = previousUtc;
                    _lastReportId = previousId;
                }
            }
            throw;
        }

        ct.ThrowIfCancellationRequested();

        var postUrl = BuildPostUrl(discordWebhookUrl);
        if (postUrl is null)
        {
            Log.Info($"Reporte {id} guardado en la bandeja de salida: no hay webhook de Discord válido (se enviará cuando lo haya).");
            return FeedbackSendOutcome.Queued;
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Mientras esperaba, otro envío (FlushOutboxAsync) pudo mandarlo o moverlo a failed\.
            if (!Directory.Exists(folder))
                return WasSent(id) ? FeedbackSendOutcome.Sent : FeedbackSendOutcome.Queued;
            if (!await WaitForRateLimitAsync(ct).ConfigureAwait(false))
                return FeedbackSendOutcome.Queued;

            var result = await SendFolderAsync(folder, report, id, postUrl, ct).ConfigureAwait(false);
            return result == SendResult.Sent ? FeedbackSendOutcome.Sent : FeedbackSendOutcome.Queued;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<int> FlushCoreAsync(string? discordWebhookUrl, CancellationToken ct, int maxItems)
    {
        var sent = 0;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (maxItems <= 0) return 0;

            var postUrl = BuildPostUrl(discordWebhookUrl);
            if (postUrl is null)
            {
                var pending = PendingCount;
                if (pending > 0)
                    Log.Info($"Hay {pending} reportes pendientes, pero no hay webhook de Discord válido; siguen en la bandeja.");
                return 0;
            }

            CleanupStaleStaging(TimeSpan.FromDays(1));

            var processed = 0;
            foreach (var folder in ListOutboxFolders())
            {
                if (processed >= maxItems) break;
                ct.ThrowIfCancellationRequested();

                await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // SubmitAsync pudo enviarlo mientras tanto.
                    if (!Directory.Exists(folder)) continue;
                    processed++;

                    var nameId = GetIdFromFolderName(folder);
                    if (WasSent(nameId))
                    {
                        FileSystemUtil.TryDeleteDirectory(folder, attempts: 2, delayMs: 100); // ya enviado; quedó porque no se pudo borrar
                        continue;
                    }

                    var report = ReadReportFile(folder, out var corrupt);
                    if (report is null)
                    {
                        if (corrupt) MoveToFailed(folder, "reporte dañado");
                        continue;
                    }
                    var id = SafeIdRegex.IsMatch(report.Id ?? "") ? report.Id! : nameId;
                    if (WasSent(id))
                    {
                        FileSystemUtil.TryDeleteDirectory(folder, attempts: 2, delayMs: 100);
                        continue;
                    }

                    if (!await WaitForRateLimitAsync(ct).ConfigureAwait(false)) break;

                    var result = await SendFolderAsync(folder, report, id, postUrl, ct).ConfigureAwait(false);
                    if (result == SendResult.Sent) sent++;
                    else if (result != SendResult.Rejected) break; // 429, webhook no válido, 5xx o red: se para
                }
                finally
                {
                    _sendLock.Release();
                }
            }

            if (sent > 0) Log.Info($"Bandeja de reportes: {sent} reportes pendientes enviados a Discord.");
            return sent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("Error inesperado al vaciar la bandeja de reportes.", ex);
            return sent;
        }
    }

    private enum SendResult { Sent, RateLimited, Rejected, WebhookInvalid, ServerError, NetworkError }

    /// <summary>
    /// Envía la carpeta de un reporte (con <see cref="_sendLock"/> tomado): 2xx → se borra; 413 con logs → un reintento solo
    /// con summary.txt; 400/413/422 → failed\. Solo lanza OperationCanceledException de <paramref name="ct"/>.
    /// </summary>
    private async Task<SendResult> SendFolderAsync(string folder, BugReport report, string id, string postUrl, CancellationToken ct)
    {
        var files = LoadAttachments(folder, report, id);

        var (result, status) = await PostAsync(report, files, logsOmitted: false, id, postUrl, ct).ConfigureAwait(false);
        if (status == 413 && files.Any(f => !IsSummary(f.Name)))
        {
            Log.Warn($"Discord rechazó el reporte {id} por tamaño (413); se reintenta una vez solo con el resumen, sin los registros.");
            var summaryOnly = files.Where(f => IsSummary(f.Name)).ToList();
            (result, status) = await PostAsync(report, summaryOnly, logsOmitted: true, id, postUrl, ct).ConfigureAwait(false);
        }

        switch (result)
        {
            case SendResult.Sent:
                MarkSent(id);
                if (!FileSystemUtil.TryDeleteDirectory(folder, attempts: 3, delayMs: 150))
                    Log.Warn($"Reporte {id} enviado, pero no se pudo borrar de la bandeja (no se volverá a enviar en esta sesión).");
                Log.Info($"Reporte {id} enviado a Discord ({status}).");
                break;

            case SendResult.Rejected:
                MoveToFailed(folder, status is null ? "no se pudo preparar el mensaje" : $"Discord lo rechazó (código {status})");
                break;
        }
        return result;
    }

    /// <summary>Un POST multipart a Discord. Devuelve el resultado y el código HTTP (null si no hubo respuesta).</summary>
    private async Task<(SendResult Result, int? Status)> PostAsync(BugReport report, IReadOnlyList<Attachment> files, bool logsOmitted,
        string id, string postUrl, CancellationToken ct)
    {
        string payload;
        try
        {
            payload = BuildDiscordPayload(report, files.Select(f => f.Name).ToList(), logsOmitted);
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo preparar el mensaje de Discord del reporte {id}.", ex);
            return (SendResult.Rejected, null);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, postUrl) { Content = BuildMultipart(payload, files) };
        if (_addUserAgent) request.Headers.TryAddWithoutValidation("User-Agent", HttpFactory.UserAgent);

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (status is >= 200 and <= 299)
            {
                NotePreemptiveRateLimit(response);
                return (SendResult.Sent, status);
            }

            var body = await ReadBodySafeAsync(response.Content, timeoutCts.Token).ConfigureAwait(false);

            if (status == 429)
            {
                var wait = GetRetryAfter(response, body);
                lock (_stateLock) _blockedUntilUtc = UtcNow() + wait;
                Log.Warn($"Discord: demasiados mensajes (429); el reporte {id} se queda en la bandeja y se reintentará dentro de " +
                         $"{wait.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s.");
                return (SendResult.RateLimited, status);
            }

            if (status is 400 or 413 or 422)
            {
                Log.Error($"Discord rechazó el reporte {id} (código {status}): {FeedbackText.Truncate(body, 2000)}");
                return (SendResult.Rejected, status);
            }

            if (status is 401 or 403 or 404)
            {
                Log.Warn($"El webhook de Discord no es válido o se borró (código {status}); el reporte {id} se queda en la bandeja " +
                         "hasta que haya uno válido.");
                return (SendResult.WebhookInvalid, status);
            }

            Log.Warn($"Discord respondió con el código {status}; el reporte {id} se queda en la bandeja.");
            return (status >= 500 ? SendResult.ServerError : SendResult.WebhookInvalid, status);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            Log.Warn($"Discord tardó demasiado en responder; el reporte {id} se queda en la bandeja.", ex);
            return (SendResult.NetworkError, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Log.Warn($"No se pudo conectar con Discord; el reporte {id} se queda en la bandeja.", ex);
            return (SendResult.NetworkError, null);
        }
        catch (Exception ex)
        {
            Log.Error($"Error inesperado al enviar el reporte {id} a Discord; se queda en la bandeja.", ex);
            return (SendResult.NetworkError, null);
        }
    }

    /// <summary>multipart/form-data: "payload_json" + "files[i]" (texto UTF-8, así Discord los previsualiza).</summary>
    private static MultipartFormDataContent BuildMultipart(string payload, IReadOnlyList<Attachment> files)
    {
        var content = new MultipartFormDataContent();

        var json = new StringContent(payload, Utf8NoBom, "application/json");
        json.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"payload_json\"" };
        content.Add(json);

        for (var i = 0; i < files.Count; i++)
        {
            var part = new ByteArrayContent(files[i].Bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
            part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = $"\"files[{i.ToString(CultureInfo.InvariantCulture)}]\"",
                FileName = $"\"{files[i].Name}\"",
            };
            content.Add(part);
        }
        return content;
    }

    /// <summary>Respeta una espera pedida por Discord: si es corta, espera; si no, false (se deja para más tarde).</summary>
    private async Task<bool> WaitForRateLimitAsync(CancellationToken ct)
    {
        DateTimeOffset? until;
        lock (_stateLock) until = _blockedUntilUtc;
        if (until is null) return true;

        var wait = until.Value - UtcNow();
        if (wait <= TimeSpan.Zero) return true;
        if (wait > MaxInlineWait)
        {
            Log.Info($"Discord pidió esperar; los reportes se reintentarán más tarde (quedan {wait.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s).");
            return false;
        }

        await Task.Delay(wait, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>2xx con X-RateLimit-Remaining 0: el siguiente envío espera X-RateLimit-Reset-After para no provocar un 429.</summary>
    private void NotePreemptiveRateLimit(HttpResponseMessage response)
    {
        if (GetHeader(response, "X-RateLimit-Remaining") != "0") return;
        if (!TryParseSeconds(GetHeader(response, "X-RateLimit-Reset-After"), out var resetAfter)) return;
        var wait = Clamp(resetAfter, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        lock (_stateLock) _blockedUntilUtc = UtcNow() + wait;
    }

    /// <summary>Espera de un 429: retry_after del cuerpo JSON, si no Retry-After, si no X-RateLimit-Reset-After (1 s..1 h).</summary>
    private TimeSpan GetRetryAfter(HttpResponseMessage response, string body)
    {
        TimeSpan? wait = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("retry_after", out var retry) && retry.ValueKind == JsonValueKind.Number
                    && retry.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds >= 0)
                {
                    wait = TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryAfter.TotalSeconds));
                }
            }
        }
        catch (JsonException)
        {
            // Cuerpo no JSON (p.ej. página de Cloudflare): se usan las cabeceras.
        }

        if (wait is null && response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta) wait = delta;
            else if (retryAfter.Date is { } date) wait = date - (response.Headers.Date ?? UtcNow());
        }
        if (wait is null && TryParseSeconds(GetHeader(response, "X-RateLimit-Reset-After"), out var resetAfter))
            wait = resetAfter;

        return Clamp(wait ?? DefaultRetryAfter, TimeSpan.FromSeconds(1), MaxRetryAfter);
    }

    private static string? BuildPostUrl(string? discordWebhookUrl)
    {
        var normalized = SurveyProvider.NormalizeDiscordWebhook(discordWebhookUrl);
        if (normalized is null) return null;
        return normalized + (normalized.Contains('?') ? "&" : "?") + "wait=true";
    }

    private static async Task<string> ReadBodySafeAsync(HttpContent content, CancellationToken ct)
    {
        try
        {
            var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[MaxResponseBodyBytes];
                var total = 0;
                int read;
                while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0)
                    total += read;
                return Encoding.UTF8.GetString(buffer, 0, total);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return "";
        }
    }

    private static string? GetHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.NonValidated.TryGetValues(name, out var values)) return null;
        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrEmpty(trimmed)) return trimmed;
        }
        return null;
    }

    private static bool TryParseSeconds(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds) || seconds < 0)
            return false;
        value = TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryAfter.TotalSeconds));
        return true;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
        => value < min ? min : value > max ? max : value;

    // ───────────────────────────── Bandeja ─────────────────────────────

    /// <summary>
    /// Prepara la carpeta del reporte en una carpeta temporal (adjuntos + report.json) y la pone en su sitio al final, así
    /// nunca hay en la bandeja un reporte a medias. Si ya estaba en la bandeja (mismo Id) se sustituye su contenido.
    /// Errores de disco → LauncherException.
    /// </summary>
    private string SaveToOutbox(BugReport report, string id, BugReportSources? sources)
    {
        string? staging = null;
        string? folder = null;
        try
        {
            Directory.CreateDirectory(_outboxDir);
            staging = Path.Combine(_outboxDir, $"{StagingPrefix}{id}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");
            Directory.CreateDirectory(staging);

            var names = new List<string>();
            if (sources is not null)
            {
                foreach (var attachment in BuildAttachments(report, sources))
                {
                    File.WriteAllBytes(Path.Combine(staging, attachment.Name), attachment.Bytes);
                    names.Add(attachment.Name);
                }
            }
            report.AttachmentNames = names;

            string json;
            try
            {
                json = JsonSerializer.Serialize(report, OutboxJsonOptions);
            }
            catch (Exception ex) when (ex is not IOException)
            {
                Log.Error($"No se pudo preparar el reporte {id} para guardarlo.", ex);
                throw new LauncherException("No se pudo preparar tu reporte para enviarlo. Vuelve a intentarlo.", ex);
            }
            FileSystemUtil.WriteAllTextAtomic(Path.Combine(staging, ReportFileName), json);

            folder = FindOutboxFolder(id) ?? Path.Combine(_outboxDir,
                $"{report.CreatedAtUtc.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}-{id}");
            PromoteStaging(staging, folder);
            staging = null;
            return folder;
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (IOException ex) when (FileSystemUtil.IsDiskFull(ex))
        {
            Log.Warn($"Disco lleno al guardar el reporte {id} en {folder ?? _outboxDir}.", ex);
            throw new LauncherException("No hay espacio libre en el disco para guardar el reporte. Libera algo de espacio y vuelve a intentarlo.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            Log.Warn($"No se pudo guardar el reporte {id} en {folder ?? _outboxDir}.", ex);
            throw new LauncherException("No se pudo guardar el reporte en el disco. Vuelve a intentarlo; si se repite, reinicia el launcher.", ex);
        }
        finally
        {
            if (staging is not null) FileSystemUtil.TryDeleteDirectory(staging, attempts: 2, delayMs: 100);
        }
    }

    /// <summary>Mueve la carpeta preparada a su sitio (renombrado atómico); si ya existe, sustituye sus archivos (report.json el último).</summary>
    private static void PromoteStaging(string staging, string folder)
    {
        for (var attempt = 1; !Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Move(staging, folder);
                return;
            }
            catch (IOException) when (Directory.Exists(folder))
            {
                break; // apareció a la vez: se sustituye el contenido
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 3)
            {
                Thread.Sleep(150); // antivirus revisando los archivos recién escritos
            }
        }

        // Mismo reporte reenviado (o carpeta que apareció a la vez): se sustituye el contenido.
        foreach (var old in Directory.GetFiles(folder))
        {
            if (!File.Exists(Path.Combine(staging, Path.GetFileName(old)))) FileSystemUtil.TryDeleteFile(old);
        }
        foreach (var file in Directory.GetFiles(staging))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, ReportFileName, StringComparison.OrdinalIgnoreCase)) continue;
            File.Move(file, Path.Combine(folder, name), overwrite: true);
        }
        File.Move(Path.Combine(staging, ReportFileName), Path.Combine(folder, ReportFileName), overwrite: true);
        FileSystemUtil.TryDeleteDirectory(staging, attempts: 2, delayMs: 100);
    }

    /// <summary>Carpeta de la bandeja con ese Id (o null).</summary>
    private string? FindOutboxFolder(string id)
    {
        if (!Directory.Exists(_outboxDir)) return null;
        foreach (var path in Directory.EnumerateDirectories(_outboxDir, $"*-{id}", SearchOption.TopDirectoryOnly))
        {
            if (OutboxFolderRegex.Match(Path.GetFileName(path)) is { Success: true } match
                && string.Equals(match.Groups["id"].Value, id, StringComparison.OrdinalIgnoreCase))
                return path;
        }
        return null;
    }

    /// <summary>Carpetas pendientes (solo las que siguen el formato de nombre), de la más vieja a la más nueva.</summary>
    private List<string> ListOutboxFolders()
    {
        if (!Directory.Exists(_outboxDir)) return new List<string>();
        return Directory.EnumerateDirectories(_outboxDir, "*", SearchOption.TopDirectoryOnly)
            .Where(path => OutboxFolderRegex.IsMatch(Path.GetFileName(path)))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetIdFromFolderName(string path)
        => OutboxFolderRegex.Match(Path.GetFileName(path)) is { Success: true } match ? match.Groups["id"].Value : "";

    /// <summary>Lee report.json de una carpeta. <paramref name="corrupt"/> = true si está dañado o falta (hay que apartarlo).</summary>
    private static BugReport? ReadReportFile(string folder, out bool corrupt)
    {
        corrupt = false;
        var path = Path.Combine(folder, ReportFileName);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                Log.Warn($"Reporte sin {ReportFileName} en la bandeja: {folder}");
                corrupt = true;
                return null;
            }
            if (info.Length > MaxReportFileBytes)
            {
                Log.Warn($"{ReportFileName} demasiado grande ({info.Length} bytes): {folder}");
                corrupt = true;
                return null;
            }

            var report = JsonSerializer.Deserialize<BugReport>(File.ReadAllText(path, Encoding.UTF8), OutboxJsonOptions);
            if (report is null)
            {
                corrupt = true;
                return null;
            }
            report.Description ??= "";
            report.LauncherVersion ??= "";
            report.AttachmentNames ??= new List<string>();
            if (!Enum.IsDefined(report.Category)) report.Category = BugCategory.Other;
            return report;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            Log.Warn($"Reporte dañado en la bandeja: {folder}", ex);
            corrupt = true;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Bloqueado (antivirus…): se reintentará la próxima vez.
            Log.Warn($"No se pudo leer el reporte de la bandeja: {folder}", ex);
            return null;
        }
    }

    /// <summary>
    /// Adjuntos de la carpeta según report.json (solo nombres simples, sin rutas; máx. 10 y el total de bytes permitido).
    /// Los que falten o no se puedan leer se omiten (se registra).
    /// </summary>
    private List<Attachment> LoadAttachments(string folder, BugReport report, string id)
    {
        var result = new List<Attachment>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var raw in report.AttachmentNames ?? new List<string>())
        {
            if (result.Count >= MaxDiscordAttachments) break;
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name) || !SafeAttachmentNameRegex.IsMatch(name)
                || string.Equals(name, ReportFileName, StringComparison.OrdinalIgnoreCase) || !seen.Add(name))
            {
                Log.Warn($"Reporte {id}: se ignora un adjunto con nombre no válido.");
                continue;
            }

            var path = Path.Combine(folder, name);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    Log.Warn($"Reporte {id}: falta el adjunto {name}; se envía sin él.");
                    continue;
                }
                if (info.Length == 0) continue;
                if (total + info.Length > MaxTotalAttachmentBytes)
                {
                    Log.Warn($"Reporte {id}: el adjunto {name} ({info.Length} bytes) no cabe en el límite; se envía sin él.");
                    continue;
                }

                var bytes = File.ReadAllBytes(path);
                total += bytes.Length;
                result.Add(new Attachment(name, bytes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Reporte {id}: no se pudo leer el adjunto {name}; se envía sin él.", ex);
            }
        }
        return result;
    }

    private void MoveToFailed(string folder, string reason)
    {
        try
        {
            Directory.CreateDirectory(_failedDir);
            var dest = Path.Combine(_failedDir, Path.GetFileName(folder));
            if (Directory.Exists(dest)) FileSystemUtil.TryDeleteDirectory(dest, attempts: 2, delayMs: 100);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Move(folder, dest);
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(100);
                }
            }
            Log.Warn($"Reporte apartado en {_failedDir} ({reason}): {Path.GetFileName(folder)}");
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo mover {folder} a {_failedDir} ({reason}).", ex);
        }
    }

    /// <summary>Borra (best effort) carpetas en preparación abandonadas (p.ej. el launcher se cerró mientras guardaba).</summary>
    private void CleanupStaleStaging(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(_outboxDir)) return;
            foreach (var dir in new DirectoryInfo(_outboxDir).EnumerateDirectories(StagingPrefix + "*", SearchOption.TopDirectoryOnly))
            {
                if (dir.Name.StartsWith(StagingPrefix, StringComparison.Ordinal) && DateTime.UtcNow - dir.LastWriteTimeUtc > olderThan)
                    FileSystemUtil.TryDeleteDirectory(dir.FullName, attempts: 2, delayMs: 100);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"No se pudieron limpiar las carpetas temporales de la bandeja de reportes: {ex.Message}");
        }
    }

    /// <summary>Id seguro para el nombre de la carpeta; si el del reporte no lo es, se le asigna uno nuevo.</summary>
    private static string EnsureSafeId(BugReport report)
    {
        var id = report.Id?.Trim();
        if (string.IsNullOrEmpty(id) || !SafeIdRegex.IsMatch(id))
        {
            id = Guid.NewGuid().ToString("N");
        }
        report.Id = id;
        return id;
    }

    /// <summary>Limpia los textos del reporte antes de guardarlo (límites de la doc del modelo).</summary>
    private static void NormalizeReport(BugReport report, string description)
    {
        report.Description = description;
        report.Steps = FeedbackText.Clean(report.Steps, MaxStepsChars, multiline: true);
        report.Nickname = FeedbackText.Clean(report.Nickname, MaxNicknameChars, multiline: false);
        report.LauncherVersion ??= "";
        if (!Enum.IsDefined(report.Category)) report.Category = BugCategory.Other;
    }

    private bool WasSent(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_stateLock) return _sentIds.Contains(id);
    }

    private void MarkSent(string id)
    {
        lock (_stateLock) _sentIds.Add(id);
    }

    // ───────────────────────────── Adjuntos ─────────────────────────────

    private sealed record Attachment(string Name, byte[] Bytes);

    /// <summary>Estado de un log al prepararlo (para summary.txt).</summary>
    private sealed class LogPart
    {
        public required string Name { get; init; }
        public byte[]? Bytes { get; set; }
        public long OriginalLength { get; set; }
        public bool TailOnly { get; set; }
        public bool TrimmedForTotal { get; set; }
        public string? Problem { get; set; }
    }

    private static bool IsSummary(string name) => string.Equals(name, SummaryFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Logs anonimizados (final de cada uno) + summary.txt, dentro del límite total.</summary>
    private List<Attachment> BuildAttachments(BugReport report, BugReportSources sources)
    {
        var logs = new List<LogPart>
        {
            ReadLogTail(PlayerLogName, sources.PlayerLog),
            ReadLogTail(PlayerPrevLogName, sources.PlayerPrevLog),
            ReadLogTail(LauncherLogName, sources.LauncherLog),
        };

        var summary = Utf8NoBom.GetBytes(BuildSummary(report, sources, logs));
        var budget = (long)MaxTotalAttachmentBytes - summary.Length;
        if (logs.Sum(l => (long)(l.Bytes?.Length ?? 0)) > budget)
        {
            // Se deja margen para las notas del recorte en summary.txt.
            TrimLogsToBudget(logs, Math.Max(0, budget - SummaryNotesSlackBytes));
            summary = Utf8NoBom.GetBytes(BuildSummary(report, sources, logs));
        }
        if (summary.Length > MaxTotalAttachmentBytes) summary = KeepHead(summary, MaxTotalAttachmentBytes);

        var result = new List<Attachment>(4);
        foreach (var log in logs)
        {
            if (log.Bytes is { Length: > 0 } bytes) result.Add(new Attachment(log.Name, bytes));
        }
        result.Add(new Attachment(SummaryFileName, summary));
        return result;
    }

    /// <summary>
    /// Lee como mucho los últimos MaxLogTailBytes de un log (empezando en un salto de línea) aunque otro proceso lo tenga
    /// abierto (el juego o el propio launcher escribiendo), y lo anonimiza. Nunca lanza: los problemas quedan en Problem.
    /// </summary>
    private LogPart ReadLogTail(string name, string? path)
    {
        var part = new LogPart { Name = name };
        if (string.IsNullOrWhiteSpace(path))
        {
            part.Problem = "no se encontró";
            return part;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                part.Problem = "no se encontró";
                return part;
            }

            byte[] buffer;
            int read;
            long start;
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                       81920, FileOptions.SequentialScan))
            {
                var length = stream.Length;
                part.OriginalLength = length;
                var maxTail = Math.Max(1, MaxLogTailBytes);
                var take = (int)Math.Min(length, maxTail);
                start = length - take;
                stream.Seek(start, SeekOrigin.Begin);
                buffer = new byte[take];
                read = 0;
                int n;
                while (read < take && (n = stream.Read(buffer, read, take - read)) > 0) read += n;
            }

            if (read == 0)
            {
                part.Problem = "está vacío";
                return part;
            }

            var offset = start > 0 ? FindCutPoint(buffer, 0, read) : 0;
            var text = new UTF8Encoding(false, false).GetString(buffer, offset, read - offset);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

            var bytes = Utf8NoBom.GetBytes(Redact(text, RedactUserName, RedactUserProfile, RedactMachineName));
            var tailOnly = start > 0;
            if (bytes.Length > MaxLogTailBytes)
            {
                // Al anonimizar puede crecer un poco ("C:\Users\Al" → "%USERPROFILE%").
                bytes = KeepTail(bytes, MaxLogTailBytes);
                tailOnly = true;
            }
            if (bytes.Length == 0)
            {
                part.Problem = "está vacío";
                return part;
            }

            part.Bytes = bytes;
            part.TailOnly = tailOnly;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            Log.Warn($"No se pudo leer {name} para el reporte.", ex);
            part.Problem = "no se pudo leer: " + Redact(ex.Message, RedactUserName, RedactUserProfile, RedactMachineName);
            part.Bytes = null;
        }
        return part;
    }

    /// <summary>
    /// Recorta los logs más grandes (conservando su final) hasta que entren en <paramref name="budget"/> bytes: todos quedan
    /// por debajo de un mismo tope y los pequeños no se tocan.
    /// </summary>
    private static void TrimLogsToBudget(List<LogPart> logs, long budget)
    {
        var lengths = logs.Select(l => (long)(l.Bytes?.Length ?? 0)).ToArray();
        if (lengths.Sum() <= budget) return;

        // Mayor tope C tal que sum(min(len, C)) <= budget (búsqueda binaria).
        long lo = 0, hi = lengths.Max();
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (lengths.Sum(len => Math.Min(len, mid)) <= budget) lo = mid;
            else hi = mid - 1;
        }

        foreach (var log in logs)
        {
            if (log.Bytes is null || log.Bytes.Length <= lo) continue;
            log.Bytes = lo > 0 ? KeepTail(log.Bytes, (int)lo) : Array.Empty<byte>();
            log.TrimmedForTotal = true;
            log.TailOnly = true;
            if (log.Bytes.Length == 0)
            {
                log.Bytes = null;
                log.Problem = "se omitió para no pasar del límite de tamaño";
            }
        }
    }

    /// <summary>Últimos <paramref name="maxBytes"/> bytes empezando en un salto de línea (o, si no hay, en un carácter UTF-8 completo).</summary>
    private static byte[] KeepTail(byte[] bytes, int maxBytes)
    {
        if (bytes.Length <= maxBytes) return bytes;
        if (maxBytes <= 0) return Array.Empty<byte>();
        var start = bytes.Length - maxBytes;
        var offset = start + FindCutPoint(bytes, start, maxBytes);
        return bytes.AsSpan(offset).ToArray();
    }

    /// <summary>Primeros <paramref name="maxBytes"/> bytes sin partir un carácter UTF-8.</summary>
    private static byte[] KeepHead(byte[] bytes, int maxBytes)
    {
        if (bytes.Length <= maxBytes) return bytes;
        var end = Math.Max(0, maxBytes);
        while (end > 0 && (bytes[end] & 0xC0) == 0x80) end--;
        return bytes.AsSpan(0, end).ToArray();
    }

    /// <summary>
    /// Desplazamiento (desde <paramref name="start"/>) donde empieza la primera línea completa del trozo; si no hay salto de
    /// línea útil, el primer byte que no es continuación de un carácter UTF-8.
    /// </summary>
    private static int FindCutPoint(byte[] buffer, int start, int count)
    {
        var newline = Array.IndexOf(buffer, (byte)'\n', start, count);
        if (newline >= 0 && newline < start + count - 1) return newline + 1 - start;

        var offset = 0;
        while (offset < count && offset < 4 && (buffer[start + offset] & 0xC0) == 0x80) offset++;
        return offset;
    }

    /// <summary>summary.txt: datos del reporte, Summary (anonimizado) y qué se adjunta.</summary>
    private string BuildSummary(BugReport report, BugReportSources sources, IReadOnlyList<LogPart> logs)
    {
        var sb = new StringBuilder(4096);
        var category = Enum.IsDefined(report.Category) ? report.Category : BugCategory.Other;
        var version = FeedbackText.Clean(report.GameVersionDisplay, 120, multiline: false);
        var rawVersion = FeedbackText.Clean(report.GameVersion, 120, multiline: false);
        var versionText = (version, rawVersion) switch
        {
            (null, null) => "desconocida",
            (null, _) => rawVersion!,
            (_, null) => version!,
            _ when string.Equals(version, rawVersion, StringComparison.OrdinalIgnoreCase) => version!,
            _ => $"{version} ({rawVersion})",
        };

        sb.Append("Reporte de problema · ").Append(LauncherConfig.GameDisplayName).Append('\n');
        sb.Append("=====================================\n");
        sb.Append("Id: ").Append(report.Id).Append('\n');
        sb.Append("Fecha (UTC): ").Append(report.CreatedAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("Categoría: ").Append(CategoryDisplayName(category)).Append('\n');
        sb.Append("Versión del juego: ").Append(versionText).Append('\n');
        sb.Append("Launcher: ").Append(FeedbackText.Clean(report.LauncherVersion, 40, multiline: false) ?? "?").Append('\n');
        sb.Append("Sistema: ").Append(FeedbackText.Clean(report.OsDescription, 150, multiline: false) ?? "?").Append('\n');
        if (report.ExitCode is { } exitCode) sb.Append("Cierre del juego: ").Append(FormatExitCode(exitCode)).Append('\n');
        sb.Append("Jugador: ").Append(FeedbackText.Clean(report.Nickname, MaxNicknameChars, multiline: false) ?? "Anónimo").Append('\n');

        sb.Append("\nQué ha pasado:\n").Append(FeedbackText.Clean(report.Description, MaxDescriptionChars, multiline: true) ?? "—").Append('\n');
        sb.Append("\nCómo reproducirlo:\n").Append(FeedbackText.Clean(report.Steps, MaxStepsChars, multiline: true) ?? "—").Append('\n');

        sb.Append("\nEstado del launcher:\n");
        var entries = 0;
        if (sources.Summary is { } summary)
        {
            foreach (var pair in summary)
            {
                if (entries >= MaxSummaryEntries)
                {
                    sb.Append("- …\n");
                    break;
                }
                var key = FeedbackText.Clean(pair.Key, MaxSummaryKeyChars, multiline: false);
                if (key is null) continue;
                var value = FeedbackText.Clean(pair.Value, MaxSummaryValueChars, multiline: false) ?? "—";
                sb.Append("- ").Append(RedactLocal(key)).Append(": ").Append(RedactLocal(value)).Append('\n');
                entries++;
            }
        }
        if (entries == 0) sb.Append("- —\n");

        sb.Append("\nRegistros (se ocultan el usuario de Windows, la carpeta del perfil y el nombre del equipo):\n");
        foreach (var log in logs)
        {
            sb.Append("- ").Append(log.Name).Append(": ");
            if (log.Bytes is { Length: > 0 } bytes)
            {
                if (!log.TailOnly)
                {
                    sb.Append(FileSystemUtil.FormatBytes(bytes.Length));
                }
                else
                {
                    sb.Append(FileSystemUtil.FormatBytes(log.OriginalLength)).Append(" en total; se adjuntan los últimos ")
                        .Append(FileSystemUtil.FormatBytes(bytes.Length));
                    if (log.TrimmedForTotal) sb.Append(" (recortado para no pasar del límite de tamaño)");
                }
            }
            else
            {
                sb.Append(log.Problem ?? "no disponible");
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private string RedactLocal(string text) => Redact(text, RedactUserName, RedactUserProfile, RedactMachineName);

    // ───────────────────────────── Formato ─────────────────────────────

    /// <summary>«código 3», «código -1073741819 (0xC0000005)» (los NTSTATUS se entienden mejor en hexadecimal).</summary>
    private static string FormatExitCode(int code) => code switch
    {
        -1 => "código -1 (no se pudo leer)",
        < 0 or > 0xFFFF => string.Create(CultureInfo.InvariantCulture, $"código {code} (0x{unchecked((uint)code):X8})"),
        _ => string.Create(CultureInfo.InvariantCulture, $"código {code}"),
    };

    /// <summary>Texto del campo «Registros» del embed.</summary>
    private static string DescribeAttachments(IReadOnlyList<string> names, bool logsOmitted)
    {
        if (logsOmitted)
            return "Los registros eran demasiado grandes para Discord: solo se adjunta el resumen (summary.txt).";
        if (names.Count == 0) return "No se adjuntaron registros.";
        return FeedbackText.Truncate(string.Join(" · ", names), MaxFieldValueChars);
    }

    /// <summary>«Launcher 1.0.5 · Windows 10.0.26200».</summary>
    private static string BuildFooter(BugReport report)
    {
        var launcher = FeedbackText.Clean(report.LauncherVersion, 40, multiline: false) ?? "?";
        var os = FeedbackText.Clean(report.OsDescription, 150, multiline: false);
        if (os is not null && os.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) && os.Length > "Microsoft ".Length)
            os = os["Microsoft ".Length..];
        return os is null ? $"Launcher {launcher}" : $"Launcher {launcher} · {os}";
    }

    // ───────────────────────────── Anonimizar ─────────────────────────────

    /// <summary>Segmentos de la ruta del perfil ("C:", "Users", "Ana"); null si no hay o es demasiado corta (una raíz como "C:\").</summary>
    private static string[]? GetProfileSegments(string? userProfile)
    {
        if (string.IsNullOrWhiteSpace(userProfile)) return null;
        var segments = userProfile.Trim().Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        segments = segments.Where(s => s.Length > 0).ToArray();
        return segments.Length >= 2 ? segments : null;
    }

    private static string? CleanRedactWord(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length < 3 ? null : trimmed;
    }

    /// <summary>Plan B de <see cref="Redact"/>: reemplazos literales sin distinguir mayúsculas (sin límites de palabra).</summary>
    private static string RedactSimple(string text, string? user, string[]? profileSegments, string? machine)
    {
        try
        {
            if (profileSegments is not null)
            {
                text = text.Replace(string.Join('\\', profileSegments), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                           .Replace(string.Join('/', profileSegments), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                           .Replace(string.Join(@"\\", profileSegments), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            }
            if (user is not null) text = text.Replace(user, "<usuario>", StringComparison.OrdinalIgnoreCase);
            if (machine is not null) text = text.Replace(machine, "<equipo>", StringComparison.OrdinalIgnoreCase);
            return text;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo anonimizar un registro; se omite su contenido.", ex);
            return "";
        }
    }

    private static string? SafeEnvironment(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
