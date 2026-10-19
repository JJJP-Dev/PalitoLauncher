using System.Globalization;

namespace PalitoLauncher.Core.Missions;

/// <summary>Resultado de aplicar sesiones del juego a un conjunto de misiones.</summary>
public sealed class MissionEvaluation
{
    /// <summary>Misiones que se completaron en esta pasada (en el orden del conjunto).</summary>
    public List<Mission> NewlyCompleted { get; } = new();

    /// <summary>Sesiones que contaron (con eventos y dentro de la ventana).</summary>
    public int SessionsCounted { get; internal set; }

    /// <summary>Sesiones descartadas por tener una versión del juego anterior a minGameVersion.</summary>
    public int SessionsTooOld { get; internal set; }

    /// <summary>Cambió algo del estado (progreso, misiones completadas o sesiones procesadas).</summary>
    public bool Changed { get; internal set; }
}

/// <summary>
/// Aplica las sesiones del juego (eventos de Player.log) a las misiones de un conjunto. Puro y determinista: no hace E/S
/// y solo modifica el <see cref="MissionSetState"/> que recibe.
///
/// Reglas (docs/MISIONES.md, sección 4): cada sesión se procesa una vez (GameSession.Key); solo cuentan las que arrancaron
/// dentro de la ventana del conjunto (desde StartsUtc, o desde que el launcher vio el conjunto, hasta EndsUtc) y con una
/// versión ≥ MinGameVersion. Una partida va de match_joined a match_left; desde el primer cheat_used de una partida, sus
/// eventos están «con trucos» y no cuentan para las misiones sin AllowCheats. AllowSolo = false exige que en esa partida
/// hubiera al menos 2 jugadores (los eventos de fuera de una partida, como join_result, siempre cuentan).
/// </summary>
public static class MissionEvaluator
{
    /// <summary>Margen para sesiones que arrancaron justo antes de que el launcher viera el conjunto (relojes, arranque).</summary>
    internal static readonly TimeSpan CreatedTolerance = TimeSpan.FromMinutes(10);

    internal const int MaxProcessedSessions = 200;

    public static MissionEvaluation Apply(MissionSet set, MissionSetState state, IEnumerable<GameSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(sessions);

        var result = new MissionEvaluation();
        foreach (var mission in set.Missions) state.GetOrCreate(mission);

        var processed = new HashSet<string>(state.ProcessedSessions ?? new List<string>(), StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (session is null || session.Events.Count == 0) continue;
            var key = session.Key;
            if (processed.Contains(key)) continue;
            if (!IsInWindow(set, state, session)) continue; // ni se marca: aún puede contar si cambia la ventana

            processed.Add(key);
            state.ProcessedSessions ??= new List<string>();
            state.ProcessedSessions.Add(key);
            if (state.ProcessedSessions.Count > MaxProcessedSessions)
                state.ProcessedSessions.RemoveRange(0, state.ProcessedSessions.Count - MaxProcessedSessions);
            result.Changed = true;

            if (set.MinGameVersion is { } min && (session.GameVersion is null || session.GameVersion < min))
            {
                state.SessionsTooOld++;
                result.SessionsTooOld++;
                continue;
            }

            state.SessionsCounted++;
            result.SessionsCounted++;
            ApplySession(set, state, session, result);
        }

        if (result.Changed || result.NewlyCompleted.Count > 0) state.Dirty = true;
        return result;
    }

    /// <summary>¿Una entrada del log cumple las condiciones de un objetivo (nombre del evento y «where»)?</summary>
    public static bool Matches(MissionGoal goal, GameEvent e)
    {
        if (!string.Equals(goal.Event, e.Name, StringComparison.Ordinal)) return false;
        foreach (var condition in goal.Where)
            if (!Matches(condition, e.Get(condition.Field))) return false;
        return true;
    }

    internal static bool Matches(FieldCondition condition, string? value)
    {
        if (value is null) return false; // un campo que no viene no cumple ninguna condición
        switch (condition.Operator)
        {
            case ConditionOperator.Equal:
                return condition.Number is { } n
                    ? GameEventParser.TryParseNumber(value, out var v) && v == n
                    : string.Equals(value, condition.Text, StringComparison.OrdinalIgnoreCase);
            case ConditionOperator.Prefix:
                return condition.Text is { } prefix && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            case ConditionOperator.AnyOf:
                return condition.Alternatives.Any(a => Matches(a, value));
            case ConditionOperator.NotEqual:
                return condition.Number is { } ne
                    ? GameEventParser.TryParseNumber(value, out var nv) && nv != ne
                    : !string.Equals(value, condition.Text, StringComparison.OrdinalIgnoreCase);
            default:
                if (condition.Number is not { } limit || !GameEventParser.TryParseNumber(value, out var number)) return false;
                return condition.Operator switch
                {
                    ConditionOperator.GreaterOrEqual => number >= limit,
                    ConditionOperator.Greater => number > limit,
                    ConditionOperator.LessOrEqual => number <= limit,
                    ConditionOperator.Less => number < limit,
                    _ => false,
                };
        }
    }

    private static bool IsInWindow(MissionSet set, MissionSetState state, GameSession session)
    {
        // Sin hora de arranque no se puede situar: se acepta solo si el conjunto no tiene ventana.
        if (session.StartUtc is not { } start) return set.StartsUtc is null && set.EndsUtc is null;
        var from = set.StartsUtc ?? state.CreatedUtc - CreatedTolerance;
        if (start < from) return false;
        if (set.EndsUtc is { } end && start >= end) return false;
        return true;
    }

    // ───────────────────────────── Una sesión ─────────────────────────────

    /// <summary>Eventos de la sesión con su partida (-1 = fuera de partida) y si van «con trucos».</summary>
    private readonly record struct TaggedEvent(GameEvent Event, int Match, bool Tainted);

    private static void ApplySession(MissionSet set, MissionSetState state, GameSession session, MissionEvaluation result)
    {
        var tagged = new List<TaggedEvent>(session.Events.Count);
        var maxPlayers = new Dictionary<int, double>();
        var errors = new Dictionary<int, int>();
        int match = -1, matches = 0;
        bool tainted = false;

        foreach (var e in session.Events)
        {
            if (e.Name == "match_joined")
            {
                match = matches++;
                tainted = false;
            }
            if (e.Name == "cheat_used" && match >= 0) tainted = true;

            if (e.GetNumber("players") is { } players && match >= 0 && e.Name is "match_joined" or "player_joined" or "player_left")
                maxPlayers[match] = Math.Max(maxPlayers.TryGetValue(match, out var m) ? m : 0, players);
            if (e.Name == "log_error") errors[match] = (errors.TryGetValue(match, out var c) ? c : 0) + 1;

            tagged.Add(new TaggedEvent(e, match, match >= 0 && tainted));

            if (e.Name == "match_left")
            {
                match = -1;
                tainted = false;
            }
        }

        foreach (var mission in set.Missions)
        {
            if (mission.Manual || mission.Goals.Count == 0) continue;
            var progress = state.GetOrCreate(mission);
            if (progress.State == MissionState.Done) continue;

            GameEvent? completingEvent = null;
            bool completingTainted = false;
            int completingMatch = -1;

            for (var i = 0; i < mission.Goals.Count; i++)
            {
                var part = progress.Parts[i];
                if (part.Done) continue;
                var goal = mission.Goals[i];
                var hit = EvaluateGoal(mission, goal, part, tagged, maxPlayers);
                if (hit is { } h)
                {
                    part.Done = true;
                    // La misión se da por completada con el último objetivo que se cumple.
                    if (completingEvent is null || h.Event.Seq > completingEvent.Seq)
                    {
                        completingEvent = h.Event;
                        completingTainted = h.Tainted;
                        completingMatch = h.Match;
                    }
                }
            }

            bool done = mission.RequireAllGoals ? progress.Parts.All(p => p.Done) : progress.Parts.Any(p => p.Done);
            if (!done) continue;

            // Con «all», los objetivos pudieron cumplirse en sesiones anteriores: si en esta no se cumplió ninguno, se usa
            // el último evento de la sesión como referencia de la hora.
            var reference = completingEvent ?? session.Events[^1];
            progress.State = MissionState.Done;
            progress.Automatic = true;
            progress.CompletedUtc = session.TimeOf(reference) ?? session.StartUtc;
            progress.GameVersion = session.GameVersionText;
            progress.WithCheats = completingTainted;
            progress.ErrorsNearby = errors.TryGetValue(completingMatch, out var n) ? n : 0;
            result.NewlyCompleted.Add(mission);
            result.Changed = true;
        }
    }

    /// <summary>
    /// Evalúa un objetivo sobre una sesión. Devuelve el evento con el que se alcanza la cantidad pedida, o null. Con
    /// scope «set» acumula en <paramref name="part"/>.Progress lo que haya en la sesión aunque no llegue.
    /// </summary>
    private static TaggedEvent? EvaluateGoal(Mission mission, MissionGoal goal, MissionPartProgress part,
        List<TaggedEvent> events, Dictionary<int, double> maxPlayers)
    {
        var totals = new Dictionary<int, double>(); // por partida (scope match)
        double sessionTotal = 0;
        double setTotal = part.Progress;
        TaggedEvent? hit = null;

        foreach (var t in events)
        {
            if (!Matches(goal, t.Event)) continue;
            if (t.Tainted && !mission.AllowCheats) continue;
            if (!mission.AllowSolo && t.Match >= 0 && (!maxPlayers.TryGetValue(t.Match, out var players) || players < 2)) continue;

            double amount = 1;
            if (goal.Sum is { } field)
            {
                amount = t.Event.GetNumber(field) ?? 0;
                if (amount <= 0) continue;
            }

            switch (goal.Scope)
            {
                case MissionScope.Match:
                    var total = (totals.TryGetValue(t.Match, out var current) ? current : 0) + amount;
                    totals[t.Match] = total;
                    if (hit is null && total >= goal.Count) hit = t;
                    break;
                case MissionScope.Session:
                    sessionTotal += amount;
                    if (hit is null && sessionTotal >= goal.Count) hit = t;
                    break;
                case MissionScope.Set:
                    setTotal += amount;
                    if (hit is null && setTotal >= goal.Count) hit = t;
                    break;
            }
        }

        if (goal.Scope == MissionScope.Set) part.Progress = Math.Min(setTotal, goal.Count);
        return hit;
    }

    /// <summary>Progreso para mostrar («12 / 30») de una misión con un único objetivo de scope «set», o null.</summary>
    public static string? ProgressText(Mission mission, MissionProgress? progress)
    {
        if (mission.Manual || mission.Goals.Count != 1 || mission.Goals[0] is not { Scope: MissionScope.Set, Count: > 1 } goal) return null;
        var done = progress?.Parts is { Count: > 0 } parts ? parts[0].Progress : 0;
        var inv = CultureInfo.InvariantCulture;
        return $"{Math.Floor(Math.Min(done, goal.Count)).ToString("0", inv)} / {goal.Count.ToString("0", inv)}";
    }
}
