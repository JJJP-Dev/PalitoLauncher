using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Logging;

namespace PalitoLauncher.Core.Feedback;

/// <summary>
/// Obtiene la encuesta: launcher/feedback.json del repo del juego (LauncherConfig.RemoteFeedbackConfigUrl, vía
/// GitHubClient.GetCachedTextFileAsync con caché/ETag; no gasta cupo de la API) o, si no existe / no es válida, la encuesta
/// por defecto. Para probar sin publicar nada, un archivo local &lt;AppPaths.AppDataDir&gt;\feedback.local.json tiene prioridad
/// sobre el remoto. Thread-safe.
/// </summary>
public sealed class SurveyProvider
{
    public const string LocalOverrideFileName = "feedback.local.json";

    /// <summary>Nombre del archivo de caché del feedback.json remoto (dentro de la caché del GitHubClient).</summary>
    internal const string RemoteCacheFileName = "feedback.json";

    /// <summary>Tamaño máximo del archivo local (el remoto lo limita GitHubClient a 256 KB).</summary>
    internal const int MaxLocalFileBytes = 256 * 1024;

    /// <summary>Por encima de esto ni se intenta parsear (protege ante archivos absurdos).</summary>
    internal const int MaxJsonChars = 1024 * 1024;

    internal const string DefaultTitle = "Tu opinión sobre " + LauncherConfig.GameDisplayName;

    internal const int MaxQuestions = 12;
    internal const int MaxOptions = 10;
    internal const int MaxTitleLength = 120;
    internal const int MaxIntroLength = 300;
    internal const int MaxQuestionTextLength = 200;
    internal const int MaxHintLength = 200;
    internal const int MaxOptionLength = 80;
    internal const int MaxLabelLength = 60;
    internal const int MaxPlaceholderLength = 200;
    internal const int MaxIdLength = 64;
    internal const int MaxTextAnswerLength = 1000;

    // Límites de lectura en bruto (antes de sanear) para no procesar listas enormes.
    private const int MaxRawQuestions = 500;
    private const int MaxRawOptions = 100;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 32,
    };

    private static readonly HashSet<string> DiscordHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord.com", "discordapp.com", "ptb.discord.com", "canary.discord.com",
    };

    // id = snowflake (solo dígitos); token = base64url.
    private static readonly Regex WebhookPathRegex = new(@"^/api/webhooks/(?<id>[0-9]{1,25})/(?<token>[A-Za-z0-9_\-]{1,200})/?$",
        RegexOptions.CultureInvariant);

    private static readonly Regex ThreadIdRegex = new(@"(?:^|&)thread_id=(?<tid>[0-9]{1,25})(?:&|$)", RegexOptions.CultureInvariant);

    private readonly GitHubClient _github;
    private readonly string _appDataDir;

    // Webhook del último feedback.json descargado y rechazado (p.ej. webhook válido pero ninguna pregunta válida). El archivo
    // no se guarda en caché (un archivo roto no sustituye a la última copia buena), pero la encuesta por defecto conserva su
    // webhook. Call = valor de _surveyCalls al validar: así también lo ve una llamada que se une a la descarga en curso, y
    // una llamada posterior (p.ej. 404 o sin red) no reutiliza un rechazo antiguo.
    private long _surveyCalls;
    private RejectedRemote? _lastRejectedRemote;

    private sealed record RejectedRemote(long Call, string? Webhook, string? Problem);

    public SurveyProvider(GitHubClient github, string appDataDir)
    {
        ArgumentNullException.ThrowIfNull(github);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDir);
        _github = github;
        _appDataDir = Path.GetFullPath(appDataDir);
    }

    /// <summary>Ruta del archivo local de pruebas.</summary>
    internal string LocalOverridePath => Path.Combine(_appDataDir, LocalOverrideFileName);

    /// <summary>
    /// Devuelve la encuesta efectiva (local → remota → por defecto), ya validada y saneada con <see cref="Sanitize"/>.
    /// Nunca lanza (salvo OperationCanceledException si se canceló <paramref name="ct"/>); ante cualquier problema registra
    /// un Warn y devuelve la encuesta por defecto (conservando, si la había, una webhook válida del archivo local o de la
    /// remota aunque sus preguntas no sirvan).
    /// </summary>
    public async Task<SurveyDefinition> GetSurveyAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string? fallbackWebhook = null;
        var call = Interlocked.Increment(ref _surveyCalls);

        try
        {
            // 1) Archivo local (pruebas del desarrollador): si es válido, ni se pregunta al remoto.
            var localText = await ReadLocalOverrideAsync(ct).ConfigureAwait(false);
            if (localText is not null)
            {
                var local = Parse(localText, out var problem);
                if (local is not null)
                {
                    Log.Info($"Encuesta de feedback: se usa el archivo local {LocalOverrideFileName} " +
                             $"(«{local.Id}», {local.Questions.Count} preguntas, webhook: {DescribeWebhook(local.DiscordWebhookUrl)}).");
                    return local;
                }
                Log.Warn($"Encuesta de feedback: {LocalOverrideFileName} no se puede usar ({problem}); se ignora.");
                fallbackWebhook = ExtractWebhook(localText);
            }

            // 2) feedback.json del repo del juego (raw.githubusercontent.com, con caché y ETag).
            var remoteText = await _github.GetCachedTextFileAsync(LauncherConfig.RemoteFeedbackConfigUrl, RemoteCacheFileName,
                ValidateRemote, ct).ConfigureAwait(false);
            var rejected = Volatile.Read(ref _lastRejectedRemote);
            if (remoteText is null && rejected is not null && rejected.Call >= call)
            {
                // Descargado en esta llamada pero rechazado (y sin copia buena en caché): encuesta por defecto con su webhook.
                fallbackWebhook ??= rejected.Webhook;
                if (fallbackWebhook is not null)
                    Log.Warn($"Encuesta de feedback: launcher/feedback.json no trae preguntas válidas ({rejected.Problem}); " +
                             "se usa la encuesta por defecto con su webhook.");
                else
                    Log.Warn($"Encuesta de feedback: launcher/feedback.json no se puede usar ({rejected.Problem}); se usa la encuesta por defecto.");
            }
            else if (remoteText is not null)
            {
                var remote = Parse(remoteText, out var problem);
                if (remote is not null)
                {
                    Log.Info($"Encuesta de feedback: «{remote.Id}» ({remote.Questions.Count} preguntas, " +
                             $"webhook: {DescribeWebhook(remote.DiscordWebhookUrl)}).");
                    return remote;
                }

                fallbackWebhook ??= ExtractWebhook(remoteText);
                if (fallbackWebhook is not null)
                    Log.Info("Encuesta de feedback: launcher/feedback.json no trae preguntas válidas; se usa la encuesta por defecto con su webhook.");
                else
                    Log.Warn($"Encuesta de feedback: launcher/feedback.json no se puede usar ({problem}); se usa la encuesta por defecto.");
            }
            else
            {
                Log.Info("Encuesta de feedback: no hay launcher/feedback.json (o no se pudo descargar); se usa la encuesta por defecto.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Encuesta de feedback: no se pudo obtener; se usa la encuesta por defecto.", ex);
        }

        var survey = CreateDefault();
        survey.DiscordWebhookUrl = fallbackWebhook;
        return survey;
    }

    /// <summary>
    /// Encuesta por defecto (español), ~1 minuto, pensada para playtests:
    /// overall (Rating, obligatoria) «En general, ¿qué tal la partida?»;
    /// again (Choice, obligatoria) «¿Te apetece volver a jugar?»: «Sí, otra partida ya» / «Sí, otro día» / «No mucho»;
    /// difficulty (Scale 1..5, «Muy fácil» … «Muy difícil») «¿Qué tal la dificultad?»;
    /// players (Choice) «¿Cuántos jugabais?»: «Solo» / «2» / «3» / «4»;
    /// issues (Multi) «¿Tuviste algún problema?»: «Lag o desconexiones» / «El juego se cerró» / «Va lento o a tirones» /
    ///   «Fallos visuales» / «Controles incómodos» / «Ninguno»;
    /// best (Text) «¿Qué fue lo mejor?»; improve (Text) «¿Qué cambiarías o mejorarías?».
    /// Sin webhook. Id "default".
    /// </summary>
    public static SurveyDefinition CreateDefault() => new()
    {
        Id = "default",
        Title = DefaultTitle,
        Intro = "Cuéntanos qué tal te ha ido. Lo leemos todo y nos ayuda a decidir qué mejorar.",
        AskAfterSession = true,
        MinSessionMinutes = 5,
        MinHoursBetweenPrompts = 12,
        DiscordWebhookUrl = null,
        Questions =
        {
            new SurveyQuestion
            {
                Id = "overall", Type = SurveyQuestionType.Rating, Required = true,
                Text = "En general, ¿qué tal la partida?",
            },
            new SurveyQuestion
            {
                Id = "again", Type = SurveyQuestionType.Choice, Required = true,
                Text = "¿Te apetece volver a jugar?",
                Options = { "Sí, otra partida ya", "Sí, otro día", "No mucho" },
            },
            new SurveyQuestion
            {
                Id = "difficulty", Type = SurveyQuestionType.Scale,
                Text = "¿Qué tal la dificultad?",
                Min = 1, Max = 5, MinLabel = "Muy fácil", MaxLabel = "Muy difícil",
            },
            new SurveyQuestion
            {
                Id = "players", Type = SurveyQuestionType.Choice,
                Text = "¿Cuántos jugabais?",
                Options = { "Solo", "2", "3", "4" },
            },
            new SurveyQuestion
            {
                Id = "issues", Type = SurveyQuestionType.Multi,
                Text = "¿Tuviste algún problema?",
                Hint = "Marca todos los que quieras.",
                Options =
                {
                    "Lag o desconexiones", "El juego se cerró", "Va lento o a tirones", "Fallos visuales",
                    "Controles incómodos", "Ninguno",
                },
            },
            new SurveyQuestion
            {
                Id = "best", Type = SurveyQuestionType.Text,
                Text = "¿Qué fue lo mejor?",
                MaxLength = MaxTextAnswerLength,
                Placeholder = "Un momento, una mecánica, algo que te hizo gracia…",
            },
            new SurveyQuestion
            {
                Id = "improve", Type = SurveyQuestionType.Text,
                Text = "¿Qué cambiarías o mejorarías?",
                MaxLength = MaxTextAnswerLength,
                Placeholder = "Lo que te frustró, lo que echaste de menos…",
            },
        },
    };

    /// <summary>
    /// Parsea JSON (camelCase, comentarios y comas finales permitidos, tipos como texto sin distinguir mayúsculas).
    /// Devuelve null si no es JSON válido o no tiene ninguna pregunta válida tras sanear. No lanza.
    /// </summary>
    public static SurveyDefinition? Parse(string json) => Parse(json, out _);

    /// <summary>Igual que <see cref="Parse(string)"/>; en <paramref name="problem"/> el motivo (para el log) si devuelve null.</summary>
    internal static SurveyDefinition? Parse(string? json, out string? problem)
    {
        problem = null;
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                problem = "está vacío";
                return null;
            }
            if (json.Length > MaxJsonChars)
            {
                problem = "es demasiado grande";
                return null;
            }

            using var doc = JsonDocument.Parse(StripBom(json), DocumentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                problem = "no es un objeto JSON";
                return null;
            }

            var survey = Sanitize(ReadSurvey(doc.RootElement));
            if (survey.Questions.Count == 0)
            {
                problem = "no tiene ninguna pregunta válida";
                return null;
            }
            return survey;
        }
        catch (JsonException ex)
        {
            problem = "no es JSON válido: " + ex.Message;
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("Error inesperado al leer la encuesta de feedback.", ex);
            problem = "error inesperado";
            return null;
        }
    }

    /// <summary>
    /// Sanea in-place y devuelve la misma instancia: recorta textos a sus máximos, descarta preguntas sin Id válido / sin
    /// texto / de tipo desconocido / con Id repetido / Choice-Multi con menos de 2 opciones (opciones vacías o repetidas
    /// fuera, máx. 10), limita Scale a 0..10 con Min &lt; Max, MaxLength a 1..1000, Questions a 12, MinSessionMinutes a
    /// 1..120, MinHoursBetweenPrompts a 0..168, título vacío → "Tu opinión sobre Coop2D". DiscordWebhookUrl solo se conserva
    /// si es https y su host es discord.com / discordapp.com / ptb.discord.com / canary.discord.com con ruta
    /// /api/webhooks/{id}/{token}; si no, null.
    /// </summary>
    public static SurveyDefinition Sanitize(SurveyDefinition survey)
    {
        ArgumentNullException.ThrowIfNull(survey);

        survey.Id = CleanSurveyId(survey.Id);
        survey.Title = FeedbackText.Clean(survey.Title, MaxTitleLength, multiline: false) ?? DefaultTitle;
        survey.Intro = FeedbackText.Clean(survey.Intro, MaxIntroLength, multiline: true);
        survey.MinSessionMinutes = Math.Clamp(survey.MinSessionMinutes, 1, 120);
        survey.MinHoursBetweenPrompts = Math.Clamp(survey.MinHoursBetweenPrompts, 0, 168);
        survey.DiscordWebhookUrl = NormalizeDiscordWebhook(survey.DiscordWebhookUrl);

        var source = survey.Questions ?? new List<SurveyQuestion>();
        var kept = new List<SurveyQuestion>(Math.Min(source.Count, MaxQuestions));
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discarded = new List<string>();

        foreach (var question in source)
        {
            if (question is null)
            {
                discarded.Add("pregunta vacía");
                continue;
            }

            var reason = SanitizeQuestion(question);
            // Si es válida, SanitizeQuestion ya dejó el Id recortado (nunca null).
            if (reason is null && !ids.Add(question.Id!))
                reason = "Id repetido";
            if (reason is not null)
            {
                discarded.Add($"«{FeedbackText.Truncate(question.Id ?? "", 40)}»: {reason}");
                continue;
            }

            if (kept.Count >= MaxQuestions)
            {
                discarded.Add($"«{question.Id}»: más de {MaxQuestions} preguntas");
                continue;
            }
            kept.Add(question);
        }

        survey.Questions = kept;
        if (discarded.Count > 0)
        {
            Log.Warn($"Encuesta de feedback «{survey.Id}»: se descartan {discarded.Count} preguntas: " +
                     string.Join("; ", discarded.Take(8)) + (discarded.Count > 8 ? "; …" : "") + ".");
        }
        return survey;
    }

    /// <summary>True si la URL es un webhook de Discord con el formato aceptado por <see cref="Sanitize"/>.</summary>
    public static bool IsValidDiscordWebhook(string? url) => NormalizeDiscordWebhook(url) is not null;

    /// <summary>
    /// Webhook en forma canónica ("https://discord.com/api/webhooks/{id}/{token}", host en minúsculas, sin barra final ni
    /// fragmento; solo se conserva el parámetro thread_id) o null si no es un webhook de Discord válido.
    /// </summary>
    internal static string? NormalizeDiscordWebhook(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var trimmed = url.Trim();
        if (trimmed.Length > 2048) return null;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;
        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        if (!DiscordHosts.Contains(uri.Host)) return null;

        var match = WebhookPathRegex.Match(uri.AbsolutePath);
        if (!match.Success) return null;

        var normalized = $"https://{uri.Host.ToLowerInvariant()}/api/webhooks/{match.Groups["id"].Value}/{match.Groups["token"].Value}";
        var query = uri.Query.TrimStart('?');
        if (query.Length > 0 && ThreadIdRegex.Match(query) is { Success: true } thread)
            normalized += "?thread_id=" + thread.Groups["tid"].Value;
        return normalized;
    }

    /// <summary>Webhook para el log sin el token (la URL completa permite publicar en el canal).</summary>
    internal static string DescribeWebhook(string? url)
    {
        var normalized = NormalizeDiscordWebhook(url);
        if (normalized is null) return string.IsNullOrWhiteSpace(url) ? "ninguno" : "no válido";
        var uri = new Uri(normalized);
        var match = WebhookPathRegex.Match(uri.AbsolutePath);
        return $"{uri.Host}/api/webhooks/{match.Groups["id"].Value}/***";
    }

    // ───────────────────────────── Interno ─────────────────────────────

    private async Task<string?> ReadLocalOverrideAsync(CancellationToken ct)
    {
        var path = LocalOverridePath;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (info.Length > MaxLocalFileBytes)
            {
                Log.Warn($"Encuesta de feedback: {LocalOverrideFileName} ocupa {info.Length} bytes (máx. {MaxLocalFileBytes}); se ignora.");
                return null;
            }

            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096,
                useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"Encuesta de feedback: no se pudo leer {path}; se ignora.", ex);
            return null;
        }
    }

    /// <summary>
    /// Validador que se pasa a GitHubClient: <see cref="IsAcceptableRemote"/> y, si rechaza el texto, apunta su webhook
    /// (aunque sea null) y el motivo en <see cref="_lastRejectedRemote"/> para que <see cref="GetSurveyAsync"/> lo use.
    /// </summary>
    private bool ValidateRemote(string text)
    {
        if (IsAcceptableRemote(text)) return true;
        Parse(text, out var problem);
        Volatile.Write(ref _lastRejectedRemote,
            new RejectedRemote(Interlocked.Read(ref _surveyCalls), ExtractWebhook(text), problem ?? "no es válido"));
        return false;
    }

    /// <summary>
    /// Validación del feedback.json descargado: se acepta si tiene preguntas válidas, o si solo trae un webhook válido sin
    /// lista de preguntas (encuesta por defecto + webhook). Un archivo roto (aunque traiga webhook) no sustituye a la última
    /// copia buena; si no hay copia buena, <see cref="ValidateRemote"/> guarda su webhook para la encuesta por defecto.
    /// </summary>
    private static bool IsAcceptableRemote(string text)
    {
        if (Parse(text, out _) is not null) return true;
        try
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxJsonChars) return false;
            using var doc = JsonDocument.Parse(StripBom(text), DocumentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var props = ReadProperties(doc.RootElement);
            var noQuestions = !props.TryGetValue("questions", out var questions)
                              || questions.ValueKind == JsonValueKind.Null
                              || (questions.ValueKind == JsonValueKind.Array && questions.GetArrayLength() == 0);
            return noQuestions && NormalizeDiscordWebhook(GetString(props, "discordWebhookUrl")) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Webhook válido de un JSON aunque sus preguntas no sirvan (o null). No lanza.</summary>
    internal static string? ExtractWebhook(string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonChars) return null;
            using var doc = JsonDocument.Parse(StripBom(json), DocumentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return NormalizeDiscordWebhook(GetString(ReadProperties(doc.RootElement), "discordWebhookUrl"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SurveyDefinition ReadSurvey(JsonElement root)
    {
        var props = ReadProperties(root);
        var survey = new SurveyDefinition
        {
            Id = GetString(props, "id") ?? "",
            Title = GetString(props, "title") ?? "",
            Intro = GetString(props, "intro"),
            AskAfterSession = GetBool(props, "askAfterSession") ?? true,
            MinSessionMinutes = GetInt(props, "minSessionMinutes") ?? 5,
            MinHoursBetweenPrompts = GetInt(props, "minHoursBetweenPrompts") ?? 12,
            DiscordWebhookUrl = GetString(props, "discordWebhookUrl"),
        };

        if (props.TryGetValue("questions", out var questions) && questions.ValueKind == JsonValueKind.Array)
        {
            var count = 0;
            foreach (var element in questions.EnumerateArray())
            {
                if (++count > MaxRawQuestions) break;
                // Cualquier cosa que no sea un objeto cuenta como pregunta vacía (se descarta al sanear).
                survey.Questions.Add(element.ValueKind == JsonValueKind.Object ? ReadQuestion(element) : null!);
            }
        }
        return survey;
    }

    private static SurveyQuestion ReadQuestion(JsonElement element)
    {
        var props = ReadProperties(element);
        var question = new SurveyQuestion
        {
            Id = GetString(props, "id") ?? "",
            Type = ParseType(props.TryGetValue("type", out var type) ? type : default),
            Text = GetString(props, "text") ?? "",
            Hint = GetString(props, "hint"),
            Required = GetBool(props, "required") ?? false,
            Min = GetInt(props, "min") ?? 1,
            Max = GetInt(props, "max") ?? 5,
            MinLabel = GetString(props, "minLabel"),
            MaxLabel = GetString(props, "maxLabel"),
            MaxLength = GetInt(props, "maxLength") ?? MaxTextAnswerLength,
            Placeholder = GetString(props, "placeholder"),
        };

        if (props.TryGetValue("options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            var count = 0;
            foreach (var option in options.EnumerateArray())
            {
                if (++count > MaxRawOptions) break;
                if (ElementToString(option) is { } text) question.Options.Add(text);
            }
        }
        return question;
    }

    /// <summary>Tipo como texto ("rating", "Scale"…); cualquier otra cosa → valor fuera del enum (se descarta al sanear).</summary>
    private static SurveyQuestionType ParseType(JsonElement element)
    {
        const SurveyQuestionType unknown = (SurveyQuestionType)(-1);
        if (element.ValueKind != JsonValueKind.String) return unknown;
        var text = ElementToString(element)?.Trim();
        // Solo nombres (Enum.TryParse aceptaría también números como "2").
        if (string.IsNullOrEmpty(text) || !text.All(char.IsAsciiLetter)) return unknown;
        return Enum.TryParse<SurveyQuestionType>(text, ignoreCase: true, out var value) && Enum.IsDefined(value) ? value : unknown;
    }

    /// <summary>Sanea una pregunta in-place. Devuelve el motivo para descartarla o null si es válida.</summary>
    private static string? SanitizeQuestion(SurveyQuestion q)
    {
        var id = q.Id?.Trim() ?? "";
        if (!IsValidQuestionId(id)) return "Id no válido";
        q.Id = id;

        if (!Enum.IsDefined(q.Type)) return "tipo desconocido";

        var text = FeedbackText.Clean(q.Text, MaxQuestionTextLength, multiline: false);
        if (text is null) return "sin texto";
        q.Text = text;
        q.Hint = FeedbackText.Clean(q.Hint, MaxHintLength, multiline: false);

        // Opciones: solo Choice/Multi; vacías y repetidas fuera, máx. 10.
        var options = new List<string>();
        if (q.Type is SurveyQuestionType.Choice or SurveyQuestionType.Multi)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in q.Options ?? new List<string>())
            {
                var option = FeedbackText.Clean(raw, MaxOptionLength, multiline: false);
                if (option is null || !seen.Add(option)) continue;
                options.Add(option);
                if (options.Count >= MaxOptions) break;
            }
            if (options.Count < 2) return "menos de 2 opciones";
        }
        q.Options = options;

        // Escala: 0..10 con Min < Max (al revés → se intercambian; iguales → 1..5).
        if (q.Type == SurveyQuestionType.Scale)
        {
            var min = Math.Clamp(q.Min, 0, 10);
            var max = Math.Clamp(q.Max, 0, 10);
            if (min > max) (min, max) = (max, min);
            if (min == max) (min, max) = (1, 5);
            q.Min = min;
            q.Max = max;
            q.MinLabel = FeedbackText.Clean(q.MinLabel, MaxLabelLength, multiline: false);
            q.MaxLabel = FeedbackText.Clean(q.MaxLabel, MaxLabelLength, multiline: false);
        }
        else
        {
            q.Min = 1;
            q.Max = 5;
            q.MinLabel = null;
            q.MaxLabel = null;
        }

        q.MaxLength = Math.Clamp(q.MaxLength, 1, MaxTextAnswerLength);
        q.Placeholder = q.Type == SurveyQuestionType.Text
            ? FeedbackText.Clean(q.Placeholder, MaxPlaceholderLength, multiline: false)
            : null;
        return null;
    }

    /// <summary>Letras, números, '-' y '_' (1..64 caracteres).</summary>
    internal static bool IsValidQuestionId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength) return false;
        foreach (var c in id)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;
        }
        return true;
    }

    /// <summary>Id de la encuesta: se quitan los caracteres no permitidos (letras, números, '-', '_' y '.'); vacío → "default".</summary>
    private static string CleanSurveyId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "default";
        var sb = new StringBuilder(Math.Min(id.Length, MaxIdLength));
        foreach (var c in id.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.') sb.Append(c);
            if (sb.Length >= MaxIdLength) break;
        }
        return sb.Length == 0 ? "default" : sb.ToString();
    }

    private static string StripBom(string text) => text.Length > 0 && text[0] == '﻿' ? text[1..] : text;

    /// <summary>Propiedades del objeto sin distinguir mayúsculas (si se repite una, gana la última, como System.Text.Json).</summary>
    private static Dictionary<string, JsonElement> ReadProperties(JsonElement obj)
    {
        var props = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in obj.EnumerateObject()) props[p.Name] = p.Value;
        return props;
    }

    private static string? GetString(Dictionary<string, JsonElement> props, string name)
        => props.TryGetValue(name, out var value) ? ElementToString(value) : null;

    /// <summary>Texto o número (p.ej. opciones 2, 3, 4 escritas sin comillas); cualquier otro tipo → null.</summary>
    private static string? ElementToString(JsonElement element)
    {
        try
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                _ => null,
            };
        }
        catch (InvalidOperationException)
        {
            // Escapes \uD800 sueltos (UTF-16 no válido): ese valor se ignora, no toda la encuesta.
            return null;
        }
    }

    private static bool? GetBool(Dictionary<string, JsonElement> props, string name)
    {
        if (!props.TryGetValue(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(ElementToString(value)?.Trim(), out var b) => b,
            _ => null,
        };
    }

    private static int? GetInt(Dictionary<string, JsonElement> props, string name)
    {
        if (!props.TryGetValue(name, out var value)) return null;
        double number;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt32(out var i)) return i;
            if (!value.TryGetDouble(out number)) return null;
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            if (!double.TryParse(ElementToString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return null;
        }
        else
        {
            return null;
        }

        if (!double.IsFinite(number)) return null;
        number = Math.Round(number, MidpointRounding.AwayFromZero);
        return number >= int.MaxValue ? int.MaxValue : number <= int.MinValue ? int.MinValue : (int)number;
    }
}

/// <summary>Limpieza de textos del feedback (encuesta remota y respuestas) antes de mostrarlos o enviarlos.</summary>
internal static class FeedbackText
{
    /// <summary>
    /// Quita caracteres de control (salvo saltos de línea si <paramref name="multiline"/>; si no, pasan a espacio), marcas
    /// de dirección bidi (pueden falsear el texto mostrado) y surrogates sueltos (no se pueden codificar en JSON); normaliza
    /// \r\n → \n, colapsa más de dos saltos seguidos, recorta espacios y limita a <paramref name="maxLength"/> con «…».
    /// Vacío → null.
    /// </summary>
    public static string? Clean(string? value, int maxLength, bool multiline)
    {
        if (value is null || maxLength <= 0) return null;

        // Cota previa para no recorrer textos enormes enteros.
        var limit = Math.Min(value.Length, Math.Max(maxLength * 2, maxLength + 1024));
        var sb = new StringBuilder(Math.Min(limit, maxLength + 16));
        var newlines = 0;
        for (var i = 0; i < limit; i++)
        {
            var c = value[i];

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    sb.Append(c).Append(value[i + 1]);
                    i++;
                    newlines = 0;
                }
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;

            if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < limit && value[i + 1] == '\n') i++;
                if (!multiline)
                {
                    sb.Append(' ');
                    continue;
                }
                if (++newlines <= 2) sb.Append('\n');
                continue;
            }

            if (c == '\t')
            {
                sb.Append(' ');
                continue;
            }
            if (char.IsControl(c) || IsBidiControl(c)) continue;

            sb.Append(c);
            newlines = 0;
        }

        var text = sb.ToString().Trim();
        return text.Length == 0 ? null : Truncate(text, maxLength);
    }

    /// <summary>Recorta a <paramref name="maxLength"/> caracteres (UTF-16) terminando en «…» sin partir surrogates.</summary>
    public static string Truncate(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= maxLength) return text;
        if (maxLength <= 0) return "";
        if (maxLength == 1) return "…";

        var cut = maxLength - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut].TrimEnd() + "…";
    }

    private static bool IsBidiControl(char c)
        => c is '‎' or '‏' or '؜' || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩');
}
