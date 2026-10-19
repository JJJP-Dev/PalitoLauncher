using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Logging;

namespace PalitoLauncher.Core.Remote;

/// <summary>
/// Obtiene launcher/launcher.json del repo del juego (LauncherConfig.RemoteManifestUrl, vía
/// GitHubClient.GetCachedTextFileAsync con caché/ETag; raw.githubusercontent.com, no gasta cupo de la API). Para probar sin
/// publicar, &lt;AppPaths.AppDataDir&gt;\launcher.local.json tiene prioridad sobre el remoto. Thread-safe.
/// </summary>
public sealed class ManifestProvider
{
    public const string LocalOverrideFileName = "launcher.local.json";

    /// <summary>Nombre del archivo de caché del launcher.json remoto (dentro de la caché del GitHubClient).</summary>
    internal const string RemoteCacheFileName = "launcher.json";

    /// <summary>Tamaño máximo del JSON (el remoto ya lo limita GitHubClient a 256 KB; el local, este mismo valor).</summary>
    internal const int MaxJsonBytes = 256 * 1024;

    internal const int MaxDepth = 32;

    internal const int MaxAnnouncements = 10;
    internal const int MaxBlockedBuilds = 50;
    internal const int MaxLinks = 6;
    internal const int MaxPlaytests = 10;

    internal const int MaxIdLength = 64;
    internal const int MaxAnnouncementTitleLength = 80;
    internal const int MaxAnnouncementMessageLength = 400;
    internal const int MaxLinkTextLength = 40;
    internal const int MaxMatchLength = 120;
    internal const int MaxReasonLength = 300;
    internal const int MaxLinkLabelLength = 30;
    internal const int MaxPlaytestTitleLength = 80;
    internal const int MaxPlaytestDescriptionLength = 500;
    internal const int MaxHostingNotesLength = 300;
    internal const int MaxUrlLength = 2048;

    internal const int MinDurationMinutes = 15;
    internal const int MaxDurationMinutes = 720;
    internal const int DefaultDurationMinutes = 120;

    internal const string DefaultLinkText = "Más información";
    internal const string DefaultPlaytestTitle = "Sesión de prueba";
    internal const string DefaultBlockReason = "Esta versión tiene un problema grave conocido.";
    internal const string DefaultIcon = "web";

    // Límite de elementos leídos de cada lista ANTES de sanear (protege ante listas enormes).
    private const int MaxRawItems = 500;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = MaxDepth,
    };

    /// <summary>Iconos conocidos → nombre por defecto del botón si el enlace no trae texto.</summary>
    private static readonly Dictionary<string, string> KnownIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["discord"] = "Discord",
        ["itch"] = "itch.io",
        ["steam"] = "Steam",
        ["youtube"] = "YouTube",
        ["github"] = "GitHub",
        ["web"] = "Web",
    };

    // Fechas ISO 8601: "2026-10-24T21:00:00Z", "2026-10-24T21:00+02:00", "2026-10-24 21:00", "2026-10-24".
    private static readonly Regex IsoDateRegex = new(
        @"^(?<y>[0-9]{4})-(?<mo>[0-9]{2})-(?<d>[0-9]{2})" +
        @"(?:[T ](?<h>[0-9]{2}):(?<mi>[0-9]{2})(?::(?<s>[0-9]{2})(?:[.,](?<f>[0-9]{1,7}))?)?)?" +
        @"\s*(?<z>Z|[+-][0-9]{2}(?::?[0-9]{2})?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // Avisos del log ya escritos (el manifiesto se relee cada 15 min: no repetir lo mismo cada vez).
    private static readonly object WarnLock = new();
    private static readonly HashSet<string> WarnedMessages = new(StringComparer.Ordinal);

    private readonly GitHubClient _github;
    private readonly string _appDataDir;

    // Llamadas a GetManifestAsync y la última en la que el validador rechazó el remoto (como en SurveyProvider: así también
    // lo ve una llamada que se une a la descarga en curso, y una llamada posterior no reutiliza un rechazo antiguo).
    private long _calls;
    private long _lastRejectedCall;

    // Último JSON bueno usado (local o remoto) por si algo falla de forma inesperada; null = "no hay manifiesto".
    private string? _lastGoodText;
    private string? _lastLoggedSummary;

    public ManifestProvider(GitHubClient github, string appDataDir)
    {
        ArgumentNullException.ThrowIfNull(github);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDir);
        _github = github;
        _appDataDir = Path.GetFullPath(appDataDir);
    }

    /// <summary>Ruta del archivo local de pruebas.</summary>
    internal string LocalOverridePath => Path.Combine(_appDataDir, LocalOverrideFileName);

    /// <summary>
    /// Manifiesto efectivo (local → remoto → vacío), saneado con <see cref="Sanitize"/>. Nunca lanza (salvo
    /// OperationCanceledException si se canceló <paramref name="ct"/>); ante cualquier problema registra un Warn y devuelve
    /// el último bueno en caché o un manifiesto vacío. El validador del remoto solo acepta JSON que <see cref="Parse"/> acepta,
    /// así una versión rota no reemplaza a la última buena en caché.
    /// </summary>
    public async Task<LauncherManifest> GetManifestAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var call = Interlocked.Increment(ref _calls);

        try
        {
            // 1) Archivo local (pruebas del desarrollador): si es válido, ni se pregunta al remoto.
            var localText = await ReadLocalOverrideAsync(ct).ConfigureAwait(false);
            if (localText is not null)
            {
                var local = Parse(localText);
                if (local is not null)
                {
                    Remember(localText, local, $"se usa el archivo local {LocalOverrideFileName}");
                    return local;
                }
                Log.Warn($"Manifiesto del launcher: {LocalOverrideFileName} no es un objeto JSON válido (o pasa de " +
                         $"{MaxJsonBytes / 1024} KB); se ignora y se usa el de GitHub.");
            }

            // 2) launcher/launcher.json del repo del juego (raw.githubusercontent.com, con caché y ETag).
            var remoteText = await _github.GetCachedTextFileAsync(LauncherConfig.RemoteManifestUrl, RemoteCacheFileName,
                ValidateRemote, ct).ConfigureAwait(false);
            if (remoteText is not null)
            {
                var remote = Parse(remoteText);
                if (remote is not null)
                {
                    Remember(remoteText, remote, "launcher/launcher.json");
                    return remote;
                }
                // No debería pasar (el validador usa el mismo Parse): se trata como un fallo inesperado.
                Log.Warn("Manifiesto del launcher: launcher/launcher.json dejó de ser válido al leerlo; se usa el último bueno.");
                return LastGoodOrEmpty();
            }

            if (Interlocked.Read(ref _lastRejectedCall) >= call)
            {
                // Descargado en esta llamada pero roto, y sin copia buena en la caché de disco.
                Log.Warn("Manifiesto del launcher: launcher/launcher.json no es un objeto JSON válido y no hay copia buena " +
                         "guardada; no se muestran anuncios ni sesiones hasta que se arregle.");
                return LastGoodOrEmpty();
            }

            // 404 (no existe: lo normal mientras no se publique) o sin red y sin caché.
            Volatile.Write(ref _lastGoodText, null);
            LogSummaryIfChanged("Manifiesto del launcher: no hay launcher/launcher.json (o no se pudo descargar); " +
                                "no hay anuncios, sesiones ni enlaces que mostrar.");
            return new LauncherManifest();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Manifiesto del launcher: no se pudo obtener; se usa el último bueno (o ninguno).", ex);
            return LastGoodOrEmpty();
        }
    }

    /// <summary>
    /// Parsea JSON (camelCase, nombres sin distinguir mayúsculas, comentarios y comas finales permitidos, enums como texto
    /// sin distinguir mayúsculas, valores de tipo incorrecto se ignoran campo a campo). Devuelve null solo si no es un
    /// objeto JSON válido (máx. 256 KB, profundidad 32). El resultado ya va saneado. No lanza.
    /// </summary>
    /// <remarks>
    /// Detalles: los niveles aceptan también «aviso», «crítico»/«critico» e «información»/«informacion»; un nivel
    /// desconocido se queda en Info. Las fechas son ISO 8601 ("2026-10-24T19:00:00Z", "2026-10-24T21:00:00+02:00", también
    /// con espacio en vez de 'T' o sin segundos); sin zona horaria se interpretan como hora local de este PC (y se avisa en
    /// el log). Ids y textos pueden venir como número (se convierten a texto).
    /// </remarks>
    public static LauncherManifest? Parse(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var text = StripBom(json);
            // Cota rápida por caracteres (cada carácter ocupa al menos 1 byte en UTF-8) y luego la exacta en bytes.
            if (text.Length > MaxJsonBytes || Encoding.UTF8.GetByteCount(text) > MaxJsonBytes) return null;

            using var doc = JsonDocument.Parse(text, DocumentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return Sanitize(ReadManifest(doc.RootElement));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("Error inesperado al leer el manifiesto del launcher (launcher.json).", ex);
            return null;
        }
    }

    /// <summary>
    /// Sanea in-place y devuelve la misma instancia: recorta textos a sus máximos (quitando caracteres de control y marcas
    /// bidi), descarta anuncios sin Id válido o sin título, enlaces/URLs que no sean https absolutos, sesiones sin Id o con
    /// StartsUtc por defecto, BlockedBuilds sin Match; Ids repetidos (se queda el primero); limita cantidades (10/50/6/10);
    /// DurationMinutes a 15..720; Critical → Dismissible = false; EndsUtc anterior a StartsUtc → se descarta el anuncio;
    /// Icon desconocido → "web"; Hosting.Port fuera de 1..65535 → null; BugReportWebhookUrl inválido (mismas reglas que
    /// SurveyProvider.IsValidDiscordWebhook) → null.
    /// </summary>
    /// <remarks>
    /// Además: fechas pasadas a UTC; Url de anuncio no válida → null (el anuncio se queda sin botón); LinkText solo si hay
    /// Url (vacío → «Más información»); enlace sin texto → nombre del icono («Discord», «Steam»…); sesión sin título →
    /// «Sesión de prueba»; build bloqueada sin motivo → texto genérico; Matches repetidos fuera; Hosting sin puerto ni
    /// notas → null; BugReportWebhookUrl queda en forma canónica.
    /// </remarks>
    public static LauncherManifest Sanitize(LauncherManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var discarded = new List<string>();

        manifest.Announcements = SanitizeAnnouncements(manifest.Announcements, discarded);
        manifest.BlockedBuilds = SanitizeBlockedBuilds(manifest.BlockedBuilds, discarded);
        manifest.Links = SanitizeLinks(manifest.Links, discarded);
        manifest.Playtests = SanitizePlaytests(manifest.Playtests, discarded);
        manifest.Hosting = SanitizeHosting(manifest.Hosting);

        WebhookObfuscation.WarnIfPlain(manifest.BugReportWebhookUrl, "launcher.json");
        var webhook = SurveyProvider.NormalizeDiscordWebhook(manifest.BugReportWebhookUrl);
        if (webhook is null && !string.IsNullOrWhiteSpace(manifest.BugReportWebhookUrl))
            discarded.Add("bugReportWebhookUrl: no es un webhook de Discord válido");
        manifest.BugReportWebhookUrl = webhook;

        if (discarded.Count > 0)
        {
            WarnOnce($"Manifiesto del launcher: se descartan {discarded.Count} elementos: " +
                     string.Join("; ", discarded.Take(8)) + (discarded.Count > 8 ? "; …" : "") + ".");
        }
        return manifest;
    }

    // ───────────────────────────── Saneado ─────────────────────────────

    private static List<Announcement> SanitizeAnnouncements(List<Announcement>? source, List<string> discarded)
    {
        var kept = new List<Announcement>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in source ?? new List<Announcement>())
        {
            if (a is null) continue;
            var label = $"anuncio «{Describe(a.Id)}»";

            var id = a.Id?.Trim() ?? "";
            if (!IsValidId(id))
            {
                discarded.Add($"{label}: Id no válido");
                continue;
            }
            var title = FeedbackText.Clean(a.Title, MaxAnnouncementTitleLength, multiline: false);
            if (title is null)
            {
                discarded.Add($"{label}: sin título");
                continue;
            }

            var starts = a.StartsUtc?.ToUniversalTime();
            var ends = a.EndsUtc?.ToUniversalTime();
            if (starts is not null && ends is not null && ends < starts)
            {
                discarded.Add($"{label}: termina antes de empezar");
                continue;
            }
            if (!ids.Add(id))
            {
                discarded.Add($"{label}: Id repetido");
                continue;
            }
            if (kept.Count >= MaxAnnouncements)
            {
                discarded.Add($"{label}: más de {MaxAnnouncements} anuncios");
                continue;
            }

            a.Id = id;
            a.Title = title;
            a.Level = Enum.IsDefined(a.Level) ? a.Level : AnnouncementLevel.Info;
            a.Message = FeedbackText.Clean(a.Message, MaxAnnouncementMessageLength, multiline: true);
            a.Url = NormalizeHttpsUrl(a.Url);
            a.LinkText = a.Url is null
                ? null
                : FeedbackText.Clean(a.LinkText, MaxLinkTextLength, multiline: false) ?? DefaultLinkText;
            a.StartsUtc = starts;
            a.EndsUtc = ends;
            if (a.Level == AnnouncementLevel.Critical) a.Dismissible = false;
            kept.Add(a);
        }
        return kept;
    }

    private static List<BlockedBuild> SanitizeBlockedBuilds(List<BlockedBuild>? source, List<string> discarded)
    {
        var kept = new List<BlockedBuild>();
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in source ?? new List<BlockedBuild>())
        {
            if (b is null) continue;

            var match = FeedbackText.Clean(b.Match, MaxMatchLength, multiline: false);
            if (match is null)
            {
                discarded.Add("build bloqueada sin «match»");
                continue;
            }
            // Repetidas (sin distinguir mayúsculas ni espacios): no aportan nada.
            if (!matches.Add(ManifestLogic.NormalizeBuildText(match))) continue;
            if (kept.Count >= MaxBlockedBuilds)
            {
                discarded.Add($"build bloqueada «{Describe(match)}»: más de {MaxBlockedBuilds}");
                continue;
            }

            b.Match = match;
            b.Reason = FeedbackText.Clean(b.Reason, MaxReasonLength, multiline: false) ?? DefaultBlockReason;
            kept.Add(b);
        }
        return kept;
    }

    private static List<CommunityLink> SanitizeLinks(List<CommunityLink>? source, List<string> discarded)
    {
        var kept = new List<CommunityLink>();
        foreach (var link in source ?? new List<CommunityLink>())
        {
            if (link is null) continue;

            var url = NormalizeHttpsUrl(link.Url);
            if (url is null)
            {
                discarded.Add($"enlace «{Describe(link.Label)}»: la URL no es https");
                continue;
            }
            if (kept.Count >= MaxLinks)
            {
                discarded.Add($"enlace «{Describe(link.Label)}»: más de {MaxLinks} enlaces");
                continue;
            }

            var icon = link.Icon?.Trim().ToLowerInvariant() ?? "";
            if (!KnownIcons.ContainsKey(icon)) icon = DefaultIcon;

            link.Url = url;
            link.Icon = icon;
            link.Label = FeedbackText.Clean(link.Label, MaxLinkLabelLength, multiline: false) ?? KnownIcons[icon];
            kept.Add(link);
        }
        return kept;
    }

    private static List<Playtest> SanitizePlaytests(List<Playtest>? source, List<string> discarded)
    {
        var kept = new List<Playtest>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Con esto de margen, StartsUtc + duración nunca se sale del rango de DateTimeOffset.
        var latestStart = DateTimeOffset.MaxValue - TimeSpan.FromDays(2);
        foreach (var p in source ?? new List<Playtest>())
        {
            if (p is null) continue;
            var label = $"sesión «{Describe(p.Id)}»";

            var id = p.Id?.Trim() ?? "";
            if (!IsValidId(id))
            {
                discarded.Add($"{label}: Id no válido");
                continue;
            }
            if (p.StartsUtc == default)
            {
                discarded.Add($"{label}: sin fecha de inicio (startsUtc) válida");
                continue;
            }
            if (p.StartsUtc > latestStart)
            {
                discarded.Add($"{label}: fecha imposible");
                continue;
            }
            if (!ids.Add(id))
            {
                discarded.Add($"{label}: Id repetido");
                continue;
            }
            if (kept.Count >= MaxPlaytests)
            {
                discarded.Add($"{label}: más de {MaxPlaytests} sesiones");
                continue;
            }

            p.Id = id;
            p.Title = FeedbackText.Clean(p.Title, MaxPlaytestTitleLength, multiline: false) ?? DefaultPlaytestTitle;
            p.Description = FeedbackText.Clean(p.Description, MaxPlaytestDescriptionLength, multiline: true);
            p.StartsUtc = p.StartsUtc.ToUniversalTime();
            p.DurationMinutes = Math.Clamp(p.DurationMinutes, MinDurationMinutes, MaxDurationMinutes);
            p.RequiredVersion = FeedbackText.Clean(p.RequiredVersion, MaxMatchLength, multiline: false);
            p.DiscordUrl = NormalizeHttpsUrl(p.DiscordUrl);
            kept.Add(p);
        }
        return kept;
    }

    private static HostingInfo? SanitizeHosting(HostingInfo? hosting)
    {
        if (hosting is null) return null;
        hosting.Port = hosting.Port is >= 1 and <= 65535 ? hosting.Port : null;
        hosting.Notes = FeedbackText.Clean(hosting.Notes, MaxHostingNotesLength, multiline: true);
        return hosting.Port is null && hosting.Notes is null ? null : hosting;
    }

    /// <summary>Letras, números, '-', '_' y '.' (1..64 caracteres).</summary>
    internal static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength) return false;
        foreach (var c in id)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('-' or '_' or '.')) return false;
        }
        return true;
    }

    /// <summary>
    /// URL https absoluta con host y sin usuario/contraseña (evita trucos tipo "https://discord.com@otra-web.com"), en forma
    /// canónica (Uri.AbsoluteUri); cualquier otra cosa → null.
    /// </summary>
    internal static string? NormalizeHttpsUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var trimmed = url.Trim();
        if (trimmed.Length > MaxUrlLength) return null;
        foreach (var c in trimmed)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c)) return null;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var absolute = uri.AbsoluteUri;
        return absolute.Length > MaxUrlLength ? null : absolute;
    }

    private static string Describe(string? value) => FeedbackText.Truncate(FeedbackText.Clean(value, 40, multiline: false) ?? "", 40);

    // ───────────────────────────── Lectura del JSON ─────────────────────────────

    private static LauncherManifest ReadManifest(JsonElement root)
    {
        var props = ReadProperties(root);
        var manifest = new LauncherManifest
        {
            Announcements = ReadList(props, "announcements", ReadAnnouncement),
            BlockedBuilds = ReadList(props, "blockedBuilds", ReadBlockedBuild),
            Links = ReadList(props, "links", ReadLink),
            Playtests = ReadList(props, "playtests", ReadPlaytest),
            BugReportWebhookUrl = GetString(props, "bugReportWebhookUrl"),
        };

        if (props.TryGetValue("hosting", out var hosting) && hosting.ValueKind == JsonValueKind.Object)
        {
            var h = ReadProperties(hosting);
            manifest.Hosting = new HostingInfo { Port = GetInt(h, "port"), Notes = GetString(h, "notes") };
        }
        return manifest;
    }

    /// <summary>Lista de objetos; lo que no sea un objeto se ignora. Máx. <see cref="MaxRawItems"/> elementos leídos.</summary>
    private static List<T> ReadList<T>(Dictionary<string, JsonElement> props, string name, Func<Dictionary<string, JsonElement>, T> read)
    {
        var list = new List<T>();
        if (!props.TryGetValue(name, out var array) || array.ValueKind != JsonValueKind.Array) return list;
        var count = 0;
        foreach (var element in array.EnumerateArray())
        {
            if (++count > MaxRawItems) break;
            if (element.ValueKind == JsonValueKind.Object) list.Add(read(ReadProperties(element)));
        }
        return list;
    }

    private static Announcement ReadAnnouncement(Dictionary<string, JsonElement> p) => new()
    {
        Id = GetString(p, "id") ?? "",
        Level = ParseLevel(p.TryGetValue("level", out var level) ? level : default),
        Title = GetString(p, "title") ?? "",
        Message = GetString(p, "message"),
        Url = GetString(p, "url"),
        LinkText = GetString(p, "linkText"),
        StartsUtc = GetDate(p, "startsUtc"),
        EndsUtc = GetDate(p, "endsUtc"),
        Dismissible = GetBool(p, "dismissible") ?? true,
    };

    private static BlockedBuild ReadBlockedBuild(Dictionary<string, JsonElement> p) => new()
    {
        Match = GetString(p, "match") ?? "",
        Reason = GetString(p, "reason") ?? "",
    };

    private static CommunityLink ReadLink(Dictionary<string, JsonElement> p) => new()
    {
        Label = GetString(p, "label") ?? "",
        Url = GetString(p, "url") ?? "",
        Icon = GetString(p, "icon") ?? DefaultIcon,
    };

    private static Playtest ReadPlaytest(Dictionary<string, JsonElement> p) => new()
    {
        Id = GetString(p, "id") ?? "",
        Title = GetString(p, "title") ?? "",
        Description = GetString(p, "description"),
        StartsUtc = GetDate(p, "startsUtc") ?? default,
        DurationMinutes = GetInt(p, "durationMinutes") ?? DefaultDurationMinutes,
        RequiredVersion = GetString(p, "requiredVersion"),
        DiscordUrl = GetString(p, "discordUrl"),
    };

    /// <summary>Nivel como texto (nombre del enum o alias en español); cualquier otra cosa → Info.</summary>
    private static AnnouncementLevel ParseLevel(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) return AnnouncementLevel.Info;
        var text = ElementToString(element)?.Trim().ToLowerInvariant();
        switch (text)
        {
            case "info" or "información" or "informacion": return AnnouncementLevel.Info;
            case "warning" or "aviso": return AnnouncementLevel.Warning;
            case "critical" or "crítico" or "critico": return AnnouncementLevel.Critical;
            case null or "": return AnnouncementLevel.Info;
            default:
                WarnOnce($"Manifiesto del launcher: nivel de anuncio desconocido «{Describe(text)}»; se usa «info» " +
                         "(valores válidos: info, warning, critical).");
                return AnnouncementLevel.Info;
        }
    }

    private static DateTimeOffset? GetDate(Dictionary<string, JsonElement> props, string name)
    {
        if (!props.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = ElementToString(value);
        var date = ParseDate(text, out var hadZone);
        if (date is null && !string.IsNullOrWhiteSpace(text))
            WarnOnce($"Manifiesto del launcher: fecha no válida en «{name}»: «{Describe(text)}» (formato: 2026-10-24T21:00:00+02:00).");
        else if (date is not null && !hadZone)
            WarnOnce($"Manifiesto del launcher: la fecha «{Describe(text)}» no indica zona horaria; se interpreta como la hora " +
                     "local de cada PC. Mejor añade la zona («Z» para UTC o «+02:00» para la hora de verano de España).");
        return date;
    }

    /// <summary>
    /// Fecha ISO 8601 (ver <see cref="IsoDateRegex"/>) o null. <paramref name="hadZone"/> = false si no traía zona horaria
    /// (entonces se interpreta como hora local de este PC).
    /// </summary>
    internal static DateTimeOffset? ParseDate(string? text, out bool hadZone)
    {
        hadZone = false;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64) return null;
        try
        {
            var m = IsoDateRegex.Match(text.Trim());
            if (!m.Success) return null;

            static int Num(Group g) => g.Success ? int.Parse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture) : 0;
            var fraction = m.Groups["f"].Success
                ? long.Parse(m.Groups["f"].Value.PadRight(7, '0'), NumberStyles.None, CultureInfo.InvariantCulture)
                : 0;
            var local = new DateTime(Num(m.Groups["y"]), Num(m.Groups["mo"]), Num(m.Groups["d"]), Num(m.Groups["h"]),
                Num(m.Groups["mi"]), Num(m.Groups["s"]), DateTimeKind.Unspecified).AddTicks(fraction);

            var zone = m.Groups["z"];
            if (!zone.Success)
                return new DateTimeOffset(local); // Unspecified → zona horaria local del PC.

            hadZone = true;
            if (zone.Value is "Z" or "z") return new DateTimeOffset(local, TimeSpan.Zero);

            var digits = zone.Value[1..].Replace(":", "", StringComparison.Ordinal);
            var hours = int.Parse(digits[..2], NumberStyles.None, CultureInfo.InvariantCulture);
            var minutes = digits.Length > 2 ? int.Parse(digits[2..], NumberStyles.None, CultureInfo.InvariantCulture) : 0;
            if (minutes > 59) return null;
            var offset = new TimeSpan(hours, minutes, 0);
            return new DateTimeOffset(local, zone.Value[0] == '-' ? -offset : offset);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or RegexMatchTimeoutException)
        {
            // Mes 13, día 31 de febrero, zona de más de 14 h, fuera de rango…
            return null;
        }
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

    /// <summary>Texto o número (p.ej. un Id escrito sin comillas); cualquier otro tipo → null.</summary>
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
            // Escapes \uD800 sueltos (UTF-16 no válido): ese valor se ignora, no todo el archivo.
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

    // ───────────────────────────── Interno ─────────────────────────────

    private async Task<string?> ReadLocalOverrideAsync(CancellationToken ct)
    {
        var path = LocalOverridePath;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (info.Length > MaxJsonBytes)
            {
                Log.Warn($"Manifiesto del launcher: {LocalOverrideFileName} ocupa {info.Length} bytes (máx. {MaxJsonBytes}); se ignora.");
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
            Log.Warn($"Manifiesto del launcher: no se pudo leer {path}; se ignora.", ex);
            return null;
        }
    }

    /// <summary>Validador que se pasa a GitHubClient: solo acepta lo que <see cref="Parse"/> acepta (y apunta el rechazo).</summary>
    private bool ValidateRemote(string text)
    {
        if (Parse(text) is not null) return true;
        Volatile.Write(ref _lastRejectedCall, Interlocked.Read(ref _calls));
        return false;
    }

    private void Remember(string text, LauncherManifest manifest, string source)
    {
        Volatile.Write(ref _lastGoodText, text);
        LogSummaryIfChanged($"Manifiesto del launcher ({source}): {manifest.Announcements.Count} anuncios, " +
                            $"{manifest.BlockedBuilds.Count} builds con problemas, {manifest.Links.Count} enlaces, " +
                            $"{manifest.Playtests.Count} sesiones de prueba, hosting: {(manifest.Hosting is null ? "no" : "sí")}, " +
                            $"webhook de reportes: {SurveyProvider.DescribeWebhook(manifest.BugReportWebhookUrl)}.");
    }

    /// <summary>Resumen en Info solo cuando cambia (se relee cada 15 min); si no, en Debug.</summary>
    private void LogSummaryIfChanged(string summary)
    {
        var previous = Interlocked.Exchange(ref _lastLoggedSummary, summary);
        if (string.Equals(previous, summary, StringComparison.Ordinal)) Log.Debug(summary);
        else Log.Info(summary);
    }

    /// <summary>Copia nueva del último manifiesto bueno de esta sesión, o uno vacío.</summary>
    private LauncherManifest LastGoodOrEmpty()
    {
        var text = Volatile.Read(ref _lastGoodText);
        return (text is null ? null : Parse(text)) ?? new LauncherManifest();
    }

    private static void WarnOnce(string message)
    {
        lock (WarnLock)
        {
            if (WarnedMessages.Count >= 200) WarnedMessages.Clear();
            if (!WarnedMessages.Add(message)) return;
        }
        Log.Warn(message);
    }
}
