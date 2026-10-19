using System.Globalization;
using System.Text;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Versioning;

namespace PalitoLauncher.Core.Remote;

/// <summary>Lógica pura del manifiesto (sin E/S), testeable.</summary>
public static class ManifestLogic
{
    /// <summary>Antelación del aviso de una sesión de prueba.</summary>
    public static readonly TimeSpan PlaytestReminderLead = TimeSpan.FromMinutes(15);

    /// <summary>Máximo de anuncios visibles a la vez.</summary>
    internal const int MaxVisibleAnnouncements = 3;

    /// <summary>Solo se muestran sesiones futuras que empiecen dentro de este plazo.</summary>
    internal static readonly TimeSpan PlaytestLookahead = TimeSpan.FromDays(30);

    /// <summary>Se sigue avisando de una sesión que ya empezó si hace menos de esto.</summary>
    internal static readonly TimeSpan PlaytestLateReminder = TimeSpan.FromMinutes(5);

    /// <summary>Máximo de releases en «Qué ha cambiado desde tu versión».</summary>
    internal const int MaxReleasesSince = 10;

    /// <summary>
    /// Anuncios visibles ahora: dentro de su ventana [StartsUtc, EndsUtc) (los límites que falten no restringen), y no
    /// descartados (los Critical se muestran aunque su Id esté en <paramref name="dismissedIds"/>). Orden: Critical, Warning,
    /// Info; dentro del mismo nivel, StartsUtc más reciente primero (sin fecha al final) y luego orden del archivo. Máx. 3.
    /// </summary>
    /// <remarks>
    /// Los Ids se comparan sin distinguir mayúsculas. Un anuncio con Dismissible = false tampoco se oculta aunque su Id
    /// esté en la lista (el usuario no puede cerrarlo: si se descartó cuando sí se podía, el autor ha decidido que se vea).
    /// </remarks>
    public static IReadOnlyList<Announcement> GetActiveAnnouncements(LauncherManifest manifest, DateTimeOffset nowUtc,
        IReadOnlyCollection<string> dismissedIds)
    {
        if (manifest?.Announcements is not { Count: > 0 } announcements) return Array.Empty<Announcement>();

        var dismissed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in dismissedIds ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(id)) dismissed.Add(id.Trim());
        }

        return announcements
            .Where(a => a is not null && IsInWindow(a, nowUtc))
            .Where(a => a.Level == AnnouncementLevel.Critical || !a.Dismissible || !dismissed.Contains(a.Id?.Trim() ?? ""))
            .OrderByDescending(a => LevelRank(a.Level))
            .ThenBy(a => a.StartsUtc is null ? 1 : 0)
            .ThenByDescending(a => a.StartsUtc ?? DateTimeOffset.MinValue)
            .Take(MaxVisibleAnnouncements)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// ¿<paramref name="match"/> identifica esta build? Sin distinguir mayúsculas y quitando espacios: igual al tag, igual al
    /// nombre del asset, o igual a la versión canónica (GameVersion.ToString()); también acepta el tag sin la "v" inicial
    /// y la versión escrita de otra forma que parsee a la misma GameVersion (p.ej. "0.0.4_Prototype2" ≡ "0.0.4-prototype.2").
    /// match vacío → false.
    /// </summary>
    /// <remarks>
    /// Si <paramref name="match"/> es un nombre de archivo (.zip/.rar/.7z/.exe) solo vale el nombre EXACTO del asset (así
    /// bloquear "2D_0.0.4_Prototype2.zip" no bloquea una resubida corregida con otro nombre y la misma versión).
    /// <paramref name="version"/> puede venir en cualquier forma que GameVersion entienda.
    /// </remarks>
    public static bool MatchesBuild(string? match, string? tag, string? assetName, string? version)
    {
        var m = NormalizeBuildText(match);
        if (m.Length == 0) return false;

        var t = NormalizeBuildText(tag);
        var a = NormalizeBuildText(assetName);
        var v = NormalizeBuildText(version);
        if (EqualsNonEmpty(m, t) || EqualsNonEmpty(m, a) || EqualsNonEmpty(m, v)) return true;

        // "0.0.4" ≡ tag "v0.0.4" (y al revés).
        if (EqualsNonEmpty(StripLeadingV(m), StripLeadingV(t))) return true;

        if (IsArchiveName(m)) return false;
        return GameVersion.TryParse(match, out var wanted) && GameVersion.TryParse(version, out var actual) && wanted == actual;
    }

    /// <summary>Primera BlockedBuild que coincide con la versión instalada (tag, asset y versión del marcador), o null.</summary>
    public static BlockedBuild? FindBlock(LauncherManifest manifest, InstalledGameInfo? installed)
    {
        if (installed is null || manifest?.BlockedBuilds is not { Count: > 0 } blocks) return null;
        var version = InstalledVersionText(installed);
        return blocks.FirstOrDefault(b => b is not null && MatchesBuild(b.Match, installed.Tag, installed.AssetName, version));
    }

    /// <summary>Primera BlockedBuild que coincide con esa release (tag, asset y versión parseada), o null.</summary>
    public static BlockedBuild? FindBlock(LauncherManifest manifest, GameRelease release)
    {
        if (release is null || manifest?.BlockedBuilds is not { Count: > 0 } blocks) return null;
        var (tag, asset, version) = ReleaseTexts(release);
        return blocks.FirstOrDefault(b => b is not null && MatchesBuild(b.Match, tag, asset, version));
    }

    /// <summary>
    /// Próxima sesión: la que está en curso (StartsUtc ≤ ahora &lt; StartsUtc + DurationMinutes; si hay varias, la que empezó
    /// antes) y, si no hay, la futura más cercana que empiece en los próximos 30 días. Null si ninguna.
    /// </summary>
    /// <remarks>
    /// UntilStart = StartsUtc − ahora (≤ 0 si está en curso). Empates: orden del archivo. La duración se limita a 15..720 min
    /// aunque el manifiesto no venga saneado.
    /// </remarks>
    public static PlaytestStatus? GetNextPlaytest(LauncherManifest manifest, DateTimeOffset nowUtc)
    {
        if (manifest?.Playtests is not { Count: > 0 } playtests) return null;

        Playtest? ongoing = null;
        Playtest? upcoming = null;
        foreach (var p in playtests)
        {
            if (p is null || p.StartsUtc == default) continue;

            if (p.StartsUtc <= nowUtc)
            {
                if (nowUtc < GetEnd(p) && (ongoing is null || p.StartsUtc < ongoing.StartsUtc)) ongoing = p;
            }
            else if (p.StartsUtc - nowUtc <= PlaytestLookahead && (upcoming is null || p.StartsUtc < upcoming.StartsUtc))
            {
                upcoming = p;
            }
        }

        var chosen = ongoing ?? upcoming;
        return chosen is null ? null : new PlaytestStatus(chosen, ongoing is not null, chosen.StartsUtc - nowUtc, GetEnd(chosen));
    }

    /// <summary>
    /// Texto de cuenta atrás en español: ≥ 2 días → "en 3 días"; ≥ 1 día → "en 1 día y 5 h"; ≥ 1 h → "en 3 h 12 min";
    /// ≥ 1 min → "en 8 min"; menos → "en menos de un minuto"; ≤ 0 → "¡Ya ha empezado!".
    /// </summary>
    /// <remarks>Se redondea hacia abajo; las partes a cero se omiten ("en 1 día", "en 3 h").</remarks>
    public static string FormatCountdown(TimeSpan untilStart)
    {
        if (untilStart <= TimeSpan.Zero) return "¡Ya ha empezado!";
        if (untilStart < TimeSpan.FromMinutes(1)) return "en menos de un minuto";

        var inv = CultureInfo.InvariantCulture;
        if (untilStart < TimeSpan.FromHours(1))
            return string.Create(inv, $"en {untilStart.Minutes} min");
        if (untilStart < TimeSpan.FromDays(1))
        {
            return untilStart.Minutes == 0
                ? string.Create(inv, $"en {untilStart.Hours} h")
                : string.Create(inv, $"en {untilStart.Hours} h {untilStart.Minutes} min");
        }
        if (untilStart < TimeSpan.FromDays(2))
            return untilStart.Hours == 0 ? "en 1 día" : string.Create(inv, $"en 1 día y {untilStart.Hours} h");
        return string.Create(inv, $"en {untilStart.Days} días");
    }

    /// <summary>
    /// Archivo de calendario iCalendar (RFC 5545) para la sesión: VCALENDAR/VEVENT con UID "&lt;id&gt;@palito-launcher",
    /// DTSTAMP (<paramref name="nowUtc"/>), DTSTART/DTEND en UTC ("yyyyMMddTHHmmssZ"), SUMMARY "&lt;juego&gt;: &lt;título&gt;",
    /// DESCRIPTION (descripción + versión necesaria + enlace de Discord), URL si hay Discord, y un VALARM de 15 min antes.
    /// Escapa \ ; , y saltos de línea; líneas plegadas a 75 octetos; separador CRLF.
    /// </summary>
    /// <remarks>
    /// Pensado para guardarse en UTF-8 (sin BOM). Cada línea termina en CRLF, también la última. Los caracteres de control
    /// se quitan; las líneas se pliegan sin partir caracteres UTF-8. La versión necesaria se muestra legible
    /// ("0.0.4 · Prototipo 2") si se puede parsear.
    /// </remarks>
    public static string BuildIcs(Playtest playtest, string gameName, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(playtest);

        var game = string.IsNullOrWhiteSpace(gameName) ? LauncherConfig.GameDisplayName : gameName.Trim();
        var title = string.IsNullOrWhiteSpace(playtest.Title) ? ManifestProvider.DefaultPlaytestTitle : playtest.Title.Trim();
        var id = string.IsNullOrWhiteSpace(playtest.Id) ? "playtest" : playtest.Id.Trim();
        var summary = $"{game}: {title}";
        var discord = ManifestProvider.NormalizeHttpsUrl(playtest.DiscordUrl);

        var description = new List<string>();
        if (!string.IsNullOrWhiteSpace(playtest.Description)) description.Add(playtest.Description.Trim());
        if (!string.IsNullOrWhiteSpace(playtest.RequiredVersion))
        {
            var required = playtest.RequiredVersion.Trim();
            var shown = GameVersion.TryParse(required, out var parsed) ? parsed.ToDisplayString() : required;
            description.Add($"Versión necesaria: {shown}");
        }
        if (discord is not null) description.Add($"Discord: {discord}");

        var sb = new StringBuilder();
        AppendLine(sb, "BEGIN:VCALENDAR");
        AppendLine(sb, "VERSION:2.0");
        AppendLine(sb, $"PRODID:-//PalitoGames//{LauncherConfig.LauncherName}//ES");
        AppendLine(sb, "CALSCALE:GREGORIAN");
        AppendLine(sb, "BEGIN:VEVENT");
        AppendLine(sb, "UID:" + EscapeText(id + "@palito-launcher"));
        AppendLine(sb, "DTSTAMP:" + FormatUtc(nowUtc));
        AppendLine(sb, "DTSTART:" + FormatUtc(playtest.StartsUtc));
        AppendLine(sb, "DTEND:" + FormatUtc(GetEnd(playtest)));
        AppendLine(sb, "SUMMARY:" + EscapeText(summary));
        if (description.Count > 0) AppendLine(sb, "DESCRIPTION:" + EscapeText(string.Join("\n\n", description)));
        if (discord is not null) AppendLine(sb, "URL:" + discord);
        AppendLine(sb, "BEGIN:VALARM");
        AppendLine(sb, "ACTION:DISPLAY");
        AppendLine(sb, "DESCRIPTION:" + EscapeText(summary));
        AppendLine(sb, string.Create(CultureInfo.InvariantCulture, $"TRIGGER:-PT{(int)PlaytestReminderLead.TotalMinutes}M"));
        AppendLine(sb, "END:VALARM");
        AppendLine(sb, "END:VEVENT");
        AppendLine(sb, "END:VCALENDAR");
        return sb.ToString();
    }

    /// <summary>
    /// ¿Avisar ya de esta sesión? True si no está en <paramref name="remindedIds"/> y: empieza dentro de
    /// <see cref="PlaytestReminderLead"/>, o empezó hace menos de 5 minutos.
    /// </summary>
    /// <remarks>Es decir: −5 min &lt; UntilStart ≤ 15 min. Los Ids se comparan sin distinguir mayúsculas.</remarks>
    public static bool ShouldRemind(PlaytestStatus status, IReadOnlyCollection<string> remindedIds)
    {
        if (status?.Playtest is null) return false;
        var id = status.Playtest.Id?.Trim() ?? "";
        if (remindedIds is not null && remindedIds.Any(r => string.Equals(r?.Trim(), id, StringComparison.OrdinalIgnoreCase)))
            return false;
        return status.UntilStart <= PlaytestReminderLead && status.UntilStart > -PlaytestLateReminder;
    }

    /// <summary>
    /// Releases (de <paramref name="releases"/>, ya ordenadas de más nueva a más vieja) con versión MAYOR que la instalada,
    /// en el mismo orden, máx. 10. Instalada null o sin versión parseable → solo la primera (la última). Lista vacía → vacía.
    /// </summary>
    /// <remarks>Las releases sin versión parseable no se pueden comparar y se omiten.</remarks>
    public static IReadOnlyList<GameRelease> GetReleasesSince(InstalledGameInfo? installed, IReadOnlyList<GameRelease> releases)
    {
        if (releases is not { Count: > 0 }) return Array.Empty<GameRelease>();

        var installedVersion = installed?.ParsedVersion;
        if (installedVersion is null)
        {
            var latest = releases.FirstOrDefault(r => r is not null);
            return latest is null ? Array.Empty<GameRelease>() : new[] { latest };
        }

        return releases
            .Where(r => r?.Version is not null && r.Version > installedVersion)
            .Take(MaxReleasesSince)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>Release de <paramref name="releases"/> que coincide con la versión requerida por la sesión (o null).</summary>
    /// <remarks>Si varias coinciden, la primera de la lista (la más nueva).</remarks>
    public static GameRelease? FindRequiredRelease(Playtest playtest, IReadOnlyList<GameRelease> releases)
    {
        if (string.IsNullOrWhiteSpace(playtest?.RequiredVersion) || releases is not { Count: > 0 }) return null;
        foreach (var release in releases)
        {
            if (release is null) continue;
            var (tag, asset, version) = ReleaseTexts(release);
            if (MatchesBuild(playtest.RequiredVersion, tag, asset, version)) return release;
        }
        return null;
    }

    /// <summary>¿La instalada es la que pide la sesión? null si la sesión no pide versión o no hay nada instalado.</summary>
    public static bool? IsRequiredBuildInstalled(Playtest playtest, InstalledGameInfo? installed)
    {
        if (string.IsNullOrWhiteSpace(playtest?.RequiredVersion) || installed is null) return null;
        return MatchesBuild(playtest.RequiredVersion, installed.Tag, installed.AssetName, InstalledVersionText(installed));
    }

    // ───────────────────────────── Interno ─────────────────────────────

    /// <summary>Texto para comparar builds: sin espacios (ninguno, ni en medio) y en minúsculas. null → "".</summary>
    internal static string NormalizeBuildText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static bool EqualsNonEmpty(string a, string b) => a.Length > 0 && string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>"v0.0.4" → "0.0.4" (solo si a la "v" le sigue un dígito).</summary>
    private static string StripLeadingV(string normalized)
        => normalized.Length > 1 && normalized[0] == 'v' && char.IsAsciiDigit(normalized[1]) ? normalized[1..] : normalized;

    private static bool IsArchiveName(string normalized)
        => normalized.EndsWith(".zip", StringComparison.Ordinal) || normalized.EndsWith(".rar", StringComparison.Ordinal)
           || normalized.EndsWith(".7z", StringComparison.Ordinal) || normalized.EndsWith(".exe", StringComparison.Ordinal);

    /// <summary>Versión de la instalada en forma canónica (la del marcador o, si falta, deducida del asset/tag).</summary>
    private static string? InstalledVersionText(InstalledGameInfo installed)
        => installed.ParsedVersion?.ToString() ?? installed.Version;

    private static (string? Tag, string? Asset, string? Version) ReleaseTexts(GameRelease release)
        => (release.Release?.TagName, release.Asset?.Name, release.Version?.ToString());

    private static bool IsInWindow(Announcement a, DateTimeOffset nowUtc)
        => (a.StartsUtc is null || a.StartsUtc <= nowUtc) && (a.EndsUtc is null || nowUtc < a.EndsUtc);

    private static int LevelRank(AnnouncementLevel level) => level switch
    {
        AnnouncementLevel.Critical => 2,
        AnnouncementLevel.Warning => 1,
        _ => 0,
    };

    /// <summary>Fin de la sesión (duración limitada a 15..720 min; sin desbordar DateTimeOffset).</summary>
    private static DateTimeOffset GetEnd(Playtest p)
    {
        var duration = TimeSpan.FromMinutes(Math.Clamp(p.DurationMinutes, ManifestProvider.MinDurationMinutes,
            ManifestProvider.MaxDurationMinutes));
        return p.StartsUtc > DateTimeOffset.MaxValue - duration ? DateTimeOffset.MaxValue : p.StartsUtc + duration;
    }

    private static string FormatUtc(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Escapa un valor TEXT de iCalendar: \ → \\, ; → \;, , → \, y saltos de línea (CRLF, LF o CR) → \n. Tabuladores →
    /// espacio; resto de caracteres de control, marcas bidi y surrogates sueltos fuera.
    /// </summary>
    internal static string EscapeText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length + 16);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '\\': sb.Append(@"\\"); continue;
                case ';': sb.Append(@"\;"); continue;
                case ',': sb.Append(@"\,"); continue;
                case '\r':
                    if (i + 1 < value.Length && value[i + 1] == '\n') i++;
                    sb.Append(@"\n");
                    continue;
                case '\n': sb.Append(@"\n"); continue;
                case '\t': sb.Append(' '); continue;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    sb.Append(c).Append(value[i + 1]);
                    i++;
                }
                continue;
            }
            if (char.IsLowSurrogate(c) || char.IsControl(c) || IsBidiControl(c)) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Añade una línea de contenido plegada (RFC 5545 §3.1): como mucho 75 octetos UTF-8 por línea física sin contar el
    /// CRLF; las de continuación empiezan por un espacio (que cuenta). Nunca parte un carácter.
    /// </summary>
    private static void AppendLine(StringBuilder sb, string line)
    {
        const int maxOctets = 75;
        var octets = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            var isPair = char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]);
            var size = isPair ? 4 : c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
            if (octets + size > maxOctets)
            {
                sb.Append("\r\n ");
                octets = 1;
            }
            sb.Append(c);
            if (isPair) sb.Append(line[++i]);
            octets += size;
        }
        sb.Append("\r\n");
    }

    private static bool IsBidiControl(char c)
        => c is '‎' or '‏' or '؜' || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩');
}
