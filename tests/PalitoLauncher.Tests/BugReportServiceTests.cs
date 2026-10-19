using System.Net;
using System.Text;
using System.Text.Json;
using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.Net;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Tests.TestHelpers.B5;

namespace PalitoLauncher.Tests;

public sealed class BugReportServiceTests : IDisposable
{
    private const string Webhook = "https://discord.com/api/webhooks/123456789012345678/AbC-dEf_123xyz";
    private const string PostUrl = Webhook + "?wait=true";

    private const string TestUser = "Ana";
    private const string TestProfile = @"C:\Users\Ana";
    private const string TestMachine = "DESKTOP-AB12CD";

    private readonly TempDir _temp = new();
    private DateTimeOffset _now = new(2026, 10, 19, 18, 0, 0, TimeSpan.Zero);

    public void Dispose() => _temp.Dispose();

    private string OutboxDir => _temp.Combine("reports", "outbox");

    private BugReportService CreateService(MultipartCapturingHandler handler, string? outboxDir = null)
        => new(HttpFactory.Create(handler), outboxDir ?? OutboxDir)
        {
            UtcNow = () => _now,
            RedactUserName = TestUser,
            RedactUserProfile = TestProfile,
            RedactMachineName = TestMachine,
        };

    private static MultipartCapturingHandler OkHandler() => new(_ => MultipartCapturingHandler.DiscordOk());

    private static MultipartCapturingHandler NoNetworkHandler()
        => new(_ => throw new InvalidOperationException("No debería haber peticiones de red."));

    private static BugReport CreateReport(BugCategory category = BugCategory.Bug, string? nickname = "Marta",
        DateTimeOffset? createdAt = null, int? exitCode = null, string description = "El cofre del nivel 2 no se abre al pulsar E.")
        => new()
        {
            Category = category,
            Description = description,
            Steps = "1. Entrar al nivel 2\n2. Ir al cofre\n3. Pulsar E",
            Nickname = nickname,
            GameVersion = "0.0.3-pre.2",
            GameVersionDisplay = "0.0.3 · Prototipo 2",
            LauncherVersion = "1.0.5",
            OsDescription = "Microsoft Windows 10.0.26200",
            ExitCode = exitCode,
            CreatedAtUtc = createdAt ?? new DateTimeOffset(2026, 10, 19, 17, 30, 15, TimeSpan.Zero),
        };

    private const string PlayerLogText =
        "Mono path[0] = 'C:/Users/Ana/AppData/Local/PalitoGames/Coop2D/Coop2D_Data/Managed'\n" +
        "Machine: DESKTOP-AB12CD\n" +
        "User Ana connected\n" +
        "Anabel stays\n";

    private const string PlayerLogRedacted =
        "Mono path[0] = '%USERPROFILE%/AppData/Local/PalitoGames/Coop2D/Coop2D_Data/Managed'\n" +
        "Machine: <equipo>\n" +
        "User <usuario> connected\n" +
        "Anabel stays\n";

    /// <summary>Los tres logs con contenido conocido + Summary.</summary>
    private BugReportSources CreateSources()
        => new()
        {
            PlayerLog = _temp.WriteFile(@"LocalLow\Player.log", PlayerLogText),
            PlayerPrevLog = _temp.WriteFile(@"LocalLow\Player-prev.log", "Sesión anterior de Ana en DESKTOP-AB12CD\n"),
            LauncherLog = _temp.WriteFile(@"logs\launcher-20261019.log", @"[INFO ] Instalado en C:\Users\Ana\AppData\Local\PalitoGames\Coop2D" + "\n"),
            Summary = new Dictionary<string, string?>
            {
                ["Estado"] = "Listo para jugar",
                ["Carpeta del juego"] = @"C:\Users\Ana\AppData\Local\PalitoGames\Coop2D",
                ["Espacio libre"] = "120 GB",
                ["Vacío"] = null,
            },
        };

    private string[] OutboxFolders() => Directory.Exists(OutboxDir)
        ? Directory.GetDirectories(OutboxDir)
            .Where(d => !string.Equals(Path.GetFileName(d), "failed", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileName(d).StartsWith(".tmp-", StringComparison.Ordinal))
            .ToArray()
        : Array.Empty<string>();

    private static string[] FailedFolders(BugReportService service) => Directory.Exists(service.FailedDir)
        ? Directory.GetDirectories(service.FailedDir)
        : Array.Empty<string>();

    private sealed record Field(string Name, string Value, bool Inline);

    private static JsonElement GetEmbed(JsonDocument doc)
    {
        var embeds = doc.RootElement.GetProperty("embeds");
        Assert.Equal(1, embeds.GetArrayLength());
        return embeds[0];
    }

    private static List<Field> GetFields(JsonElement embed)
        => embed.GetProperty("fields").EnumerateArray()
            .Select(f => new Field(f.GetProperty("name").GetString()!, f.GetProperty("value").GetString()!, f.GetProperty("inline").GetBoolean()))
            .ToList();

    private static List<(int Id, string Filename)> GetAttachments(JsonDocument doc)
        => doc.RootElement.GetProperty("attachments").EnumerateArray()
            .Select(a => (a.GetProperty("id").GetInt32(), a.GetProperty("filename").GetString()!))
            .ToList();

    private static JsonDocument Payload(BugReport report, IReadOnlyList<string>? names = null, bool logsOmitted = false)
        => JsonDocument.Parse(BugReportService.BuildDiscordPayload(report, names ?? Array.Empty<string>(), logsOmitted));

    private static string FieldValue(CapturedMultipartRequest request, string name)
    {
        using var doc = JsonDocument.Parse(request.PayloadJson);
        return GetFields(GetEmbed(doc)).Single(f => f.Name == name).Value;
    }

    private static string NicknameInRequest(CapturedMultipartRequest request) => FieldValue(request, "Jugador");

    /// <summary>Encola sin webhook varios reportes, separados lo justo para no chocar con el anti-spam.</summary>
    private async Task QueueAsync(BugReportService service, params BugReport[] reports)
    {
        foreach (var report in reports)
        {
            Assert.Equal(FeedbackSendOutcome.Queued, await service.SubmitAsync(report, null, null, CancellationToken.None));
            _now += TimeSpan.FromMinutes(2);
        }
    }

    // ───────────────────────────── CategoryDisplayName ─────────────────────────────

    [Theory]
    [InlineData(BugCategory.Crash, "Cierre del juego")]
    [InlineData(BugCategory.Bug, "Fallo")]
    [InlineData(BugCategory.Network, "Red / conexión")]
    [InlineData(BugCategory.Performance, "Rendimiento")]
    [InlineData(BugCategory.Visual, "Gráficos o interfaz")]
    [InlineData(BugCategory.Launcher, "Launcher")]
    [InlineData(BugCategory.Other, "Otro")]
    [InlineData((BugCategory)99, "Otro")]
    public void CategoryDisplayName_IsSpanish(BugCategory category, string expected)
        => Assert.Equal(expected, BugReportService.CategoryDisplayName(category));

    // ───────────────────────────── Redact ─────────────────────────────

    [Theory]
    [InlineData(@"Log en C:\Users\Ana\AppData\LocalLow\PalitoGames\Coop2D\Player.log", @"Log en %USERPROFILE%\AppData\LocalLow\PalitoGames\Coop2D\Player.log")]
    [InlineData("Mono path[0] = 'c:/users/ANA/AppData/Local/x'", "Mono path[0] = '%USERPROFILE%/AppData/Local/x'")]
    [InlineData(@"{""path"":""C:\\Users\\Ana\\Documents""}", @"{""path"":""%USERPROFILE%\\Documents""}")]
    [InlineData(@"Perfil: C:\Users\Ana", "Perfil: %USERPROFILE%")]
    [InlineData(@"Perfil: C:\Users\Ana.", "Perfil: %USERPROFILE%.")]
    public void Redact_ReplacesTheProfilePath_WithEitherSeparator(string text, string expected)
        => Assert.Equal(expected, BugReportService.Redact(text, TestUser, TestProfile, TestMachine));

    [Fact]
    public void Redact_ProfileOfAnotherUserWithTheSamePrefix_IsNotTreatedAsTheProfile()
    {
        const string text = @"Otro perfil: C:\Users\Anabel\Desktop";
        Assert.Equal(text, BugReportService.Redact(text, TestUser, TestProfile, TestMachine));
        // "Ana-2" no es el perfil, pero "Ana" sí aparece como palabra completa.
        Assert.Equal(@"C:\Users\<usuario>-2\x", BugReportService.Redact(@"C:\Users\Ana-2\x", TestUser, TestProfile, TestMachine));
    }

    [Fact]
    public void Redact_TrailingSeparatorInTheProfile_IsIgnored()
        => Assert.Equal(@"%USERPROFILE%\x", BugReportService.Redact(@"C:\Users\Ana\x", null, @"C:\Users\Ana\", null));

    [Fact]
    public void Redact_UserName_OnlyAsAWholeWord_IgnoringCase()
    {
        var result = BugReportService.Redact("Ana entró; ANA salió; Anabel, mañana y _ana_ no cambian; (ana).", TestUser, null, null);
        Assert.Equal("<usuario> entró; <usuario> salió; Anabel, mañana y _ana_ no cambian; (<usuario>).", result);
    }

    [Fact]
    public void Redact_MachineName_AsAWholeWord()
    {
        var result = BugReportService.Redact("Equipo DESKTOP-AB12CD conectado (desktop-ab12cd.local) pero no XDESKTOP-AB12CD", null, null, TestMachine);
        Assert.Equal("Equipo <equipo> conectado (<equipo>.local) pero no XDESKTOP-AB12CD", result);
    }

    [Fact]
    public void Redact_ShortEmptyOrRootValues_AreIgnored()
    {
        const string text = @"Al en C:\Users\Al y en C:\ con PC";
        Assert.Equal(text, BugReportService.Redact(text, "Al", @"C:\", "PC"));
        Assert.Equal(text, BugReportService.Redact(text, null, null, null));
        Assert.Equal(text, BugReportService.Redact(text, "", "   ", ""));
        Assert.Equal(text, BugReportService.Redact(text, "  Al  ", "C:", " PC "));
    }

    [Fact]
    public void Redact_IsASinglePass_ReplacementsAreNotProcessedAgain()
    {
        // Usuario "equipo" y equipo "usuario": lo sustituido no se vuelve a sustituir.
        var result = BugReportService.Redact(@"equipo usuario C:\Users\equipo\x", "equipo", @"C:\Users\equipo", "usuario");
        Assert.Equal(@"<usuario> <equipo> %USERPROFILE%\x", result);
    }

    [Fact]
    public void Redact_TreatsNamesLiterally()
    {
        var result = BugReportService.Redact("a.b+c y axb+c y a.b+c(1)", "a.b+c", null, null);
        Assert.Equal("<usuario> y axb+c y <usuario>(1)", result);
    }

    [Fact]
    public void Redact_NullOrEmptyText_ReturnsEmpty()
    {
        Assert.Equal("", BugReportService.Redact(null!, TestUser, TestProfile, TestMachine));
        Assert.Equal("", BugReportService.Redact("", TestUser, TestProfile, TestMachine));
    }

    [Fact]
    public void Redact_LargeText_IsFast()
    {
        var text = string.Concat(Enumerable.Repeat(PlayerLogText, 40_000)); // ~4 MB
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = BugReportService.Redact(text, TestUser, TestProfile, TestMachine);
        watch.Stop();

        Assert.Equal(string.Concat(Enumerable.Repeat(PlayerLogRedacted, 40_000)), result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Tardó {watch.Elapsed}");
    }

    // ───────────────────────────── Mensaje de Discord ─────────────────────────────

    [Fact]
    public void BuildDiscordPayload_FollowsTheSpecifiedFormat()
    {
        var names = new[] { "Player.log", "launcher.log", "summary.txt" };
        using var doc = Payload(CreateReport(), names);
        var root = doc.RootElement;

        Assert.Equal("Coop2D · Reportes", root.GetProperty("username").GetString());
        var parse = root.GetProperty("allowed_mentions").GetProperty("parse");
        Assert.Equal(JsonValueKind.Array, parse.ValueKind);
        Assert.Equal(0, parse.GetArrayLength());

        var embed = GetEmbed(doc);
        Assert.Equal("🐞 Fallo · Coop2D 0.0.3 · Prototipo 2", embed.GetProperty("title").GetString());
        Assert.Equal("El cofre del nivel 2 no se abre al pulsar E.", embed.GetProperty("description").GetString());
        Assert.Equal(0x5AA9FF, embed.GetProperty("color").GetInt32());
        Assert.Equal("Launcher 1.0.5 · Windows 10.0.26200", embed.GetProperty("footer").GetProperty("text").GetString());
        Assert.Equal("2026-10-19T17:30:15.000Z", embed.GetProperty("timestamp").GetString());
        Assert.Equal(new[]
        {
            new Field("Cómo reproducirlo", "1. Entrar al nivel 2\n2. Ir al cofre\n3. Pulsar E", false),
            new Field("Jugador", "Marta", true),
            new Field("Registros", "Player.log · launcher.log · summary.txt", false),
        }, GetFields(embed));

        Assert.Equal(new[] { (0, "Player.log"), (1, "launcher.log"), (2, "summary.txt") }, GetAttachments(doc));
    }

    [Theory]
    [InlineData(BugCategory.Crash, 0xFF5C6C)]
    [InlineData(BugCategory.Network, 0xFFB627)]
    [InlineData(BugCategory.Bug, 0x5AA9FF)]
    [InlineData(BugCategory.Performance, 0x5AA9FF)]
    [InlineData(BugCategory.Visual, 0x5AA9FF)]
    [InlineData(BugCategory.Launcher, 0x5AA9FF)]
    [InlineData(BugCategory.Other, 0x5AA9FF)]
    [InlineData((BugCategory)42, 0x5AA9FF)]
    public void BuildDiscordPayload_ColorDependsOnTheCategory(BugCategory category, int color)
    {
        using var doc = Payload(CreateReport(category));
        var embed = GetEmbed(doc);
        Assert.Equal(color, embed.GetProperty("color").GetInt32());
        Assert.StartsWith($"🐞 {BugReportService.CategoryDisplayName(category)} · Coop2D", embed.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(3, "código 3")]
    [InlineData(0, "código 0")]
    [InlineData(-1, "código -1 (no se pudo leer)")]
    [InlineData(-1073741819, "código -1073741819 (0xC0000005)")]
    public void BuildDiscordPayload_ExitCode_GoesToThePartidaField(int exitCode, string expected)
    {
        using var doc = Payload(CreateReport(BugCategory.Crash, exitCode: exitCode));
        Assert.Equal(new Field("Partida", expected, true), GetFields(GetEmbed(doc)).Single(f => f.Name == "Partida"));
    }

    [Fact]
    public void BuildDiscordPayload_WithoutExitCodeStepsOrNickname()
    {
        var report = CreateReport(nickname: "   ");
        report.Steps = " \n ";
        using var doc = Payload(report);

        Assert.Equal(new[]
        {
            new Field("Jugador", "Anónimo", true),
            new Field("Registros", "No se adjuntaron registros.", false),
        }, GetFields(GetEmbed(doc)));
        Assert.Equal(0, doc.RootElement.GetProperty("attachments").GetArrayLength());
    }

    [Fact]
    public void BuildDiscordPayload_Title_WithUnknownOrPlainVersion()
    {
        var report = CreateReport(BugCategory.Crash);
        report.GameVersionDisplay = null;
        report.GameVersion = null;
        using (var doc = Payload(report))
            Assert.Equal("🐞 Cierre del juego · Coop2D · versión desconocida", GetEmbed(doc).GetProperty("title").GetString());

        report.GameVersion = "0.0.2";
        using (var doc = Payload(report))
            Assert.Equal("🐞 Cierre del juego · Coop2D 0.0.2", GetEmbed(doc).GetProperty("title").GetString());
    }

    [Fact]
    public void BuildDiscordPayload_LogsOmitted_SaysSoInTheEmbed()
    {
        using var doc = Payload(CreateReport(), new[] { "summary.txt" }, logsOmitted: true);
        var registros = GetFields(GetEmbed(doc)).Single(f => f.Name == "Registros").Value;
        Assert.Contains("demasiado grandes", registros);
        Assert.Contains("summary.txt", registros);
        Assert.Equal(new[] { (0, "summary.txt") }, GetAttachments(doc));
    }

    [Fact]
    public void BuildDiscordPayload_RespectsDiscordLimits_WithHugeTexts()
    {
        var report = CreateReport(description: new string('d', 10_000), nickname: new string('n', 500));
        report.Steps = new string('s', 5000);
        report.GameVersionDisplay = new string('v', 1000);
        report.LauncherVersion = new string('l', 1000);
        report.OsDescription = new string('o', 1000);
        using var doc = Payload(report, new[] { "Player.log", "summary.txt" });
        var embed = GetEmbed(doc);

        var title = embed.GetProperty("title").GetString()!;
        var description = embed.GetProperty("description").GetString()!;
        var footer = embed.GetProperty("footer").GetProperty("text").GetString()!;
        var fields = GetFields(embed);
        Assert.True(title.Length <= 256);
        Assert.True(description.Length <= 4096);
        Assert.EndsWith("…", description);
        Assert.True(footer.Length <= 256);
        Assert.All(fields, f => Assert.True(f.Name.Length <= 256 && f.Value.Length <= 1024));
        Assert.True(title.Length + description.Length + footer.Length + fields.Sum(f => f.Name.Length + f.Value.Length) <= 6000);
    }

    [Fact]
    public void BuildDiscordPayload_KeepsMentionsAsTextButDisablesThem()
    {
        using var doc = Payload(CreateReport(description: "@everyone el juego peta <@123456789>"));
        Assert.Equal("@everyone el juego peta <@123456789>", GetEmbed(doc).GetProperty("description").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
    }

    [Fact]
    public void BuildDiscordPayload_ImplausibleDate_OmitsTimestamp()
    {
        using var doc = Payload(CreateReport(createdAt: DateTimeOffset.MinValue));
        Assert.False(GetEmbed(doc).TryGetProperty("timestamp", out _));
    }

    // ───────────────────────────── SubmitAsync: envío ─────────────────────────────

    [Fact]
    public async Task SubmitAsync_WithSources_PostsMultipartWithAnonymizedLogs_AndClearsTheOutbox()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        var report = CreateReport();

        var outcome = await service.SubmitAsync(report, CreateSources(), Webhook, CancellationToken.None);

        Assert.Equal(FeedbackSendOutcome.Sent, outcome);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(PostUrl, request.Uri.ToString());
        Assert.StartsWith("multipart/form-data", request.ContentType);
        Assert.Contains("PalitoLauncher", request.Headers["User-Agent"]);

        // payload_json primero, luego files[0..3] en orden.
        Assert.Equal(new[] { "payload_json", "files[0]", "files[1]", "files[2]", "files[3]" }, request.Parts.Select(p => p.Name));
        Assert.StartsWith("application/json", request.Part("payload_json")!.ContentType);
        Assert.Equal(new[] { "Player.log", "Player-prev.log", "launcher.log", "summary.txt" }, request.FileNames);
        Assert.All(request.Files, f => Assert.StartsWith("text/plain", f.ContentType));

        using (var doc = JsonDocument.Parse(request.PayloadJson))
        {
            Assert.Equal(new[] { (0, "Player.log"), (1, "Player-prev.log"), (2, "launcher.log"), (3, "summary.txt") }, GetAttachments(doc));
            Assert.Equal("🐞 Fallo · Coop2D 0.0.3 · Prototipo 2", GetEmbed(doc).GetProperty("title").GetString());
        }

        Assert.Equal(PlayerLogRedacted, request.File("Player.log").Text);
        Assert.Equal("Sesión anterior de <usuario> en <equipo>\n", request.File("Player-prev.log").Text);
        Assert.Equal(@"[INFO ] Instalado en %USERPROFILE%\AppData\Local\PalitoGames\Coop2D" + "\n", request.File("launcher.log").Text);
        Assert.All(request.Files, f => Assert.False(f.Body.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "sin BOM"));

        Assert.Empty(OutboxFolders());
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public async Task SubmitAsync_Summary_HasTheReportDataAndAnonymizedLauncherState()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        var report = CreateReport(BugCategory.Crash, exitCode: -1073741819);

        await service.SubmitAsync(report, CreateSources(), Webhook, CancellationToken.None);

        var summary = Assert.Single(handler.Requests).File("summary.txt").Text;
        Assert.Contains($"Id: {report.Id}", summary);
        Assert.Contains("Fecha (UTC): 2026-10-19 17:30:15", summary);
        Assert.Contains("Categoría: Cierre del juego", summary);
        Assert.Contains("Versión del juego: 0.0.3 · Prototipo 2 (0.0.3-pre.2)", summary);
        Assert.Contains("Launcher: 1.0.5", summary);
        Assert.Contains("Sistema: Microsoft Windows 10.0.26200", summary);
        Assert.Contains("Cierre del juego: código -1073741819 (0xC0000005)", summary);
        Assert.Contains("Jugador: Marta", summary);
        Assert.Contains("Qué ha pasado:\nEl cofre del nivel 2 no se abre al pulsar E.", summary);
        Assert.Contains("Cómo reproducirlo:\n1. Entrar al nivel 2\n2. Ir al cofre\n3. Pulsar E", summary);
        Assert.Contains("- Estado: Listo para jugar", summary);
        Assert.Contains(@"- Carpeta del juego: %USERPROFILE%\AppData\Local\PalitoGames\Coop2D", summary);
        Assert.Contains("- Espacio libre: 120 GB", summary);
        Assert.Contains("- Vacío: —", summary);
        Assert.Contains("- Player.log: ", summary);
        Assert.DoesNotContain(TestProfile, summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestMachine, summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitAsync_WithoutSources_PostsOnlyThePayload()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        var report = CreateReport();
        report.AttachmentNames = new List<string> { "inventado.txt" }; // lo que traiga el llamador no cuenta

        Assert.Equal(FeedbackSendOutcome.Sent, await service.SubmitAsync(report, null, Webhook, CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new[] { "payload_json" }, request.Parts.Select(p => p.Name));
        Assert.Equal("No se adjuntaron registros.", FieldValue(request, "Registros"));
        using var doc = JsonDocument.Parse(request.PayloadJson);
        Assert.Empty(GetAttachments(doc));
        Assert.Empty(report.AttachmentNames);
    }

    [Fact]
    public async Task SubmitAsync_AddsUserAgentWhenTheClientHasNone()
    {
        var handler = OkHandler();
        var service = new BugReportService(new HttpClient(handler), OutboxDir) { UtcNow = () => _now };

        await service.SubmitAsync(CreateReport(), null, Webhook, CancellationToken.None);

        Assert.Contains("PalitoLauncher", Assert.Single(handler.Requests).Headers["User-Agent"]);
    }

    [Fact]
    public async Task SubmitAsync_ThreadWebhook_KeepsThreadIdAndAddsWait()
    {
        var handler = OkHandler();
        var service = CreateService(handler);

        await service.SubmitAsync(CreateReport(), null, Webhook + "?thread_id=987654321", CancellationToken.None);

        Assert.Equal(Webhook + "?thread_id=987654321&wait=true", Assert.Single(handler.Requests).Uri.ToString());
    }

    // ───────────────────────────── SubmitAsync: bandeja ─────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://discord.com/api/webhooks/PEGA_AQUI_LA_URL_DE_TU_WEBHOOK")]
    [InlineData("https://evil.example/api/webhooks/1/token")]
    [InlineData("http://discord.com/api/webhooks/1/token")]
    public async Task SubmitAsync_WithoutValidWebhook_QueuesTheFolderWithoutNetwork(string? webhook)
    {
        var service = CreateService(NoNetworkHandler());
        var report = CreateReport();

        var outcome = await service.SubmitAsync(report, CreateSources(), webhook, CancellationToken.None);

        Assert.Equal(FeedbackSendOutcome.Queued, outcome);
        Assert.Equal(1, service.PendingCount);
        var folder = Assert.Single(OutboxFolders());
        Assert.Equal($"20261019173015-{report.Id}", Path.GetFileName(folder));
        Assert.Equal(new[] { "Player-prev.log", "Player.log", "launcher.log", "report.json", "summary.txt" },
            Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(PlayerLogRedacted, File.ReadAllText(Path.Combine(folder, "Player.log")));
        // No quedan carpetas temporales.
        Assert.Equal(new[] { folder }, Directory.GetDirectories(OutboxDir));
    }

    [Fact]
    public async Task SubmitAsync_ReportJson_RoundTripsTheReport()
    {
        var service = CreateService(NoNetworkHandler());
        var report = CreateReport(BugCategory.Network, exitCode: 7);

        await service.SubmitAsync(report, CreateSources(), null, CancellationToken.None);

        var json = File.ReadAllText(Path.Combine(Assert.Single(OutboxFolders()), "report.json"));
        Assert.Contains("\"category\": \"network\"", json);
        var back = JsonSerializer.Deserialize<BugReport>(json, BugReportService.OutboxJsonOptions)!;
        Assert.Equal(report.Id, back.Id);
        Assert.Equal(BugCategory.Network, back.Category);
        Assert.Equal(report.Description, back.Description);
        Assert.Equal(report.Steps, back.Steps);
        Assert.Equal(7, back.ExitCode);
        Assert.Equal(report.CreatedAtUtc, back.CreatedAtUtc);
        Assert.Equal(new[] { "Player.log", "Player-prev.log", "launcher.log", "summary.txt" }, back.AttachmentNames);
        Assert.Equal(back.AttachmentNames, report.AttachmentNames);
    }

    [Fact]
    public async Task SubmitAsync_TextWithLoneSurrogates_IsSavedAndSent()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        var report = CreateReport(nickname: "Ana\uDC00", description: "Pegado raro \uD800 aquí 😀 con tildes: ñandú");
        report.GameVersionDisplay = "0.0.3 \uD800";
        report.OsDescription = "Windows \uDC00";
        report.Steps = "Paso \uD800 uno";

        Assert.Equal(FeedbackSendOutcome.Queued, await service.SubmitAsync(report, CreateSources(), null, CancellationToken.None));
        var json = File.ReadAllText(Path.Combine(Assert.Single(OutboxFolders()), "report.json"));
        Assert.Contains("ñandú", json); // legible a mano
        Assert.Equal(1, await service.FlushOutboxAsync(Webhook, CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        using var doc = JsonDocument.Parse(request.PayloadJson);
        var description = GetEmbed(doc).GetProperty("description").GetString()!;
        Assert.StartsWith("Pegado raro ", description);
        Assert.EndsWith(" aquí 😀 con tildes: ñandú", description);
        Assert.DoesNotContain('\uD800', description);
        Assert.DoesNotContain('\uD800', request.File("summary.txt").Text);
    }

    [Fact]
    public async Task SubmitAsync_MissingOrUnreadableLogs_AreNotedInTheSummary()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        var sources = new BugReportSources
        {
            PlayerLog = _temp.WriteFile("Player.log", "solo este\n"),
            PlayerPrevLog = null,
            LauncherLog = _temp.Combine("no-existe.log"),
        };

        await service.SubmitAsync(CreateReport(), sources, Webhook, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new[] { "Player.log", "summary.txt" }, request.FileNames);
        var summary = request.File("summary.txt").Text;
        Assert.Contains("- Player.log: 10 B", summary);
        Assert.Contains("- Player-prev.log: no se encontró", summary);
        Assert.Contains("- launcher.log: no se encontró", summary);
        Assert.Contains("Estado del launcher:\n- —", summary);
    }

    [Fact]
    public async Task SubmitAsync_EmptyLog_IsNotAttached()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        var sources = new BugReportSources { PlayerLog = _temp.WriteFile("Player.log", "") };

        await service.SubmitAsync(CreateReport(), sources, Webhook, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new[] { "summary.txt" }, request.FileNames);
        Assert.Contains("- Player.log: está vacío", request.File("summary.txt").Text);
    }

    [Fact]
    public async Task SubmitAsync_LogBeingWrittenByAnotherProcess_IsStillRead()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        var path = _temp.WriteFile("Player.log", "primera línea\n");
        // Como el juego o Log: abierto para escribir compartiendo lectura/escritura/borrado.
        using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            writer.Write("segunda línea\n"u8);
            writer.Flush();

            await service.SubmitAsync(CreateReport(), new BugReportSources { PlayerLog = path }, Webhook, CancellationToken.None);
        }

        Assert.Equal("primera línea\nsegunda línea\n", Assert.Single(handler.Requests).File("Player.log").Text);
    }

    [Fact]
    public async Task SubmitAsync_ExclusivelyLockedLog_IsNotedWithAnAnonymizedReason()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        service.RedactUserProfile = _temp.Path; // el mensaje de error lleva la ruta: debe salir anonimizada
        var path = _temp.WriteFile("Player.log", "bloqueado\n");

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(FeedbackSendOutcome.Sent,
                await service.SubmitAsync(CreateReport(), new BugReportSources { PlayerLog = path }, Webhook, CancellationToken.None));
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new[] { "summary.txt" }, request.FileNames);
        var summary = request.File("summary.txt").Text;
        Assert.Contains("- Player.log: no se pudo leer: ", summary);
        Assert.DoesNotContain(_temp.Path, summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitAsync_BigLog_KeepsOnlyTheLast2MB_StartingAtALine()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        var sb = new StringBuilder();
        var lines = 0;
        while (sb.Length < 3 * 1024 * 1024)
            sb.Append("Línea ").Append((++lines).ToString("D7")).Append(" — algo de texto de relleno para el registro\n");
        var path = _temp.WriteFile("Player.log", sb.ToString());

        await service.SubmitAsync(CreateReport(), new BugReportSources { PlayerLog = path }, Webhook, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        var file = request.File("Player.log");
        Assert.True(file.Body.Length <= 2 * 1024 * 1024, $"{file.Body.Length} bytes");
        Assert.True(file.Body.Length > 2 * 1024 * 1024 - 200, "debe aprovechar casi todo el límite");
        var text = file.Text;
        Assert.Matches(@"^Línea \d{7} — algo", text);
        Assert.EndsWith($"Línea {lines:D7} — algo de texto de relleno para el registro\n", text);
        Assert.DoesNotContain('\uFFFD', text);
        Assert.Contains("en total; se adjuntan los últimos", request.File("summary.txt").Text);
    }

    [Fact]
    public async Task SubmitAsync_TailWithoutNewlines_DoesNotSplitACharacter()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        service.MaxLogTailBytes = 1001; // 'ñ' ocupa 2 bytes: el corte cae en medio de un carácter
        var path = _temp.WriteFile("Player.log", new string('ñ', 5000));

        await service.SubmitAsync(CreateReport(), new BugReportSources { PlayerLog = path }, Webhook, CancellationToken.None);

        var text = Assert.Single(handler.Requests).File("Player.log").Text;
        Assert.Equal(new string('ñ', 500), text);
    }

    [Fact]
    public async Task SubmitAsync_TotalLimit_TrimsTheBiggestLogsAndKeepsTheSmallOnes()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        service.MaxLogTailBytes = 10_000;
        service.MaxTotalAttachmentBytes = 15_000;
        string Lines(string prefix, int count) => string.Concat(Enumerable.Range(1, count).Select(i => $"{prefix} {i:D4} xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n"));
        var small = Lines("prev", 40); // ~1.8 KB
        var sources = new BugReportSources
        {
            PlayerLog = _temp.WriteFile("Player.log", Lines("player", 200)), // ~9 KB
            PlayerPrevLog = _temp.WriteFile("Player-prev.log", small),
            LauncherLog = _temp.WriteFile("launcher.log", Lines("launcher", 400)), // ~19 KB → 10 KB por el tope de cada log
        };

        await service.SubmitAsync(CreateReport(), sources, Webhook, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new[] { "Player.log", "Player-prev.log", "launcher.log", "summary.txt" }, request.FileNames);
        Assert.True(request.Files.Sum(f => f.Body.Length) <= 15_000, $"{request.Files.Sum(f => f.Body.Length)} bytes");
        Assert.Equal(small, request.File("Player-prev.log").Text);
        Assert.StartsWith("player ", request.File("Player.log").Text);
        Assert.EndsWith("player 0200 xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n", request.File("Player.log").Text);
        Assert.StartsWith("launcher ", request.File("launcher.log").Text);
        Assert.EndsWith("launcher 0400 xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n", request.File("launcher.log").Text);
        Assert.Contains("recortado para no pasar del límite de tamaño", request.File("summary.txt").Text);
    }

    // ───────────────────────────── SubmitAsync: validación y anti-spam ─────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("corto")]
    [InlineData("   no va        ")]
    [InlineData("123456789")]
    public async Task SubmitAsync_ShortDescription_Throws_WithoutSavingOrSending(string description)
    {
        var handler = OkHandler();
        var service = CreateService(handler);

        var ex = await Assert.ThrowsAsync<LauncherException>(() =>
            service.SubmitAsync(CreateReport(description: description), CreateSources(), Webhook, CancellationToken.None));

        Assert.Equal("Cuéntanos un poco más qué ha pasado.", ex.Message);
        Assert.Empty(handler.Requests);
        Assert.Empty(OutboxFolders());

        // No gasta el anti-spam.
        Assert.Equal(FeedbackSendOutcome.Sent, await service.SubmitAsync(CreateReport(), null, Webhook, CancellationToken.None));
    }

    [Fact]
    public async Task SubmitAsync_TenCharacters_IsEnough_AndLongTextsAreCapped()
    {
        var service = CreateService(NoNetworkHandler());
        service.MinIntervalBetweenReports = TimeSpan.Zero;

        Assert.Equal(FeedbackSendOutcome.Queued, await service.SubmitAsync(CreateReport(description: "  1234567890  "), null, null, CancellationToken.None));

        var report = CreateReport(description: new string('a', 3000));
        report.Steps = new string('b', 1500);
        report.Nickname = new string('c', 300);
        await service.SubmitAsync(report, null, null, CancellationToken.None);

        Assert.Equal(2000, report.Description.Length);
        Assert.Equal(1000, report.Steps!.Length);
        Assert.Equal(100, report.Nickname!.Length);
        Assert.Equal(2, service.PendingCount);
    }

    [Fact]
    public async Task SubmitAsync_AntiSpam_BlocksASecondReportWithinTheInterval()
    {
        var handler = OkHandler();
        var service = CreateService(handler);

        Assert.Equal(FeedbackSendOutcome.Sent, await service.SubmitAsync(CreateReport(), null, Webhook, CancellationToken.None));

        _now += TimeSpan.FromSeconds(30);
        var ex = await Assert.ThrowsAsync<LauncherException>(() => service.SubmitAsync(CreateReport(), null, Webhook, CancellationToken.None));
        Assert.Equal("Espera un momento antes de enviar otro reporte.", ex.Message);
        Assert.Single(handler.Requests);
        Assert.Empty(OutboxFolders()); // el rechazado no se guarda (el diálogo conserva lo escrito)

        _now += TimeSpan.FromSeconds(31);
        Assert.Equal(FeedbackSendOutcome.Sent, await service.SubmitAsync(CreateReport(), null, Webhook, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SubmitAsync_AntiSpam_IntervalIsConfigurable_AndClockGoingBackDoesNotBlock()
    {
        var service = CreateService(NoNetworkHandler());
        Assert.Equal(TimeSpan.FromSeconds(60), service.MinIntervalBetweenReports);
        service.MinIntervalBetweenReports = TimeSpan.Zero;

        await service.SubmitAsync(CreateReport(), null, null, CancellationToken.None);
        await service.SubmitAsync(CreateReport(), null, null, CancellationToken.None);

        service.MinIntervalBetweenReports = TimeSpan.FromSeconds(60);
        _now -= TimeSpan.FromHours(1); // la hora de Windows retrocede
        await service.SubmitAsync(CreateReport(), null, null, CancellationToken.None);

        Assert.Equal(3, service.PendingCount);
    }

    [Fact]
    public async Task SubmitAsync_RetryingAQueuedReport_ReplacesItsFolder()
    {
        var service = CreateService(new MultipartCapturingHandler(_ => throw new HttpRequestException("sin red")));
        var report = CreateReport();

        Assert.Equal(FeedbackSendOutcome.Queued, await service.SubmitAsync(report, CreateSources(), Webhook, CancellationToken.None));
        report.Description = "Ahora con más detalle: el cofre no se abre nunca.";
        // Reintentar el mismo reporte enseguida no cuenta como spam; esta vez sin registros.
        Assert.Equal(FeedbackSendOutcome.Queued, await service.SubmitAsync(report, null, Webhook, CancellationToken.None));

        var folder = Assert.Single(OutboxFolders());
        Assert.Equal(new[] { "report.json" }, Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.Contains("Ahora con más detalle", File.ReadAllText(Path.Combine(folder, "report.json")));
        Assert.Equal(1, service.PendingCount);
    }

    [Fact]
    public async Task SubmitAsync_SameReportAgainAfterSending_IsNotSentTwice()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        var report = CreateReport();

        Assert.Equal(FeedbackSendOutcome.Sent, await service.SubmitAsync(report, CreateSources(), Webhook, CancellationToken.None));
        Assert.Equal(FeedbackSendOutcome.Sent, await service.SubmitAsync(report, CreateSources(), Webhook, CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Empty(OutboxFolders());
    }

    [Fact]
    public async Task SubmitAsync_UnsafeId_IsReplacedBeforeNamingTheFolder()
    {
        var service = CreateService(NoNetworkHandler());
        var report = CreateReport();
        report.Id = @"..\..\evil";

        await service.SubmitAsync(report, null, null, CancellationToken.None);

        Assert.Matches("^[0-9a-f]{32}$", report.Id);
        var folder = Assert.Single(OutboxFolders());
        Assert.Equal(Path.GetFullPath(OutboxDir), Path.GetDirectoryName(folder));
        Assert.EndsWith($"-{report.Id}", folder);
    }

    [Fact]
    public async Task SubmitAsync_DiskError_ThrowsClearMessage_AndDoesNotStartTheAntiSpamWindow()
    {
        var blocked = _temp.WriteFile("bloqueado", "soy un archivo, no una carpeta");
        var handler = OkHandler();
        var service = CreateService(handler, blocked);

        var ex = await Assert.ThrowsAsync<LauncherException>(() => service.SubmitAsync(CreateReport(), CreateSources(), Webhook, CancellationToken.None));
        Assert.Contains("No se pudo guardar el reporte", ex.Message);
        Assert.Empty(handler.Requests);

        File.Delete(blocked);
        Assert.Equal(FeedbackSendOutcome.Sent, await service.SubmitAsync(CreateReport(), CreateSources(), Webhook, CancellationToken.None));
    }

    [Fact]
    public async Task SubmitAsync_Null_Throws()
    {
        var service = CreateService(NoNetworkHandler());
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SubmitAsync(null!, null, Webhook, CancellationToken.None));
    }

    // ───────────────────────────── SubmitAsync: respuestas de Discord ─────────────────────────────

    [Fact]
    public async Task SubmitAsync_413_RetriesOnceWithOnlyTheSummary_AndSaysSo()
    {
        var calls = 0;
        var handler = new MultipartCapturingHandler(_ => Interlocked.Increment(ref calls) == 1
            ? MultipartCapturingHandler.TooLarge()
            : MultipartCapturingHandler.DiscordOk());
        var service = CreateService(handler);

        var outcome = await service.SubmitAsync(CreateReport(), CreateSources(), Webhook, CancellationToken.None);

        Assert.Equal(FeedbackSendOutcome.Sent, outcome);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(new[] { "Player.log", "Player-prev.log", "launcher.log", "summary.txt" }, handler.Requests[0].FileNames);
        var retry = handler.Requests[1];
        Assert.Equal(new[] { "summary.txt" }, retry.FileNames);
        Assert.Contains("demasiado grandes", FieldValue(retry, "Registros"));
        using (var doc = JsonDocument.Parse(retry.PayloadJson))
            Assert.Equal(new[] { (0, "summary.txt") }, GetAttachments(doc));
        Assert.Empty(OutboxFolders());
        Assert.Empty(FailedFolders(service));
    }

    [Fact]
    public async Task SubmitAsync_413Twice_MovesToFailed()
    {
        var handler = new MultipartCapturingHandler(_ => MultipartCapturingHandler.TooLarge());
        var service = CreateService(handler);
        var report = CreateReport();

        var outcome = await service.SubmitAsync(report, CreateSources(), Webhook, CancellationToken.None);

        Assert.Equal(FeedbackSendOutcome.Queued, outcome);
        Assert.Equal(2, handler.Requests.Count); // solo UN reintento
        Assert.Equal(0, service.PendingCount);
        var failed = Assert.Single(FailedFolders(service));
        Assert.EndsWith($"-{report.Id}", failed);
        Assert.True(File.Exists(Path.Combine(failed, "report.json")));
        Assert.True(File.Exists(Path.Combine(failed, "Player.log")));
    }

    [Fact]
    public async Task SubmitAsync_413WithoutLogs_DoesNotRetry()
    {
        var handler = new MultipartCapturingHandler(_ => MultipartCapturingHandler.TooLarge());
        var service = CreateService(handler);

        Assert.Equal(FeedbackSendOutcome.Queued, await service.SubmitAsync(CreateReport(), null, Webhook, CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Single(FailedFolders(service));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task SubmitAsync_PayloadRejected_MovesToFailed(HttpStatusCode status)
    {
        var handler = new MultipartCapturingHandler(_ => MultipartCapturingHandler.Json(status, """{"embeds":["0"],"message":"Invalid Form Body"}"""));
        var service = CreateService(handler);
        var report = CreateReport();

        var outcome = await service.SubmitAsync(report, CreateSources(), Webhook, CancellationToken.None);

        Assert.Equal(FeedbackSendOutcome.Queued, outcome);
        Assert.Single(handler.Requests); // un 400 no se reintenta sin logs
        Assert.Equal(0, service.PendingCount);
        Assert.Empty(OutboxFolders());
        Assert.EndsWith($"-{report.Id}", Assert.Single(FailedFolders(service)));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task SubmitAsync_ServerOrWebhookErrors_StayInTheOutbox(HttpStatusCode status)
    {
        var handler = new MultipartCapturingHandler(_ => MultipartCapturingHandler.Json(status, """{"message":"Unknown Webhook","code":10015}"""));
        var service = CreateService(handler);

        var outcome = await service.SubmitAsync(CreateReport(), CreateSources(), Webhook, CancellationToken.None);

        Assert.Equal(FeedbackSendOutcome.Queued, outcome);
        Assert.Single(handler.Requests);
        Assert.Equal(1, service.PendingCount);
        Assert.Empty(FailedFolders(service));
    }

    [Fact]
    public async Task SubmitAsync_NetworkError_Queues()
    {
        var service = CreateService(new MultipartCapturingHandler(_ => throw new HttpRequestException("sin red")));

        var outcome = await service.SubmitAsync(CreateReport(), CreateSources(), Webhook, CancellationToken.None);

        Assert.Equal(FeedbackSendOutcome.Queued, outcome);
        Assert.Equal(1, service.PendingCount);
    }

    [Fact]
    public async Task SubmitAsync_RateLimited_RespectsRetryAfterBeforeTryingAgain()
    {
        var calls = 0;
        var handler = new MultipartCapturingHandler(_ => Interlocked.Increment(ref calls) == 1
            ? MultipartCapturingHandler.TooManyRequests(120)
            : MultipartCapturingHandler.DiscordOk());
        var service = CreateService(handler);

        Assert.Equal(FeedbackSendOutcome.Queued, await service.SubmitAsync(CreateReport(), CreateSources(), Webhook, CancellationToken.None));
        Assert.Equal(1, service.PendingCount);

        _now += TimeSpan.FromSeconds(60);
        Assert.Equal(0, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
        Assert.Single(handler.Requests);

        // Un reporte nuevo tampoco llama a Discord mientras dure la espera.
        var later = CreateReport(createdAt: new DateTimeOffset(2026, 10, 19, 17, 31, 0, TimeSpan.Zero));
        Assert.Equal(FeedbackSendOutcome.Queued, await service.SubmitAsync(later, null, Webhook, CancellationToken.None));
        Assert.Single(handler.Requests);

        _now += TimeSpan.FromSeconds(61);
        Assert.Equal(2, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(0, service.PendingCount);
        // El reintento lleva los mismos adjuntos.
        Assert.Equal(PlayerLogRedacted, handler.Requests[1].File("Player.log").Text);
    }

    [Fact]
    public async Task SubmitAsync_RateLimited_WithOnlyRetryAfterHeader()
    {
        var calls = 0;
        var handler = new MultipartCapturingHandler(_ =>
        {
            if (Interlocked.Increment(ref calls) > 1) return MultipartCapturingHandler.DiscordOk();
            var response = MultipartCapturingHandler.Status((HttpStatusCode)429, "<html>Cloudflare</html>");
            response.Headers.TryAddWithoutValidation("Retry-After", "30");
            return response;
        });
        var service = CreateService(handler);

        await service.SubmitAsync(CreateReport(), null, Webhook, CancellationToken.None);
        _now += TimeSpan.FromSeconds(20);
        Assert.Equal(0, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
        _now += TimeSpan.FromSeconds(11);
        Assert.Equal(1, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
    }

    [Fact]
    public async Task SubmitAsync_CanceledWhileSending_ThrowsAndKeepsTheFolder()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new MultipartCapturingHandler(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return MultipartCapturingHandler.DiscordOk();
        });
        var service = CreateService(handler);
        using var cts = new CancellationTokenSource();

        var task = service.SubmitAsync(CreateReport(), CreateSources(), Webhook, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, service.PendingCount);
        Assert.True(File.Exists(Path.Combine(Assert.Single(OutboxFolders()), "Player.log")));
    }

    [Fact]
    public async Task SubmitAsync_AlreadyCanceled_StillSavesToTheOutbox()
    {
        var service = CreateService(NoNetworkHandler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SubmitAsync(CreateReport(), CreateSources(), Webhook, cts.Token));
        Assert.Equal(1, service.PendingCount);
    }

    [Fact]
    public async Task SubmitAsync_Timeout_QueuesInsteadOfThrowing()
    {
        var handler = new MultipartCapturingHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return MultipartCapturingHandler.DiscordOk();
        });
        var service = CreateService(handler);
        service.RequestTimeout = TimeSpan.FromMilliseconds(200);

        var outcome = await service.SubmitAsync(CreateReport(), null, Webhook, CancellationToken.None);

        Assert.Equal(FeedbackSendOutcome.Queued, outcome);
        Assert.Equal(1, service.PendingCount);
    }

    // ───────────────────────────── FlushOutboxAsync ─────────────────────────────

    private async Task<BugReportService> CreateServiceWithThreeQueuedAsync(MultipartCapturingHandler handler)
    {
        var service = CreateService(handler);
        // Encolados en desorden: se envían por fecha de creación.
        await QueueAsync(service,
            CreateReport(nickname: "C", createdAt: new DateTimeOffset(2026, 10, 19, 10, 0, 3, TimeSpan.Zero)),
            CreateReport(nickname: "A", createdAt: new DateTimeOffset(2026, 10, 19, 10, 0, 1, TimeSpan.Zero)),
            CreateReport(nickname: "B", createdAt: new DateTimeOffset(2026, 10, 19, 10, 0, 2, TimeSpan.Zero)));
        Assert.Equal(3, service.PendingCount);
        Assert.Empty(handler.Requests);
        return service;
    }

    [Fact]
    public async Task FlushOutboxAsync_SendsOldestFirst()
    {
        var handler = OkHandler();
        var service = await CreateServiceWithThreeQueuedAsync(handler);

        var sent = await service.FlushOutboxAsync(Webhook, CancellationToken.None);

        Assert.Equal(3, sent);
        Assert.Equal(new[] { "A", "B", "C" }, handler.Requests.Select(NicknameInRequest));
        Assert.All(handler.Requests, r => Assert.Equal(PostUrl, r.Uri.ToString()));
        Assert.Equal(0, service.PendingCount);
        Assert.Empty(OutboxFolders());
    }

    [Fact]
    public async Task FlushOutboxAsync_DefaultsToFiveItems_AndRespectsMaxItems()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        await QueueAsync(service, Enumerable.Range(1, 7).Select(i =>
            CreateReport(nickname: $"N{i}", createdAt: new DateTimeOffset(2026, 10, 19, 10, 0, i, TimeSpan.Zero))).ToArray());

        Assert.Equal(5, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
        Assert.Equal(2, service.PendingCount);

        Assert.Equal(0, await service.FlushOutboxAsync(Webhook, CancellationToken.None, maxItems: 0));
        Assert.Equal(1, await service.FlushOutboxAsync(Webhook, CancellationToken.None, maxItems: 1));
        Assert.Equal(new[] { "N1", "N2", "N3", "N4", "N5", "N6" }, handler.Requests.Select(NicknameInRequest));
    }

    [Fact]
    public async Task FlushOutboxAsync_AfterARestart_SendsTheSavedAttachments()
    {
        var first = CreateService(NoNetworkHandler());
        var report = CreateReport();
        await first.SubmitAsync(report, CreateSources(), null, CancellationToken.None);

        var handler = OkHandler();
        var second = CreateService(handler); // otro arranque del launcher
        Assert.Equal(1, second.PendingCount);
        Assert.Equal(1, await second.FlushOutboxAsync(Webhook, CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new[] { "Player.log", "Player-prev.log", "launcher.log", "summary.txt" }, request.FileNames);
        Assert.Equal(PlayerLogRedacted, request.File("Player.log").Text);
        Assert.Contains($"Id: {report.Id}", request.File("summary.txt").Text);
        Assert.Equal("Marta", NicknameInRequest(request));
        Assert.Equal(0, second.PendingCount);
    }

    [Fact]
    public async Task FlushOutboxAsync_StopsAtTheFirst429()
    {
        var calls = 0;
        var handler = new MultipartCapturingHandler(_ => Interlocked.Increment(ref calls) == 2
            ? MultipartCapturingHandler.TooManyRequests(5)
            : MultipartCapturingHandler.DiscordOk());
        var service = await CreateServiceWithThreeQueuedAsync(handler);

        Assert.Equal(1, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, service.PendingCount);
    }

    [Fact]
    public async Task FlushOutboxAsync_StopsAtTheFirstNetworkError()
    {
        var handler = new MultipartCapturingHandler(_ => throw new HttpRequestException("sin red"));
        var service = await CreateServiceWithThreeQueuedAsync(handler);

        Assert.Equal(0, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.Equal(3, service.PendingCount);
    }

    [Fact]
    public async Task FlushOutboxAsync_StopsWhenTheWebhookIsGone()
    {
        var handler = new MultipartCapturingHandler(_ => MultipartCapturingHandler.Json(HttpStatusCode.NotFound, """{"message":"Unknown Webhook","code":10015}"""));
        var service = await CreateServiceWithThreeQueuedAsync(handler);

        Assert.Equal(0, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.Equal(3, service.PendingCount);
        Assert.Empty(FailedFolders(service));
    }

    [Fact]
    public async Task FlushOutboxAsync_PayloadRejection_MovesThatOneAndContinues()
    {
        var calls = 0;
        var handler = new MultipartCapturingHandler(_ => Interlocked.Increment(ref calls) == 2
            ? MultipartCapturingHandler.Json(HttpStatusCode.BadRequest, """{"message":"Invalid Form Body"}""")
            : MultipartCapturingHandler.DiscordOk());
        var service = await CreateServiceWithThreeQueuedAsync(handler);

        Assert.Equal(2, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(0, service.PendingCount);
        Assert.Single(FailedFolders(service));
    }

    [Fact]
    public async Task FlushOutboxAsync_CorruptFolders_GoToFailed_AndTheRestIsSent()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        await QueueAsync(service, CreateReport(nickname: "Buena"));
        Directory.CreateDirectory(Path.Combine(OutboxDir, "20200101000000-sinreporte"));
        _temp.WriteFile(@"reports\outbox\20200101000001-corrupto\report.json", "{ esto no es json");
        _temp.WriteFile(@"reports\outbox\20200101000002-nulo\report.json", "null");
        _temp.WriteFile(@"reports\outbox\20200101000003-categoria\report.json", """{ "description": "algo pasó aquí", "category": "volcan" }""");
        _temp.WriteFile(@"reports\outbox\20200101000004-enorme\report.json", "{\"description\":\"" + new string('x', 300_000) + "\"}");
        Assert.Equal(6, service.PendingCount);

        var sent = await service.FlushOutboxAsync(Webhook, CancellationToken.None, maxItems: 10);

        Assert.Equal(1, sent);
        Assert.Equal("Buena", NicknameInRequest(Assert.Single(handler.Requests)));
        Assert.Equal(0, service.PendingCount);
        Assert.Equal(5, FailedFolders(service).Length);
    }

    [Fact]
    public async Task FlushOutboxAsync_HostileReport_DoesNotEscapeItsFolder()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        _temp.WriteFile(@"reports\secret.txt", "contraseña");
        _temp.WriteFile(@"reports\outbox\20260101000000-abc123\Player.log", "log bueno\n");
        _temp.WriteFile(@"reports\outbox\20260101000000-abc123\report.json", """
            {
              "id": "..\\..\\otra-cosa",
              "category": 99,
              "description": "@everyone",
              "attachmentNames": [ "..\\secret.txt", "../secret.txt", "C:\\Windows\\win.ini", "report.json", "Player.log", "player.log", null, "" ],
              "nickname": null,
              "launcherVersion": null,
              "createdAtUtc": "0001-01-01T00:00:00+00:00"
            }
            """);

        Assert.Equal(1, await service.FlushOutboxAsync(Webhook, CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new[] { "Player.log" }, request.FileNames);
        Assert.Equal("log bueno\n", request.File("Player.log").Text);
        using var doc = JsonDocument.Parse(request.PayloadJson);
        var embed = GetEmbed(doc);
        Assert.StartsWith("🐞 Otro · Coop2D", embed.GetProperty("title").GetString());
        Assert.False(embed.TryGetProperty("timestamp", out _));
        Assert.Equal("Anónimo", GetFields(embed).Single(f => f.Name == "Jugador").Value);
        Assert.Empty(OutboxFolders());
        Assert.True(File.Exists(_temp.Combine("reports", "secret.txt")));
    }

    [Fact]
    public async Task FlushOutboxAsync_MissingAttachment_IsSentWithoutIt()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        Assert.Equal(FeedbackSendOutcome.Queued, await service.SubmitAsync(CreateReport(), CreateSources(), null, CancellationToken.None));
        File.Delete(Path.Combine(Assert.Single(OutboxFolders()), "Player-prev.log")); // p.ej. lo borró el usuario o el antivirus

        Assert.Equal(1, await service.FlushOutboxAsync(Webhook, CancellationToken.None));

        Assert.Equal(new[] { "Player.log", "launcher.log", "summary.txt" }, Assert.Single(handler.Requests).FileNames);
        Assert.Empty(OutboxFolders());
    }

    [Fact]
    public async Task FlushOutboxAsync_WithoutValidWebhook_DoesNothing()
    {
        var handler = OkHandler();
        var service = await CreateServiceWithThreeQueuedAsync(handler);

        Assert.Equal(0, await service.FlushOutboxAsync(null, CancellationToken.None));
        Assert.Equal(0, await service.FlushOutboxAsync("https://evil.example/api/webhooks/1/t", CancellationToken.None));

        Assert.Empty(handler.Requests);
        Assert.Equal(3, service.PendingCount);
    }

    [Fact]
    public async Task FlushOutboxAsync_EmptyOrMissingOutbox_ReturnsZero()
    {
        var service = CreateService(NoNetworkHandler());
        Assert.Equal(0, service.PendingCount);
        Assert.Equal(0, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
    }

    [Fact]
    public async Task FlushOutboxAsync_Canceled_Throws()
    {
        var service = await CreateServiceWithThreeQueuedAsync(OkHandler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FlushOutboxAsync(Webhook, cts.Token));
        Assert.Equal(3, service.PendingCount);
    }

    [Fact]
    public async Task FlushOutboxAsync_PreemptiveRateLimitHeader_LongWait_StopsEarly()
    {
        var handler = new MultipartCapturingHandler(_ =>
        {
            var response = MultipartCapturingHandler.DiscordOk();
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset-After", "30.5");
            return response;
        });
        var service = await CreateServiceWithThreeQueuedAsync(handler);

        Assert.Equal(1, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.Equal(2, service.PendingCount);
    }

    [Fact]
    public async Task PendingCount_IgnoresFailedTemporaryAndUnknownFolders()
    {
        var service = CreateService(NoNetworkHandler());
        await QueueAsync(service, CreateReport());
        Directory.CreateDirectory(Path.Combine(OutboxDir, "failed", "20200101000000-viejo"));
        Directory.CreateDirectory(Path.Combine(OutboxDir, ".tmp-abc-12345678"));
        Directory.CreateDirectory(Path.Combine(OutboxDir, "carpeta-cualquiera"));
        _temp.WriteFile(@"reports\outbox\20200101000000-archivo.json", "{}");

        Assert.Equal(1, service.PendingCount);
    }

    [Fact]
    public async Task FlushOutboxAsync_CleansUpOldTemporaryFolders()
    {
        var service = CreateService(OkHandler());
        var old = Path.Combine(OutboxDir, ".tmp-viejo-12345678");
        var recent = Path.Combine(OutboxDir, ".tmp-nuevo-12345678");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(recent);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromDays(2));

        await service.FlushOutboxAsync(Webhook, CancellationToken.None);

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
    }

    // ───────────────────────────── Concurrencia ─────────────────────────────

    [Fact]
    public async Task SubmitAndFlushAtTheSameTime_SendOneAtATime_WithoutDuplicates()
    {
        var current = 0;
        var maxConcurrent = 0;
        var handler = new MultipartCapturingHandler(async (_, ct) =>
        {
            var now = Interlocked.Increment(ref current);
            int seen;
            while ((seen = Volatile.Read(ref maxConcurrent)) < now && Interlocked.CompareExchange(ref maxConcurrent, now, seen) != seen)
            {
            }
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref current);
            return MultipartCapturingHandler.DiscordOk();
        });
        var service = CreateService(handler);
        await QueueAsync(service, CreateReport(nickname: "Q1"), CreateReport(nickname: "Q2"));

        var flush = service.FlushOutboxAsync(Webhook, CancellationToken.None);
        var submit = service.SubmitAsync(CreateReport(nickname: "Nuevo"), CreateSources(), Webhook, CancellationToken.None);
        await Task.WhenAll(flush, submit);

        Assert.Equal(FeedbackSendOutcome.Sent, await submit);
        Assert.Equal(1, maxConcurrent);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(new[] { "Nuevo", "Q1", "Q2" }, handler.Requests.Select(NicknameInRequest).Order(StringComparer.Ordinal));
        Assert.Equal(0, service.PendingCount);
    }
}
