using PalitoLauncher.Core.Missions;
using PalitoLauncher.Tests.TestHelpers.M6;
using static PalitoLauncher.Tests.TestHelpers.M6.MissionSamples;

namespace PalitoLauncher.Tests;

public sealed class MissionEvaluatorTests
{
    private const string VaporDuo = """{ "event": "reaction", "where": { "variant": "vapor_*", "duo": true, "me": ["aura", "detonator"] } }""";

    private static MissionProgress P(MissionSetState state, string id) => state.Missions[id];

    [Fact]
    public void Apply_CompletesAMissionAndRecordsWhenAndWithWhat()
    {
        var set = SetWithGoals(VaporDuo);
        var state = NewState(set);
        var log = new LogBuilder().Match().Reaction("electrocution_water_electric").Reaction("vapor_fire_water").Leave();

        var result = MissionEvaluator.Apply(set, state, log.Sessions());

        var mission = Assert.Single(result.NewlyCompleted);
        Assert.Equal("m0", mission.Id);
        var p = P(state, "m0");
        Assert.Equal(MissionState.Done, p.State);
        Assert.True(p.Automatic);
        Assert.False(p.WithCheats);
        Assert.Equal("0.0.4-prototype.2", p.GameVersion);
        Assert.Equal(Start.AddSeconds(3), p.CompletedUtc); // session_start t=1, Vapor t=4
        Assert.Equal(1, result.SessionsCounted);
        Assert.True(state.Dirty);
    }

    [Theory]
    [InlineData("vapor_water_fire", true, "aura", true)]
    [InlineData("vapor_fire_water", false, "detonator", false)]   // en solitario
    [InlineData("vapor_fire_water", true, "none", false)]         // lo hicieron otros
    [InlineData("overload_fire_electric", true, "detonator", false)]
    public void Apply_ChecksEveryCondition(string variant, bool duo, string me, bool expected)
    {
        var set = SetWithGoals(VaporDuo);
        var state = NewState(set);
        MissionEvaluator.Apply(set, state, new LogBuilder().Match().Reaction(variant, duo, me).Sessions());
        Assert.Equal(expected, P(state, "m0").State == MissionState.Done);
    }

    [Theory]
    [InlineData("""{ ">=": 4 }""", 4, true)]
    [InlineData("""{ ">=": 4 }""", 3, false)]
    [InlineData("""{ ">": 3 }""", 4, true)]
    [InlineData("""{ "<=": 2 }""", 2, true)]
    [InlineData("""{ "<": 2 }""", 2, false)]
    [InlineData("""{ "!=": 2 }""", 3, true)]
    [InlineData("""{ "!=": 2 }""", 2, false)]
    [InlineData("4", 4, true)]
    [InlineData("[3, 4]", 4, true)]
    public void Apply_NumericComparisons(string condition, int players, bool expected)
    {
        var set = SetWithGoals($$"""{ "event": "match_joined", "where": { "players": {{condition}} } }""");
        var state = NewState(set);
        MissionEvaluator.Apply(set, state, new LogBuilder().Match(players: players).Sessions());
        Assert.Equal(expected, P(state, "m0").State == MissionState.Done);
    }

    [Fact]
    public void Apply_AMissingFieldNeverMatches()
    {
        var set = SetWithGoals("""{ "event": "match_joined", "where": { "nuevo_campo": { "!=": "x" } } }""");
        var state = NewState(set);
        MissionEvaluator.Apply(set, state, new LogBuilder().Match().Sessions());
        Assert.Equal(MissionState.Pending, P(state, "m0").State);
    }

    [Fact]
    public void Apply_CountAndSumWithinTheSession()
    {
        var set = SetWithGoals(
            """{ "event": "enemy_killed", "where": { "by": "me" }, "count": 3 }""",
            """{ "event": "reaction", "sum": "targets", "count": 5 }""");
        var state = NewState(set);
        var log = new LogBuilder().Match()
            .Kill().Kill("ally").Kill()
            .Reaction("vapor_fire_water", targets: 2).Reaction("vapor_fire_water", targets: 2)
            .Leave();

        MissionEvaluator.Apply(set, state, log.Sessions());

        Assert.Equal(MissionState.Pending, P(state, "m0").State); // solo 2 «by=me»
        Assert.Equal(MissionState.Pending, P(state, "m1").State); // 4 de 5
    }

    [Fact]
    public void Apply_MatchScopeDoesNotAddUpAcrossMatches()
    {
        var set = SetWithGoals("""{ "event": "enemy_killed", "count": 2, "scope": "match" }""");
        var state = NewState(set);
        var log = new LogBuilder().Match().Kill().Leave().Match().Kill().Leave();
        MissionEvaluator.Apply(set, state, log.Sessions());
        Assert.Equal(MissionState.Pending, P(state, "m0").State);

        var state2 = NewState(set);
        MissionEvaluator.Apply(set, state2, new LogBuilder().Match().Kill().Kill().Leave().Sessions());
        Assert.Equal(MissionState.Done, P(state2, "m0").State);
    }

    [Fact]
    public void Apply_SetScopeAccumulatesAcrossSessionsAndCountsEachSessionOnce()
    {
        var set = SetWithGoals("""{ "event": "enemy_killed", "count": 3, "scope": "set" }""");
        var state = NewState(set);
        var first = new LogBuilder(session: "s1").Match().Kill().Kill().Leave().Sessions();

        MissionEvaluator.Apply(set, state, first);
        Assert.Equal(2, P(state, "m0").Parts[0].Progress);
        Assert.Equal("2 / 3", MissionEvaluator.ProgressText(set.Missions[0], P(state, "m0")));

        // La misma sesión otra vez (Player.log releído al arrancar): no suma.
        var again = MissionEvaluator.Apply(set, state, first);
        Assert.Equal(0, again.SessionsCounted);
        Assert.Equal(2, P(state, "m0").Parts[0].Progress);

        MissionEvaluator.Apply(set, state, new LogBuilder(session: "s2", utc: Start.AddHours(1)).Match().Kill().Sessions());
        Assert.Equal(MissionState.Done, P(state, "m0").State);
        Assert.Equal(new[] { "s1", "s2" }, state.ProcessedSessions);
    }

    [Fact]
    public void Apply_CheatsTaintTheRestOfTheMatch()
    {
        var set = Parse("""
            { "id": "s", "missions": [
              { "id": "sin", "title": "Sin trucos", "goal": { "event": "enemy_killed" } },
              { "id": "con", "title": "Con trucos", "allowCheats": true, "goal": { "event": "enemy_killed" } } ] }
            """);
        var state = NewState(set);
        var log = new LogBuilder().Match().Cheat().Kill().Leave();

        MissionEvaluator.Apply(set, state, log.Sessions());

        Assert.Equal(MissionState.Pending, P(state, "sin").State);
        Assert.Equal(MissionState.Done, P(state, "con").State);
        Assert.True(P(state, "con").WithCheats);

        // En la partida siguiente, sin trucos, ya cuenta.
        var state2 = NewState(set);
        MissionEvaluator.Apply(set, state2, new LogBuilder().Match().Cheat().Leave().Match().Kill().Sessions());
        Assert.Equal(MissionState.Done, P(state2, "sin").State);
    }

    [Fact]
    public void Apply_AllowSoloFalseNeedsTwoPlayersInThatMatch()
    {
        var set = Parse("""
            { "id": "s", "missions": [
              { "id": "a", "title": "A", "allowSolo": false, "goal": { "event": "enemy_killed" } },
              { "id": "b", "title": "B", "allowSolo": false, "goal": { "event": "join_result", "where": { "result": "ok" } } } ] }
            """);
        var state = NewState(set);
        var log = new LogBuilder()
            .Event("join_result", ("result", "ok"), ("target", "lan"), ("ms", "500")) // fuera de partida: cuenta
            .Match(role: "host", players: 1).Kill().Leave();

        MissionEvaluator.Apply(set, state, log.Sessions());
        Assert.Equal(MissionState.Pending, P(state, "a").State);
        Assert.Equal(MissionState.Done, P(state, "b").State);

        var state2 = NewState(set);
        var withFriend = new LogBuilder().Match(role: "host", players: 1)
            .Event("player_joined", ("slot", "2"), ("players", "2")).Kill().Leave();
        MissionEvaluator.Apply(set, state2, withFriend.Sessions());
        Assert.Equal(MissionState.Done, P(state2, "a").State);
    }

    [Fact]
    public void Apply_AllAndAny()
    {
        var set = Parse("""
            { "id": "s", "missions": [
              { "id": "todas", "title": "Las tres", "goal": { "all": [
                  { "event": "reaction", "where": { "variant": "vapor_*", "me": { "!=": "none" } }, "scope": "set" },
                  { "event": "reaction", "where": { "variant": "electrocution_*", "me": { "!=": "none" } }, "scope": "set" },
                  { "event": "reaction", "where": { "variant": "overload_*", "me": { "!=": "none" } }, "scope": "set" } ] } },
              { "id": "alguna", "title": "Cuatro", "goal": { "any": [
                  { "event": "match_joined", "where": { "players": { ">=": 4 } } },
                  { "event": "player_joined", "where": { "players": { ">=": 4 } } } ] } } ] }
            """);
        var state = NewState(set);

        MissionEvaluator.Apply(set, state, new LogBuilder(session: "s1").Match(players: 3)
            .Reaction("vapor_fire_water").Reaction("overload_fire_electric", me: "none")
            .Event("player_joined", ("slot", "4"), ("players", "4")).Sessions());
        Assert.Equal(MissionState.Pending, P(state, "todas").State);
        Assert.Equal(new[] { true, false, false }, P(state, "todas").Parts.Select(p => p.Done));
        Assert.Equal(MissionState.Done, P(state, "alguna").State);

        MissionEvaluator.Apply(set, state, new LogBuilder(session: "s2", utc: Start.AddHours(1)).Match()
            .Reaction("electrocution_water_electric").Reaction("overload_electric_fire", me: "aura").Sessions());
        Assert.Equal(MissionState.Done, P(state, "todas").State);
    }

    [Fact]
    public void Apply_RespectsTheWindowAndTheMinimumVersion()
    {
        var set = Parse("""
            { "id": "s", "minGameVersion": "0.0.4-prototype.2", "startsUtc": "2026-10-31T19:00:00Z", "endsUtc": "2026-11-01T00:00:00Z",
              "missions": [ { "id": "a", "title": "A", "goal": { "event": "match_joined" } } ] }
            """);
        var state = NewState(set);

        var before = new LogBuilder(session: "antes", utc: new DateTimeOffset(2026, 10, 31, 18, 0, 0, TimeSpan.Zero)).Match();
        var after = new LogBuilder(session: "despues", utc: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)).Match();
        var old = new LogBuilder(version: "0.0.3-prototype.2", session: "vieja").Match();

        var result = MissionEvaluator.Apply(set, state, before.Sessions().Concat(after.Sessions()).Concat(old.Sessions()));

        Assert.Equal(MissionState.Pending, P(state, "a").State);
        Assert.Equal(1, result.SessionsTooOld);
        Assert.Equal(new[] { "vieja" }, state.ProcessedSessions); // las de fuera de la ventana ni se marcan

        MissionEvaluator.Apply(set, state, new LogBuilder(session: "buena").Match().Sessions());
        Assert.Equal(MissionState.Done, P(state, "a").State);
    }

    [Fact]
    public void Apply_WithoutStartsUtcIgnoresSessionsFromBeforeTheSetWasSeen()
    {
        var set = SetWithGoals("""{ "event": "match_joined" }""");
        var state = NewState(set, created: Start.AddHours(1));

        MissionEvaluator.Apply(set, state, new LogBuilder(session: "vieja").Match().Sessions()); // a las 20:00, visto a las 21:00
        Assert.Equal(MissionState.Pending, P(state, "m0").State);

        MissionEvaluator.Apply(set, state, new LogBuilder(session: "casi", utc: Start.AddMinutes(55)).Match().Sessions()); // margen de 10 min
        Assert.Equal(MissionState.Done, P(state, "m0").State);
    }

    [Fact]
    public void Apply_ManualMissionsAndUserMarks()
    {
        var set = Parse("""
            { "id": "s", "missions": [
              { "id": "manual", "title": "A mano", "manual": true },
              { "id": "auto", "title": "Sola", "goal": { "event": "match_joined" } } ] }
            """);
        var state = NewState(set);
        state.GetOrCreate(set.Missions[1]).State = MissionState.Failed;
        state.Missions["auto"].Note = "No me dejaba entrar";

        MissionEvaluator.Apply(set, state, new LogBuilder().Match().Sessions());

        Assert.Equal(MissionState.Pending, P(state, "manual").State); // nunca se completa sola
        Assert.Equal(MissionState.Done, P(state, "auto").State);      // funcionó después de fallar…
        Assert.Equal("No me dejaba entrar", P(state, "auto").Note);   // …y se conserva la nota
    }

    [Fact]
    public void Apply_CountsErrorsInTheMatchAsAHint()
    {
        var set = SetWithGoals("""{ "event": "enemy_killed" }""");
        var state = NewState(set);
        var log = new LogBuilder().Match()
            .Event("log_error", ("type", "exception"), ("sig", "1a2b3c4d"), ("count", "1"))
            .Kill().Leave();

        MissionEvaluator.Apply(set, state, log.Sessions());
        Assert.Equal(1, P(state, "m0").ErrorsNearby);
    }

    [Fact]
    public void Apply_SessionsWithoutStartTimeOnlyCountWithoutAWindow()
    {
        var lines = new[]
        {
            "[PalitoEvent] v=1 seq=1 t=0.500 ev=session_start game_version=0.0.4 dev_build=false launcher_session=x",
            "[PalitoEvent] v=1 seq=2 t=1.500 ev=match_joined role=host slot=1 players=1 match_time=0 late=false",
        };
        var sessions = GameEventParser.Parse(lines);

        var set = SetWithGoals("""{ "event": "match_joined" }""");
        var state = NewState(set);
        MissionEvaluator.Apply(set, state, sessions);
        Assert.Equal(MissionState.Done, P(state, "m0").State);

        var windowed = Parse("""{ "id": "s", "startsUtc": "2026-10-01T00:00:00Z", "missions": [ { "id": "a", "title": "A", "goal": { "event": "match_joined" } } ] }""");
        var state2 = NewState(windowed);
        MissionEvaluator.Apply(windowed, state2, sessions);
        Assert.Equal(MissionState.Pending, P(state2, "a").State);
    }
}
