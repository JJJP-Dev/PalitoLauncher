using PalitoLauncher.Core;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Missions;
using PalitoLauncher.Core.Net;
using PalitoLauncher.Tests.TestHelpers.M5;

namespace PalitoLauncher.Tests;

public sealed class MissionsProviderTests
{
    private const string FullJson = """
        {
          // comentarios y comas finales permitidos
          "id": "pre2-sesion-1",
          "title": "Misiones del Pre 2",
          "intro": "Prueba esto en la sesión del sábado.",
          "minGameVersion": "0.0.4-prototype.2",
          "startsUtc": "2026-10-31T19:00:00+01:00",
          "endsUtc": "2026-11-07T23:59:00+01:00",
          "discordWebhookUrl": "https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz_ABCDEFGHIJ-0123456789",
          "missions": [
            {
              "id": "duo-vapor",
              "title": "Provoca un Vapor en dúo",
              "hint": "Uno marca con fuego y el otro detona con agua.",
              "goal": { "event": "reaction", "where": { "variant": "vapor_*", "duo": true, "me": ["aura", "detonator"] } },
              "check": "¿Se vio el titular?",
            },
            { "id": "cuatro", "title": "Juega una partida de cuatro",
              "goal": { "any": [
                { "event": "match_joined", "where": { "players": { ">=": 4 } } },
                { "event": "player_joined", "where": { "players": { ">=": 4 } } } ] } },
            { "id": "kills", "title": "Elimina 30", "allowCheats": true, "allowSolo": false,
              "goal": { "event": "enemy_killed", "where": { "by": "me" }, "count": 30, "scope": "set" } },
            { "id": "numeros", "title": "¿Se leen los números?", "manual": true },
          ],
        }
        """;

    [Fact]
    public void Parse_ReadsAFullSet()
    {
        var set = MissionsProvider.Parse(FullJson);

        Assert.NotNull(set);
        Assert.Equal("pre2-sesion-1", set!.Id);
        Assert.Equal("Misiones del Pre 2", set.Title);
        Assert.Equal("0.0.4-prototype.2", set.MinGameVersion!.ToString());
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 18, 0, 0, TimeSpan.Zero), set.StartsUtc);
        Assert.Equal(TimeSpan.Zero, set.StartsUtc!.Value.Offset); // pasadas a UTC
        Assert.NotNull(set.DiscordWebhookUrl);
        Assert.Equal(new[] { "duo-vapor", "cuatro", "kills", "numeros" }, set.Missions.Select(m => m.Id));

        var vapor = set.Missions[0];
        Assert.False(vapor.Manual);
        Assert.Equal("¿Se vio el titular?", vapor.Check);
        var goal = Assert.Single(vapor.Goals);
        Assert.Equal("reaction", goal.Event);
        Assert.Equal(MissionScope.Session, goal.Scope);
        Assert.Equal(1, goal.Count);
        Assert.Equal(3, goal.Where.Count);
        Assert.Equal(ConditionOperator.Prefix, goal.Where[0].Operator);
        Assert.Equal("vapor_", goal.Where[0].Text);
        Assert.Equal("true", goal.Where[1].Text);
        Assert.Equal(ConditionOperator.AnyOf, goal.Where[2].Operator);
        Assert.Equal(2, goal.Where[2].Alternatives.Count);

        var four = set.Missions[1];
        Assert.False(four.RequireAllGoals);
        Assert.Equal(2, four.Goals.Count);
        Assert.Equal(ConditionOperator.GreaterOrEqual, four.Goals[0].Where[0].Operator);
        Assert.Equal(4, four.Goals[0].Where[0].Number);

        var kills = set.Missions[2];
        Assert.True(kills.AllowCheats);
        Assert.False(kills.AllowSolo);
        Assert.Equal(MissionScope.Set, kills.Goals[0].Scope);
        Assert.Equal(30, kills.Goals[0].Count);

        Assert.True(set.Missions[3].Manual);
        Assert.Empty(set.Missions[3].Goals);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{ roto")]
    [InlineData("""{ "missions": [ { "id": "a", "title": "A", "manual": true } ] }""")]             // sin id
    [InlineData("""{ "id": "con espacio", "missions": [ { "id": "a", "title": "A", "manual": true } ] }""")]
    [InlineData("""{ "id": "s", "missions": [] }""")]                                               // sin misiones
    [InlineData("""{ "id": "s", "missions": [ { "id": "a" } ] }""")]                               // misión sin título
    public void Parse_ReturnsNullForInvalidSets(string json)
    {
        Assert.Null(MissionsProvider.Parse(json));
    }

    [Theory]
    [InlineData("""{ "where": { "a": 1 } }""")]                                  // falta event
    [InlineData("""{ "event": "Reaction" }""")]                                  // nombre no válido
    [InlineData("""{ "event": "reaction", "count": 0 }""")]
    [InlineData("""{ "event": "reaction", "count": "3" }""")]
    [InlineData("""{ "event": "reaction", "scope": "siempre" }""")]
    [InlineData("""{ "event": "reaction", "where": { "me": { "~": 1 } } }""")]
    [InlineData("""{ "event": "reaction", "where": { "me": { ">=": "a" } } }""")]
    [InlineData("""{ "event": "reaction", "where": { "me": { ">=": 1, "<": 3 } } }""")]
    [InlineData("""{ "event": "reaction", "where": { "me": [] } }""")]
    [InlineData("""{ "event": "reaction", "where": { "me": "con espacio" } }""")]
    [InlineData("""{ "event": "reaction", "where": { "me": "a*b" } }""")]
    [InlineData("""{ "event": "reaction", "where": { "me": { "!=": "a*" } } }""")]
    [InlineData("""{ "all": [ { "event": "reaction" } ], "any": [ { "event": "reaction" } ] }""")]
    [InlineData("""{ "all": [ { "any": [ { "event": "reaction" } ] } ] }""")]   // anidado
    [InlineData("""{ "all": [] }""")]
    public void Parse_TurnsAMissionWithABrokenGoalIntoAManualOne(string goal)
    {
        var set = MissionsProvider.Parse($$"""{ "id": "s", "missions": [ { "id": "a", "title": "A", "goal": {{goal}} } ] }""");

        var mission = Assert.Single(set!.Missions);
        Assert.True(mission.Manual);
        Assert.Empty(mission.Goals);
    }

    [Fact]
    public void Parse_SanitizesTextsLimitsAndDuplicates()
    {
        var missions = Enumerable.Range(0, 15).Select(i => $$"""{ "id": "m{{i}}", "title": "M{{i}}", "manual": true }""").ToList();
        missions.Insert(1, """{ "id": "M0", "title": "repetida", "manual": true }""");
        var json = $$"""
            { "id": "s", "title": "{{new string('x', 200)}}", "discordWebhookUrl": "https://ejemplo.com/hook",
              "minGameVersion": "sin versión", "startsUtc": "2026-11-02T00:00:00Z", "endsUtc": "2026-11-01T00:00:00Z",
              "missions": [ {{string.Join(",", missions)}} ] }
            """;

        var set = MissionsProvider.Parse(json)!;

        Assert.Equal(MissionsProvider.MaxSetTitleLength, set.Title.Length);
        Assert.Null(set.DiscordWebhookUrl);   // no es un webhook de Discord
        Assert.Null(set.MinGameVersion);      // no es una versión
        Assert.Null(set.EndsUtc);             // anterior al inicio: se ignora
        Assert.Equal(MissionsProvider.MaxMissions, set.Missions.Count);
        Assert.DoesNotContain(set.Missions, m => m.Title == "repetida");
    }

    [Fact]
    public void Parse_AcceptsUnknownEventsForwardCompatible()
    {
        var set = MissionsProvider.Parse("""{ "id": "s", "missions": [ { "id": "a", "title": "A", "goal": { "event": "boss_defeated" } } ] }""");
        Assert.False(set!.Missions[0].Manual);
    }

    [Fact]
    public void Parse_DocsExampleFile_IsAcceptedWithoutDowngrades()
    {
        // docs/ejemplos/missions.example.json tiene que ser válido y ninguna misión automática puede acabar como manual.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PalitoLauncher.sln"))) dir = dir.Parent;
        var example = dir is null ? null : Path.Combine(dir.FullName, "docs", "ejemplos", "missions.example.json");
        if (example is null || !File.Exists(example)) return;

        var set = MissionsProvider.Parse(File.ReadAllText(example));
        Assert.NotNull(set);
        Assert.Equal(new[] { "numeros" }, set!.Missions.Where(m => m.Manual).Select(m => m.Id));
    }

    [Fact]
    public void IsActive_RespectsTheWindow()
    {
        var set = MissionsProvider.Parse(FullJson)!;
        Assert.False(set.IsActive(set.StartsUtc!.Value.AddTicks(-1)));
        Assert.True(set.IsActive(set.StartsUtc!.Value));
        Assert.True(set.IsActive(set.EndsUtc!.Value.AddTicks(-1)));
        Assert.False(set.IsActive(set.EndsUtc!.Value));
        Assert.True(set.HasEnded(set.EndsUtc!.Value));
    }

    // ───────────────────────────── Descarga ─────────────────────────────

    private static MissionsProvider CreateProvider(TempDir temp, FakeRawServer server)
        => new(new GitHubClient(HttpFactory.Create(server), temp.Combine("cache")), temp.Path);

    [Fact]
    public async Task GetMissionSetAsync_PrefersTheLocalFile()
    {
        using var temp = new TempDir();
        var server = new FakeRawServer(LauncherConfig.RemoteMissionsUrl, FullJson);
        var provider = CreateProvider(temp, server);
        File.WriteAllText(provider.LocalOverridePath, """{ "id": "local", "missions": [ { "id": "a", "title": "A", "manual": true } ] }""");

        var set = await provider.GetMissionSetAsync(CancellationToken.None);

        Assert.Equal("local", set!.Id);
        Assert.Equal(0, server.RequestsTo(LauncherConfig.RemoteMissionsUrl));
    }

    [Fact]
    public async Task GetMissionSetAsync_ReadsTheRemoteAndReturnsNullOn404()
    {
        using var temp = new TempDir();
        var server = new FakeRawServer(LauncherConfig.RemoteMissionsUrl, FullJson);
        var provider = CreateProvider(temp, server);

        var set = await provider.GetMissionSetAsync(CancellationToken.None);
        Assert.Equal("pre2-sesion-1", set!.Id);
        Assert.Equal(1, server.RequestsTo(LauncherConfig.RemoteMissionsUrl));

        server.Body = null; // 404
        Assert.Null(await provider.GetMissionSetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetMissionSetAsync_KeepsTheLastGoodCopyWhenTheRemoteBreaks()
    {
        using var temp = new TempDir();
        var server = new FakeRawServer(LauncherConfig.RemoteMissionsUrl, FullJson);
        var provider = CreateProvider(temp, server);

        Assert.NotNull(await provider.GetMissionSetAsync(CancellationToken.None));
        server.Body = "{ roto";
        var set = await provider.GetMissionSetAsync(CancellationToken.None);
        Assert.Equal("pre2-sesion-1", set!.Id);
    }
}
