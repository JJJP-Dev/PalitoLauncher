using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Net;

namespace PalitoLauncher.Core.Missions;

/// <summary>Datos del jugador y del launcher para el mensaje de resultados.</summary>
public sealed class MissionReportContext
{
    /// <summary>Nombre del jugador (el de las opiniones: su nombre de Discord). Null = anónimo.</summary>
    public string? Nickname { get; init; }

    public string LauncherVersion { get; init; } = "";

    /// <summary>Versión instalada del juego, para mostrar ("0.0.4 · Prototipo 2").</summary>
    public string? GameVersionDisplay { get; init; }

    /// <summary>Envío final (el conjunto terminó).</summary>
    public bool Final { get; init; }

    public DateTimeOffset NowUtc { get; init; } = DateTimeOffset.UtcNow;
}

public enum MissionSendStatus
{
    /// <summary>Publicado o actualizado en Discord.</summary>
    Sent,

    /// <summary>No hay un webhook válido: se enviará cuando lo haya.</summary>
    NoWebhook,

    /// <summary>Discord pidió esperar (429).</summary>
    RateLimited,

    /// <summary>El webhook se borró o no es válido (401/403/404 al publicar).</summary>
    WebhookInvalid,

    /// <summary>Discord rechazó el mensaje (400/413/422): no tiene sentido reintentar el mismo.</summary>
    Rejected,

    /// <summary>Error de red o 5xx: se reintentará.</summary>
    Failed,
}

/// <summary>Resultado de un envío. <see cref="MessageId"/> es el mensaje de Discord (nuevo o el mismo de antes).</summary>
public sealed record MissionSendResult(MissionSendStatus Status, string? MessageId, TimeSpan? RetryAfter = null);

/// <summary>
/// Publica los resultados de las misiones de un jugador en Discord como UN mensaje que se va editando: si ya hay un
/// mensaje, PATCH {webhook}/messages/{id}; si no (o Discord dice que ya no existe), POST {webhook}?wait=true, que devuelve
/// el id del mensaje nuevo. Mismo HttpClient y mismas reglas de respuesta que FeedbackService. El estado (qué está
/// pendiente) lo lleva el llamador en MissionSetState: este servicio no guarda nada. Un envío a la vez.
/// </summary>
public sealed class MissionReportService
{
    internal const string Username = LauncherConfig.GameDisplayName + " · Misiones";

    internal const int MaxEmbedTitleChars = 256;
    internal const int MaxDescriptionChars = 1000;
    internal const int MaxFieldValueChars = 1024;
    internal const int MaxFooterChars = 256;
    private const int MaxNicknameChars = 100;
    private const int MaxResponseBodyBytes = 64 * 1024;

    internal const int ColorGood = 0x3DDC97;
    internal const int ColorBad = 0xFF5C6C;
    internal const int ColorInProgress = 0x5AA9FF;

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly bool _addUserAgent;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public MissionReportService(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _addUserAgent = !http.DefaultRequestHeaders.Contains("User-Agent");
    }

    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Webhook sin el token ("discord.com/123"), para saber si el mensaje guardado es de este webhook. Null si no es válido.</summary>
    public static string? WebhookKey(string? webhook)
    {
        var normalized = SurveyProvider.NormalizeDiscordWebhook(webhook);
        if (normalized is null) return null;
        var (baseUrl, query) = Split(normalized);
        var parts = new Uri(baseUrl).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries); // api, webhooks, id, token
        return $"{new Uri(baseUrl).Host}/{(parts.Length >= 3 ? parts[2] : "?")}{query}";
    }

    /// <summary>
    /// Publica o actualiza el mensaje. <paramref name="payload"/> es el de <see cref="BuildPayload"/>. Nunca lanza salvo
    /// OperationCanceledException si se canceló <paramref name="ct"/>.
    /// </summary>
    public async Task<MissionSendResult> SendAsync(string payload, string? webhook, string? messageId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var normalized = SurveyProvider.NormalizeDiscordWebhook(webhook);
        if (normalized is null) return new MissionSendResult(MissionSendStatus.NoWebhook, messageId);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (baseUrl, query) = Split(normalized);

            if (!string.IsNullOrEmpty(messageId) && IsSnowflake(messageId))
            {
                var patch = await SendCoreAsync(HttpMethod.Patch, $"{baseUrl}/messages/{messageId}{query}", payload, ct).ConfigureAwait(false);
                if (patch.Status != 404)
                    return ToResult(patch, messageId, isPatch: true);
                Log.Info("Misiones: el mensaje de resultados ya no existe en Discord; se publica uno nuevo.");
            }

            var postUrl = baseUrl + "?wait=true" + (query.Length > 0 ? "&" + query[1..] : "");
            var post = await SendCoreAsync(HttpMethod.Post, postUrl, WithUsername(payload), ct).ConfigureAwait(false);
            var newId = post.Status is >= 200 and <= 299 ? ReadMessageId(post.Body) : null;
            return ToResult(post, newId, isPatch: false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>JSON del mensaje (embeds + allowed_mentions; el username se añade al publicar). Puro, para tests.</summary>
    public static string BuildPayload(MissionSet set, MissionSetState state, MissionReportContext context)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);

        var done = new List<string>();
        var failed = new List<string>();
        var pending = new List<string>();
        var skipped = new List<string>();

        foreach (var mission in set.Missions)
        {
            state.Missions.TryGetValue(mission.Id, out var p);
            var missionTitle = Escape(mission.Title);
            var note = Escape(FeedbackText.Clean(p?.Note, MissionNotes.MaxNoteLength, multiline: false));
            var noteLine = note is null ? "" : $"\n  ↳ {note}";
            switch (p?.State ?? MissionState.Pending)
            {
                case MissionState.Done:
                {
                    var details = new List<string>();
                    if (p!.CompletedUtc is { } completed && completed.Year is >= 2015 and <= 9000)
                        details.Add($"<t:{completed.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}:f>");
                    if (!p.Automatic) details.Add("marcada a mano");
                    if (p.WithCheats) details.Add("con trucos");
                    if (p.CheckAnswer == true) details.Add("se vio bien");
                    if (p.CheckAnswer == false) details.Add("**se vio mal**");
                    if (p.ErrorsNearby > 0) details.Add($"⚠ {p.ErrorsNearby} error{(p.ErrorsNearby == 1 ? "" : "es")} en esa partida");
                    done.Add($"• {missionTitle}{(details.Count > 0 ? " — " + string.Join(" · ", details) : "")}{noteLine}");
                    break;
                }
                case MissionState.Failed:
                    failed.Add($"• {missionTitle}{noteLine}");
                    break;
                case MissionState.Skipped:
                    skipped.Add($"• {missionTitle}");
                    break;
                default:
                    var progress = MissionEvaluator.ProgressText(mission, p);
                    pending.Add($"• {missionTitle}{(progress is null ? "" : $" ({progress})")}");
                    break;
            }
        }

        var total = set.Missions.Count;
        var name = FeedbackText.Clean(context.Nickname, MaxNicknameChars, multiline: false);
        var who = name ?? $"Jugador anónimo #{(state.ReporterId.Length >= 4 ? state.ReporterId[..4] : state.ReporterId)}";
        var title = FeedbackText.Truncate($"🧪 {set.Title} · {who}{(context.Final ? " · Final" : "")}", MaxEmbedTitleChars);

        var description = new StringBuilder();
        description.Append($"**{done.Count} de {total}** completadas");
        if (failed.Count > 0) description.Append($" · {failed.Count} con fallos");
        if (skipped.Count > 0) description.Append($" · {skipped.Count} sin probar");
        description.Append('\n').Append(state.SessionsCounted == 1 ? "1 sesión de juego registrada" : $"{state.SessionsCounted} sesiones de juego registradas");
        if (state.SessionsTooOld > 0)
            description.Append($" · {state.SessionsTooOld} con una versión del juego anterior a la necesaria (no cuentan)");

        var fields = new List<(string Name, string Value)>();
        void AddField(string label, List<string> lines)
        {
            if (lines.Count == 0) return;
            fields.Add(($"{label} ({lines.Count})", FeedbackText.Truncate(string.Join("\n", lines), MaxFieldValueChars)));
        }
        AddField("✅ Conseguidas", done);
        AddField("❌ Fallaron", failed);
        AddField("⏳ Pendientes", pending);
        AddField("➖ No las probó", skipped);

        var footer = FeedbackText.Truncate(
            $"Launcher {context.LauncherVersion} · {LauncherConfig.GameDisplayName} {context.GameVersionDisplay ?? "versión desconocida"}" +
            $" · {set.Id} · id {state.ReporterId}", MaxFooterChars);

        var color = failed.Count > 0 ? ColorBad : done.Count == total ? ColorGood : ColorInProgress;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("allowed_mentions");
            writer.WriteStartArray("parse");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartArray("embeds");
            writer.WriteStartObject();
            writer.WriteString("title", title);
            writer.WriteString("description", FeedbackText.Truncate(description.ToString(), MaxDescriptionChars));
            writer.WriteNumber("color", color);
            writer.WriteStartArray("fields");
            foreach (var (fieldName, value) in fields)
            {
                writer.WriteStartObject();
                writer.WriteString("name", fieldName);
                writer.WriteString("value", value);
                writer.WriteBoolean("inline", false);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartObject("footer");
            writer.WriteString("text", footer);
            writer.WriteEndObject();
            writer.WriteString("timestamp", context.NowUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // ───────────────────────────── Interno ─────────────────────────────

    private readonly record struct RawResponse(int Status, string Body, TimeSpan? RetryAfter, bool NetworkError);

    private async Task<RawResponse> SendCoreAsync(HttpMethod method, string url, string payload, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(payload, new UTF8Encoding(false), "application/json"),
        };
        if (_addUserAgent) request.Headers.TryAddWithoutValidation("User-Agent", HttpFactory.UserAgent);

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var body = await ReadBodyAsync(response.Content, timeoutCts.Token).ConfigureAwait(false);
            TimeSpan? retry = status == 429 ? GetRetryAfter(response, body) : null;
            return new RawResponse(status, body, retry, NetworkError: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
        {
            Log.Warn("Misiones: no se pudo conectar con Discord; los resultados se enviarán más tarde.", ex);
            return new RawResponse(0, "", null, NetworkError: true);
        }
        catch (Exception ex)
        {
            Log.Error("Misiones: error inesperado al enviar los resultados a Discord.", ex);
            return new RawResponse(0, "", null, NetworkError: true);
        }
    }

    private static MissionSendResult ToResult(RawResponse r, string? messageId, bool isPatch)
    {
        if (r.NetworkError) return new MissionSendResult(MissionSendStatus.Failed, isPatch ? messageId : null);
        var verb = isPatch ? "actualizar" : "publicar";
        switch (r.Status)
        {
            case >= 200 and <= 299:
                if (messageId is null) Log.Warn("Misiones: Discord no devolvió el id del mensaje; el próximo envío creará uno nuevo.");
                Log.Info($"Misiones: resultados {(isPatch ? "actualizados" : "publicados")} en Discord ({r.Status}).");
                return new MissionSendResult(MissionSendStatus.Sent, messageId);
            case 429:
                Log.Warn($"Misiones: Discord pidió esperar (429) al {verb} los resultados.");
                return new MissionSendResult(MissionSendStatus.RateLimited, isPatch ? messageId : null, r.RetryAfter ?? DefaultRetryAfter);
            case 400 or 413 or 422:
                Log.Error($"Misiones: Discord rechazó los resultados al {verb} (código {r.Status}): {FeedbackText.Truncate(r.Body, 1000)}");
                return new MissionSendResult(MissionSendStatus.Rejected, isPatch ? messageId : null);
            case 401 or 403 or 404:
                Log.Warn($"Misiones: el webhook de Discord no es válido o se borró (código {r.Status}).");
                return new MissionSendResult(MissionSendStatus.WebhookInvalid, null);
            default:
                Log.Warn($"Misiones: Discord respondió con el código {r.Status} al {verb} los resultados.");
                return new MissionSendResult(MissionSendStatus.Failed, isPatch ? messageId : null);
        }
    }

    private static (string BaseUrl, string Query) Split(string normalized)
    {
        var q = normalized.IndexOf('?');
        return q < 0 ? (normalized, "") : (normalized[..q], normalized[q..]);
    }

    private static bool IsSnowflake(string id) => id.Length is >= 1 and <= 25 && id.All(char.IsAsciiDigit);

    private static string WithUsername(string payload)
    {
        try
        {
            if (JsonNode.Parse(payload) is JsonObject obj)
            {
                obj["username"] = Username;
                return obj.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // No debería pasar (lo genera BuildPayload): se envía tal cual.
        }
        return payload;
    }

    private static string? ReadMessageId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String && id.GetString() is { } text && IsSnowflake(text))
            {
                return text;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    /// <summary>Neutraliza el formato de Discord en textos que vienen del JSON o del jugador (*, _, `, ~, |, &gt;, enlaces).</summary>
    internal static string? Escape(string? text)
    {
        if (text is null) return null;
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '*' or '_' or '`' or '~' or '|' or '>' or '[' or ']' or '\\' or '<' or '#') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct)
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

    private static TimeSpan GetRetryAfter(HttpResponseMessage response, string body)
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
        }
        if (wait is null && response.Headers.RetryAfter?.Delta is { } delta) wait = delta;
        var value = wait ?? DefaultRetryAfter;
        return value < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : value > MaxRetryAfter ? MaxRetryAfter : value;
    }
}
