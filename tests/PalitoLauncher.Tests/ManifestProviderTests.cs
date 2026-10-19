using System.Text;
using PalitoLauncher.Core;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Net;
using PalitoLauncher.Core.Remote;
using PalitoLauncher.Tests.TestHelpers.M5;

namespace PalitoLauncher.Tests;

public sealed class ManifestProviderTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) => new(y, mo, d, h, mi, s, TimeSpan.Zero);

    private static LauncherManifest ParseOk(string json)
    {
        var manifest = ManifestProvider.Parse(json);
        Assert.NotNull(manifest);
        return manifest;
    }

    // ───────────────────────────── Parse: archivo realista ─────────────────────────────

    [Fact]
    public void Parse_RealisticSpanishManifest_ReadsEverything()
    {
        var m = ParseOk(ManifestSamples.Full);

        Assert.Equal(3, m.Announcements.Count);
        var welcome = m.Announcements[0];
        Assert.Equal("bienvenida-pre2", welcome.Id);
        Assert.Equal(AnnouncementLevel.Info, welcome.Level);
        Assert.Equal("¡Bienvenidos al Prototipo 2!", welcome.Title);
        Assert.Equal("Gracias por probar Coop2D.\nSi algo falla, usa «Reportar un problema».", welcome.Message);
        Assert.Equal("https://discord.gg/coop2d", welcome.Url);
        Assert.Equal("Abrir Discord", welcome.LinkText);
        Assert.Null(welcome.StartsUtc);
        Assert.Null(welcome.EndsUtc);
        Assert.True(welcome.Dismissible);

        var maintenance = m.Announcements[1];
        Assert.Equal("mantenimiento.servidor_1", maintenance.Id);
        Assert.Equal(AnnouncementLevel.Warning, maintenance.Level);
        Assert.Equal(Utc(2026, 10, 20, 8), maintenance.StartsUtc);
        Assert.Equal(Utc(2026, 10, 25, 20), maintenance.EndsUtc);
        Assert.Equal(TimeSpan.Zero, maintenance.EndsUtc!.Value.Offset);
        Assert.Null(maintenance.Url);
        Assert.Null(maintenance.LinkText);

        var critical = m.Announcements[2];
        Assert.Equal(AnnouncementLevel.Critical, critical.Level);
        Assert.False(critical.Dismissible);

        Assert.Equal(2, m.BlockedBuilds.Count);
        Assert.Equal("v0.0.3", m.BlockedBuilds[0].Match);
        Assert.Equal("Se desconecta al entrar en la sala del jefe", m.BlockedBuilds[0].Reason);
        Assert.Equal("2D_0.0.2_Protype3.zip", m.BlockedBuilds[1].Match);

        Assert.Equal(new[] { "discord", "itch", "web" }, m.Links.Select(l => l.Icon));
        Assert.Equal(new[] { "Discord", "itch.io", "Web" }, m.Links.Select(l => l.Label));
        Assert.Equal("https://palito.itch.io/coop2d", m.Links[1].Url);

        var playtest = Assert.Single(m.Playtests);
        Assert.Equal("pre2-diversion", playtest.Id);
        Assert.Equal("Prueba de diversión del Pre 2", playtest.Title);
        Assert.Equal("Probamos el jefe nuevo y el modo 4 jugadores.\nEntrad al canal de voz 10 min antes.", playtest.Description);
        Assert.Equal(Utc(2026, 10, 24, 19), playtest.StartsUtc);
        Assert.Equal(TimeSpan.Zero, playtest.StartsUtc.Offset);
        Assert.Equal(90, playtest.DurationMinutes);
        Assert.Equal("0.0.4_Prototype2", playtest.RequiredVersion);
        Assert.Equal("https://discord.com/events/111/222", playtest.DiscordUrl);

        Assert.NotNull(m.Hosting);
        Assert.Equal(7770, m.Hosting.Port);
        Assert.Equal("Abre el puerto UDP 7770 en tu router o usad Radmin VPN.", m.Hosting.Notes);
        Assert.Equal(ManifestSamples.Webhook, m.BugReportWebhookUrl);
    }

    [Fact]
    public void Parse_EmptyObject_IsValidAndShowsNothing()
    {
        var m = ParseOk("{}");

        Assert.Empty(m.Announcements);
        Assert.Empty(m.BlockedBuilds);
        Assert.Empty(m.Links);
        Assert.Empty(m.Playtests);
        Assert.Null(m.Hosting);
        Assert.Null(m.BugReportWebhookUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n ")]
    [InlineData("[]")]
    [InlineData("[{ \"id\": \"a\" }]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"texto\"")]
    [InlineData("<html><body>Inicia sesión en la wifi del hotel</body></html>")]
    [InlineData("{ \"announcements\": [ ")]
    [InlineData("{} {}")]
    [InlineData("{ 'id': 'comillas simples' }")]
    public void Parse_NotAJsonObject_ReturnsNull(string json)
    {
        Assert.Null(ManifestProvider.Parse(json));
    }

    [Fact]
    public void Parse_Null_ReturnsNullWithoutThrowing()
    {
        Assert.Null(ManifestProvider.Parse(null!));
    }

    [Fact]
    public void Parse_Bom_IsAccepted()
    {
        var m = ParseOk("\uFEFF{ \"links\": [ { \"url\": \"https://discord.gg/x\", \"icon\": \"discord\" } ] }");
        Assert.Single(m.Links);
    }

    [Fact]
    public void Parse_UnknownPropertiesAndCaseInsensitiveNames_AreFine()
    {
        var m = ParseOk("""
            {
              "$schema": "https://example.invalid/launcher.schema.json",
              "version": 3,
              "ANNOUNCEMENTS": [ { "ID": "a", "TITLE": "Hola", "extra": { "x": [1, 2, 3] } } ],
              "Hosting": { "PORT": 7777 }
            }
            """);

        Assert.Equal("Hola", Assert.Single(m.Announcements).Title);
        Assert.Equal(7777, m.Hosting!.Port);
    }

    [Fact]
    public void Parse_DuplicatedProperty_LastOneWins()
    {
        var m = ParseOk("""{ "announcements": [ { "id": "a", "title": "Primero", "Title": "Segundo" } ] }""");
        Assert.Equal("Segundo", m.Announcements[0].Title);
    }

    // ───────────────────────────── Parse: entradas hostiles ─────────────────────────────

    [Fact]
    public void Parse_LargerThan256KB_ReturnsNull_ButJustBelowIsAccepted()
    {
        const string prefix = "{ \"announcements\": [ { \"id\": \"a\", \"title\": \"t\", \"message\": ";
        const string suffix = " } ] }";
        var room = ManifestProvider.MaxJsonBytes - prefix.Length - suffix.Length - 2;

        var fits = prefix + "\"" + new string('x', room) + "\"" + suffix;
        Assert.Equal(ManifestProvider.MaxJsonBytes, Encoding.UTF8.GetByteCount(fits));
        var m = ParseOk(fits);
        Assert.Equal(ManifestProvider.MaxAnnouncementMessageLength, m.Announcements[0].Message!.Length);

        var tooBig = prefix + "\"" + new string('x', room + 1) + "\"" + suffix;
        Assert.Null(ManifestProvider.Parse(tooBig));
    }

    [Fact]
    public void Parse_LimitIsInUtf8Bytes_NotCharacters()
    {
        // 140 000 «ñ» = 140 000 caracteres pero 280 000 bytes en UTF-8.
        var json = "{ \"announcements\": [ { \"id\": \"a\", \"title\": \"t\", \"message\": \"" + new string('ñ', 140_000) + "\" } ] }";
        Assert.True(json.Length < ManifestProvider.MaxJsonBytes);
        Assert.Null(ManifestProvider.Parse(json));
    }

    [Fact]
    public void Parse_NestingDeeperThan32_ReturnsNull_ButModerateNestingIsFine()
    {
        static string Nested(int depth) => "{ \"x\": " + string.Concat(Enumerable.Repeat("[", depth)) + string.Concat(Enumerable.Repeat("]", depth)) + " }";

        Assert.NotNull(ManifestProvider.Parse(Nested(20)));
        Assert.Null(ManifestProvider.Parse(Nested(40)));
        Assert.Null(ManifestProvider.Parse(Nested(5000)));
    }

    [Fact]
    public void Parse_HugeLists_AreCappedAfterSanitizing()
    {
        var sb = new StringBuilder("{ \"announcements\": [");
        for (var i = 0; i < 2000; i++) sb.Append($$"""{ "id": "a{{i}}", "title": "Anuncio {{i}}" },""");
        sb.Append("], \"blockedBuilds\": [");
        for (var i = 0; i < 2000; i++) sb.Append($$"""{ "match": "v0.{{i}}.0" },""");
        sb.Append("], \"links\": [");
        for (var i = 0; i < 100; i++) sb.Append($$"""{ "url": "https://example.com/{{i}}" },""");
        sb.Append("], \"playtests\": [");
        for (var i = 0; i < 100; i++) sb.Append($$"""{ "id": "p{{i}}", "startsUtc": "2026-11-01T18:00:00Z" },""");
        sb.Append("] }");

        var m = ParseOk(sb.ToString());

        Assert.Equal(ManifestProvider.MaxAnnouncements, m.Announcements.Count);
        Assert.Equal("a0", m.Announcements[0].Id);
        Assert.Equal(ManifestProvider.MaxBlockedBuilds, m.BlockedBuilds.Count);
        Assert.Equal(ManifestProvider.MaxLinks, m.Links.Count);
        Assert.Equal(ManifestProvider.MaxPlaytests, m.Playtests.Count);
    }

    [Fact]
    public void Parse_WrongTypes_AreIgnoredFieldByField()
    {
        var m = ParseOk("""
            {
              "announcements": [
                "esto no es un objeto",
                42,
                null,
                { "id": 7, "title": 2026, "level": 2, "dismissible": "false", "message": ["no", "texto"], "startsUtc": 1729800000 },
                { "id": "b", "title": "Otro", "dismissible": "quizá", "url": { "href": "https://x.com" } }
              ],
              "blockedBuilds": { "match": "v0.0.1" },
              "links": "https://discord.gg/x",
              "playtests": [
                { "id": "p1", "startsUtc": "2026-11-01T18:00:00Z", "durationMinutes": "90" },
                { "id": "p2", "startsUtc": "2026-11-02T18:00:00Z", "durationMinutes": "mucho rato" },
                { "id": "p3", "startsUtc": "2026-11-03T18:00:00Z", "durationMinutes": 89.6 }
              ],
              "hosting": { "port": "7770", "notes": false },
              "bugReportWebhookUrl": 12345
            }
            """);

        Assert.Equal(2, m.Announcements.Count);
        var first = m.Announcements[0];
        Assert.Equal("7", first.Id);
        Assert.Equal("2026", first.Title);
        Assert.Equal(AnnouncementLevel.Info, first.Level);
        Assert.False(first.Dismissible);
        Assert.Null(first.Message);
        Assert.Null(first.StartsUtc);
        Assert.True(m.Announcements[1].Dismissible);
        Assert.Null(m.Announcements[1].Url);

        Assert.Empty(m.BlockedBuilds);
        Assert.Empty(m.Links);
        Assert.Equal(new[] { 90, 120, 90 }, m.Playtests.Select(p => p.DurationMinutes));
        Assert.Equal(7770, m.Hosting!.Port);
        Assert.Null(m.Hosting.Notes);
        Assert.Null(m.BugReportWebhookUrl);
    }

    [Theory]
    [InlineData("info", AnnouncementLevel.Info)]
    [InlineData("INFO", AnnouncementLevel.Info)]
    [InlineData(" Warning ", AnnouncementLevel.Warning)]
    [InlineData("critical", AnnouncementLevel.Critical)]
    [InlineData("aviso", AnnouncementLevel.Warning)]
    [InlineData("Crítico", AnnouncementLevel.Critical)]
    [InlineData("critico", AnnouncementLevel.Critical)]
    [InlineData("información", AnnouncementLevel.Info)]
    [InlineData("urgentísimo", AnnouncementLevel.Info)]
    [InlineData("2", AnnouncementLevel.Info)]
    [InlineData("", AnnouncementLevel.Info)]
    public void Parse_Level_TextCaseInsensitiveWithSpanishAliases_UnknownIsInfo(string level, AnnouncementLevel expected)
    {
        var m = ParseOk($$"""{ "announcements": [ { "id": "a", "title": "t", "level": "{{level}}" } ] }""");
        Assert.Equal(expected, m.Announcements[0].Level);
    }

    [Fact]
    public void Parse_LoneSurrogateEscape_DropsOnlyThatValue()
    {
        var m = ParseOk("""{ "announcements": [ { "id": "a", "title": "Hola", "message": "\uD800 roto" } ] }""");
        var a = Assert.Single(m.Announcements);
        Assert.Equal("Hola", a.Title);
        Assert.Null(a.Message);
    }

    [Fact]
    public void Parse_ControlCharactersAndBidiMarks_AreRemoved()
    {
        var m = ParseOk("""{ "announcements": [ { "id": "a", "title": "Ho\u0007la\u202E gpj.exe\u0000", "message": "L1\r\nL2\n\n\n\nL3\tfin" } ] }""");
        Assert.Equal("Hola gpj.exe", m.Announcements[0].Title);
        Assert.Equal("L1\nL2\n\nL3 fin", m.Announcements[0].Message);
    }

    // ───────────────────────────── Parse: fechas ─────────────────────────────

    [Theory]
    [InlineData("2026-10-24T19:00:00Z", 19, 0)]
    [InlineData("2026-10-24T19:00:00z", 19, 0)]
    [InlineData("2026-10-24T21:00:00+02:00", 19, 0)]
    [InlineData("2026-10-24T21:00+02:00", 19, 0)]
    [InlineData("2026-10-24T21:00:00+0200", 19, 0)]
    [InlineData("2026-10-24T14:00:00-05:00", 19, 0)]
    [InlineData("2026-10-24 19:30Z", 19, 30)]
    [InlineData("2026-10-24T19:00:00.5Z", 19, 0)]
    [InlineData("  2026-10-24T19:00:00Z  ", 19, 0)]
    public void Parse_PlaytestDatesWithZone_AreConvertedToUtc(string text, int hour, int minute)
    {
        var m = ParseOk($$"""{ "playtests": [ { "id": "p", "startsUtc": "{{text}}" } ] }""");
        var start = Assert.Single(m.Playtests).StartsUtc;
        Assert.Equal(TimeSpan.Zero, start.Offset);
        Assert.Equal((2026, 10, 24, hour, minute), (start.Year, start.Month, start.Day, start.Hour, start.Minute));
    }

    [Fact]
    public void Parse_DateWithoutZone_IsLocalTimeOfThisPc()
    {
        var m = ParseOk("""{ "playtests": [ { "id": "p", "startsUtc": "2026-10-24T21:00:00" } ] }""");

        var expected = new DateTimeOffset(new DateTime(2026, 10, 24, 21, 0, 0, DateTimeKind.Unspecified));
        Assert.Equal(expected, m.Playtests[0].StartsUtc);
        Assert.Equal(TimeSpan.Zero, m.Playtests[0].StartsUtc.Offset);
    }

    [Theory]
    [InlineData("24/10/2026 21:00")]
    [InlineData("10/24/2026")]
    [InlineData("mañana a las 9")]
    [InlineData("2026-13-01T10:00:00Z")]
    [InlineData("2026-02-30T10:00:00Z")]
    [InlineData("2026-10-24T24:00:00Z")]
    [InlineData("2026-10-24T21:00:00+15:00")]
    [InlineData("2026-10-24T21:00:00+02:75")]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("0000-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59Z")]
    [InlineData("")]
    public void Parse_InvalidOrDefaultPlaytestStart_DiscardsTheSession(string text)
    {
        var m = ParseOk($$"""{ "playtests": [ { "id": "mala", "startsUtc": "{{text}}" }, { "id": "buena", "startsUtc": "2026-11-01T18:00:00Z" } ] }""");
        Assert.Equal("buena", Assert.Single(m.Playtests).Id);
    }

    [Fact]
    public void ParseDate_ReportsWhetherTheTextHadAZone()
    {
        Assert.NotNull(ManifestProvider.ParseDate("2026-10-24T21:00:00+02:00", out var withZone));
        Assert.True(withZone);
        Assert.NotNull(ManifestProvider.ParseDate("2026-10-24", out var dateOnly));
        Assert.False(dateOnly);
        Assert.Null(ManifestProvider.ParseDate(new string('1', 100), out _));
        Assert.Null(ManifestProvider.ParseDate(null, out _));
    }

    // ───────────────────────────── Sanitize ─────────────────────────────

    [Fact]
    public void Sanitize_Announcements_DiscardsInvalidOnes_AndKeepsTheFirstOfRepeatedIds()
    {
        var starts = Utc(2026, 10, 20);
        var m = new LauncherManifest
        {
            Announcements =
            {
                new Announcement { Id = "con espacios", Title = "x" },
                new Announcement { Id = new string('a', 65), Title = "x" },
                new Announcement { Id = "", Title = "x" },
                new Announcement { Id = "<script>", Title = "x" },
                new Announcement { Id = "sin-titulo", Title = "  \u0007 " },
                new Announcement { Id = "al-reves", Title = "x", StartsUtc = starts, EndsUtc = starts.AddSeconds(-1) },
                null!,
                new Announcement { Id = "  Bienvenida  ", Title = "Primera" },
                new Announcement { Id = "BIENVENIDA", Title = "Repetida" },
                new Announcement { Id = new string('b', 64), Title = "Id de 64" },
                new Announcement { Id = "mismo-instante", Title = "x", StartsUtc = starts, EndsUtc = starts },
                new Announcement { Id = "año.2026_ñ", Title = "Letras con tilde" },
            },
        };

        ManifestProvider.Sanitize(m);

        Assert.Equal(new[] { "Bienvenida", new string('b', 64), "mismo-instante", "año.2026_ñ" }, m.Announcements.Select(a => a.Id));
        Assert.Equal("Primera", m.Announcements[0].Title);
    }

    [Fact]
    public void Sanitize_Announcements_FixesFields()
    {
        var m = new LauncherManifest
        {
            Announcements =
            {
                new Announcement { Id = "a", Title = new string('T', 200), Message = new string('M', 1000), Url = "https://x.com/a", LinkText = new string('L', 100) },
                new Announcement { Id = "b", Title = "t", Url = "http://inseguro.com", LinkText = "Abrir" },
                new Announcement { Id = "c", Title = "t", Url = "https://discord.gg/abc" },
                new Announcement { Id = "d", Title = "t", Level = AnnouncementLevel.Critical, Dismissible = true },
                new Announcement { Id = "e", Title = "t", Level = (AnnouncementLevel)99 },
                new Announcement { Id = "f", Title = "t", StartsUtc = new DateTimeOffset(2026, 10, 24, 21, 0, 0, TimeSpan.FromHours(2)) },
            },
        };

        ManifestProvider.Sanitize(m);

        var a = m.Announcements[0];
        Assert.Equal(ManifestProvider.MaxAnnouncementTitleLength, a.Title.Length);
        Assert.EndsWith("…", a.Title);
        Assert.Equal(ManifestProvider.MaxAnnouncementMessageLength, a.Message!.Length);
        Assert.Equal(ManifestProvider.MaxLinkTextLength, a.LinkText!.Length);

        Assert.Null(m.Announcements[1].Url);
        Assert.Null(m.Announcements[1].LinkText);
        Assert.Equal("Más información", m.Announcements[2].LinkText);
        Assert.False(m.Announcements[3].Dismissible);
        Assert.Equal(AnnouncementLevel.Info, m.Announcements[4].Level);
        Assert.Equal(TimeSpan.Zero, m.Announcements[5].StartsUtc!.Value.Offset);
        Assert.Equal(Utc(2026, 10, 24, 19), m.Announcements[5].StartsUtc);
    }

    [Fact]
    public void Sanitize_AnnouncementsAreCappedAtTen_CountingOnlyValidOnes()
    {
        var m = new LauncherManifest();
        m.Announcements.Add(new Announcement { Id = "", Title = "inválido" });
        for (var i = 0; i < 15; i++) m.Announcements.Add(new Announcement { Id = $"a{i}", Title = "t" });

        ManifestProvider.Sanitize(m);

        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"a{i}"), m.Announcements.Select(a => a.Id));
    }

    [Theory]
    [InlineData("http://discord.gg/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ftp://example.com/juego.zip")]
    [InlineData("discord.gg/x")]
    [InlineData("/relativa")]
    [InlineData("https://discord.com@evil.example/")]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("https://exa mple.com/")]
    [InlineData("https://example.com/\nlinea")]
    [InlineData("   ")]
    public void NormalizeHttpsUrl_RejectsAnythingButAbsoluteHttps(string url)
    {
        Assert.Null(ManifestProvider.NormalizeHttpsUrl(url));
    }

    [Theory]
    [InlineData("https://discord.gg/coop2d", "https://discord.gg/coop2d")]
    [InlineData("  HTTPS://Discord.GG/Coop2D  ", "https://discord.gg/Coop2D")]
    [InlineData("https://itch.io", "https://itch.io/")]
    [InlineData("https://example.com:8443/a?b=c#d", "https://example.com:8443/a?b=c#d")]
    public void NormalizeHttpsUrl_ReturnsCanonicalForm(string url, string expected)
    {
        Assert.Equal(expected, ManifestProvider.NormalizeHttpsUrl(url));
    }

    [Fact]
    public void NormalizeHttpsUrl_RejectsAbsurdlyLongUrls()
    {
        Assert.Null(ManifestProvider.NormalizeHttpsUrl("https://example.com/" + new string('a', 3000)));
    }

    [Fact]
    public void Sanitize_Links()
    {
        var m = new LauncherManifest
        {
            Links =
            {
                new CommunityLink { Label = "Malo", Url = "http://inseguro.com", Icon = "web" },
                new CommunityLink { Label = "", Url = "https://discord.gg/x", Icon = " Discord " },
                new CommunityLink { Label = "Canal", Url = "https://youtube.com/@palito", Icon = "tiktok" },
                new CommunityLink { Label = new string('L', 50), Url = "https://store.steampowered.com/app/1", Icon = "STEAM" },
                new CommunityLink { Label = "Sin icono", Url = "https://github.com/JJJP-Dev", Icon = null! },
                null!,
            },
        };

        ManifestProvider.Sanitize(m);

        Assert.Equal(4, m.Links.Count);
        Assert.Equal(("Discord", "discord"), (m.Links[0].Label, m.Links[0].Icon));
        Assert.Equal(("Canal", "web"), (m.Links[1].Label, m.Links[1].Icon));
        Assert.Equal(ManifestProvider.MaxLinkLabelLength, m.Links[2].Label.Length);
        Assert.Equal("steam", m.Links[2].Icon);
        Assert.Equal("web", m.Links[3].Icon);
    }

    [Theory]
    [InlineData("discord", "Discord")]
    [InlineData("itch", "itch.io")]
    [InlineData("steam", "Steam")]
    [InlineData("youtube", "YouTube")]
    [InlineData("github", "GitHub")]
    [InlineData("web", "Web")]
    [InlineData("desconocido", "Web")]
    public void Sanitize_LinkWithoutLabel_UsesTheIconName(string icon, string expectedLabel)
    {
        var m = new LauncherManifest { Links = { new CommunityLink { Url = "https://example.com/", Icon = icon } } };
        ManifestProvider.Sanitize(m);
        Assert.Equal(expectedLabel, m.Links[0].Label);
    }

    [Fact]
    public void Sanitize_Playtests()
    {
        var start = new DateTimeOffset(2026, 10, 24, 21, 0, 0, TimeSpan.FromHours(2));
        var m = new LauncherManifest
        {
            Playtests =
            {
                new Playtest { Id = "", StartsUtc = start },
                new Playtest { Id = "sin-fecha" },
                new Playtest { Id = "imposible", StartsUtc = DateTimeOffset.MaxValue },
                new Playtest
                {
                    Id = " pre2 ", Title = "  ", Description = "Línea 1\r\nLínea 2", StartsUtc = start, DurationMinutes = 5,
                    RequiredVersion = "  v0.0.4  ", DiscordUrl = "http://discord.gg/x",
                },
                new Playtest { Id = "PRE2", Title = "Repetida", StartsUtc = start },
                new Playtest { Id = "larga", Title = new string('T', 100), StartsUtc = start, DurationMinutes = 100_000, DiscordUrl = "https://discord.gg/x" },
                new Playtest { Id = "cero", StartsUtc = start, DurationMinutes = 0, RequiredVersion = "   " },
            },
        };

        ManifestProvider.Sanitize(m);

        Assert.Equal(new[] { "pre2", "larga", "cero" }, m.Playtests.Select(p => p.Id));
        var pre2 = m.Playtests[0];
        Assert.Equal("Sesión de prueba", pre2.Title);
        Assert.Equal("Línea 1\nLínea 2", pre2.Description);
        Assert.Equal(Utc(2026, 10, 24, 19), pre2.StartsUtc);
        Assert.Equal(TimeSpan.Zero, pre2.StartsUtc.Offset);
        Assert.Equal(15, pre2.DurationMinutes);
        Assert.Equal("v0.0.4", pre2.RequiredVersion);
        Assert.Null(pre2.DiscordUrl);

        Assert.Equal(720, m.Playtests[1].DurationMinutes);
        Assert.Equal(ManifestProvider.MaxPlaytestTitleLength, m.Playtests[1].Title.Length);
        Assert.Equal("https://discord.gg/x", m.Playtests[1].DiscordUrl);
        Assert.Equal(15, m.Playtests[2].DurationMinutes);
        Assert.Null(m.Playtests[2].RequiredVersion);
    }

    [Fact]
    public void Sanitize_BlockedBuilds()
    {
        var m = new LauncherManifest
        {
            BlockedBuilds =
            {
                new BlockedBuild { Match = "   ", Reason = "sin match" },
                new BlockedBuild { Match = " v0.0.3 ", Reason = "" },
                new BlockedBuild { Match = "V0.0.3", Reason = "repetida" },
                new BlockedBuild { Match = "2D_0.0.2_Protype3.zip", Reason = new string('R', 500) },
                new BlockedBuild { Match = new string('m', 200), Reason = "larga" },
                null!,
            },
        };

        ManifestProvider.Sanitize(m);

        Assert.Equal(3, m.BlockedBuilds.Count);
        Assert.Equal("v0.0.3", m.BlockedBuilds[0].Match);
        Assert.Equal(ManifestProvider.DefaultBlockReason, m.BlockedBuilds[0].Reason);
        Assert.Equal(ManifestProvider.MaxReasonLength, m.BlockedBuilds[1].Reason.Length);
        Assert.Equal(ManifestProvider.MaxMatchLength, m.BlockedBuilds[2].Match.Length);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(65536, null)]
    [InlineData(1, 1)]
    [InlineData(7770, 7770)]
    [InlineData(65535, 65535)]
    public void Sanitize_HostingPort(int port, int? expected)
    {
        var m = new LauncherManifest { Hosting = new HostingInfo { Port = port, Notes = "Usad Radmin VPN" } };
        ManifestProvider.Sanitize(m);
        Assert.Equal(expected, m.Hosting!.Port);
    }

    [Fact]
    public void Sanitize_HostingWithoutPortNorNotes_BecomesNull_AndNotesAreCleaned()
    {
        var empty = new LauncherManifest { Hosting = new HostingInfo { Port = 0, Notes = " \u0000 " } };
        Assert.Null(ManifestProvider.Sanitize(empty).Hosting);

        var notes = new LauncherManifest { Hosting = new HostingInfo { Notes = new string('n', 1000) } };
        Assert.Equal(ManifestProvider.MaxHostingNotesLength, ManifestProvider.Sanitize(notes).Hosting!.Notes!.Length);
    }

    [Theory]
    [InlineData(ManifestSamples.Webhook, ManifestSamples.Webhook)]
    [InlineData("  https://DiscordApp.com/api/webhooks/123/tok_en-1/  ", "https://discordapp.com/api/webhooks/123/tok_en-1")]
    [InlineData("https://evil.example/api/webhooks/1/x", null)]
    [InlineData("http://discord.com/api/webhooks/123/abc", null)]
    [InlineData("https://discord.com/channels/1/2", null)]
    [InlineData("", null)]
    public void Sanitize_BugReportWebhook_UsesSurveyRules(string url, string? expected)
    {
        var m = new LauncherManifest { BugReportWebhookUrl = url };
        Assert.Equal(expected, ManifestProvider.Sanitize(m).BugReportWebhookUrl);
    }

    [Fact]
    public void Sanitize_NullListsBecomeEmpty_ReturnsSameInstance_AndIsIdempotent()
    {
        var bare = new LauncherManifest { Announcements = null!, BlockedBuilds = null!, Links = null!, Playtests = null! };
        var result = ManifestProvider.Sanitize(bare);
        Assert.Same(bare, result);
        Assert.Empty(result.Announcements);
        Assert.Empty(result.BlockedBuilds);
        Assert.Empty(result.Links);
        Assert.Empty(result.Playtests);

        var once = ParseOk(ManifestSamples.Full);
        var twice = ManifestProvider.Sanitize(ParseOk(ManifestSamples.Full));
        ManifestProvider.Sanitize(twice);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(once), System.Text.Json.JsonSerializer.Serialize(twice));
    }

    [Fact]
    public void Sanitize_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ManifestProvider.Sanitize(null!));
    }

    [Fact]
    public void Parse_DocsExampleFile_IsAccepted()
    {
        // Si la documentación incluye un ejemplo completo (docs/launcher.example.json), tiene que ser válido.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PalitoLauncher.sln"))) dir = dir.Parent;
        var example = dir is null ? null : Path.Combine(dir.FullName, "docs", "launcher.example.json");
        if (example is null || !File.Exists(example)) return;

        Assert.NotNull(ManifestProvider.Parse(File.ReadAllText(example)));
    }

    // ───────────────────────────── GetManifestAsync ─────────────────────────────

    private (ManifestProvider Provider, FakeRawServer Server, string AppData) CreateProvider(string? remoteBody = null)
    {
        var server = new FakeRawServer(LauncherConfig.RemoteManifestUrl, remoteBody);
        var github = new GitHubClient(HttpFactory.Create(server), _temp.Combine("cache"));
        var appData = _temp.Combine("appdata");
        Directory.CreateDirectory(appData);
        return (new ManifestProvider(github, appData), server, appData);
    }

    private string RemoteCachePath => Path.Combine(_temp.Combine("cache"), ManifestProvider.RemoteCacheFileName);

    [Fact]
    public async Task GetManifestAsync_NoFileAnywhere_ReturnsEmptyManifest()
    {
        var (provider, server, _) = CreateProvider(remoteBody: null);

        var m = await provider.GetManifestAsync(CancellationToken.None);

        Assert.Empty(m.Announcements);
        Assert.Empty(m.Playtests);
        Assert.Empty(m.Links);
        Assert.Null(m.BugReportWebhookUrl);
        Assert.Equal(1, server.RequestsTo(LauncherConfig.RemoteManifestUrl));
    }

    [Fact]
    public async Task GetManifestAsync_Remote_IsUsedAndCached_ThenRevalidatedWithETag()
    {
        var (provider, server, _) = CreateProvider(ManifestSamples.Full);

        var first = await provider.GetManifestAsync(CancellationToken.None);
        var second = await provider.GetManifestAsync(CancellationToken.None);

        Assert.Equal(3, first.Announcements.Count);
        Assert.Equal(3, second.Announcements.Count);
        Assert.True(File.Exists(RemoteCachePath));
        var requests = server.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Null(requests[0].IfNoneMatch);
        Assert.Equal("\"v0\"", requests[1].IfNoneMatch);
    }

    [Fact]
    public async Task GetManifestAsync_LocalOverride_WinsWithoutTouchingTheNetwork()
    {
        var (provider, server, appData) = CreateProvider(ManifestSamples.Full);
        File.WriteAllText(Path.Combine(appData, ManifestProvider.LocalOverrideFileName),
            """{ "announcements": [ { "id": "local", "title": "Probando en local" } ] }""");

        var m = await provider.GetManifestAsync(CancellationToken.None);

        Assert.Equal("local", Assert.Single(m.Announcements).Id);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task GetManifestAsync_LocalEmptyObject_IsValid_AndHidesTheRemote()
    {
        var (provider, server, appData) = CreateProvider(ManifestSamples.Full);
        File.WriteAllText(Path.Combine(appData, ManifestProvider.LocalOverrideFileName), "{}");

        var m = await provider.GetManifestAsync(CancellationToken.None);

        Assert.Empty(m.Announcements);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task GetManifestAsync_LocalWithBomAndUtf8_IsRead()
    {
        var (provider, _, appData) = CreateProvider();
        File.WriteAllText(Path.Combine(appData, ManifestProvider.LocalOverrideFileName),
            """{ "announcements": [ { "id": "a", "title": "Año nuevo, versión nueva" } ] }""", new UTF8Encoding(true));

        var m = await provider.GetManifestAsync(CancellationToken.None);

        Assert.Equal("Año nuevo, versión nueva", m.Announcements[0].Title);
    }

    [Fact]
    public async Task GetManifestAsync_InvalidLocal_FallsBackToRemote()
    {
        var (provider, _, appData) = CreateProvider(ManifestSamples.Full);
        File.WriteAllText(Path.Combine(appData, ManifestProvider.LocalOverrideFileName), "{ esto no es json ");

        var m = await provider.GetManifestAsync(CancellationToken.None);

        Assert.Equal(3, m.Announcements.Count);
    }

    [Fact]
    public async Task GetManifestAsync_OversizedLocal_IsIgnored()
    {
        var (provider, _, appData) = CreateProvider(remoteBody: null);
        var padding = new string(' ', ManifestProvider.MaxJsonBytes);
        File.WriteAllText(Path.Combine(appData, ManifestProvider.LocalOverrideFileName),
            """{ "announcements": [ { "id": "a", "title": "t" } ] }""" + padding);

        var m = await provider.GetManifestAsync(CancellationToken.None);

        Assert.Empty(m.Announcements);
    }

    [Fact]
    public async Task GetManifestAsync_BrokenRemote_KeepsTheLastGoodCopy()
    {
        var (provider, server, _) = CreateProvider(ManifestSamples.Full);

        var good = await provider.GetManifestAsync(CancellationToken.None);
        server.Body = "{ \"announcements\": [ { \"id\": \"roto\", "; // commit a medias
        var broken = await provider.GetManifestAsync(CancellationToken.None);

        Assert.Equal(3, good.Announcements.Count);
        Assert.Equal(3, broken.Announcements.Count);
        Assert.Contains("bienvenida-pre2", File.ReadAllText(RemoteCachePath));
    }

    [Fact]
    public async Task GetManifestAsync_BrokenRemote_FromAFreshProvider_UsesTheDiskCache()
    {
        var (provider, server, appData) = CreateProvider(ManifestSamples.Full);
        await provider.GetManifestAsync(CancellationToken.None);
        server.Body = "<html>Error 500</html>";

        // Otro arranque del launcher: misma caché de disco, sin memoria de la sesión anterior.
        var restarted = new ManifestProvider(new GitHubClient(HttpFactory.Create(server), _temp.Combine("cache")), appData);
        var m = await restarted.GetManifestAsync(CancellationToken.None);

        Assert.Equal(3, m.Announcements.Count);
    }

    [Fact]
    public async Task GetManifestAsync_BrokenRemoteWithoutCache_ReturnsEmpty_AndDoesNotCacheIt()
    {
        var (provider, _, _) = CreateProvider("[ \"no es un objeto\" ]");

        var m = await provider.GetManifestAsync(CancellationToken.None);

        Assert.Empty(m.Announcements);
        Assert.False(File.Exists(RemoteCachePath));
    }

    [Fact]
    public async Task GetManifestAsync_Offline_UsesTheCache_OrEmptyWithoutIt()
    {
        var (provider, server, _) = CreateProvider(ManifestSamples.Full);
        server.Fail = true;
        var offlineNoCache = await provider.GetManifestAsync(CancellationToken.None);
        server.Fail = false;
        await provider.GetManifestAsync(CancellationToken.None);
        server.Fail = true;
        var offlineWithCache = await provider.GetManifestAsync(CancellationToken.None);

        Assert.Empty(offlineNoCache.Announcements);
        Assert.Equal(3, offlineWithCache.Announcements.Count);
    }

    [Fact]
    public async Task GetManifestAsync_RemoteDeleted_ReturnsEmpty_AndClearsTheCache()
    {
        var (provider, server, _) = CreateProvider(ManifestSamples.Full);
        await provider.GetManifestAsync(CancellationToken.None);
        server.Body = null;

        var m = await provider.GetManifestAsync(CancellationToken.None);

        Assert.Empty(m.Announcements);
        Assert.False(File.Exists(RemoteCachePath));
    }

    [Fact]
    public async Task GetManifestAsync_RemoteChange_IsPickedUp()
    {
        var (provider, server, _) = CreateProvider(ManifestSamples.Full);
        await provider.GetManifestAsync(CancellationToken.None);
        server.Body = """{ "announcements": [ { "id": "nuevo", "title": "Ya está el Prototipo 3" } ] }""";

        var m = await provider.GetManifestAsync(CancellationToken.None);

        Assert.Equal("nuevo", Assert.Single(m.Announcements).Id);
    }

    [Fact]
    public async Task GetManifestAsync_ReturnsAFreshInstanceEachTime()
    {
        var (provider, _, _) = CreateProvider(ManifestSamples.Full);

        var first = await provider.GetManifestAsync(CancellationToken.None);
        first.Announcements.Clear();
        first.Playtests[0].Title = "modificado por la UI";
        var second = await provider.GetManifestAsync(CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.Equal(3, second.Announcements.Count);
        Assert.Equal("Prueba de diversión del Pre 2", second.Playtests[0].Title);
    }

    [Fact]
    public async Task GetManifestAsync_ConcurrentCalls_ShareOneDownload()
    {
        var (provider, server, _) = CreateProvider(ManifestSamples.Full);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Gate = gate.Task;

        var first = provider.GetManifestAsync(CancellationToken.None);
        var second = provider.GetManifestAsync(CancellationToken.None);
        gate.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, server.RequestsTo(LauncherConfig.RemoteManifestUrl));
        Assert.All(results, m => Assert.Equal(3, m.Announcements.Count));
        Assert.NotSame(results[0], results[1]);
    }

    [Fact]
    public async Task GetManifestAsync_Canceled_Throws()
    {
        var (provider, _, _) = CreateProvider(ManifestSamples.Full);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetManifestAsync(cts.Token));
    }

    [Fact]
    public async Task GetManifestAsync_CanceledWhileDownloading_Throws()
    {
        var (provider, server, _) = CreateProvider(ManifestSamples.Full);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Gate = gate.Task; // no responde hasta que se suelte
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetManifestAsync(cts.Token));
        }
        finally
        {
            // La descarga de fondo termina con un 404 (no escribe nada en la carpeta temporal).
            server.Body = null;
            gate.SetResult();
        }
    }

    [Fact]
    public void Constructor_ValidatesArguments()
    {
        var github = new GitHubClient(HttpFactory.Create(new FakeRawServer(LauncherConfig.RemoteManifestUrl)), _temp.Combine("cache"));
        Assert.Throws<ArgumentNullException>(() => new ManifestProvider(null!, _temp.Path));
        Assert.ThrowsAny<ArgumentException>(() => new ManifestProvider(github, " "));
        Assert.Equal("launcher.local.json", ManifestProvider.LocalOverrideFileName);
    }
}
