using System.Globalization;
using System.Text;
using System.Text.Json;
using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Remote;
using PalitoLauncher.Core.Versioning;

namespace PalitoLauncher.Core.Missions;

/// <summary>
/// Obtiene launcher/missions.json del repo del juego (LauncherConfig.RemoteMissionsUrl, vía
/// GitHubClient.GetCachedTextFileAsync con caché/ETag; raw.githubusercontent.com, no gasta cupo de la API). Para probar sin
/// publicar, &lt;AppPaths.AppDataDir&gt;\missions.local.json tiene prioridad. Mismo comportamiento que ManifestProvider: un JSON
/// roto nunca reemplaza al último bueno. Thread-safe.
/// </summary>
public sealed class MissionsProvider
{
    public const string LocalOverrideFileName = "missions.local.json";
    internal const string RemoteCacheFileName = "missions.json";

    internal const string DefaultTitle = "Misiones de prueba";

    internal const int MaxJsonBytes = 256 * 1024;
    internal const int MaxDepth = 32;
    internal const int MaxMissions = 12;
    internal const int MaxGoalsPerMission = 6;
    internal const int MaxConditions = 8;
    internal const int MaxAlternatives = 16;
    internal const int MaxSetTitleLength = 80;
    internal const int MaxIntroLength = 300;
    internal const int MaxMissionTitleLength = 60;
    internal const int MaxHintLength = 200;
    internal const int MaxCheckLength = 120;
    internal const int MaxNameLength = 32;
    internal const int MaxValueLength = 64;
    internal const double MaxCount = 1_000_000;

    /// <summary>Eventos del contrato v1 (docs/juego/EVENTOS-JUEGO.md). Uno desconocido no invalida la misión: solo se avisa.</summary>
    internal static readonly HashSet<string> KnownEvents = new(StringComparer.Ordinal)
    {
        "session_start", "session_end", "host_started", "join_attempt", "join_result", "match_joined", "player_joined",
        "player_left", "match_left", "wave_started", "waves_finished", "element_set", "ability_cast", "reaction",
        "enemy_killed", "cheat_used", "player_died", "player_revived", "hitch", "perf", "net", "log_error",
        "level_up", "upgrade_picked", "offer_action", "match_over", "player_downed", "skill_gained",
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = MaxDepth,
    };

    private static readonly object WarnLock = new();
    private static readonly HashSet<string> WarnedMessages = new(StringComparer.Ordinal);

    private readonly GitHubClient _github;
    private readonly string _appDataDir;
    private long _calls;
    private long _lastRejectedCall;
    private string? _lastGoodText;
    private string? _lastLoggedSummary;

    public MissionsProvider(GitHubClient github, string appDataDir)
    {
        ArgumentNullException.ThrowIfNull(github);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDir);
        _github = github;
        _appDataDir = Path.GetFullPath(appDataDir);
    }

    internal string LocalOverridePath => Path.Combine(_appDataDir, LocalOverrideFileName);

    /// <summary>
    /// Conjunto efectivo (local → remoto), ya saneado, o null si no hay ninguno (404, sin red y sin caché, o roto sin copia
    /// buena). Nunca lanza salvo OperationCanceledException si se canceló <paramref name="ct"/>.
    /// </summary>
    public async Task<MissionSet?> GetMissionSetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var call = Interlocked.Increment(ref _calls);
        try
        {
            var localText = await ReadLocalOverrideAsync(ct).ConfigureAwait(false);
            if (localText is not null)
            {
                var local = Parse(localText);
                if (local is not null)
                {
                    Remember(localText, local, $"se usa el archivo local {LocalOverrideFileName}");
                    return local;
                }
                Log.Warn($"Misiones: {LocalOverrideFileName} no es válido (JSON roto, sin id o sin misiones); se ignora y se usa el de GitHub.");
            }

            var remoteText = await _github.GetCachedTextFileAsync(LauncherConfig.RemoteMissionsUrl, RemoteCacheFileName,
                ValidateRemote, ct).ConfigureAwait(false);
            if (remoteText is not null)
            {
                var remote = Parse(remoteText);
                if (remote is not null)
                {
                    Remember(remoteText, remote, "launcher/missions.json");
                    return remote;
                }
                return LastGood();
            }

            if (Interlocked.Read(ref _lastRejectedCall) >= call)
            {
                Log.Warn("Misiones: launcher/missions.json no es válido y no hay copia buena guardada; no se muestran misiones.");
                return LastGood();
            }

            Volatile.Write(ref _lastGoodText, null);
            LogSummaryIfChanged("Misiones: no hay launcher/missions.json (o no se pudo descargar).");
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Misiones: no se pudo obtener missions.json; se usa el último bueno (o ninguno).", ex);
            return LastGood();
        }
    }

    /// <summary>
    /// Parsea y sanea. Null si no es un objeto JSON válido (máx. 256 KB, profundidad 32), si no tiene un id válido o si no
    /// queda ninguna misión. Una misión con un objetivo mal escrito pasa a manual (y se apunta en el log). No lanza.
    /// </summary>
    public static MissionSet? Parse(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var text = json.Length > 0 && json[0] == '﻿' ? json[1..] : json;
            if (text.Length > MaxJsonBytes || Encoding.UTF8.GetByteCount(text) > MaxJsonBytes) return null;

            using var doc = JsonDocument.Parse(text, DocumentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return ReadSet(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("Error inesperado al leer missions.json.", ex);
            return null;
        }
    }

    // ───────────────────────────── Lectura y saneado ─────────────────────────────

    private static MissionSet? ReadSet(JsonElement root)
    {
        var props = ReadProperties(root);
        var id = GetString(props, "id")?.Trim();
        if (!ManifestProvider.IsValidId(id))
        {
            WarnOnce("Misiones: missions.json no tiene un «id» válido (letras, números, '-', '_' o '.'; máx. 64); se ignora.");
            return null;
        }

        var discarded = new List<string>();
        var missions = new List<Mission>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (props.TryGetValue("missions", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in list.EnumerateArray())
            {
                if (missions.Count >= MaxMissions)
                {
                    discarded.Add($"más de {MaxMissions} misiones (sobran las últimas)");
                    break;
                }
                if (element.ValueKind != JsonValueKind.Object) continue;
                var mission = ReadMission(ReadProperties(element), discarded);
                if (mission is null) continue;
                if (!ids.Add(mission.Id))
                {
                    discarded.Add($"misión «{mission.Id}» repetida");
                    continue;
                }
                missions.Add(mission);
            }
        }

        if (discarded.Count > 0)
            WarnOnce($"Misiones «{id}»: {string.Join("; ", discarded.Take(8))}{(discarded.Count > 8 ? "; …" : "")}.");
        if (missions.Count == 0)
        {
            WarnOnce($"Misiones «{id}»: no hay ninguna misión válida; no se muestra la tarjeta.");
            return null;
        }

        var minText = Clean(GetString(props, "minGameVersion"), 64);
        GameVersion? minVersion = null;
        if (minText is not null && !GameVersion.TryParse(minText, out minVersion))
            WarnOnce($"Misiones «{id}»: minGameVersion «{minText}» no es una versión; se ignora.");

        var starts = GetDate(props, "startsUtc");
        var ends = GetDate(props, "endsUtc");
        if (starts is not null && ends is not null && ends <= starts)
        {
            WarnOnce($"Misiones «{id}»: endsUtc es anterior a startsUtc; se ignora endsUtc.");
            ends = null;
        }

        var webhookText = GetString(props, "discordWebhookUrl");
        WebhookObfuscation.WarnIfPlain(webhookText, "missions.json");
        var webhook = SurveyProvider.NormalizeDiscordWebhook(webhookText);
        if (webhook is null && !string.IsNullOrWhiteSpace(webhookText))
            WarnOnce($"Misiones «{id}»: discordWebhookUrl no es un webhook de Discord válido; se usa el de la encuesta.");

        return new MissionSet
        {
            Id = id!,
            Title = Clean(GetString(props, "title"), MaxSetTitleLength) ?? DefaultTitle,
            Intro = FeedbackText.Clean(GetString(props, "intro"), MaxIntroLength, multiline: true),
            MinGameVersionText = minVersion is null ? null : minText,
            MinGameVersion = minVersion,
            StartsUtc = starts,
            EndsUtc = ends,
            DiscordWebhookUrl = webhook,
            Missions = missions,
        };
    }

    private static Mission? ReadMission(Dictionary<string, JsonElement> p, List<string> discarded)
    {
        var id = GetString(p, "id")?.Trim();
        if (!ManifestProvider.IsValidId(id))
        {
            discarded.Add($"misión sin «id» válido ({Describe(id)})");
            return null;
        }
        var title = Clean(GetString(p, "title"), MaxMissionTitleLength);
        if (title is null)
        {
            discarded.Add($"misión «{id}» sin título");
            return null;
        }

        bool manual = GetBool(p, "manual") ?? false;
        var goals = new List<MissionGoal>();
        bool requireAll = true;
        if (!manual)
        {
            string? problem = null;
            if (p.TryGetValue("goal", out var goalElement) && goalElement.ValueKind == JsonValueKind.Object)
            {
                ReadGoalOrGroup(ReadProperties(goalElement), goals, ref requireAll, ref problem);
            }
            else
            {
                problem = "falta «goal» (o pon \"manual\": true)";
            }

            if (problem is not null || goals.Count == 0)
            {
                discarded.Add($"misión «{id}»: {problem ?? "objetivo vacío"}; pasa a manual");
                goals.Clear();
                requireAll = true;
                manual = true;
            }
            else
            {
                foreach (var g in goals.Where(x => !KnownEvents.Contains(x.Event)))
                    WarnOnce($"Misiones: la misión «{id}» usa el evento «{g.Event}», que no está en el contrato v1 (¿errata?).");
            }
        }

        return new Mission
        {
            Id = id!,
            Title = title,
            Hint = Clean(GetString(p, "hint"), MaxHintLength),
            Manual = manual,
            Check = Clean(GetString(p, "check"), MaxCheckLength),
            AllowCheats = GetBool(p, "allowCheats") ?? false,
            AllowSolo = GetBool(p, "allowSolo") ?? true,
            Goals = goals,
            RequireAllGoals = requireAll,
        };
    }

    /// <summary>Un objetivo, o un grupo «all» / «any» de objetivos (sin anidar).</summary>
    private static void ReadGoalOrGroup(Dictionary<string, JsonElement> p, List<MissionGoal> goals, ref bool requireAll, ref string? problem)
    {
        bool hasAll = p.TryGetValue("all", out var all);
        bool hasAny = p.TryGetValue("any", out var any);
        if (hasAll || hasAny)
        {
            if (hasAll && hasAny)
            {
                problem = "«all» y «any» a la vez";
                return;
            }
            var group = hasAll ? all : any;
            requireAll = hasAll;
            if (group.ValueKind != JsonValueKind.Array)
            {
                problem = $"«{(hasAll ? "all" : "any")}» tiene que ser una lista";
                return;
            }
            foreach (var element in group.EnumerateArray())
            {
                if (goals.Count >= MaxGoalsPerMission)
                {
                    problem = $"más de {MaxGoalsPerMission} objetivos";
                    return;
                }
                if (element.ValueKind != JsonValueKind.Object)
                {
                    problem = "un objetivo del grupo no es un objeto";
                    return;
                }
                var sub = ReadProperties(element);
                if (sub.ContainsKey("all") || sub.ContainsKey("any"))
                {
                    problem = "«all»/«any» no se pueden anidar";
                    return;
                }
                var goal = ReadGoal(sub, ref problem);
                if (goal is null) return;
                goals.Add(goal);
            }
            if (goals.Count == 0) problem = "grupo vacío";
            return;
        }

        var single = ReadGoal(p, ref problem);
        if (single is not null) goals.Add(single);
    }

    private static MissionGoal? ReadGoal(Dictionary<string, JsonElement> p, ref string? problem)
    {
        var name = GetString(p, "event")?.Trim();
        if (!IsValidName(name))
        {
            problem = $"«event» no válido ({Describe(name)})";
            return null;
        }

        var where = new List<FieldCondition>();
        if (p.TryGetValue("where", out var whereElement))
        {
            if (whereElement.ValueKind != JsonValueKind.Object)
            {
                problem = "«where» tiene que ser un objeto";
                return null;
            }
            foreach (var prop in whereElement.EnumerateObject())
            {
                if (where.Count >= MaxConditions)
                {
                    problem = $"más de {MaxConditions} condiciones";
                    return null;
                }
                var condition = ReadCondition(prop.Name, prop.Value, ref problem);
                if (condition is null) return null;
                where.Add(condition);
            }
        }

        double count = 1;
        if (p.TryGetValue("count", out var countElement))
        {
            if (countElement.ValueKind != JsonValueKind.Number || !countElement.TryGetDouble(out count)
                || !double.IsFinite(count) || count <= 0 || count > MaxCount)
            {
                problem = "«count» tiene que ser un número mayor que 0";
                return null;
            }
        }

        string? sum = null;
        if (p.ContainsKey("sum"))
        {
            sum = GetString(p, "sum")?.Trim();
            if (!IsValidName(sum))
            {
                problem = $"«sum» no es un campo válido ({Describe(sum)})";
                return null;
            }
        }

        var scope = MissionScope.Session;
        if (p.ContainsKey("scope"))
        {
            switch (GetString(p, "scope")?.Trim().ToLowerInvariant())
            {
                case "match": scope = MissionScope.Match; break;
                case "session": scope = MissionScope.Session; break;
                case "set": scope = MissionScope.Set; break;
                default:
                    problem = "«scope» tiene que ser match, session o set";
                    return null;
            }
        }

        return new MissionGoal { Event = name!, Where = where, Count = count, Sum = sum, Scope = scope };
    }

    private static FieldCondition? ReadCondition(string field, JsonElement value, ref string? problem)
    {
        field = field.Trim();
        if (!IsValidName(field))
        {
            problem = $"campo de «where» no válido ({Describe(field)})";
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Number:
                return ReadSimpleCondition(field, value, ref problem);

            case JsonValueKind.Array:
            {
                var alternatives = new List<FieldCondition>();
                foreach (var item in value.EnumerateArray())
                {
                    if (alternatives.Count >= MaxAlternatives)
                    {
                        problem = $"«{field}»: más de {MaxAlternatives} valores en la lista";
                        return null;
                    }
                    if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number))
                    {
                        problem = $"«{field}»: la lista solo puede tener textos, números o true/false";
                        return null;
                    }
                    var alternative = ReadSimpleCondition(field, item, ref problem);
                    if (alternative is null) return null;
                    alternatives.Add(alternative);
                }
                if (alternatives.Count == 0)
                {
                    problem = $"«{field}»: lista vacía";
                    return null;
                }
                return new FieldCondition { Field = field, Operator = ConditionOperator.AnyOf, Alternatives = alternatives };
            }

            case JsonValueKind.Object:
            {
                var ops = value.EnumerateObject().ToList();
                if (ops.Count != 1)
                {
                    problem = $"«{field}»: una comparación lleva un solo operador, p.ej. {{ \">=\": 3 }}";
                    return null;
                }
                var (op, operand) = (ops[0].Name.Trim(), ops[0].Value);
                ConditionOperator? kind = op switch
                {
                    ">=" => ConditionOperator.GreaterOrEqual,
                    ">" => ConditionOperator.Greater,
                    "<=" => ConditionOperator.LessOrEqual,
                    "<" => ConditionOperator.Less,
                    "!=" => ConditionOperator.NotEqual,
                    _ => null,
                };
                if (kind is null)
                {
                    problem = $"«{field}»: operador desconocido «{Describe(op)}» (usa >=, >, <=, < o !=)";
                    return null;
                }
                if (kind == ConditionOperator.NotEqual)
                {
                    var simple = ReadSimpleCondition(field, operand, ref problem);
                    if (simple is null) return null;
                    if (simple.Operator == ConditionOperator.Prefix)
                    {
                        problem = $"«{field}»: «!=» no admite comodines";
                        return null;
                    }
                    return new FieldCondition { Field = field, Operator = ConditionOperator.NotEqual, Text = simple.Text, Number = simple.Number };
                }
                if (operand.ValueKind != JsonValueKind.Number || !operand.TryGetDouble(out var number) || !double.IsFinite(number))
                {
                    problem = $"«{field}»: «{op}» compara con un número";
                    return null;
                }
                return new FieldCondition { Field = field, Operator = kind.Value, Number = number };
            }

            default:
                problem = $"«{field}»: valor no admitido";
                return null;
        }
    }

    /// <summary>Texto ("valor" o "prefijo*"), número o booleano.</summary>
    private static FieldCondition? ReadSimpleCondition(string field, JsonElement value, ref string? problem)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return new FieldCondition { Field = field, Operator = ConditionOperator.Equal, Text = "true" };
            case JsonValueKind.False:
                return new FieldCondition { Field = field, Operator = ConditionOperator.Equal, Text = "false" };
            case JsonValueKind.Number:
                if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
                {
                    problem = $"«{field}»: número no válido";
                    return null;
                }
                return new FieldCondition { Field = field, Operator = ConditionOperator.Equal, Number = number };
            case JsonValueKind.String:
            {
                var text = value.GetString()?.Trim() ?? "";
                bool prefix = text.EndsWith('*');
                if (prefix) text = text[..^1];
                if (text.Length == 0 || text.Length > MaxValueLength || text.Contains('*') || text.Contains(' ') || text.Contains('='))
                {
                    problem = $"«{field}»: valor no válido «{Describe(value.GetString())}»";
                    return null;
                }
                return new FieldCondition
                {
                    Field = field,
                    Operator = prefix ? ConditionOperator.Prefix : ConditionOperator.Equal,
                    Text = text,
                };
            }
            default:
                problem = $"«{field}»: valor no admitido";
                return null;
        }
    }

    // ───────────────────────────── Utilidades ─────────────────────────────

    private static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name[0] is < 'a' or > 'z') return false;
        foreach (var c in name)
            if (c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_') return false;
        return true;
    }

    private static string? Clean(string? value, int max) => FeedbackText.Clean(value, max, multiline: false);

    private static Dictionary<string, JsonElement> ReadProperties(JsonElement obj)
    {
        var props = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in obj.EnumerateObject()) props[p.Name] = p.Value;
        return props;
    }

    private static string? GetString(Dictionary<string, JsonElement> props, string name)
    {
        if (!props.TryGetValue(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static bool? GetBool(Dictionary<string, JsonElement> props, string name)
    {
        if (!props.TryGetValue(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString()?.Trim(), out var b) => b,
            _ => null,
        };
    }

    private static DateTimeOffset? GetDate(Dictionary<string, JsonElement> props, string name)
    {
        var text = GetString(props, name);
        if (string.IsNullOrWhiteSpace(text)) return null;
        var date = ManifestProvider.ParseDate(text, out var hadZone);
        if (date is null)
            WarnOnce($"Misiones: fecha no válida en «{name}»: «{Describe(text)}» (formato: 2026-10-31T21:00:00+01:00).");
        else if (!hadZone)
            WarnOnce($"Misiones: la fecha «{Describe(text)}» no indica zona horaria; se interpreta como la hora local de cada PC.");
        return date?.ToUniversalTime();
    }

    private static string Describe(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "vacío";
        var clean = FeedbackText.Clean(text, 40, multiline: false);
        return clean ?? "vacío";
    }

    private static void WarnOnce(string message)
    {
        lock (WarnLock)
        {
            if (!WarnedMessages.Add(message)) return;
            if (WarnedMessages.Count > 500) WarnedMessages.Clear();
        }
        Log.Warn(message);
    }

    private bool ValidateRemote(string text)
    {
        if (Parse(text) is not null) return true;
        Volatile.Write(ref _lastRejectedCall, Interlocked.Read(ref _calls));
        return false;
    }

    private void Remember(string text, MissionSet set, string source)
    {
        Volatile.Write(ref _lastGoodText, text);
        var summary = $"Misiones: {source}: «{set.Id}», {set.Missions.Count} misiones " +
                      $"({set.Missions.Count(m => m.Manual)} manuales)" +
                      (set.MinGameVersion is { } v ? $", desde la versión {v}" : "") +
                      (set.EndsUtc is { } end ? $", hasta {end.ToString("u", CultureInfo.InvariantCulture)}" : "") + ".";
        var previous = Interlocked.Exchange(ref _lastLoggedSummary, summary);
        if (string.Equals(previous, summary, StringComparison.Ordinal)) Log.Debug(summary);
        else Log.Info(summary);
    }

    private void LogSummaryIfChanged(string summary)
    {
        var previous = Interlocked.Exchange(ref _lastLoggedSummary, summary);
        if (string.Equals(previous, summary, StringComparison.Ordinal)) Log.Debug(summary);
        else Log.Info(summary);
    }

    private MissionSet? LastGood()
    {
        var text = Volatile.Read(ref _lastGoodText);
        return text is null ? null : Parse(text);
    }

    private async Task<string?> ReadLocalOverrideAsync(CancellationToken ct)
    {
        var path = LocalOverridePath;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (info.Length > MaxJsonBytes)
            {
                Log.Warn($"Misiones: {LocalOverrideFileName} ocupa {info.Length} bytes (máx. {MaxJsonBytes}); se ignora.");
                return null;
            }
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
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
            Log.Warn($"Misiones: no se pudo leer {path}; se ignora.", ex);
            return null;
        }
    }
}
