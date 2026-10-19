using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Net;
using PalitoLauncher.Core.Util;

namespace PalitoLauncher.Core.Feedback;

/// <summary>
/// Envía respuestas de la encuesta a un webhook de Discord con una bandeja de salida en disco para que nunca se pierdan.
///
/// Bandeja: &lt;outboxDir&gt;\&lt;yyyyMMddHHmmss&gt;-&lt;id&gt;.json (FeedbackSubmission en JSON, escritura atómica). Una respuesta se
/// guarda SIEMPRE en la bandeja antes de intentar enviarla y se borra solo cuando Discord confirma (2xx).
///
/// Envío: POST {webhook}?wait=true con JSON { username: "Coop2D · Feedback", embeds: [embed] } (HttpClient de HttpFactory,
/// timeout 15 s). El embed: título «Opinión · Coop2D 0.0.3 · Prototipo 2» (o «versión desconocida»), color según la
/// valoración Rating de la encuesta (1-2 rojo #FF5C6C, 3 ámbar #FFB627, 4-5 verde #3DDC97, sin valoración azul #5AA9FF),
/// un campo por pregunta respondida en el orden de la encuesta (nombre = texto de la pregunta ≤ 256; valor ≤ 1024:
/// Rating → «★★★★☆ (4/5)», Scale → «4 / 5 (Muy difícil)» con la etiqueta del extremo si coincide, Choice/Multi → opciones
/// separadas por « · », Text → el texto), campos finales «Jugador» (apodo o «Anónimo»), «Partida» («34 min · salió bien»
/// / «código X» si ExitCode != 0) y «Encuesta» (SurveyId), footer «Launcher 1.0.3 · Windows …» y timestamp ISO
/// (CreatedAtUtc). Respeta los límites de Discord (≤ 25 campos, ≤ 6000 caracteres en total: si se pasa, recorta los textos
/// más largos con «…»). Las menciones se neutralizan (allowed_mentions: { parse: [] }).
///
/// Respuestas de Discord: 2xx → enviado (borrar de la bandeja). 429 → respetar retry_after/Retry-After y dejar en la
/// bandeja. 400 (payload) → registrar el cuerpo y mover el archivo a &lt;outboxDir&gt;\failed\ para no reintentar sin fin.
/// 401/403/404 (webhook borrado o inválido) → dejar en la bandeja (se enviará cuando haya un webhook válido). 5xx / red →
/// dejar en la bandeja. Thread-safe: un único envío a la vez (SemaphoreSlim).
/// </summary>
public sealed class FeedbackService
{
    internal const string FailedFolderName = "failed";
    internal const string Username = LauncherConfig.GameDisplayName + " · Feedback";

    // Límites de Discord para embeds.
    internal const int MaxEmbedFields = 25;
    internal const int MaxEmbedTotalChars = 6000;
    internal const int MaxEmbedTitleChars = 256;
    internal const int MaxFieldNameChars = 256;
    internal const int MaxFieldValueChars = 1024;
    private const int MaxFooterChars = 256;
    private const int MaxNicknameChars = 100;
    private const int MaxChoiceChars = 200;

    // Colores del embed (paleta del launcher).
    internal const int ColorBad = 0xFF5C6C;
    internal const int ColorNeutral = 0xFFB627;
    internal const int ColorGood = 0x3DDC97;
    internal const int ColorNoRating = 0x5AA9FF;

    private const int MaxOutboxFileBytes = 1024 * 1024;
    private const int MaxResponseBodyBytes = 64 * 1024;

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>Si Discord pide esperar menos que esto, se espera en el momento en lugar de dejarlo para más tarde.</summary>
    private static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(3);

    private static readonly Regex OutboxFileRegex = new(@"^(?<stamp>[0-9]{14})-(?<id>[A-Za-z0-9_-]{1,64})\.json$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SafeIdRegex = new(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

    internal static readonly JsonSerializerOptions OutboxJsonOptions = new()
    {
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
    private DateTimeOffset? _lastSubmissionUtc;
    private string? _lastSubmissionId;
    private DateTimeOffset? _blockedUntilUtc;

    public FeedbackService(HttpClient http, string outboxDir)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxDir);
        _http = http;
        _outboxDir = Path.GetFullPath(outboxDir);
        _failedDir = Path.Combine(_outboxDir, FailedFolderName);
        // Se mira una sola vez: leer las cabeceras por defecto desde varios hilos no es seguro.
        _addUserAgent = !http.DefaultRequestHeaders.Contains("User-Agent");
    }

    /// <summary>Tiempo mínimo entre dos envíos del usuario (anti doble clic / spam). Por defecto 30 s.</summary>
    public TimeSpan MinIntervalBetweenSubmissions { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Reloj inyectable (tests).</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;

    /// <summary>Timeout de cada POST a Discord (tests).</summary>
    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Carpeta de respuestas que Discord rechazó o que estaban dañadas.</summary>
    internal string FailedDir => _failedDir;

    /// <summary>Número de respuestas pendientes en la bandeja (no cuenta failed\). Nunca lanza.</summary>
    public int PendingCount
    {
        get
        {
            try
            {
                return ListOutboxFiles().Count(path => !WasSent(GetIdFromFileName(path)));
            }
            catch (Exception ex)
            {
                Log.Warn("No se pudo contar las opiniones pendientes de enviar.", ex);
                return 0;
            }
        }
    }

    /// <summary>
    /// Guarda <paramref name="submission"/> en la bandeja e intenta enviarla a <paramref name="discordWebhookUrl"/> (si es
    /// null o no válido → Queued sin red). Si el usuario envió otra hace menos de MinIntervalBetweenSubmissions →
    /// LauncherException("Espera unos segundos antes de enviar otra opinión."). Error de disco al guardar →
    /// LauncherException con mensaje claro. Cualquier fallo de red/Discord → Queued (no lanza). Solo lanza además
    /// OperationCanceledException si se canceló <paramref name="ct"/> (la respuesta queda en la bandeja).
    /// </summary>
    public Task<FeedbackSendOutcome> SubmitAsync(FeedbackSubmission submission, string? discordWebhookUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submission);
        // En el pool: guardar en disco y preparar el envío no debe tocar el hilo de la UI.
        return Task.Run(() => SubmitCoreAsync(submission, discordWebhookUrl, ct));
    }

    /// <summary>
    /// Reintenta enviar lo pendiente de la bandeja (de más viejo a más nuevo, máx. <paramref name="maxItems"/>), parando al
    /// primer 429 o error de red. Devuelve cuántas se enviaron. Sin webhook válido no hace nada. Nunca lanza (salvo
    /// OperationCanceledException si se canceló <paramref name="ct"/>). Archivos corruptos → a failed\.
    /// </summary>
    public Task<int> FlushOutboxAsync(string? discordWebhookUrl, CancellationToken ct, int maxItems = 10)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<int>(ct);
        return Task.Run(() => FlushCoreAsync(discordWebhookUrl, ct, maxItems));
    }

    /// <summary>Construye el JSON del mensaje de Discord (expuesto para tests; no hace E/S).</summary>
    internal static string BuildDiscordPayload(FeedbackSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var questions = submission.Questions ?? new List<SurveyQuestion>();
        var answers = submission.Answers ?? new Dictionary<string, SurveyAnswer>();
        var fields = new List<EmbedField>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? rating = null;

        foreach (var question in questions)
        {
            // Se reservan 3 campos para Jugador / Partida / Encuesta.
            if (fields.Count >= MaxEmbedFields - 3) break;
            if (question is null || string.IsNullOrWhiteSpace(question.Id) || !seenIds.Add(question.Id)) continue;

            var answer = FindAnswer(answers, question.Id);
            if (answer is null) continue;

            var value = FeedbackText.Clean(FormatAnswer(question, answer, out var ratingValue), MaxFieldValueChars,
                multiline: question.Type == SurveyQuestionType.Text);
            if (value is null) continue;
            if (ratingValue is not null) rating ??= ratingValue;

            var name = FeedbackText.Clean(question.Text, MaxFieldNameChars, multiline: false)
                       ?? FeedbackText.Clean(question.Id, MaxFieldNameChars, multiline: false)
                       ?? "Pregunta";
            fields.Add(new EmbedField(name, value, Inline: false));
        }

        fields.Add(new EmbedField("Jugador",
            FeedbackText.Clean(submission.Nickname, MaxNicknameChars, multiline: false) ?? "Anónimo", Inline: true));
        fields.Add(new EmbedField("Partida", FormatSession(submission), Inline: true));
        fields.Add(new EmbedField("Encuesta",
            FeedbackText.Clean(submission.SurveyId, MaxFieldValueChars, multiline: false) ?? "—", Inline: true));

        var version = FeedbackText.Clean(submission.GameVersionDisplay, 120, multiline: false)
                      ?? FeedbackText.Clean(submission.GameVersion, 120, multiline: false);
        var title = FeedbackText.Truncate(version is null
            ? $"Opinión · {LauncherConfig.GameDisplayName} · versión desconocida"
            : $"Opinión · {LauncherConfig.GameDisplayName} {version}", MaxEmbedTitleChars);

        var footer = FeedbackText.Truncate(BuildFooter(submission), MaxFooterChars);
        FitToTotalLimit(fields, MaxEmbedTotalChars - title.Length - footer.Length);

        var color = rating switch
        {
            null => ColorNoRating,
            <= 2 => ColorBad,
            3 => ColorNeutral,
            _ => ColorGood,
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
            var created = submission.CreatedAtUtc.UtcDateTime;
            if (created.Year is >= 2015 and <= 9000)
                writer.WriteString("timestamp", created.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// ¿Hay que invitar a responder tras esta partida? True solo si: askEnabled (ajuste del usuario) &amp;&amp;
    /// survey.AskAfterSession &amp;&amp; la partida duró ≥ survey.MinSessionMinutes &amp;&amp; (lastPromptUtc es null o hace ≥
    /// survey.MinHoursBetweenPrompts horas, o está en el futuro por un cambio de reloj). Puro, no lanza.
    /// </summary>
    public static bool ShouldPromptAfterSession(bool askEnabled, SurveyDefinition survey, TimeSpan sessionDuration,
        DateTimeOffset? lastPromptUtc, DateTimeOffset nowUtc)
    {
        if (!askEnabled || survey is null || !survey.AskAfterSession) return false;

        // Mismos rangos que SurveyProvider.Sanitize, por si llega una encuesta sin sanear.
        var minSession = TimeSpan.FromMinutes(Math.Clamp(survey.MinSessionMinutes, 1, 120));
        if (sessionDuration < minSession) return false;

        if (lastPromptUtc is not { } last) return true;
        var elapsed = nowUtc - last;
        if (elapsed < TimeSpan.Zero) return true; // la hora de Windows retrocedió
        return elapsed >= TimeSpan.FromHours(Math.Clamp(survey.MinHoursBetweenPrompts, 0, 168));
    }

    // ───────────────────────────── Envío ─────────────────────────────

    private async Task<FeedbackSendOutcome> SubmitCoreAsync(FeedbackSubmission submission, string? discordWebhookUrl, CancellationToken ct)
    {
        var id = EnsureSafeId(submission);
        if (WasSent(id))
        {
            Log.Info($"La opinión {id} ya se había enviado; no se repite.");
            return FeedbackSendOutcome.Sent;
        }

        // Anti doble clic / spam (reintentar la misma respuesta no cuenta: sobrescribe su archivo).
        var now = UtcNow();
        DateTimeOffset? previousUtc;
        string? previousId;
        lock (_stateLock)
        {
            previousUtc = _lastSubmissionUtc;
            previousId = _lastSubmissionId;
            if (previousUtc is { } last && !string.Equals(previousId, id, StringComparison.OrdinalIgnoreCase))
            {
                var elapsed = now - last;
                if (elapsed >= TimeSpan.Zero && elapsed < MinIntervalBetweenSubmissions)
                    throw new LauncherException("Espera unos segundos antes de enviar otra opinión.");
            }
            _lastSubmissionUtc = now;
            _lastSubmissionId = id;
        }

        string path;
        try
        {
            path = SaveToOutbox(submission, id);
        }
        catch
        {
            // No se guardó: el usuario puede reintentar enseguida.
            lock (_stateLock)
            {
                if (_lastSubmissionUtc == now && string.Equals(_lastSubmissionId, id, StringComparison.OrdinalIgnoreCase))
                {
                    _lastSubmissionUtc = previousUtc;
                    _lastSubmissionId = previousId;
                }
            }
            throw;
        }

        ct.ThrowIfCancellationRequested();

        var postUrl = BuildPostUrl(discordWebhookUrl);
        if (postUrl is null)
        {
            Log.Info($"Opinión {id} guardada en la bandeja de salida: no hay webhook de Discord válido (se enviará cuando lo haya).");
            return FeedbackSendOutcome.Queued;
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Mientras esperaba, otro envío (FlushOutboxAsync) pudo mandarla o moverla a failed\.
            if (!File.Exists(path))
                return WasSent(id) ? FeedbackSendOutcome.Sent : FeedbackSendOutcome.Queued;
            if (!await WaitForRateLimitAsync(ct).ConfigureAwait(false))
                return FeedbackSendOutcome.Queued;

            var result = await SendFileAsync(path, submission, id, postUrl, ct).ConfigureAwait(false);
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
                    Log.Info($"Hay {pending} opiniones pendientes, pero no hay webhook de Discord válido; siguen en la bandeja.");
                return 0;
            }

            FileSystemUtil.CleanupStaleTempFiles(_outboxDir, TimeSpan.FromDays(1));

            var processed = 0;
            foreach (var path in ListOutboxFiles())
            {
                if (processed >= maxItems) break;
                ct.ThrowIfCancellationRequested();

                await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // SubmitAsync pudo enviarla mientras tanto.
                    if (!File.Exists(path)) continue;
                    processed++;

                    var nameId = GetIdFromFileName(path);
                    if (WasSent(nameId))
                    {
                        FileSystemUtil.TryDeleteFile(path); // ya enviada; quedó porque no se pudo borrar
                        continue;
                    }

                    var submission = ReadOutboxFile(path, out var corrupt);
                    if (submission is null)
                    {
                        if (corrupt) MoveToFailed(path, "archivo dañado");
                        continue;
                    }
                    var id = SafeIdRegex.IsMatch(submission.Id ?? "") ? submission.Id! : nameId;
                    if (WasSent(id))
                    {
                        FileSystemUtil.TryDeleteFile(path);
                        continue;
                    }

                    if (!await WaitForRateLimitAsync(ct).ConfigureAwait(false)) break;

                    var result = await SendFileAsync(path, submission, id, postUrl, ct).ConfigureAwait(false);
                    if (result == SendResult.Sent) sent++;
                    else if (result != SendResult.Rejected) break; // 429, webhook no válido, 5xx o red: se para
                }
                finally
                {
                    _sendLock.Release();
                }
            }

            if (sent > 0) Log.Info($"Bandeja de feedback: {sent} opiniones pendientes enviadas a Discord.");
            return sent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("Error inesperado al vaciar la bandeja de feedback.", ex);
            return sent;
        }
    }

    private enum SendResult { Sent, RateLimited, Rejected, WebhookInvalid, ServerError, NetworkError }

    /// <summary>Envía un archivo de la bandeja (con <see cref="_sendLock"/> tomado). Solo lanza OperationCanceledException de <paramref name="ct"/>.</summary>
    private async Task<SendResult> SendFileAsync(string path, FeedbackSubmission submission, string id, string postUrl, CancellationToken ct)
    {
        string payload;
        try
        {
            payload = BuildDiscordPayload(submission);
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo preparar el mensaje de Discord de la opinión {id}.", ex);
            MoveToFailed(path, "no se pudo preparar el mensaje");
            return SendResult.Rejected;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, postUrl)
        {
            Content = new StringContent(payload, new UTF8Encoding(false), "application/json"),
        };
        if (_addUserAgent) request.Headers.TryAddWithoutValidation("User-Agent", HttpFactory.UserAgent);

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (status is >= 200 and <= 299)
            {
                MarkSent(id);
                if (!FileSystemUtil.TryDeleteFile(path))
                    Log.Warn($"Opinión {id} enviada, pero no se pudo borrar de la bandeja (no se volverá a enviar en esta sesión).");
                NotePreemptiveRateLimit(response);
                Log.Info($"Opinión {id} enviada a Discord ({status}).");
                return SendResult.Sent;
            }

            var body = await ReadBodySafeAsync(response.Content, timeoutCts.Token).ConfigureAwait(false);

            if (status == 429)
            {
                var wait = GetRetryAfter(response, body);
                lock (_stateLock) _blockedUntilUtc = UtcNow() + wait;
                Log.Warn($"Discord: demasiados mensajes (429); la opinión {id} se queda en la bandeja y se reintentará dentro de " +
                         $"{wait.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s.");
                return SendResult.RateLimited;
            }

            if (status is 400 or 413 or 422)
            {
                Log.Error($"Discord rechazó la opinión {id} (código {status}): {FeedbackText.Truncate(body, 2000)}");
                MoveToFailed(path, $"Discord la rechazó (código {status})");
                return SendResult.Rejected;
            }

            if (status is 401 or 403 or 404)
            {
                Log.Warn($"El webhook de Discord no es válido o se borró (código {status}); la opinión {id} se queda en la bandeja " +
                         "hasta que haya uno válido.");
                return SendResult.WebhookInvalid;
            }

            Log.Warn($"Discord respondió con el código {status}; la opinión {id} se queda en la bandeja.");
            return status >= 500 ? SendResult.ServerError : SendResult.WebhookInvalid;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            Log.Warn($"Discord tardó demasiado en responder; la opinión {id} se queda en la bandeja.", ex);
            return SendResult.NetworkError;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Log.Warn($"No se pudo conectar con Discord; la opinión {id} se queda en la bandeja.", ex);
            return SendResult.NetworkError;
        }
        catch (Exception ex)
        {
            Log.Error($"Error inesperado al enviar la opinión {id} a Discord; se queda en la bandeja.", ex);
            return SendResult.NetworkError;
        }
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
            Log.Info($"Discord pidió esperar; las opiniones se reintentarán más tarde (quedan {wait.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s).");
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
                if (doc.RootElement.TryGetProperty("retry_after", out var retry) && retry.ValueKind == JsonValueKind.Number
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

    /// <summary>Guarda la respuesta (sobrescribe el archivo si ya estaba en la bandeja con el mismo Id). Errores → LauncherException.</summary>
    private string SaveToOutbox(FeedbackSubmission submission, string id)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(submission, OutboxJsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo preparar la opinión {id} para guardarla.", ex);
            throw new LauncherException("No se pudo preparar tu opinión para enviarla. Vuelve a intentarlo.", ex);
        }

        string? path = null;
        try
        {
            Directory.CreateDirectory(_outboxDir);
            path = FindOutboxFile(id) ?? Path.Combine(_outboxDir,
                $"{submission.CreatedAtUtc.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}-{id}.json");
            FileSystemUtil.WriteAllTextAtomic(path, json);
            return path;
        }
        catch (IOException ex) when (FileSystemUtil.IsDiskFull(ex))
        {
            Log.Warn($"Disco lleno al guardar la opinión {id} en {path ?? _outboxDir}.", ex);
            throw new LauncherException("No hay espacio libre en el disco para guardar tu opinión. Libera algo de espacio y vuelve a intentarlo.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            Log.Warn($"No se pudo guardar la opinión {id} en {path ?? _outboxDir}.", ex);
            throw new LauncherException("No se pudo guardar tu opinión en el disco. Vuelve a intentarlo; si se repite, reinicia el launcher.", ex);
        }
    }

    /// <summary>Archivo de la bandeja con ese Id (o null).</summary>
    private string? FindOutboxFile(string id)
    {
        if (!Directory.Exists(_outboxDir)) return null;
        foreach (var path in Directory.EnumerateFiles(_outboxDir, $"*-{id}.json", SearchOption.TopDirectoryOnly))
        {
            if (OutboxFileRegex.Match(Path.GetFileName(path)) is { Success: true } match
                && string.Equals(match.Groups["id"].Value, id, StringComparison.OrdinalIgnoreCase))
                return path;
        }
        return null;
    }

    /// <summary>Archivos pendientes (solo los que siguen el formato de nombre), del más viejo al más nuevo.</summary>
    private List<string> ListOutboxFiles()
    {
        if (!Directory.Exists(_outboxDir)) return new List<string>();
        return Directory.EnumerateFiles(_outboxDir, "*.json", SearchOption.TopDirectoryOnly)
            .Where(path => OutboxFileRegex.IsMatch(Path.GetFileName(path)))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetIdFromFileName(string path)
        => OutboxFileRegex.Match(Path.GetFileName(path)) is { Success: true } match ? match.Groups["id"].Value : "";

    /// <summary>Lee un archivo de la bandeja. <paramref name="corrupt"/> = true si está dañado (hay que apartarlo).</summary>
    private static FeedbackSubmission? ReadOutboxFile(string path, out bool corrupt)
    {
        corrupt = false;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxOutboxFileBytes)
            {
                Log.Warn($"Archivo de la bandeja de feedback demasiado grande ({info.Length} bytes): {path}");
                corrupt = true;
                return null;
            }

            var submission = JsonSerializer.Deserialize<FeedbackSubmission>(File.ReadAllText(path, Encoding.UTF8), OutboxJsonOptions);
            if (submission is null)
            {
                corrupt = true;
                return null;
            }
            submission.Questions ??= new List<SurveyQuestion>();
            submission.Answers ??= new Dictionary<string, SurveyAnswer>();
            return submission;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            Log.Warn($"Archivo de la bandeja de feedback dañado: {path}", ex);
            corrupt = true;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Bloqueado (antivirus…): se reintentará la próxima vez.
            Log.Warn($"No se pudo leer el archivo de la bandeja de feedback: {path}", ex);
            return null;
        }
    }

    private void MoveToFailed(string path, string reason)
    {
        try
        {
            Directory.CreateDirectory(_failedDir);
            FileSystemUtil.MoveFileWithRetry(path, Path.Combine(_failedDir, Path.GetFileName(path)), overwrite: true,
                attempts: 3, delayMs: 100);
            Log.Warn($"Opinión apartada en {_failedDir} ({reason}): {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo mover {path} a {_failedDir} ({reason}).", ex);
        }
    }

    /// <summary>Id seguro para el nombre de archivo; si el de la respuesta no lo es, se le asigna uno nuevo.</summary>
    private static string EnsureSafeId(FeedbackSubmission submission)
    {
        var id = submission.Id?.Trim();
        if (string.IsNullOrEmpty(id) || !SafeIdRegex.IsMatch(id))
        {
            id = Guid.NewGuid().ToString("N");
            submission.Id = id;
        }
        return id;
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

    // ───────────────────────────── Formato del mensaje ─────────────────────────────

    private sealed record EmbedField(string Name, string Value, bool Inline)
    {
        public string Name { get; set; } = Name;
        public string Value { get; set; } = Value;
    }

    private static SurveyAnswer? FindAnswer(Dictionary<string, SurveyAnswer> answers, string id)
    {
        if (answers.TryGetValue(id, out var answer)) return answer;
        foreach (var pair in answers)
        {
            if (string.Equals(pair.Key, id, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        }
        return null;
    }

    /// <summary>Texto del valor de una respuesta (null si está vacía o no encaja con el tipo).</summary>
    private static string? FormatAnswer(SurveyQuestion question, SurveyAnswer answer, out int? rating)
    {
        rating = null;
        switch (question.Type)
        {
            case SurveyQuestionType.Rating:
            {
                if (answer.Number is not { } n) return null;
                n = Math.Clamp(n, 1, 5);
                rating = n;
                return new string('★', n) + new string('☆', 5 - n) + $" ({n}/5)";
            }

            case SurveyQuestionType.Scale:
            {
                if (answer.Number is not { } n) return null;
                var (min, max) = (question.Min, question.Max);
                if (min < 0 || max > 10 || min >= max) (min, max) = (1, 5);
                n = Math.Clamp(n, min, max);
                var label = n == min ? question.MinLabel : n == max ? question.MaxLabel : null;
                label = FeedbackText.Clean(label, SurveyProvider.MaxLabelLength, multiline: false);
                var text = string.Create(CultureInfo.InvariantCulture, $"{n} / {max}");
                return label is null ? text : $"{text} ({label})";
            }

            case SurveyQuestionType.Choice:
            case SurveyQuestionType.Multi:
            {
                if (answer.Choices is null) return null;
                var choices = answer.Choices
                    .Select(c => FeedbackText.Clean(c, MaxChoiceChars, multiline: false))
                    .Where(c => c is not null)
                    .ToList();
                return choices.Count == 0 ? null : string.Join(" · ", choices);
            }

            case SurveyQuestionType.Text:
                return answer.Text;

            default:
                return null;
        }
    }

    /// <summary>«34 min · salió bien», «1 h 05 min · código 3», o cómo se envió si no viene de una partida.</summary>
    private static string FormatSession(FeedbackSubmission submission)
    {
        var parts = new List<string>(2);
        if (submission.SessionMinutes is { } minutes && minutes >= 0)
        {
            parts.Add(minutes switch
            {
                0 => "menos de 1 min",
                < 60 => string.Create(CultureInfo.InvariantCulture, $"{minutes} min"),
                _ => string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h {minutes % 60:00} min"),
            });
        }
        if (submission.ExitCode is { } code)
        {
            parts.Add(code switch
            {
                0 => "salió bien",
                -1 => "código -1 (no se pudo leer)",
                // Códigos NTSTATUS (p.ej. 0xC0000005, acceso no válido) se entienden mejor en hexadecimal.
                < 0 or > 0xFFFF => string.Create(CultureInfo.InvariantCulture, $"código {code} (0x{unchecked((uint)code):X8})"),
                _ => string.Create(CultureInfo.InvariantCulture, $"código {code}"),
            });
        }

        if (parts.Count > 0) return string.Join(" · ", parts);
        return submission.Trigger == FeedbackTrigger.Manual ? "Enviada desde «Enviar opinión»" : "—";
    }

    /// <summary>«Launcher 1.0.3 · Windows 10.0.26200».</summary>
    private static string BuildFooter(FeedbackSubmission submission)
    {
        var launcher = FeedbackText.Clean(submission.LauncherVersion, 40, multiline: false) ?? "?";
        var os = FeedbackText.Clean(submission.OsDescription, 150, multiline: false);
        if (os is not null && os.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) && os.Length > "Microsoft ".Length)
            os = os["Microsoft ".Length..];
        return os is null ? $"Launcher {launcher}" : $"Launcher {launcher} · {os}";
    }

    /// <summary>
    /// Límite total de Discord: si nombres + valores superan <paramref name="budget"/>, recorta con «…» los más largos
    /// hasta un mismo tope (los cortos quedan intactos).
    /// </summary>
    private static void FitToTotalLimit(List<EmbedField> fields, int budget)
    {
        var total = fields.Sum(f => f.Name.Length + f.Value.Length);
        if (total <= budget) return;

        // Mayor tope C tal que sum(min(len, C)) <= budget (búsqueda binaria).
        var lengths = fields.SelectMany(f => new[] { f.Name.Length, f.Value.Length }).ToArray();
        int lo = 1, hi = lengths.Max();
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (lengths.Sum(len => Math.Min(len, mid)) <= budget) lo = mid;
            else hi = mid - 1;
        }

        foreach (var field in fields)
        {
            field.Name = FeedbackText.Truncate(field.Name, lo);
            field.Value = FeedbackText.Truncate(field.Value, lo);
        }
    }
}
