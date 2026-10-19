using System.Globalization;
using PalitoLauncher.Core.Missions;

namespace PalitoLauncher.Tests.TestHelpers.M6;

/// <summary>Logs y conjuntos de misiones de ejemplo para los tests de misiones.</summary>
internal static class MissionSamples
{
    public static readonly DateTimeOffset Start = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);

    /// <summary>Constructor de un log de una sesión con el formato del contrato v1.</summary>
    public sealed class LogBuilder
    {
        private readonly List<string> _lines = new();
        private long _seq;
        private double _t;

        public LogBuilder(string version = "0.0.4-prototype.2", string session = "abc123", DateTimeOffset? utc = null)
        {
            Raw("Initialize engine version: 6000.0.1f1");
            Event("session_start", ("game_version", version), ("dev_build", "true"), ("launcher_session", session),
                ("utc", (utc ?? Start).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
        }

        public IReadOnlyList<string> Lines => _lines;

        public LogBuilder Raw(string line)
        {
            _lines.Add(line);
            return this;
        }

        /// <summary>Añade un evento (avanza seq y 1 s de t).</summary>
        public LogBuilder Event(string ev, params (string Key, string Value)[] fields)
        {
            _seq++;
            _t += 1;
            var parts = new List<string>
            {
                "v=1",
                "seq=" + _seq.ToString(CultureInfo.InvariantCulture),
                "t=" + _t.ToString("0.000", CultureInfo.InvariantCulture),
                "ev=" + ev,
            };
            parts.AddRange(fields.Select(f => f.Key + "=" + f.Value));
            _lines.Add(GameEventParser.Prefix + string.Join(" ", parts));
            // Unity escribe una traza de pila y una línea en blanco detrás de cada Debug.Log.
            _lines.Add("UnityEngine.Debug:Log (object)");
            _lines.Add("");
            return this;
        }

        public LogBuilder Match(string role = "client", int players = 2, bool late = false)
            => Event("match_joined", ("role", role), ("slot", "2"), ("players", players.ToString(CultureInfo.InvariantCulture)),
                ("match_time", late ? "60" : "0"), ("late", late ? "true" : "false"));

        public LogBuilder Leave() => Event("match_left", ("reason", "quit"), ("duration", "120"));

        public LogBuilder Reaction(string variant, bool duo = true, string me = "detonator", int targets = 1)
            => Event("reaction", ("variant", variant), ("duo", duo ? "true" : "false"), ("me", me),
                ("targets", targets.ToString(CultureInfo.InvariantCulture)));

        public LogBuilder Kill(string by = "me", string source = "basic")
            => Event("enemy_killed", ("enemy", "chaser"), ("by", by), ("source", source));

        public LogBuilder Cheat(string cheat = "spawn_chaser") => Event("cheat_used", ("cheat", cheat), ("by", "me"));

        public LogBuilder End() => Event("session_end", ("duration", "300"));

        public IReadOnlyList<GameSession> Sessions() => GameEventParser.Parse(_lines);
    }

    public static MissionSet Parse(string json)
        => MissionsProvider.Parse(json) ?? throw new InvalidOperationException("El JSON de ejemplo no es válido.");

    /// <summary>Conjunto con una misión por objetivo dado (ids m0, m1…).</summary>
    public static MissionSet SetWithGoals(params string[] goalsJson)
    {
        var missions = goalsJson.Select((g, i) => $$"""{ "id": "m{{i}}", "title": "Misión {{i}}", "goal": {{g}} }""");
        return Parse($$"""{ "id": "set-1", "missions": [ {{string.Join(",", missions)}} ] }""");
    }

    public static MissionSetState NewState(MissionSet set, DateTimeOffset? created = null)
        => new() { SetId = set.Id, CreatedUtc = created ?? Start.AddHours(-1) };
}
