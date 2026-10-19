using PalitoLauncher.Core.Versioning;

namespace PalitoLauncher.Core.Missions;

// Modelo de launcher/missions.json (repo del juego) ya saneado por MissionsProvider, y estado de cada conjunto en el PC.
// Referencia del formato: docs/remoto/MISIONES.md.

/// <summary>Dónde se acumula lo que pide un objetivo.</summary>
public enum MissionScope
{
    /// <summary>Todo dentro de la misma partida (de match_joined a match_left).</summary>
    Match,

    /// <summary>Dentro de la misma ejecución del juego (por defecto).</summary>
    Session,

    /// <summary>Acumulado entre sesiones mientras dure el conjunto.</summary>
    Set,
}

public enum ConditionOperator
{
    /// <summary>Igual (texto sin distinguir mayúsculas, o número).</summary>
    Equal,

    /// <summary>Empieza por (valor escrito como "prefijo*").</summary>
    Prefix,

    /// <summary>Alguna de <see cref="FieldCondition.Alternatives"/>.</summary>
    AnyOf,

    GreaterOrEqual,
    Greater,
    LessOrEqual,
    Less,

    /// <summary>Distinto (texto o número). Un campo que no viene tampoco cumple esta condición.</summary>
    NotEqual,
}

/// <summary>Una condición de "where" sobre un campo del evento.</summary>
public sealed class FieldCondition
{
    public string Field { get; init; } = "";
    public ConditionOperator Operator { get; init; }

    /// <summary>Texto a comparar (Equal/NotEqual de texto, Prefix sin el '*', booleanos como "true"/"false").</summary>
    public string? Text { get; init; }

    /// <summary>Número a comparar (Equal/NotEqual numéricos y comparaciones).</summary>
    public double? Number { get; init; }

    /// <summary>Para AnyOf: condiciones Equal/Prefix sobre el mismo campo, basta con una.</summary>
    public IReadOnlyList<FieldCondition> Alternatives { get; init; } = Array.Empty<FieldCondition>();
}

/// <summary>Regla sobre los eventos: tantos «event» que cumplan «where» (o tanta suma de «sum») en el «scope» indicado.</summary>
public sealed class MissionGoal
{
    public string Event { get; init; } = "";
    public IReadOnlyList<FieldCondition> Where { get; init; } = Array.Empty<FieldCondition>();

    /// <summary>Cuántos eventos (o cuánta suma) hacen falta. ≥ 1.</summary>
    public double Count { get; init; } = 1;

    /// <summary>Si no es null, en vez de contar eventos se suma este campo numérico.</summary>
    public string? Sum { get; init; }

    public MissionScope Scope { get; init; } = MissionScope.Session;
}

/// <summary>Una misión del conjunto.</summary>
public sealed class Mission
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Hint { get; init; }

    /// <summary>No se detecta sola: el jugador marca Funcionó / Falló / No la probé.</summary>
    public bool Manual { get; init; }

    /// <summary>Pregunta de comprobación tras completarla («¿Se vio el titular?»): Sí / No + nota.</summary>
    public string? Check { get; init; }

    /// <summary>Cuenta lo conseguido en partidas con trucos.</summary>
    public bool AllowCheats { get; init; }

    /// <summary>Con false, los eventos de una partida solo cuentan si en ella hubo al menos 2 jugadores.</summary>
    public bool AllowSolo { get; init; } = true;

    /// <summary>Objetivos: uno (lo normal) o varios de «all» / «any». Vacío en las manuales.</summary>
    public IReadOnlyList<MissionGoal> Goals { get; init; } = Array.Empty<MissionGoal>();

    /// <summary>True: hacen falta todos los objetivos («all» o uno solo). False: basta con uno («any»).</summary>
    public bool RequireAllGoals { get; init; } = true;
}

/// <summary>Conjunto de misiones de un playtest (launcher/missions.json).</summary>
public sealed class MissionSet
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = MissionsProvider.DefaultTitle;
    public string? Intro { get; init; }

    /// <summary>Versión mínima del juego tal cual se escribió en el JSON (para mostrarla).</summary>
    public string? MinGameVersionText { get; init; }

    /// <summary>Versión mínima interpretada; las sesiones con una versión menor no cuentan.</summary>
    public GameVersion? MinGameVersion { get; init; }

    public DateTimeOffset? StartsUtc { get; init; }
    public DateTimeOffset? EndsUtc { get; init; }

    /// <summary>Webhook propio de los resultados (forma canónica) o null: entonces se usa el de la encuesta.</summary>
    public string? DiscordWebhookUrl { get; init; }

    public IReadOnlyList<Mission> Missions { get; init; } = Array.Empty<Mission>();

    /// <summary>¿Se muestra ahora? Dentro de [StartsUtc, EndsUtc) (los límites que falten no restringen).</summary>
    public bool IsActive(DateTimeOffset nowUtc)
        => (StartsUtc is not { } start || nowUtc >= start) && (EndsUtc is not { } end || nowUtc < end);

    /// <summary>¿Ya terminó (pasó EndsUtc)?</summary>
    public bool HasEnded(DateTimeOffset nowUtc) => EndsUtc is { } end && nowUtc >= end;
}

/// <summary>Estado de una misión para este jugador.</summary>
public enum MissionState
{
    Pending,
    Done,
    Failed,

    /// <summary>«No la probé».</summary>
    Skipped,
}

/// <summary>Progreso de un objetivo de una misión.</summary>
public sealed class MissionPartProgress
{
    public bool Done { get; set; }

    /// <summary>Lo acumulado en objetivos con scope «set».</summary>
    public double Progress { get; set; }
}

/// <summary>Lo que lleva un jugador en una misión (se guarda en disco).</summary>
public sealed class MissionProgress
{
    public MissionState State { get; set; } = MissionState.Pending;

    /// <summary>Un elemento por objetivo, en el mismo orden que Mission.Goals.</summary>
    public List<MissionPartProgress> Parts { get; set; } = new();

    /// <summary>Cuándo se completó (sola o marcada a mano).</summary>
    public DateTimeOffset? CompletedUtc { get; set; }

    /// <summary>Versión del juego con la que se completó.</summary>
    public string? GameVersion { get; set; }

    /// <summary>Se completó en una partida con trucos (solo si la misión los permite).</summary>
    public bool WithCheats { get; set; }

    /// <summary>Se completó sola (eventos del juego) y no marcada a mano.</summary>
    public bool Automatic { get; set; }

    /// <summary>Respuesta a la pregunta de comprobación: true = Sí, false = No, null = sin responder.</summary>
    public bool? CheckAnswer { get; set; }

    /// <summary>Nota del jugador (qué falló o qué se vio mal). Máx. 300.</summary>
    public string? Note { get; set; }

    /// <summary>Errores del juego (log_error) en la partida en la que se completó: pista para el equipo.</summary>
    public int ErrorsNearby { get; set; }
}

/// <summary>Estado de un conjunto para este jugador: %LOCALAPPDATA%\PalitoLauncher\missions\&lt;id&gt;.json.</summary>
public sealed class MissionSetState
{
    public string SetId { get; set; } = "";

    /// <summary>Primera vez que este launcher vio el conjunto. Sin startsUtc, las sesiones anteriores no cuentan.</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>Identificador aleatorio de este jugador para este conjunto (distingue a los anónimos en Discord).</summary>
    public string ReporterId { get; set; } = Guid.NewGuid().ToString("N")[..6];

    public Dictionary<string, MissionProgress> Missions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Claves (GameSession.Key) de las sesiones ya procesadas. Máx. 200 (se olvidan las más viejas).</summary>
    public List<string> ProcessedSessions { get; set; } = new();

    /// <summary>Sesiones con eventos que ya contaron para este conjunto.</summary>
    public int SessionsCounted { get; set; }

    /// <summary>Sesiones que no contaron por tener una versión del juego anterior a minGameVersion.</summary>
    public int SessionsTooOld { get; set; }

    /// <summary>Mensaje de Discord que se va editando, y el webhook (sin token) en el que está.</summary>
    public string? DiscordMessageId { get; set; }
    public string? DiscordWebhookKey { get; set; }

    /// <summary>Hay cambios sin enviar a Discord.</summary>
    public bool Dirty { get; set; }

    /// <summary>Ya se envió la versión final (tras endsUtc).</summary>
    public bool FinalSent { get; set; }

    public DateTimeOffset? LastSentUtc { get; set; }

    /// <summary>Progreso de una misión (lo crea si no existe, con un elemento por objetivo).</summary>
    public MissionProgress GetOrCreate(Mission mission)
    {
        if (!Missions.TryGetValue(mission.Id, out var progress) || progress is null)
        {
            progress = new MissionProgress();
            Missions[mission.Id] = progress;
        }
        progress.Parts ??= new List<MissionPartProgress>();
        while (progress.Parts.Count < mission.Goals.Count) progress.Parts.Add(new MissionPartProgress());
        if (progress.Parts.Count > mission.Goals.Count) progress.Parts.RemoveRange(mission.Goals.Count, progress.Parts.Count - mission.Goals.Count);
        return progress;
    }
}
