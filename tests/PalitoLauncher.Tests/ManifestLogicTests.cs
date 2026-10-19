using System.Text;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Remote;
using PalitoLauncher.Tests.TestHelpers.M5;
using static PalitoLauncher.Tests.TestHelpers.M5.ManifestSamples;

namespace PalitoLauncher.Tests;

public sealed class ManifestLogicTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 18, 0, 0, TimeSpan.Zero);
    private static readonly string[] NoIds = Array.Empty<string>();

    private static LauncherManifest WithAnnouncements(params Announcement[] announcements)
    {
        var m = new LauncherManifest();
        m.Announcements.AddRange(announcements);
        return m;
    }

    private static LauncherManifest WithPlaytests(params Playtest[] playtests)
    {
        var m = new LauncherManifest();
        m.Playtests.AddRange(playtests);
        return m;
    }

    private static string[] Ids(IEnumerable<Announcement> list) => list.Select(a => a.Id).ToArray();

    // ───────────────────────────── GetActiveAnnouncements ─────────────────────────────

    [Fact]
    public void GetActiveAnnouncements_RespectsTheHalfOpenWindow()
    {
        var m = WithAnnouncements(
            NewAnnouncement("sin-limites"),
            NewAnnouncement("empieza-ahora", starts: Now),
            NewAnnouncement("empieza-luego", starts: Now.AddTicks(1)),
            NewAnnouncement("acaba-ahora", ends: Now),
            NewAnnouncement("acaba-luego", ends: Now.AddTicks(1)));

        var active = ManifestLogic.GetActiveAnnouncements(m, Now, NoIds);

        // Máx. 3; "empieza-ahora" va primero por tener fecha de inicio.
        Assert.Equal(new[] { "empieza-ahora", "sin-limites", "acaba-luego" }, Ids(active));
    }

    [Fact]
    public void GetActiveAnnouncements_WindowWithBothBounds()
    {
        var a = NewAnnouncement("finde", starts: Now.AddHours(-1), ends: Now.AddHours(1));
        var m = WithAnnouncements(a);

        Assert.Empty(ManifestLogic.GetActiveAnnouncements(m, Now.AddHours(-1).AddTicks(-1), NoIds));
        Assert.Single(ManifestLogic.GetActiveAnnouncements(m, Now.AddHours(-1), NoIds));
        Assert.Single(ManifestLogic.GetActiveAnnouncements(m, Now.AddHours(1).AddTicks(-1), NoIds));
        Assert.Empty(ManifestLogic.GetActiveAnnouncements(m, Now.AddHours(1), NoIds));
    }

    [Fact]
    public void GetActiveAnnouncements_ComparesInstantsNotLocalClockTimes()
    {
        // 20:30 en España (+02:00) = 18:30 UTC: aún no ha empezado a las 18:00 UTC.
        var m = WithAnnouncements(NewAnnouncement("a", starts: new DateTimeOffset(2026, 10, 24, 20, 30, 0, TimeSpan.FromHours(2))));
        Assert.Empty(ManifestLogic.GetActiveAnnouncements(m, Now, NoIds));
        Assert.Single(ManifestLogic.GetActiveAnnouncements(m, Now.AddMinutes(30), NoIds));
    }

    [Fact]
    public void GetActiveAnnouncements_DismissedAreHidden_ExceptCriticalAndNonDismissible()
    {
        var m = WithAnnouncements(
            NewAnnouncement("info", AnnouncementLevel.Info),
            NewAnnouncement("aviso", AnnouncementLevel.Warning),
            NewAnnouncement("critico", AnnouncementLevel.Critical, dismissible: true), // sin sanear: aun así no se oculta
            NewAnnouncement("fijo", AnnouncementLevel.Info, dismissible: false));

        var active = ManifestLogic.GetActiveAnnouncements(m, Now, new[] { "INFO", " aviso ", "critico", "fijo" });

        Assert.Equal(new[] { "critico", "fijo" }, Ids(active));
    }

    [Fact]
    public void GetActiveAnnouncements_OrdersByLevel_ThenNewestStart_ThenFileOrder()
    {
        var m = WithAnnouncements(
            NewAnnouncement("info-sin-fecha", AnnouncementLevel.Info),
            NewAnnouncement("aviso-viejo", AnnouncementLevel.Warning, starts: Now.AddDays(-3)),
            NewAnnouncement("aviso-sin-fecha-1", AnnouncementLevel.Warning),
            NewAnnouncement("aviso-nuevo", AnnouncementLevel.Warning, starts: Now.AddDays(-1)),
            NewAnnouncement("aviso-sin-fecha-2", AnnouncementLevel.Warning),
            NewAnnouncement("critico", AnnouncementLevel.Critical));

        var all = new List<string>();
        // Se descartan de 3 en 3 para ver el orden completo (el crítico no se puede descartar).
        var dismissed = new List<string>();
        for (var round = 0; round < 4; round++)
        {
            var active = ManifestLogic.GetActiveAnnouncements(m, Now, dismissed);
            foreach (var a in active.Where(a => !all.Contains(a.Id))) all.Add(a.Id);
            dismissed.AddRange(active.Select(a => a.Id));
        }

        Assert.Equal(new[] { "critico", "aviso-nuevo", "aviso-viejo", "aviso-sin-fecha-1", "aviso-sin-fecha-2", "info-sin-fecha" }, all);
    }

    [Fact]
    public void GetActiveAnnouncements_AtMostThree()
    {
        var m = WithAnnouncements(Enumerable.Range(0, 8).Select(i => NewAnnouncement($"a{i}")).ToArray());
        Assert.Equal(new[] { "a0", "a1", "a2" }, Ids(ManifestLogic.GetActiveAnnouncements(m, Now, NoIds)));
    }

    [Fact]
    public void GetActiveAnnouncements_ToleratesNullsAndEmptyManifest()
    {
        Assert.Empty(ManifestLogic.GetActiveAnnouncements(new LauncherManifest(), Now, NoIds));
        Assert.Empty(ManifestLogic.GetActiveAnnouncements(null!, Now, NoIds));
        var m = WithAnnouncements(null!, NewAnnouncement("a"));
        Assert.Equal(new[] { "a" }, Ids(ManifestLogic.GetActiveAnnouncements(m, Now, null!)));
        Assert.Single(ManifestLogic.GetActiveAnnouncements(m, Now, new[] { null!, "", "otro" }));
    }

    [Fact]
    public void GetActiveAnnouncements_FromAParsedFile()
    {
        var m = ManifestProvider.Parse(Full)!;
        var during = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(new[] { "parche-urgente", "mantenimiento.servidor_1", "bienvenida-pre2" },
            Ids(ManifestLogic.GetActiveAnnouncements(m, during, NoIds)));
        Assert.Equal(new[] { "parche-urgente", "bienvenida-pre2" },
            Ids(ManifestLogic.GetActiveAnnouncements(m, during.AddDays(10), NoIds)));
        Assert.Equal(new[] { "parche-urgente" },
            Ids(ManifestLogic.GetActiveAnnouncements(m, during.AddDays(10), new[] { "bienvenida-pre2", "parche-urgente" })));
    }

    // ───────────────────────────── MatchesBuild ─────────────────────────────

    private const string Tag = "v0.0.4";
    private const string Asset = "2D_0.0.4_Prototype2.zip";
    private const string Canonical = "0.0.4-prototype.2";

    [Theory]
    [InlineData("v0.0.4")]
    [InlineData("V0.0.4")]
    [InlineData("  v0.0.4  ")]
    [InlineData("v 0.0.4")]
    [InlineData("0.0.4-pre2-tag-sin-v")]
    [InlineData("2D_0.0.4_Prototype2.zip")]
    [InlineData("2d_0.0.4_prototype2.ZIP")]
    [InlineData("0.0.4-prototype.2")]
    [InlineData("0.0.4-PROTOTYPE.2")]
    [InlineData("0.0.4_Prototype2")]
    [InlineData("0.0.4 Prototype 2")]
    [InlineData("0.0.4-protype-2")]
    [InlineData("2D_0.0.4_Prototype2")]
    public void MatchesBuild_AcceptedForms(string match)
    {
        var tag = match == "0.0.4-pre2-tag-sin-v" ? "v0.0.4-pre2-tag-sin-v" : Tag;
        Assert.True(ManifestLogic.MatchesBuild(match, tag, Asset, Canonical));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0.0.4-beta")]
    [InlineData("0.0.4-prototype.1")]
    [InlineData("0.0.5-prototype.2")]
    [InlineData("v0.0.3")]
    [InlineData("2D_0.0.4_Prototype2_fix.zip")] // nombre de archivo: solo vale el exacto
    [InlineData("0.0.4-prototype")]
    [InlineData("Coop2D")]
    public void MatchesBuild_RejectedForms(string? match)
    {
        Assert.False(ManifestLogic.MatchesBuild(match, Tag, Asset, Canonical));
    }

    [Fact]
    public void MatchesBuild_StableVersionIsNotThePrerelease_UnlessTheTagSaysSo()
    {
        // "0.0.4" (estable) no es "0.0.4-prototype.2"…
        Assert.False(ManifestLogic.MatchesBuild("0.0.4", "build-44", Asset, Canonical));
        // …pero sí es el tag "v0.0.4" sin la "v" (regla del tag).
        Assert.True(ManifestLogic.MatchesBuild("0.0.4", Tag, Asset, Canonical));
    }

    [Fact]
    public void MatchesBuild_TagWithoutV_WorksBothWays_OnlyBeforeADigit()
    {
        Assert.True(ManifestLogic.MatchesBuild("0.0.4", "v0.0.4", null, null));
        Assert.True(ManifestLogic.MatchesBuild("v0.0.4", "0.0.4", null, null));
        Assert.True(ManifestLogic.MatchesBuild("0.1.0-hotfix", "V0.1.0-Hotfix", null, null));
        Assert.False(ManifestLogic.MatchesBuild("acaciones", "vacaciones", null, null));
        Assert.True(ManifestLogic.MatchesBuild("vacaciones", "vacaciones", null, null));
    }

    [Fact]
    public void MatchesBuild_ArchiveName_DoesNotFallBackToTheVersion()
    {
        // Resubida corregida con otro nombre y la misma versión: bloquear el .zip viejo no bloquea el nuevo.
        Assert.False(ManifestLogic.MatchesBuild("2D_0.0.4_Prototype2.zip", "v0.0.4", "2D_0.0.4_Prototype2_fix.zip", Canonical));
        Assert.False(ManifestLogic.MatchesBuild("2D_0.0.4_Prototype2.rar", "v0.0.4", Asset, Canonical));
        // Sin extensión sí se compara por versión.
        Assert.True(ManifestLogic.MatchesBuild("2D_0.0.4_Prototype2", "v0.0.4", "2D_0.0.4_Prototype2_fix.zip", Canonical));
    }

    [Fact]
    public void MatchesBuild_VersionArgumentCanBeNonCanonical_OrMissing()
    {
        Assert.True(ManifestLogic.MatchesBuild("0.0.4-prototype.2", null, null, "0.0.4_Protype2"));
        Assert.False(ManifestLogic.MatchesBuild("0.0.4-prototype.2", "build-final", "juego.zip", null));
        Assert.False(ManifestLogic.MatchesBuild("v1", null, null, null));
    }

    [Fact]
    public void MatchesBuild_HostileInput_DoesNotThrow()
    {
        Assert.False(ManifestLogic.MatchesBuild(new string('9', 10_000) + ".1.1", Tag, Asset, Canonical));
        Assert.False(ManifestLogic.MatchesBuild("\u0000\u202E", Tag, Asset, Canonical));
        Assert.False(ManifestLogic.MatchesBuild("1.2.3", null, null, new string('9', 10_000) + ".1.1"));
    }

    // ───────────────────────────── FindBlock ─────────────────────────────

    private static LauncherManifest Blocks(params string[] matches)
    {
        var m = new LauncherManifest();
        foreach (var match in matches) m.BlockedBuilds.Add(new BlockedBuild { Match = match, Reason = "Motivo " + match });
        return ManifestProvider.Sanitize(m);
    }

    [Fact]
    public void FindBlock_Installed_ByTagAssetOrMarkerVersion()
    {
        var installed = Installed("v0.0.4", Asset);

        Assert.Equal("v0.0.4", ManifestLogic.FindBlock(Blocks("v0.0.3", "v0.0.4"), installed)?.Match);
        Assert.Equal(Asset, ManifestLogic.FindBlock(Blocks(Asset), installed)?.Match);
        Assert.Equal("0.0.4_Prototype2", ManifestLogic.FindBlock(Blocks("0.0.4_Prototype2"), installed)?.Match);
        Assert.Null(ManifestLogic.FindBlock(Blocks("v0.0.3", "0.0.4-prototype.1"), installed));
        Assert.Null(ManifestLogic.FindBlock(Blocks("v0.0.4"), (InstalledGameInfo?)null));
        Assert.Null(ManifestLogic.FindBlock(new LauncherManifest(), installed));
    }

    [Fact]
    public void FindBlock_Installed_WithoutVersionInTheMarker_DeducesItFromTheAsset()
    {
        var installed = Installed("prueba-final", "2D_0.0.2_Protype3.zip", version: null);
        Assert.Equal("0.0.2-prototype.3", ManifestLogic.FindBlock(Blocks("0.0.2-prototype.3"), installed)?.Match);
    }

    [Fact]
    public void FindBlock_ReturnsTheFirstMatch()
    {
        var installed = Installed("v0.0.4", Asset);
        var block = ManifestLogic.FindBlock(Blocks("v0.0.1", Canonical, "v0.0.4"), installed);
        Assert.Equal(Canonical, block?.Match);
        Assert.Equal("Motivo " + Canonical, block?.Reason);
    }

    [Fact]
    public void FindBlock_Release_ByTagAssetOrParsedVersion()
    {
        var release = Release("v0.0.4", Asset, assetId: 44);

        Assert.NotNull(ManifestLogic.FindBlock(Blocks("V0.0.4"), release));
        Assert.NotNull(ManifestLogic.FindBlock(Blocks(Asset.ToUpperInvariant()), release));
        Assert.NotNull(ManifestLogic.FindBlock(Blocks("0.0.4 prototype 2"), release));
        Assert.Null(ManifestLogic.FindBlock(Blocks("v0.0.5", "0.0.4-beta"), release));
        Assert.Null(ManifestLogic.FindBlock(Blocks("v0.0.4"), (GameRelease)null!));
    }

    [Fact]
    public void FindBlock_FromAParsedFile()
    {
        var m = ManifestProvider.Parse(Full)!;

        Assert.Equal("Se desconecta al entrar en la sala del jefe",
            ManifestLogic.FindBlock(m, Release("v0.0.3", "2D_0.0.3.zip"))?.Reason);
        Assert.Equal("Corrompe la partida guardada",
            ManifestLogic.FindBlock(m, Installed("v0.0.2-p3", "2D_0.0.2_Protype3.zip"))?.Reason);
        Assert.Null(ManifestLogic.FindBlock(m, Release("v0.0.4", Asset)));
    }

    // ───────────────────────────── GetNextPlaytest ─────────────────────────────

    [Fact]
    public void GetNextPlaytest_None_WhenEmptyOrAllInThePastOrTooFar()
    {
        Assert.Null(ManifestLogic.GetNextPlaytest(new LauncherManifest(), Now));
        Assert.Null(ManifestLogic.GetNextPlaytest(null!, Now));
        Assert.Null(ManifestLogic.GetNextPlaytest(WithPlaytests(
            NewPlaytest("ayer", Now.AddDays(-1)),
            NewPlaytest("acaba-justo-ahora", Now.AddMinutes(-120), durationMinutes: 120),
            NewPlaytest("muy-lejos", Now.AddDays(30).AddSeconds(1))), Now));
    }

    [Fact]
    public void GetNextPlaytest_Upcoming_Nearest_WithinThirtyDays()
    {
        var m = WithPlaytests(
            NewPlaytest("en-10-dias", Now.AddDays(10)),
            NewPlaytest("en-30-dias-justos", Now.AddDays(30)),
            NewPlaytest("en-2-dias", Now.AddDays(2), durationMinutes: 90));

        var status = ManifestLogic.GetNextPlaytest(m, Now);

        Assert.NotNull(status);
        Assert.Equal("en-2-dias", status.Playtest.Id);
        Assert.False(status.IsOngoing);
        Assert.Equal(TimeSpan.FromDays(2), status.UntilStart);
        Assert.Equal(Now.AddDays(2).AddMinutes(90), status.EndsUtc);

        Assert.Equal("en-30-dias-justos", ManifestLogic.GetNextPlaytest(WithPlaytests(NewPlaytest("en-30-dias-justos", Now.AddDays(30))), Now)?.Playtest.Id);
    }

    [Fact]
    public void GetNextPlaytest_Ongoing_WinsOverUpcoming_AndTheEarliestStartedWins()
    {
        var m = WithPlaytests(
            NewPlaytest("dentro-de-5-min", Now.AddMinutes(5)),
            NewPlaytest("empezo-hace-10", Now.AddMinutes(-10)),
            NewPlaytest("empezo-hace-60", Now.AddMinutes(-60)));

        var status = ManifestLogic.GetNextPlaytest(m, Now);

        Assert.NotNull(status);
        Assert.Equal("empezo-hace-60", status.Playtest.Id);
        Assert.True(status.IsOngoing);
        Assert.Equal(TimeSpan.FromMinutes(-60), status.UntilStart);
        Assert.Equal(Now.AddMinutes(60), status.EndsUtc);
    }

    [Fact]
    public void GetNextPlaytest_Boundaries()
    {
        var p = NewPlaytest("p", Now, durationMinutes: 60);
        var m = WithPlaytests(p);

        var before = ManifestLogic.GetNextPlaytest(m, Now.AddTicks(-1))!;
        Assert.False(before.IsOngoing);
        Assert.Equal(TimeSpan.FromTicks(1), before.UntilStart);

        var atStart = ManifestLogic.GetNextPlaytest(m, Now)!;
        Assert.True(atStart.IsOngoing);
        Assert.Equal(TimeSpan.Zero, atStart.UntilStart);

        Assert.True(ManifestLogic.GetNextPlaytest(m, Now.AddMinutes(60).AddTicks(-1))!.IsOngoing);
        Assert.Null(ManifestLogic.GetNextPlaytest(m, Now.AddMinutes(60)));
    }

    [Fact]
    public void GetNextPlaytest_TiesKeepFileOrder_AndBadEntriesAreSkipped()
    {
        var m = WithPlaytests(
            null!,
            new Playtest { Id = "sin-fecha" },
            NewPlaytest("primera", Now.AddHours(3)),
            NewPlaytest("segunda", Now.AddHours(3)));

        Assert.Equal("primera", ManifestLogic.GetNextPlaytest(m, Now)?.Playtest.Id);
    }

    [Fact]
    public void GetNextPlaytest_UnsanitizedDuration_IsClamped()
    {
        var zero = WithPlaytests(NewPlaytest("cero", Now.AddMinutes(-10), durationMinutes: 0));
        Assert.Equal(Now.AddMinutes(5), ManifestLogic.GetNextPlaytest(zero, Now)?.EndsUtc);

        var huge = WithPlaytests(NewPlaytest("enorme", Now.AddMinutes(-10), durationMinutes: int.MaxValue));
        Assert.Equal(Now.AddMinutes(710), ManifestLogic.GetNextPlaytest(huge, Now)?.EndsUtc);
    }

    [Fact]
    public void GetNextPlaytest_NearTheEndOfTime_DoesNotOverflow()
    {
        var far = DateTimeOffset.MaxValue.AddMinutes(-30);
        var m = WithPlaytests(NewPlaytest("fin", far, durationMinutes: 120));
        var status = ManifestLogic.GetNextPlaytest(m, far.AddMinutes(1));
        Assert.NotNull(status);
        Assert.True(status.IsOngoing);
        Assert.Equal(DateTimeOffset.MaxValue, status.EndsUtc);
    }

    // ───────────────────────────── FormatCountdown ─────────────────────────────

    [Theory]
    [InlineData(-3600, "¡Ya ha empezado!")]
    [InlineData(0, "¡Ya ha empezado!")]
    [InlineData(1, "en menos de un minuto")]
    [InlineData(59, "en menos de un minuto")]
    [InlineData(60, "en 1 min")]
    [InlineData(8 * 60 + 59, "en 8 min")]
    [InlineData(59 * 60 + 59, "en 59 min")]
    [InlineData(3600, "en 1 h")]
    [InlineData(3 * 3600 + 12 * 60 + 30, "en 3 h 12 min")]
    [InlineData(23 * 3600 + 59 * 60 + 59, "en 23 h 59 min")]
    [InlineData(86400, "en 1 día")]
    [InlineData(86400 + 5 * 3600 + 40 * 60, "en 1 día y 5 h")]
    [InlineData(2 * 86400 - 1, "en 1 día y 23 h")]
    [InlineData(2 * 86400, "en 2 días")]
    [InlineData(3 * 86400 + 23 * 3600, "en 3 días")]
    [InlineData(29 * 86400, "en 29 días")]
    public void FormatCountdown(int seconds, string expected)
    {
        Assert.Equal(expected, ManifestLogic.FormatCountdown(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void FormatCountdown_SubSecondAndExtremes()
    {
        Assert.Equal("en menos de un minuto", ManifestLogic.FormatCountdown(TimeSpan.FromTicks(1)));
        Assert.Equal("en menos de un minuto", ManifestLogic.FormatCountdown(TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1)));
        Assert.Equal("¡Ya ha empezado!", ManifestLogic.FormatCountdown(TimeSpan.MinValue));
        Assert.StartsWith("en ", ManifestLogic.FormatCountdown(TimeSpan.MaxValue));
        Assert.EndsWith(" días", ManifestLogic.FormatCountdown(TimeSpan.MaxValue));
    }

    // ───────────────────────────── BuildIcs ─────────────────────────────

    private static readonly DateTimeOffset IcsNow = new(2026, 10, 19, 23, 30, 15, TimeSpan.FromHours(2));

    private static Playtest SamplePlaytest() => new()
    {
        Id = "pre2-diversion",
        Title = "Prueba de diversión del Pre 2",
        Description = "Probamos el jefe nuevo y el modo 4 jugadores.\nEntrad al canal de voz 10 min antes.",
        StartsUtc = new DateTimeOffset(2026, 10, 24, 19, 0, 0, TimeSpan.Zero),
        DurationMinutes = 90,
        RequiredVersion = "0.0.4_Prototype2",
        DiscordUrl = "https://discord.com/events/111/222",
    };

    [Fact]
    public void BuildIcs_HasTheRequiredStructure()
    {
        var ics = ManifestLogic.BuildIcs(SamplePlaytest(), "Coop2D", IcsNow);

        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:", ics);
        Assert.EndsWith("END:VEVENT\r\nEND:VCALENDAR\r\n", ics);
        Assert.Equal(new[] { "VCALENDAR", "VEVENT", "VALARM" }, IcsReader.Blocks(ics));

        Assert.Equal("2.0", IcsReader.Get(ics, "VERSION", "VCALENDAR"));
        Assert.Equal("-//PalitoGames//Palito Launcher//ES", IcsReader.Get(ics, "PRODID", "VCALENDAR"));
        Assert.Equal("pre2-diversion@palito-launcher", IcsReader.Get(ics, "UID"));
        Assert.Equal("20261019T213015Z", IcsReader.Get(ics, "DTSTAMP"));
        Assert.Equal("20261024T190000Z", IcsReader.Get(ics, "DTSTART"));
        Assert.Equal("20261024T203000Z", IcsReader.Get(ics, "DTEND"));
        Assert.Equal("Coop2D: Prueba de diversión del Pre 2", IcsReader.Unescape(IcsReader.Get(ics, "SUMMARY")!));
        Assert.Equal("https://discord.com/events/111/222", IcsReader.Get(ics, "URL"));

        Assert.Equal("DISPLAY", IcsReader.Get(ics, "ACTION", "VALARM"));
        Assert.Equal("-PT15M", IcsReader.Get(ics, "TRIGGER", "VALARM"));
        Assert.Equal("Coop2D: Prueba de diversión del Pre 2", IcsReader.Unescape(IcsReader.Get(ics, "DESCRIPTION", "VALARM")!));
    }

    [Fact]
    public void BuildIcs_DescriptionCombinesTextRequiredVersionAndDiscord()
    {
        var ics = ManifestLogic.BuildIcs(SamplePlaytest(), "Coop2D", IcsNow);

        var description = IcsReader.Unescape(IcsReader.Get(ics, "DESCRIPTION")!);
        Assert.Equal(
            "Probamos el jefe nuevo y el modo 4 jugadores.\nEntrad al canal de voz 10 min antes.\n\n" +
            "Versión necesaria: 0.0.4 · Prototipo 2\n\n" +
            "Discord: https://discord.com/events/111/222",
            description);
    }

    [Fact]
    public void BuildIcs_MinimalPlaytest_OmitsOptionalParts()
    {
        var p = new Playtest { Id = "p1", Title = "Prueba rápida", StartsUtc = new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.Zero) };

        var ics = ManifestLogic.BuildIcs(p, "Coop2D", IcsNow);

        Assert.Null(IcsReader.Get(ics, "DESCRIPTION"));
        Assert.Null(IcsReader.Get(ics, "URL"));
        Assert.Equal("20261101T200000Z", IcsReader.Get(ics, "DTEND")); // 120 min por defecto
        Assert.NotNull(IcsReader.Get(ics, "DESCRIPTION", "VALARM"));
    }

    [Fact]
    public void BuildIcs_UnknownRequiredVersion_IsShownAsWritten_AndNonHttpsDiscordIsIgnored()
    {
        var p = SamplePlaytest();
        p.Description = null;
        p.RequiredVersion = "la-del-viernes";
        p.DiscordUrl = "http://discord.gg/inseguro";

        var ics = ManifestLogic.BuildIcs(p, "Coop2D", IcsNow);

        Assert.Equal("Versión necesaria: la-del-viernes", IcsReader.Unescape(IcsReader.Get(ics, "DESCRIPTION")!));
        Assert.Null(IcsReader.Get(ics, "URL"));
    }

    [Fact]
    public void BuildIcs_EscapesSpecialCharacters()
    {
        var p = SamplePlaytest();
        p.Title = @"Jefe, fase 2; ruta C:\Juegos";
        p.Description = "Línea 1\r\nLínea 2\rLínea 3\nComas, puntos; y \\barras\\";
        p.RequiredVersion = null;
        p.DiscordUrl = null;

        var ics = ManifestLogic.BuildIcs(p, "Coop2D", IcsNow);

        Assert.Contains(@"SUMMARY:Coop2D: Jefe\, fase 2\; ruta C:\\Juegos", IcsReader.LogicalLines(ics));
        Assert.Equal(@"Coop2D: Jefe, fase 2; ruta C:\Juegos", IcsReader.Unescape(IcsReader.Get(ics, "SUMMARY")!));
        Assert.Equal("Línea 1\nLínea 2\nLínea 3\nComas, puntos; y \\barras\\", IcsReader.Unescape(IcsReader.Get(ics, "DESCRIPTION")!));
    }

    [Fact]
    public void BuildIcs_StripsControlCharactersAndBidiMarks()
    {
        var p = SamplePlaytest();
        p.Title = "Ho\u0007la\u202E\u0000 mundo\tcon tab";

        var ics = ManifestLogic.BuildIcs(p, "Coop2D", IcsNow);

        Assert.Equal("Coop2D: Hola mundo con tab", IcsReader.Unescape(IcsReader.Get(ics, "SUMMARY")!));
    }

    [Fact]
    public void BuildIcs_FoldsLongLinesAt75Octets_WithoutSplittingCharacters()
    {
        var p = SamplePlaytest();
        // Mezcla de ASCII, letras de 2 bytes (ñ, á), símbolos de 3 bytes (€, ·) y emojis de 4 bytes (surrogates).
        p.Description = string.Concat(Enumerable.Repeat("Mañana jugamos ñ€·🎮 a las 21:00, ¡no faltéis! ", 12)).Trim();
        p.Title = new string('T', 70) + "🎮🎮🎮";

        var ics = ManifestLogic.BuildIcs(p, "Coop2D", IcsNow);
        var physical = IcsReader.PhysicalLines(ics);

        Assert.Contains(physical, l => l.StartsWith(' '));
        foreach (var line in physical)
        {
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, $"Línea de {Encoding.UTF8.GetByteCount(line)} octetos: {line}");
            Assert.False(line.Length > 0 && char.IsHighSurrogate(line[^1]), "Surrogate partido al final de la línea");
            Assert.False(line.Length > 1 && line[0] == ' ' && char.IsLowSurrogate(line[1]), "Surrogate partido al principio");
        }

        // Al desplegar se recupera exactamente el texto original.
        Assert.Equal(p.Description + "\n\nDiscord: https://discord.com/events/111/222",
            IcsReader.Unescape(IcsReader.Get(ics, "DESCRIPTION")!)
                .Replace("\n\nVersión necesaria: 0.0.4 · Prototipo 2", "", StringComparison.Ordinal));
        Assert.Equal("Coop2D: " + p.Title, IcsReader.Unescape(IcsReader.Get(ics, "SUMMARY")!));
    }

    [Fact]
    public void BuildIcs_FoldingUsesTheFullBudget()
    {
        // Línea de exactamente 75 octetos: no se pliega; con uno más, sí (y la continuación lleva un espacio).
        var p = SamplePlaytest();
        p.Description = null;
        p.RequiredVersion = null;
        p.DiscordUrl = null;
        p.Title = new string('a', 75 - "SUMMARY:Coop2D: ".Length);
        var exact = IcsReader.PhysicalLines(ManifestLogic.BuildIcs(p, "Coop2D", IcsNow));
        Assert.Contains("SUMMARY:Coop2D: " + p.Title, exact);

        p.Title += "b";
        var folded = IcsReader.PhysicalLines(ManifestLogic.BuildIcs(p, "Coop2D", IcsNow));
        var index = folded.ToList().FindIndex(l => l.StartsWith("SUMMARY:", StringComparison.Ordinal));
        Assert.Equal(75, folded[index].Length);
        Assert.Equal(" b", folded[index + 1]);
    }

    [Fact]
    public void BuildIcs_ConvertsEverythingToUtc_AndFallsBackForMissingNames()
    {
        var p = new Playtest
        {
            Id = "  ",
            Title = "",
            StartsUtc = new DateTimeOffset(2026, 10, 24, 21, 0, 0, TimeSpan.FromHours(2)),
            DurationMinutes = 0, // sin sanear: se limita a 15
        };

        var ics = ManifestLogic.BuildIcs(p, "  ", IcsNow);

        Assert.Equal("20261024T190000Z", IcsReader.Get(ics, "DTSTART"));
        Assert.Equal("20261024T191500Z", IcsReader.Get(ics, "DTEND"));
        Assert.Equal("Coop2D: Sesión de prueba", IcsReader.Unescape(IcsReader.Get(ics, "SUMMARY")!));
        Assert.Equal("playtest@palito-launcher", IcsReader.Get(ics, "UID"));
    }

    [Fact]
    public void BuildIcs_HostileIdIsEscaped_AndNullPlaytestThrows()
    {
        var p = SamplePlaytest();
        p.Id = "a;b,c\nd";
        var ics = ManifestLogic.BuildIcs(p, "Coop2D", IcsNow);
        Assert.Equal("a;b,c\nd@palito-launcher", IcsReader.Unescape(IcsReader.Get(ics, "UID")!));

        Assert.Throws<ArgumentNullException>(() => ManifestLogic.BuildIcs(null!, "Coop2D", IcsNow));
    }

    [Fact]
    public void BuildIcs_FromAParsedFile()
    {
        var m = ManifestProvider.Parse(Full)!;
        var ics = ManifestLogic.BuildIcs(m.Playtests[0], "Coop2D", IcsNow);

        Assert.Equal("20261024T190000Z", IcsReader.Get(ics, "DTSTART"));
        Assert.Equal("20261024T203000Z", IcsReader.Get(ics, "DTEND"));
        Assert.All(IcsReader.PhysicalLines(ics), l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75));
    }

    // ───────────────────────────── ShouldRemind ─────────────────────────────

    private static PlaytestStatus Status(TimeSpan untilStart, string id = "pre2")
    {
        var p = NewPlaytest(id, Now + untilStart);
        return new PlaytestStatus(p, untilStart <= TimeSpan.Zero, untilStart, p.StartsUtc.AddMinutes(p.DurationMinutes));
    }

    [Theory]
    [InlineData(16 * 60, false)]
    [InlineData(15 * 60 + 1, false)]
    [InlineData(15 * 60, true)]
    [InlineData(60, true)]
    [InlineData(0, true)]
    [InlineData(-4 * 60 - 59, true)]
    [InlineData(-5 * 60, false)]
    [InlineData(-60 * 60, false)]
    public void ShouldRemind_Window(int secondsUntilStart, bool expected)
    {
        Assert.Equal(expected, ManifestLogic.ShouldRemind(Status(TimeSpan.FromSeconds(secondsUntilStart)), NoIds));
    }

    [Fact]
    public void ShouldRemind_NotTwice_IdsCaseInsensitive()
    {
        var status = Status(TimeSpan.FromMinutes(10), "Pre2-Diversion");
        Assert.False(ManifestLogic.ShouldRemind(status, new[] { "otra", "pre2-diversion" }));
        Assert.True(ManifestLogic.ShouldRemind(status, new[] { "otra", null! }));
        Assert.True(ManifestLogic.ShouldRemind(status, null!));
        Assert.False(ManifestLogic.ShouldRemind(null!, NoIds));
    }

    [Fact]
    public void ShouldRemind_WithGetNextPlaytest()
    {
        var m = WithPlaytests(NewPlaytest("pre2", Now.AddMinutes(14)));
        var status = ManifestLogic.GetNextPlaytest(m, Now)!;
        Assert.True(ManifestLogic.ShouldRemind(status, NoIds));
        Assert.False(ManifestLogic.ShouldRemind(ManifestLogic.GetNextPlaytest(m, Now.AddMinutes(-2))!, NoIds));
    }

    // ───────────────────────────── GetReleasesSince ─────────────────────────────

    private static readonly GameRelease[] History =
    {
        Release("v0.0.5", "2D_0.0.5.zip", 5),
        Release("v0.0.4", "2D_0.0.4_Prototype2.zip", 4),
        Release("v0.0.3", "2D_0.0.3.zip", 3),
        Release("v0.0.2", "2D_0.0.2_Protype3.zip", 2),
        Release("v0.0.1", "2D_0.0.1.zip", 1),
    };

    private static string[] Tags(IEnumerable<GameRelease> releases) => releases.Select(r => r.Release.TagName).ToArray();

    [Fact]
    public void GetReleasesSince_NewerThanInstalled_InOrder()
    {
        var since = ManifestLogic.GetReleasesSince(Installed("v0.0.2", "2D_0.0.2_Protype3.zip"), History);
        Assert.Equal(new[] { "v0.0.5", "v0.0.4", "v0.0.3" }, Tags(since));
    }

    [Fact]
    public void GetReleasesSince_LatestInstalled_IsEmpty()
    {
        Assert.Empty(ManifestLogic.GetReleasesSince(Installed("v0.0.5", "2D_0.0.5.zip"), History));
    }

    [Fact]
    public void GetReleasesSince_NothingInstalledOrUnparseable_OnlyTheLatest()
    {
        Assert.Equal(new[] { "v0.0.5" }, Tags(ManifestLogic.GetReleasesSince(null, History)));
        Assert.Equal(new[] { "v0.0.5" }, Tags(ManifestLogic.GetReleasesSince(Installed("build-final", "juego.zip", version: null), History)));
    }

    [Fact]
    public void GetReleasesSince_EmptyOrNullList_IsEmpty()
    {
        Assert.Empty(ManifestLogic.GetReleasesSince(Installed("v0.0.2", "2D_0.0.2.zip"), Array.Empty<GameRelease>()));
        Assert.Empty(ManifestLogic.GetReleasesSince(null, Array.Empty<GameRelease>()));
        Assert.Empty(ManifestLogic.GetReleasesSince(null, null!));
    }

    [Fact]
    public void GetReleasesSince_AtMostTen_AndSkipsUnparseableReleases()
    {
        var many = Enumerable.Range(1, 15).Reverse().Select(i => Release($"v0.{i}.0", $"2D_0.{i}.0.zip", i)).ToList();
        many.Insert(3, Release("especial", "extra.zip", 99));

        var since = ManifestLogic.GetReleasesSince(Installed("v0.1.0", "2D_0.1.0.zip"), many);

        Assert.Equal(10, since.Count);
        Assert.Equal("v0.15.0", since[0].Release.TagName);
        Assert.DoesNotContain(since, r => r.Release.TagName == "especial");
    }

    [Fact]
    public void GetReleasesSince_PrereleaseOfTheSameNumber_IsOlderThanTheStable()
    {
        var releases = new[] { Release("v0.0.4", "2D_0.0.4.zip"), Release("v0.0.4-p2", "2D_0.0.4_Prototype2.zip") };
        var since = ManifestLogic.GetReleasesSince(Installed("v0.0.4-p1", "2D_0.0.4_Prototype1.zip"), releases);
        Assert.Equal(new[] { "v0.0.4", "v0.0.4-p2" }, Tags(since));
    }

    // ───────────────────────────── Versión requerida por la sesión ─────────────────────────────

    [Fact]
    public void FindRequiredRelease_ByAnyAcceptedForm()
    {
        Assert.Equal("v0.0.4", ManifestLogic.FindRequiredRelease(NewPlaytest("p", Now, requiredVersion: "0.0.4_Prototype2"), History)?.Release.TagName);
        Assert.Equal("v0.0.3", ManifestLogic.FindRequiredRelease(NewPlaytest("p", Now, requiredVersion: "V0.0.3"), History)?.Release.TagName);
        Assert.Equal("v0.0.2", ManifestLogic.FindRequiredRelease(NewPlaytest("p", Now, requiredVersion: "2D_0.0.2_Protype3.zip"), History)?.Release.TagName);
        Assert.Equal("v0.0.5", ManifestLogic.FindRequiredRelease(NewPlaytest("p", Now, requiredVersion: "0.0.5"), History)?.Release.TagName);
    }

    [Fact]
    public void FindRequiredRelease_NoneOrNotRequested_IsNull()
    {
        Assert.Null(ManifestLogic.FindRequiredRelease(NewPlaytest("p", Now, requiredVersion: "v9.9.9"), History));
        Assert.Null(ManifestLogic.FindRequiredRelease(NewPlaytest("p", Now), History));
        Assert.Null(ManifestLogic.FindRequiredRelease(NewPlaytest("p", Now, requiredVersion: "  "), History));
        Assert.Null(ManifestLogic.FindRequiredRelease(NewPlaytest("p", Now, requiredVersion: "v0.0.4"), Array.Empty<GameRelease>()));
        Assert.Null(ManifestLogic.FindRequiredRelease(NewPlaytest("p", Now, requiredVersion: "v0.0.4"), null!));
        Assert.Null(ManifestLogic.FindRequiredRelease(null!, History));
    }

    [Fact]
    public void FindRequiredRelease_SeveralMatches_ReturnsTheFirst()
    {
        // Misma versión publicada dos veces (resubida): gana la primera de la lista (la más nueva).
        var releases = new[] { Release("v0.0.4-fix", Asset, 41), Release("v0.0.4", Asset, 40) };
        Assert.Equal(41, ManifestLogic.FindRequiredRelease(NewPlaytest("p", Now, requiredVersion: Canonical), releases)?.Asset.Id);
    }

    [Fact]
    public void IsRequiredBuildInstalled()
    {
        var p = NewPlaytest("p", Now, requiredVersion: "0.0.4_Prototype2");

        Assert.True(ManifestLogic.IsRequiredBuildInstalled(p, Installed("v0.0.4", Asset)));
        Assert.True(ManifestLogic.IsRequiredBuildInstalled(p, Installed("cualquiera", Asset, version: null)));
        Assert.False(ManifestLogic.IsRequiredBuildInstalled(p, Installed("v0.0.3", "2D_0.0.3.zip")));
        Assert.Null(ManifestLogic.IsRequiredBuildInstalled(p, null));
        Assert.Null(ManifestLogic.IsRequiredBuildInstalled(NewPlaytest("p", Now), Installed("v0.0.4", Asset)));
        Assert.Null(ManifestLogic.IsRequiredBuildInstalled(null!, Installed("v0.0.4", Asset)));
    }

    [Fact]
    public void PlaytestReminderLead_IsFifteenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), ManifestLogic.PlaytestReminderLead);
    }
}
