using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Versioning;

namespace PalitoLauncher.Core.Missions;

/// <summary>
/// Un evento que el juego escribió en su Player.log (contrato v1, docs/EVENTOS-JUEGO.md):
/// <c>[PalitoEvent] v=1 seq=42 t=318.412 ev=reaction variant=vapor_fire_water duo=true me=detonator targets=3</c>.
/// </summary>
public sealed class GameEvent
{
    internal GameEvent(long seq, double time, string name, IReadOnlyDictionary<string, string> fields)
    {
        Seq = seq;
        Time = time;
        Name = name;
        Fields = fields;
    }

    /// <summary>Contador del proceso (1 en session_start).</summary>
    public long Seq { get; }

    /// <summary>Segundos desde que arrancó el proceso del juego.</summary>
    public double Time { get; }

    /// <summary>Nombre del evento (campo ev).</summary>
    public string Name { get; }

    /// <summary>Campos propios del evento (sin v, seq, t ni ev). Claves en minúsculas; sin distinguir mayúsculas al buscar.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; }

    /// <summary>Valor de un campo o null si no viene.</summary>
    public string? Get(string field) => Fields.TryGetValue(field, out var value) ? value : null;

    /// <summary>Valor numérico de un campo (punto decimal) o null si no viene o no es un número.</summary>
    public double? GetNumber(string field)
        => GameEventParser.TryParseNumber(Get(field), out var number) ? number : null;

    /// <summary>Valor booleano (true/false) o null.</summary>
    public bool? GetBool(string field) => Get(field)?.ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    public override string ToString() => $"#{Seq} {Name}";
}

/// <summary>Una ejecución del juego: de un session_start al siguiente (o al final del log).</summary>
public sealed class GameSession
{
    /// <summary>Valor de -palitoSession que pasó el launcher, o null si el juego se abrió por su cuenta («none»).</summary>
    public string? LauncherSession { get; internal set; }

    /// <summary>Application.version del juego tal cual (p.ej. "0.0.4-prototype.2").</summary>
    public string? GameVersionText { get; internal set; }

    /// <summary><see cref="GameVersionText"/> interpretada, o null si no se pudo.</summary>
    public GameVersion? GameVersion { get; internal set; }

    public bool DevBuild { get; internal set; }

    /// <summary>Hora (UTC) a la que arrancó, según el propio juego (campo utc), o null si no vino.</summary>
    public DateTimeOffset? StartUtc { get; internal set; }

    /// <summary>Hay session_end: el juego se cerró con normalidad.</summary>
    public bool EndedCleanly { get; internal set; }

    /// <summary>Eventos que faltan según los huecos de seq (líneas perdidas o cortadas).</summary>
    public long MissingEvents { get; internal set; }

    /// <summary>Eventos en orden (incluido session_start).</summary>
    public List<GameEvent> Events { get; } = new();

    /// <summary>
    /// Identificador para no procesar dos veces la misma ejecución: el -palitoSession si lo hay; si no, hora de arranque y
    /// versión.
    /// </summary>
    public string Key => LauncherSession is { Length: > 0 } id
        ? id
        : $"{StartUtc?.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture) ?? "sin-hora"}|{GameVersionText ?? "?"}";

    /// <summary>Hora (UTC) aproximada de un evento: arranque + su t. Sin hora de arranque, null.</summary>
    public DateTimeOffset? TimeOf(GameEvent e)
    {
        if (StartUtc is not { } start || !double.IsFinite(e.Time) || e.Time < 0) return null;
        // El arranque del juego se apunta unos instantes después del inicio del proceso: se resta su propio t.
        var first = Events.Count > 0 ? Events[0].Time : 0;
        var offset = Math.Clamp(e.Time - first, 0, TimeSpan.FromDays(7).TotalSeconds);
        return start + TimeSpan.FromSeconds(offset);
    }
}

/// <summary>
/// Lee los eventos del juego de un Player.log. Solo mira las líneas que empiezan por <see cref="Prefix"/>; todo lo demás
/// del log (Unity, trazas de pila, [Perf]…) se ignora. Una línea con un par mal formado o una clave repetida se descarta
/// entera. Puro salvo <see cref="ParseFile"/>, que nunca lanza.
/// </summary>
public static class GameEventParser
{
    public const string Prefix = "[PalitoEvent] ";

    /// <summary>Versión del formato que entiende este launcher (campo v).</summary>
    public const int SupportedFormatVersion = 1;

    internal const int MaxLineLength = 512;

    /// <summary>Lo que se lee como mucho de cada log (lo que pase de ahí se ignora).</summary>
    internal const long DefaultMaxBytes = 64L * 1024 * 1024;

    /// <summary>Tope de eventos por sesión (protege la memoria ante un log anómalo).</summary>
    internal const int MaxEventsPerSession = 200_000;

    private static readonly Regex KeyRegex = new(@"^[a-z][a-z0-9_]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex ValueRegex = new(@"^[A-Za-z0-9_.:+\-]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>Sesiones de un conjunto de líneas, en orden. Los eventos anteriores al primer session_start se ignoran.</summary>
    public static IReadOnlyList<GameSession> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var sessions = new List<GameSession>();
        GameSession? current = null;
        long lastSeq = 0;

        foreach (var line in lines)
        {
            var e = ParseLine(line);
            if (e is null) continue;

            if (e.Name == "session_start")
            {
                current = StartSession(e);
                sessions.Add(current);
                lastSeq = e.Seq;
                continue;
            }

            if (current is null) continue;              // de antes del primer session_start
            if (e.Seq <= lastSeq) continue;             // duplicada o desordenada
            if (current.Events.Count >= MaxEventsPerSession) continue;

            if (e.Seq > lastSeq + 1) current.MissingEvents += e.Seq - lastSeq - 1;
            lastSeq = e.Seq;
            current.Events.Add(e);
            if (e.Name == "session_end") current.EndedCleanly = true;
        }

        return sessions;
    }

    /// <summary>
    /// Sesiones de un archivo (normalmente Player.log o Player-prev.log). Lee con FileShare.ReadWrite | FileShare.Delete
    /// (Unity puede estar escribiéndolo) y como mucho <paramref name="maxBytes"/>. Si no existe o no se puede leer, lista
    /// vacía. Nunca lanza.
    /// </summary>
    public static IReadOnlyList<GameSession> ParseFile(string? path, long maxBytes = DefaultMaxBytes)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<GameSession>();
        try
        {
            if (!File.Exists(path)) return Array.Empty<GameSession>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return Parse(ReadLines(reader, maxBytes, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"Misiones: no se pudo leer el log del juego {path}.", ex);
            return Array.Empty<GameSession>();
        }
        catch (Exception ex)
        {
            Log.Error($"Misiones: error inesperado al leer el log del juego {path}.", ex);
            return Array.Empty<GameSession>();
        }
    }

    /// <summary>Un evento de una línea, o null si la línea no es un evento válido de una versión conocida del formato.</summary>
    internal static GameEvent? ParseLine(string? line)
    {
        if (line is null) return null;
        line = line.TrimEnd('\r', '\n');
        if (line.Length > MaxLineLength || !line.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        var tokens = line[Prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4) return null;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            var eq = token.IndexOf('=');
            if (eq <= 0 || eq == token.Length - 1) return null;
            var key = token[..eq];
            var value = token[(eq + 1)..];
            if (!KeyRegex.IsMatch(key) || !ValueRegex.IsMatch(value)) return null;
            if (!fields.TryAdd(key, value)) return null; // clave repetida
        }

        if (!fields.TryGetValue("v", out var v) || v != SupportedFormatVersion.ToString(CultureInfo.InvariantCulture)) return null;
        if (!fields.TryGetValue("seq", out var seqText)
            || !long.TryParse(seqText, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) || seq < 1)
        {
            return null;
        }
        if (!fields.TryGetValue("t", out var tText) || !TryParseNumber(tText, out var time) || time < 0) return null;
        if (!fields.TryGetValue("ev", out var name) || !KeyRegex.IsMatch(name)) return null;

        fields.Remove("v");
        fields.Remove("seq");
        fields.Remove("t");
        fields.Remove("ev");
        return new GameEvent(seq, time, name, fields);
    }

    /// <summary>Número con punto decimal (InvariantCulture), sin separador de miles. "318,412" no vale.</summary>
    internal static bool TryParseNumber(string? text, out double number)
    {
        number = 0;
        if (string.IsNullOrEmpty(text)) return false;
        return double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number)
               && double.IsFinite(number);
    }

    private static GameSession StartSession(GameEvent e)
    {
        var session = new GameSession
        {
            GameVersionText = e.Get("game_version"),
            DevBuild = e.GetBool("dev_build") ?? false,
        };

        var launcherSession = e.Get("launcher_session");
        session.LauncherSession = string.IsNullOrEmpty(launcherSession) || launcherSession.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? null
            : launcherSession;

        if (GameVersion.TryParse(session.GameVersionText, out var version)) session.GameVersion = version;

        var utc = e.Get("utc");
        if (utc is not null && DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start))
        {
            session.StartUtc = start;
        }

        session.Events.Add(e);
        return session;
    }

    private static IEnumerable<string> ReadLines(StreamReader reader, long maxBytes, string path)
    {
        long read = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            read += line.Length + 1;
            if (read > maxBytes)
            {
                Log.Warn($"Misiones: el log {path} pasa de {maxBytes / (1024 * 1024)} MB; se ignora el resto.");
                yield break;
            }
            yield return line;
        }
    }
}
