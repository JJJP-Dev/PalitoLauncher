using PalitoLauncher.Core.Missions;
using PalitoLauncher.Tests.TestHelpers.M5;
using PalitoLauncher.Tests.TestHelpers.M6;
using static PalitoLauncher.Tests.TestHelpers.M6.MissionSamples;

namespace PalitoLauncher.Tests;

public sealed class GameEventParserTests
{
    [Fact]
    public void ParseLine_ReadsTheEnvelopeAndTheFields()
    {
        var e = GameEventParser.ParseLine(
            "[PalitoEvent] v=1 seq=42 t=318.412 ev=reaction variant=vapor_fire_water duo=true me=detonator targets=3");

        Assert.NotNull(e);
        Assert.Equal(42, e!.Seq);
        Assert.Equal(318.412, e.Time, 3);
        Assert.Equal("reaction", e.Name);
        Assert.Equal("vapor_fire_water", e.Get("variant"));
        Assert.Equal("vapor_fire_water", e.Get("VARIANT")); // sin distinguir mayúsculas
        Assert.True(e.GetBool("duo"));
        Assert.Equal(3, e.GetNumber("targets"));
        Assert.Null(e.Get("v"));                             // el sobre no queda entre los campos
        Assert.Null(e.Get("no_existe"));
    }

    [Theory]
    [InlineData("v=1 seq=1 t=0.5 ev=x")]                     // sin el prefijo
    [InlineData(" [PalitoEvent] v=1 seq=1 t=0.5 ev=x")]      // el prefijo no está al principio
    [InlineData("[PalitoEvent] v=2 seq=1 t=0.5 ev=x")]       // versión del formato desconocida
    [InlineData("[PalitoEvent] seq=1 t=0.5 ev=x")]           // falta v
    [InlineData("[PalitoEvent] v=1 t=0.5 ev=x")]             // falta seq
    [InlineData("[PalitoEvent] v=1 seq=1 ev=x")]             // falta t
    [InlineData("[PalitoEvent] v=1 seq=1 t=0.5")]            // falta ev
    [InlineData("[PalitoEvent] v=1 seq=1 t=0,5 ev=x")]       // decimal con coma (Windows en español)
    [InlineData("[PalitoEvent] v=1 seq=0 t=0.5 ev=x")]       // seq empieza en 1
    [InlineData("[PalitoEvent] v=1 seq=1 t=0.5 ev=X")]       // nombre en mayúsculas
    [InlineData("[PalitoEvent] v=1 seq=1 t=0.5 ev=x a=1 a=2")] // clave repetida
    [InlineData("[PalitoEvent] v=1 seq=1 t=0.5 ev=x a=")]    // valor vacío
    [InlineData("[PalitoEvent] v=1 seq=1 t=0.5 ev=x a=\"b\"")] // comillas
    [InlineData("[PalitoEvent] v=1 seq=1 t=0.5 ev=x Clave=1")] // clave con mayúsculas
    [InlineData("[PalitoEvent] v=1 seq=1 t=0.5 ev=x suelto")] // token sin '='
    public void ParseLine_RejectsMalformedLines(string line)
    {
        Assert.Null(GameEventParser.ParseLine(line));
    }

    [Fact]
    public void ParseLine_RejectsLinesLongerThan512()
    {
        var line = "[PalitoEvent] v=1 seq=1 t=0.5 ev=x " + string.Join(" ", Enumerable.Range(0, 60).Select(i => $"k{i}=valor"));
        Assert.True(line.Length > 512);
        Assert.Null(GameEventParser.ParseLine(line));
    }

    [Fact]
    public void ParseLine_ToleratesCrLfAndRepeatedSpaces()
    {
        var e = GameEventParser.ParseLine("[PalitoEvent] v=1  seq=3 t=1.000 ev=hitch ms=153\r");
        Assert.NotNull(e);
        Assert.Equal("153", e!.Get("ms"));
    }

    [Fact]
    public void Parse_SplitsSessionsAndReadsSessionStart()
    {
        var first = new LogBuilder(session: "s1").Match().Leave().End();
        var second = new LogBuilder(version: "0.0.5", session: "none", utc: Start.AddHours(2)).Match();
        var sessions = GameEventParser.Parse(first.Lines.Concat(second.Lines));

        Assert.Equal(2, sessions.Count);
        Assert.Equal("s1", sessions[0].LauncherSession);
        Assert.Equal("s1", sessions[0].Key);
        Assert.True(sessions[0].EndedCleanly);
        Assert.Equal("0.0.4-prototype.2", sessions[0].GameVersionText);
        Assert.Equal("0.0.4-prototype.2", sessions[0].GameVersion!.ToString());
        Assert.True(sessions[0].DevBuild);
        Assert.Equal(Start, sessions[0].StartUtc);
        Assert.Equal(4, sessions[0].Events.Count);

        Assert.Null(sessions[1].LauncherSession);             // «none»
        Assert.False(sessions[1].EndedCleanly);               // se cerró de golpe
        Assert.Contains("0.0.5", sessions[1].Key, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_IgnoresEventsBeforeTheFirstSessionStartAndDuplicatedSeq()
    {
        var lines = new List<string>
        {
            "[PalitoEvent] v=1 seq=7 t=1.000 ev=match_joined role=host slot=1 players=1 match_time=0 late=false",
        };
        lines.AddRange(new LogBuilder().Kill().Lines);
        lines.Add("[PalitoEvent] v=1 seq=2 t=9.000 ev=enemy_killed enemy=chaser by=me source=basic"); // duplicado
        lines.Add("[PalitoEvent] v=1 seq=5 t=9.500 ev=enemy_killed enemy=chaser by=me source=basic"); // faltan 3 y 4

        var session = Assert.Single(GameEventParser.Parse(lines));
        Assert.Equal(new[] { "session_start", "enemy_killed", "enemy_killed" }, session.Events.Select(e => e.Name));
        Assert.Equal(2, session.MissingEvents);
    }

    [Fact]
    public void TimeOf_IsStartPlusElapsed()
    {
        var session = Assert.Single(new LogBuilder().Kill().Kill().Sessions());
        // session_start en t=1 y la segunda muerte en t=3: 2 s después del arranque.
        Assert.Equal(Start.AddSeconds(2), session.TimeOf(session.Events[2]));
    }

    [Fact]
    public void ParseFile_ReadsAFileThatIsStillOpenForWritingAndNeverThrows()
    {
        using var temp = new TempDir();
        var path = temp.Combine("Player.log");
        File.WriteAllLines(path, new LogBuilder().Kill().Lines);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var session = Assert.Single(GameEventParser.ParseFile(path));
            Assert.Equal(2, session.Events.Count);
        }

        Assert.Empty(GameEventParser.ParseFile(temp.Combine("no-existe.log")));
        Assert.Empty(GameEventParser.ParseFile(null));
    }

    [Fact]
    public void ParseFile_StopsAtTheSizeLimit()
    {
        using var temp = new TempDir();
        var path = temp.Combine("Player.log");
        var log = new LogBuilder();
        for (var i = 0; i < 100; i++) log.Kill();
        File.WriteAllLines(path, log.Lines);

        var session = Assert.Single(GameEventParser.ParseFile(path, maxBytes: 2000));
        Assert.InRange(session.Events.Count, 2, 50);
    }
}
