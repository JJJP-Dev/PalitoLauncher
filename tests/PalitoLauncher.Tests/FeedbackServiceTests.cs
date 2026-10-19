using System.Net;
using System.Text.Json;
using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.Net;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Tests.TestHelpers.F3;

namespace PalitoLauncher.Tests;

public sealed class FeedbackServiceTests : IDisposable
{
    private const string Webhook = "https://discord.com/api/webhooks/123456789012345678/AbC-dEf_123xyz";
    private const string PostUrl = Webhook + "?wait=true";

    private readonly TempDir _temp = new();
    private DateTimeOffset _now = new(2026, 10, 19, 18, 0, 0, TimeSpan.Zero);

    public void Dispose() => _temp.Dispose();

    private string OutboxDir => _temp.Combine("feedback", "outbox");

    private FeedbackService CreateService(CapturingHttpHandler handler, string? outboxDir = null)
        => new(HttpFactory.Create(handler), outboxDir ?? OutboxDir) { UtcNow = () => _now };

    private static CapturingHttpHandler OkHandler() => new(_ => CapturingHttpHandler.DiscordOk());

    private static CapturingHttpHandler NoNetworkHandler()
        => new(_ => throw new InvalidOperationException("No debería haber peticiones de red."));

    private static FeedbackSubmission CreateSubmission(int? rating = 4, string? nickname = "Marta", DateTimeOffset? createdAt = null)
    {
        var survey = SurveyProvider.CreateDefault();
        var submission = new FeedbackSubmission
        {
            SurveyId = survey.Id,
            SurveyTitle = survey.Title,
            Questions = survey.Questions,
            Nickname = nickname,
            Trigger = FeedbackTrigger.AfterSession,
            GameVersion = "0.0.3-pre.2",
            GameVersionDisplay = "0.0.3 · Prototipo 2",
            LauncherVersion = "1.0.3",
            OsDescription = "Microsoft Windows 10.0.26200",
            SessionMinutes = 34,
            ExitCode = 0,
            CreatedAtUtc = createdAt ?? new DateTimeOffset(2026, 10, 19, 17, 30, 15, TimeSpan.Zero),
        };
        if (rating is not null) submission.Answers["overall"] = new SurveyAnswer { Number = rating };
        submission.Answers["again"] = new SurveyAnswer { Choices = new List<string> { "Sí, otro día" } };
        submission.Answers["difficulty"] = new SurveyAnswer { Number = 5 };
        submission.Answers["issues"] = new SurveyAnswer { Choices = new List<string> { "Lag o desconexiones", "Fallos visuales" } };
        submission.Answers["best"] = new SurveyAnswer { Text = "Combinar el vapor con la electricidad" };
        submission.Answers["improve"] = new SurveyAnswer { Text = "   " }; // en blanco: no debe aparecer
        // "players" sin responder.
        return submission;
    }

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

    private static List<Field> PayloadFields(FeedbackSubmission submission)
    {
        using var doc = JsonDocument.Parse(FeedbackService.BuildDiscordPayload(submission));
        return GetFields(GetEmbed(doc));
    }

    private static string FieldValue(FeedbackSubmission submission, string name)
        => PayloadFields(submission).Single(f => f.Name == name).Value;

    private static int TotalEmbedChars(JsonElement embed)
        => embed.GetProperty("title").GetString()!.Length
           + embed.GetProperty("footer").GetProperty("text").GetString()!.Length
           + GetFields(embed).Sum(f => f.Name.Length + f.Value.Length);

    private string[] OutboxFiles() => Directory.Exists(OutboxDir)
        ? Directory.GetFiles(OutboxDir, "*.json", SearchOption.TopDirectoryOnly)
        : Array.Empty<string>();

    private string[] FailedFiles(FeedbackService service) => Directory.Exists(service.FailedDir)
        ? Directory.GetFiles(service.FailedDir)
        : Array.Empty<string>();

    /// <summary>Encola sin webhook varias respuestas, separadas lo justo para no chocar con el anti-spam.</summary>
    private async Task QueueAsync(FeedbackService service, params FeedbackSubmission[] submissions)
    {
        foreach (var submission in submissions)
        {
            Assert.Equal(FeedbackSendOutcome.Queued, await service.SubmitAsync(submission, null, CancellationToken.None));
            _now += TimeSpan.FromMinutes(1);
        }
    }

    private static string NicknameInRequest(CapturedRequest request)
    {
        using var doc = JsonDocument.Parse(request.Body!);
        return GetFields(GetEmbed(doc)).Single(f => f.Name == "Jugador").Value;
    }

    // ───────────────────────────── Mensaje de Discord ─────────────────────────────

    [Fact]
    public void BuildDiscordPayload_FollowsTheSpecifiedFormat()
    {
        using var doc = JsonDocument.Parse(FeedbackService.BuildDiscordPayload(CreateSubmission()));
        var root = doc.RootElement;

        Assert.Equal("Coop2D · Feedback", root.GetProperty("username").GetString());
        var parse = root.GetProperty("allowed_mentions").GetProperty("parse");
        Assert.Equal(JsonValueKind.Array, parse.ValueKind);
        Assert.Equal(0, parse.GetArrayLength());

        var embed = GetEmbed(doc);
        Assert.Equal("Opinión · Coop2D 0.0.3 · Prototipo 2", embed.GetProperty("title").GetString());
        Assert.Equal(0x3DDC97, embed.GetProperty("color").GetInt32());
        Assert.Equal("Launcher 1.0.3 · Windows 10.0.26200", embed.GetProperty("footer").GetProperty("text").GetString());
        Assert.Equal("2026-10-19T17:30:15.000Z", embed.GetProperty("timestamp").GetString());

        var fields = GetFields(embed);
        Assert.Equal(new[]
        {
            new Field("En general, ¿qué tal la partida?", "★★★★☆ (4/5)", false),
            new Field("¿Te apetece volver a jugar?", "Sí, otro día", false),
            new Field("¿Qué tal la dificultad?", "5 / 5 (Muy difícil)", false),
            new Field("¿Tuviste algún problema?", "Lag o desconexiones · Fallos visuales", false),
            new Field("¿Qué fue lo mejor?", "Combinar el vapor con la electricidad", false),
            new Field("Jugador", "Marta", true),
            new Field("Partida", "34 min · salió bien", true),
            new Field("Encuesta", "default", true),
        }, fields);
    }

    [Theory]
    [InlineData(1, "1 / 5 (Muy fácil)")]
    [InlineData(3, "3 / 5")]
    [InlineData(5, "5 / 5 (Muy difícil)")]
    [InlineData(-4, "1 / 5 (Muy fácil)")]
    [InlineData(99, "5 / 5 (Muy difícil)")]
    public void BuildDiscordPayload_Scale_ShowsTheEndLabelOnlyAtTheEnds(int value, string expected)
    {
        var submission = CreateSubmission();
        submission.Answers["difficulty"] = new SurveyAnswer { Number = value };

        Assert.Equal(expected, FieldValue(submission, "¿Qué tal la dificultad?"));
    }

    [Fact]
    public void BuildDiscordPayload_Scale_UsesTheQuestionRange()
    {
        var submission = new FeedbackSubmission
        {
            Questions = { new SurveyQuestion { Id = "s", Type = SurveyQuestionType.Scale, Text = "Escala", Min = 0, Max = 10, MinLabel = "Nada", MaxLabel = "Muchísimo" } },
            Answers = { ["s"] = new SurveyAnswer { Number = 0 } },
        };

        Assert.Equal("0 / 10 (Nada)", FieldValue(submission, "Escala"));
    }

    [Theory]
    [InlineData(1, "★☆☆☆☆ (1/5)", 0xFF5C6C)]
    [InlineData(2, "★★☆☆☆ (2/5)", 0xFF5C6C)]
    [InlineData(3, "★★★☆☆ (3/5)", 0xFFB627)]
    [InlineData(4, "★★★★☆ (4/5)", 0x3DDC97)]
    [InlineData(5, "★★★★★ (5/5)", 0x3DDC97)]
    [InlineData(0, "★☆☆☆☆ (1/5)", 0xFF5C6C)]
    [InlineData(9, "★★★★★ (5/5)", 0x3DDC97)]
    public void BuildDiscordPayload_RatingStarsAndColor(int rating, string stars, int color)
    {
        using var doc = JsonDocument.Parse(FeedbackService.BuildDiscordPayload(CreateSubmission(rating)));
        var embed = GetEmbed(doc);

        Assert.Equal(color, embed.GetProperty("color").GetInt32());
        Assert.Equal(stars, GetFields(embed).Single(f => f.Name == "En general, ¿qué tal la partida?").Value);
    }

    [Fact]
    public void BuildDiscordPayload_WithoutRating_IsBlue_AndSkipsTheQuestion()
    {
        var submission = CreateSubmission(rating: null);
        using var doc = JsonDocument.Parse(FeedbackService.BuildDiscordPayload(submission));
        var embed = GetEmbed(doc);

        Assert.Equal(0x5AA9FF, embed.GetProperty("color").GetInt32());
        Assert.DoesNotContain(GetFields(embed), f => f.Name == "En general, ¿qué tal la partida?");
    }

    [Fact]
    public void BuildDiscordPayload_ColorUsesTheFirstAnsweredRating()
    {
        var submission = new FeedbackSubmission
        {
            Questions =
            {
                new SurveyQuestion { Id = "a", Type = SurveyQuestionType.Rating, Text = "Primera (sin responder)" },
                new SurveyQuestion { Id = "b", Type = SurveyQuestionType.Rating, Text = "Segunda" },
                new SurveyQuestion { Id = "c", Type = SurveyQuestionType.Rating, Text = "Tercera" },
            },
            Answers = { ["b"] = new SurveyAnswer { Number = 2 }, ["c"] = new SurveyAnswer { Number = 5 } },
        };

        using var doc = JsonDocument.Parse(FeedbackService.BuildDiscordPayload(submission));
        Assert.Equal(0xFF5C6C, GetEmbed(doc).GetProperty("color").GetInt32());
    }

    [Fact]
    public void BuildDiscordPayload_Title_WithUnknownOrPlainVersion()
    {
        var unknown = CreateSubmission();
        unknown.GameVersion = null;
        unknown.GameVersionDisplay = null;
        using (var doc = JsonDocument.Parse(FeedbackService.BuildDiscordPayload(unknown)))
            Assert.Equal("Opinión · Coop2D · versión desconocida", GetEmbed(doc).GetProperty("title").GetString());

        var plain = CreateSubmission();
        plain.GameVersionDisplay = "  ";
        plain.GameVersion = "0.0.3";
        using (var doc = JsonDocument.Parse(FeedbackService.BuildDiscordPayload(plain)))
            Assert.Equal("Opinión · Coop2D 0.0.3", GetEmbed(doc).GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildDiscordPayload_Player_IsAnonymousWithoutNickname(string? nickname)
    {
        Assert.Equal("Anónimo", FieldValue(CreateSubmission(nickname: nickname), "Jugador"));
    }

    [Theory]
    [InlineData(34, 0, FeedbackTrigger.AfterSession, "34 min · salió bien")]
    [InlineData(34, 3, FeedbackTrigger.AfterSession, "34 min · código 3")]
    [InlineData(125, 0, FeedbackTrigger.AfterSession, "2 h 05 min · salió bien")]
    [InlineData(0, 0, FeedbackTrigger.AfterSession, "menos de 1 min · salió bien")]
    [InlineData(10, -1073741819, FeedbackTrigger.AfterSession, "10 min · código -1073741819 (0xC0000005)")]
    [InlineData(10, -1, FeedbackTrigger.AfterSession, "10 min · código -1 (no se pudo leer)")]
    [InlineData(null, null, FeedbackTrigger.Manual, "Enviada desde «Enviar opinión»")]
    [InlineData(null, null, FeedbackTrigger.AfterSession, "—")]
    [InlineData(null, 0, FeedbackTrigger.AfterSession, "salió bien")]
    public void BuildDiscordPayload_SessionField(int? minutes, int? exitCode, FeedbackTrigger trigger, string expected)
    {
        var submission = CreateSubmission();
        submission.SessionMinutes = minutes;
        submission.ExitCode = exitCode;
        submission.Trigger = trigger;

        Assert.Equal(expected, FieldValue(submission, "Partida"));
    }

    [Fact]
    public void BuildDiscordPayload_Footer_WithMissingVersions()
    {
        var submission = CreateSubmission();
        submission.LauncherVersion = "";
        submission.OsDescription = null;
        using var doc = JsonDocument.Parse(FeedbackService.BuildDiscordPayload(submission));

        Assert.Equal("Launcher ?", GetEmbed(doc).GetProperty("footer").GetProperty("text").GetString());
    }

    [Fact]
    public void BuildDiscordPayload_ImplausibleDate_OmitsTimestamp()
    {
        var submission = CreateSubmission();
        submission.CreatedAtUtc = default;
        using var doc = JsonDocument.Parse(FeedbackService.BuildDiscordPayload(submission));

        Assert.False(GetEmbed(doc).TryGetProperty("timestamp", out _));
    }

    [Fact]
    public void BuildDiscordPayload_RespectsDiscordLimits_WithHugeAnswers()
    {
        var submission = CreateSubmission();
        submission.Questions = Enumerable.Range(1, 30)
            .Select(i => new SurveyQuestion { Id = $"q{i}", Type = SurveyQuestionType.Text, Text = $"Pregunta {i} " + new string('p', 400) })
            .ToList();
        submission.Answers = submission.Questions.ToDictionary(q => q.Id, q => new SurveyAnswer { Text = new string('r', 5000) });
        submission.Nickname = new string('n', 5000);
        submission.SurveyId = new string('s', 5000);
        submission.GameVersionDisplay = new string('v', 5000);
        submission.OsDescription = new string('o', 5000);

        using var doc = JsonDocument.Parse(FeedbackService.BuildDiscordPayload(submission));
        var embed = GetEmbed(doc);
        var fields = GetFields(embed);

        Assert.True(fields.Count <= 25, $"{fields.Count} campos");
        Assert.True(TotalEmbedChars(embed) <= 6000, $"{TotalEmbedChars(embed)} caracteres");
        Assert.True(embed.GetProperty("title").GetString()!.Length <= 256);
        Assert.True(embed.GetProperty("footer").GetProperty("text").GetString()!.Length <= 2048);
        Assert.All(fields, f =>
        {
            Assert.InRange(f.Name.Length, 1, 256);
            Assert.InRange(f.Value.Length, 1, 1024);
        });
        Assert.Equal(new[] { "Jugador", "Partida", "Encuesta" }, fields.TakeLast(3).Select(f => f.Name));
        Assert.All(fields.Take(fields.Count - 3), f => Assert.EndsWith("…", f.Value));
        // Los textos cortos no se tocan.
        Assert.Equal("34 min · salió bien", fields.Single(f => f.Name == "Partida").Value);
    }

    [Fact]
    public void BuildDiscordPayload_CapsFieldsAt25()
    {
        var submission = new FeedbackSubmission
        {
            Questions = Enumerable.Range(1, 40).Select(i => new SurveyQuestion { Id = $"r{i}", Type = SurveyQuestionType.Rating, Text = $"Valoración {i}" }).ToList(),
        };
        foreach (var q in submission.Questions) submission.Answers[q.Id] = new SurveyAnswer { Number = 3 };

        var fields = PayloadFields(submission);

        Assert.Equal(25, fields.Count);
        Assert.Equal("Valoración 22", fields[21].Name);
        Assert.Equal(new[] { "Jugador", "Partida", "Encuesta" }, fields.TakeLast(3).Select(f => f.Name));
    }

    [Fact]
    public void BuildDiscordPayload_TruncatesLongQuestionTextAndSingleAnswer()
    {
        var submission = new FeedbackSubmission
        {
            Questions = { new SurveyQuestion { Id = "t", Type = SurveyQuestionType.Text, Text = new string('q', 400) } },
            Answers = { ["t"] = new SurveyAnswer { Text = "Línea 1\r\nLínea 2 " + new string('x', 3000) } },
        };

        var field = PayloadFields(submission)[0];

        Assert.Equal(256, field.Name.Length);
        Assert.Equal(1024, field.Value.Length);
        Assert.StartsWith("Línea 1\nLínea 2", field.Value);
        Assert.EndsWith("…", field.Value);
    }

    [Fact]
    public void BuildDiscordPayload_KeepsMentionsAsTextButDisablesThem()
    {
        var submission = CreateSubmission(nickname: "@everyone");
        submission.Answers["best"] = new SurveyAnswer { Text = "@here hola <@123456789012345678> <@&42>" };
        var json = FeedbackService.BuildDiscordPayload(submission);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(0, doc.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
        var fields = GetFields(GetEmbed(doc));
        Assert.Equal("@here hola <@123456789012345678> <@&42>", fields.Single(f => f.Name == "¿Qué fue lo mejor?").Value);
        Assert.Equal("@everyone", fields.Single(f => f.Name == "Jugador").Value);
    }

    [Fact]
    public void BuildDiscordPayload_HostileSubmission_DoesNotThrow()
    {
        var submission = new FeedbackSubmission
        {
            SurveyId = null!,
            Questions =
            {
                null!,
                new SurveyQuestion { Id = "", Type = SurveyQuestionType.Text, Text = "Sin id" },
                new SurveyQuestion { Id = "x", Type = (SurveyQuestionType)99, Text = "Tipo desconocido" },
                new SurveyQuestion { Id = "t", Type = SurveyQuestionType.Text, Text = "  " },
                new SurveyQuestion { Id = "t", Type = SurveyQuestionType.Text, Text = "Repetida" },
                new SurveyQuestion { Id = "n", Type = SurveyQuestionType.Rating, Text = "Respuesta null" },
                new SurveyQuestion { Id = "m", Type = SurveyQuestionType.Multi, Text = "Opciones null", },
                new SurveyQuestion { Id = "w", Type = SurveyQuestionType.Rating, Text = "Tipo que no encaja" },
            },
            Answers =
            {
                ["x"] = new SurveyAnswer { Number = 3 },
                ["T"] = new SurveyAnswer { Text = "Ho\u0000la \uD800 mundo‮" },
                ["n"] = null!,
                ["m"] = new SurveyAnswer { Choices = new List<string> { null!, "", "  " } },
                ["w"] = new SurveyAnswer { Text = "Texto en una valoración" },
            },
            Nickname = "\u0007\u0008",
            GameVersionDisplay = "\uDC00",
            LauncherVersion = null!,
            OsDescription = "Microsoft ",
        };

        var json = FeedbackService.BuildDiscordPayload(submission);
        using var doc = JsonDocument.Parse(json);
        var embed = GetEmbed(doc);
        var fields = GetFields(embed);

        // La pregunta "t" sin texto usa su Id como nombre; la respuesta se busca sin distinguir mayúsculas y se limpia.
        Assert.Equal(new[] { "t", "Jugador", "Partida", "Encuesta" }, fields.Select(f => f.Name));
        Assert.Equal("Hola  mundo", fields[0].Value);
        Assert.Equal("Anónimo", fields[1].Value);
        Assert.Equal("—", fields[3].Value);
        Assert.Equal("Opinión · Coop2D · versión desconocida", embed.GetProperty("title").GetString());
        Assert.Equal("Launcher ? · Microsoft", embed.GetProperty("footer").GetProperty("text").GetString());

        Assert.NotNull(FeedbackService.BuildDiscordPayload(new FeedbackSubmission { Questions = null!, Answers = null! }));
    }

    // ───────────────────────────── SubmitAsync ─────────────────────────────

    [Fact]
    public async Task SubmitAsync_Success_PostsToWebhookAndClearsOutbox()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        var submission = CreateSubmission();

        var outcome = await service.SubmitAsync(submission, Webhook, CancellationToken.None);

        Assert.Equal(FeedbackSendOutcome.Sent, outcome);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(PostUrl, request.Uri.ToString());
        Assert.StartsWith("application/json", request.ContentType);
        Assert.Contains("PalitoLauncher", request.Headers["User-Agent"]);
        Assert.Equal(FeedbackService.BuildDiscordPayload(submission), request.Body);
        Assert.Empty(OutboxFiles());
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public async Task SubmitAsync_AddsUserAgentWhenTheClientHasNone()
    {
        var handler = OkHandler();
        var service = new FeedbackService(new HttpClient(handler), OutboxDir) { UtcNow = () => _now };

        await service.SubmitAsync(CreateSubmission(), Webhook, CancellationToken.None);

        Assert.Contains("PalitoLauncher", Assert.Single(handler.Requests).Headers["User-Agent"]);
    }

    [Fact]
    public async Task SubmitAsync_ThreadWebhook_KeepsThreadIdAndAddsWait()
    {
        var handler = OkHandler();
        var service = CreateService(handler);

        await service.SubmitAsync(CreateSubmission(), Webhook + "?thread_id=987654321", CancellationToken.None);

        Assert.Equal(Webhook + "?thread_id=987654321&wait=true", Assert.Single(handler.Requests).Uri.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://discord.com/api/webhooks/PEGA_AQUI_LA_URL_DE_TU_WEBHOOK")]
    [InlineData("https://evil.example/api/webhooks/1/token")]
    [InlineData("http://discord.com/api/webhooks/1/token")]
    public async Task SubmitAsync_WithoutValidWebhook_QueuesWithoutNetwork(string? webhook)
    {
        var service = CreateService(NoNetworkHandler());
        var submission = CreateSubmission();

        var outcome = await service.SubmitAsync(submission, webhook, CancellationToken.None);

        Assert.Equal(FeedbackSendOutcome.Queued, outcome);
        Assert.Equal(1, service.PendingCount);
        var file = Assert.Single(OutboxFiles());
        Assert.Equal($"20261019173015-{submission.Id}.json", Path.GetFileName(file));
    }

    [Fact]
    public async Task SubmitAsync_OutboxFile_RoundTripsTheSubmission()
    {
        var service = CreateService(NoNetworkHandler());
        var submission = CreateSubmission();

        await service.SubmitAsync(submission, null, CancellationToken.None);

        var json = File.ReadAllText(Assert.Single(OutboxFiles()));
        Assert.Contains("\"trigger\": \"afterSession\"", json);
        Assert.Contains("\"type\": \"rating\"", json);
        Assert.DoesNotContain("isEmpty", json, StringComparison.OrdinalIgnoreCase);

        var back = JsonSerializer.Deserialize<FeedbackSubmission>(json, FeedbackService.OutboxJsonOptions)!;
        Assert.Equal(submission.Id, back.Id);
        Assert.Equal(submission.CreatedAtUtc, back.CreatedAtUtc);
        Assert.Equal(submission.Questions.Select(q => (q.Id, q.Type, q.Text)), back.Questions.Select(q => (q.Id, q.Type, q.Text)));
        Assert.Equal(4, back.Answers["overall"].Number);
        Assert.Equal(new[] { "Lag o desconexiones", "Fallos visuales" }, back.Answers["issues"].Choices);
        Assert.Equal(FeedbackService.BuildDiscordPayload(submission), FeedbackService.BuildDiscordPayload(back));
    }

    [Fact]
    public async Task SubmitAsync_TextWithLoneSurrogates_IsSavedAndSent()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        var submission = CreateSubmission(nickname: "Ana\uDC00");
        submission.Answers["best"] = new SurveyAnswer { Text = "Pegado raro \uD800 aquí 😀" };

        Assert.Equal(FeedbackSendOutcome.Queued, await service.SubmitAsync(submission, null, CancellationToken.None));
        Assert.Equal(1, await service.FlushOutboxAsync(Webhook, CancellationToken.None));

        using var doc = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        var fields = GetFields(GetEmbed(doc));
        // Al guardar en la bandeja, System.Text.Json cambia el surrogate suelto por U+FFFD; nunca llega UTF-16 inválido.
        var best = fields.Single(f => f.Name == "¿Qué fue lo mejor?").Value;
        Assert.StartsWith("Pegado raro ", best);
        Assert.EndsWith(" aquí 😀", best);
        Assert.DoesNotContain('\uD800', best);
        Assert.StartsWith("Ana", fields.Single(f => f.Name == "Jugador").Value);
        Assert.DoesNotContain('\uDC00', fields.Single(f => f.Name == "Jugador").Value);
    }

    [Fact]
    public async Task SubmitAsync_UnsafeId_IsReplacedBeforeNamingTheFile()
    {
        var service = CreateService(NoNetworkHandler());
        var submission = CreateSubmission();
        submission.Id = @"..\..\evil";

        await service.SubmitAsync(submission, null, CancellationToken.None);

        Assert.Matches("^[0-9a-f]{32}$", submission.Id);
        var file = Assert.Single(OutboxFiles());
        Assert.Equal(Path.GetFullPath(OutboxDir), Path.GetDirectoryName(file));
        Assert.EndsWith($"-{submission.Id}.json", file);
    }

    [Fact]
    public async Task SubmitAsync_NetworkError_Queues()
    {
        var service = CreateService(new CapturingHttpHandler(_ => throw new HttpRequestException("sin red")));

        var outcome = await service.SubmitAsync(CreateSubmission(), Webhook, CancellationToken.None);

        Assert.Equal(FeedbackSendOutcome.Queued, outcome);
        Assert.Equal(1, service.PendingCount);
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
        var service = CreateService(new CapturingHttpHandler(_ => CapturingHttpHandler.Json(status, """{"message":"Unknown Webhook","code":10015}""")));

        var outcome = await service.SubmitAsync(CreateSubmission(), Webhook, CancellationToken.None);

        Assert.Equal(FeedbackSendOutcome.Queued, outcome);
        Assert.Equal(1, service.PendingCount);
        Assert.Empty(FailedFiles(service));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public async Task SubmitAsync_PayloadRejected_MovesToFailed(HttpStatusCode status)
    {
        var service = CreateService(new CapturingHttpHandler(_ => CapturingHttpHandler.Json(status, """{"embeds":["0"],"message":"Invalid Form Body"}""")));
        var submission = CreateSubmission();

        var outcome = await service.SubmitAsync(submission, Webhook, CancellationToken.None);

        Assert.Equal(FeedbackSendOutcome.Queued, outcome);
        Assert.Equal(0, service.PendingCount);
        Assert.Empty(OutboxFiles());
        var failed = Assert.Single(FailedFiles(service));
        Assert.EndsWith($"-{submission.Id}.json", failed);
    }

    [Fact]
    public async Task SubmitAsync_RateLimited_RespectsRetryAfterBeforeTryingAgain()
    {
        var calls = 0;
        var handler = new CapturingHttpHandler(_ => Interlocked.Increment(ref calls) == 1
            ? CapturingHttpHandler.TooManyRequests(120)
            : CapturingHttpHandler.DiscordOk());
        var service = CreateService(handler);

        Assert.Equal(FeedbackSendOutcome.Queued, await service.SubmitAsync(CreateSubmission(), Webhook, CancellationToken.None));
        Assert.Equal(1, service.PendingCount);

        _now += TimeSpan.FromSeconds(60);
        Assert.Equal(0, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
        Assert.Single(handler.Requests);

        // Una opinión nueva tampoco llama a Discord mientras dure la espera.
        Assert.Equal(FeedbackSendOutcome.Queued, await service.SubmitAsync(CreateSubmission(), Webhook, CancellationToken.None));
        Assert.Single(handler.Requests);

        _now += TimeSpan.FromSeconds(61);
        Assert.Equal(2, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public async Task SubmitAsync_RateLimited_WithOnlyRetryAfterHeader()
    {
        var calls = 0;
        var handler = new CapturingHttpHandler(_ =>
        {
            if (Interlocked.Increment(ref calls) > 1) return CapturingHttpHandler.DiscordOk();
            var response = CapturingHttpHandler.Status((HttpStatusCode)429, "<html>Cloudflare</html>");
            response.Headers.TryAddWithoutValidation("Retry-After", "30");
            return response;
        });
        var service = CreateService(handler);

        await service.SubmitAsync(CreateSubmission(), Webhook, CancellationToken.None);
        _now += TimeSpan.FromSeconds(20);
        Assert.Equal(0, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
        _now += TimeSpan.FromSeconds(11);
        Assert.Equal(1, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
    }

    [Fact]
    public async Task SubmitAsync_AntiSpam_BlocksASecondSubmissionWithinTheInterval()
    {
        var handler = OkHandler();
        var service = CreateService(handler);

        Assert.Equal(FeedbackSendOutcome.Sent, await service.SubmitAsync(CreateSubmission(), Webhook, CancellationToken.None));

        _now += TimeSpan.FromSeconds(10);
        var ex = await Assert.ThrowsAsync<LauncherException>(() => service.SubmitAsync(CreateSubmission(), Webhook, CancellationToken.None));
        Assert.Equal("Espera unos segundos antes de enviar otra opinión.", ex.Message);
        Assert.Single(handler.Requests);
        Assert.Empty(OutboxFiles()); // la rechazada no se guarda (el diálogo conserva lo escrito)

        _now += TimeSpan.FromSeconds(21);
        Assert.Equal(FeedbackSendOutcome.Sent, await service.SubmitAsync(CreateSubmission(), Webhook, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SubmitAsync_AntiSpam_IntervalIsConfigurable_AndClockGoingBackDoesNotBlock()
    {
        var service = CreateService(NoNetworkHandler());
        service.MinIntervalBetweenSubmissions = TimeSpan.Zero;

        await service.SubmitAsync(CreateSubmission(), null, CancellationToken.None);
        await service.SubmitAsync(CreateSubmission(), null, CancellationToken.None);

        service.MinIntervalBetweenSubmissions = TimeSpan.FromSeconds(30);
        _now -= TimeSpan.FromHours(1); // la hora de Windows retrocede
        await service.SubmitAsync(CreateSubmission(), null, CancellationToken.None);

        Assert.Equal(3, service.PendingCount);
    }

    [Fact]
    public async Task SubmitAsync_SameSubmissionAgain_IsNotSentTwice()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        var submission = CreateSubmission();

        Assert.Equal(FeedbackSendOutcome.Sent, await service.SubmitAsync(submission, Webhook, CancellationToken.None));
        Assert.Equal(FeedbackSendOutcome.Sent, await service.SubmitAsync(submission, Webhook, CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Empty(OutboxFiles());
    }

    [Fact]
    public async Task SubmitAsync_RetryingAQueuedSubmission_OverwritesItsFile()
    {
        var service = CreateService(new CapturingHttpHandler(_ => throw new HttpRequestException("sin red")));
        var submission = CreateSubmission();

        Assert.Equal(FeedbackSendOutcome.Queued, await service.SubmitAsync(submission, Webhook, CancellationToken.None));
        submission.Nickname = "Marta (editado)";
        // Reintentar la misma respuesta enseguida no cuenta como spam.
        Assert.Equal(FeedbackSendOutcome.Queued, await service.SubmitAsync(submission, Webhook, CancellationToken.None));

        var file = Assert.Single(OutboxFiles());
        Assert.Contains("Marta (editado)", File.ReadAllText(file));
    }

    [Fact]
    public async Task SubmitAsync_DiskError_ThrowsClearMessage_AndDoesNotStartTheAntiSpamWindow()
    {
        var blocked = _temp.WriteFile("bloqueado", "soy un archivo, no una carpeta");
        var handler = OkHandler();
        var service = CreateService(handler, blocked);

        var ex = await Assert.ThrowsAsync<LauncherException>(() => service.SubmitAsync(CreateSubmission(), Webhook, CancellationToken.None));
        Assert.Contains("No se pudo guardar tu opinión", ex.Message);
        Assert.Empty(handler.Requests);

        File.Delete(blocked);
        Assert.Equal(FeedbackSendOutcome.Sent, await service.SubmitAsync(CreateSubmission(), Webhook, CancellationToken.None));
    }

    [Fact]
    public async Task SubmitAsync_CanceledWhileSending_ThrowsAndKeepsTheFile()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CapturingHttpHandler(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return CapturingHttpHandler.DiscordOk();
        });
        var service = CreateService(handler);
        using var cts = new CancellationTokenSource();

        var task = service.SubmitAsync(CreateSubmission(), Webhook, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, service.PendingCount);
    }

    [Fact]
    public async Task SubmitAsync_AlreadyCanceled_StillSavesToTheOutbox()
    {
        var service = CreateService(NoNetworkHandler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SubmitAsync(CreateSubmission(), Webhook, cts.Token));
        Assert.Equal(1, service.PendingCount);
    }

    [Fact]
    public async Task SubmitAsync_Timeout_QueuesInsteadOfThrowing()
    {
        var handler = new CapturingHttpHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return CapturingHttpHandler.DiscordOk();
        });
        var service = CreateService(handler);
        service.RequestTimeout = TimeSpan.FromMilliseconds(200);

        var outcome = await service.SubmitAsync(CreateSubmission(), Webhook, CancellationToken.None);

        Assert.Equal(FeedbackSendOutcome.Queued, outcome);
        Assert.Equal(1, service.PendingCount);
    }

    [Fact]
    public async Task SubmitAsync_Null_Throws()
    {
        var service = CreateService(NoNetworkHandler());
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SubmitAsync(null!, Webhook, CancellationToken.None));
    }

    // ───────────────────────────── FlushOutboxAsync ─────────────────────────────

    private async Task<FeedbackService> CreateServiceWithThreeQueuedAsync(CapturingHttpHandler handler)
    {
        var service = CreateService(handler);
        // Encoladas en desorden: se envían por fecha de creación.
        await QueueAsync(service,
            CreateSubmission(nickname: "C", createdAt: new DateTimeOffset(2026, 10, 19, 10, 0, 3, TimeSpan.Zero)),
            CreateSubmission(nickname: "A", createdAt: new DateTimeOffset(2026, 10, 19, 10, 0, 1, TimeSpan.Zero)),
            CreateSubmission(nickname: "B", createdAt: new DateTimeOffset(2026, 10, 19, 10, 0, 2, TimeSpan.Zero)));
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
        Assert.Empty(OutboxFiles());
    }

    [Fact]
    public async Task FlushOutboxAsync_RespectsMaxItems()
    {
        var handler = OkHandler();
        var service = await CreateServiceWithThreeQueuedAsync(handler);

        Assert.Equal(2, await service.FlushOutboxAsync(Webhook, CancellationToken.None, maxItems: 2));
        Assert.Equal(new[] { "A", "B" }, handler.Requests.Select(NicknameInRequest));
        Assert.Equal(1, service.PendingCount);

        Assert.Equal(0, await service.FlushOutboxAsync(Webhook, CancellationToken.None, maxItems: 0));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FlushOutboxAsync_StopsAtTheFirst429()
    {
        var calls = 0;
        var handler = new CapturingHttpHandler(_ => Interlocked.Increment(ref calls) == 2
            ? CapturingHttpHandler.TooManyRequests(5)
            : CapturingHttpHandler.DiscordOk());
        var service = await CreateServiceWithThreeQueuedAsync(handler);

        Assert.Equal(1, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, service.PendingCount);
    }

    [Fact]
    public async Task FlushOutboxAsync_StopsAtTheFirstNetworkError()
    {
        var handler = new CapturingHttpHandler(_ => throw new HttpRequestException("sin red"));
        var service = await CreateServiceWithThreeQueuedAsync(handler);

        Assert.Equal(0, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.Equal(3, service.PendingCount);
    }

    [Fact]
    public async Task FlushOutboxAsync_StopsWhenTheWebhookIsGone()
    {
        var handler = new CapturingHttpHandler(_ => CapturingHttpHandler.Json(HttpStatusCode.NotFound, """{"message":"Unknown Webhook","code":10015}"""));
        var service = await CreateServiceWithThreeQueuedAsync(handler);

        Assert.Equal(0, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.Equal(3, service.PendingCount);
        Assert.Empty(FailedFiles(service));
    }

    [Fact]
    public async Task FlushOutboxAsync_PayloadRejection_MovesThatOneAndContinues()
    {
        var calls = 0;
        var handler = new CapturingHttpHandler(_ => Interlocked.Increment(ref calls) == 2
            ? CapturingHttpHandler.Json(HttpStatusCode.BadRequest, """{"message":"Invalid Form Body"}""")
            : CapturingHttpHandler.DiscordOk());
        var service = await CreateServiceWithThreeQueuedAsync(handler);

        Assert.Equal(2, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(0, service.PendingCount);
        Assert.Single(FailedFiles(service));
    }

    [Fact]
    public async Task FlushOutboxAsync_CorruptFiles_GoToFailed_AndTheRestIsSent()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        await QueueAsync(service, CreateSubmission(nickname: "Buena"));
        File.WriteAllText(Path.Combine(OutboxDir, "20200101000000-corrupto.json"), "{ esto no es json");
        File.WriteAllText(Path.Combine(OutboxDir, "20200101000001-nulo.json"), "null");
        File.WriteAllText(Path.Combine(OutboxDir, "20200101000002-tiporaro.json"), """{ "questions": [ { "id": "x", "type": "slider" } ] }""");
        File.WriteAllText(Path.Combine(OutboxDir, "20200101000003-enorme.json"), "{\"nickname\":\"" + new string('x', 1_100_000) + "\"}");
        Assert.Equal(5, service.PendingCount);

        var sent = await service.FlushOutboxAsync(Webhook, CancellationToken.None);

        Assert.Equal(1, sent);
        Assert.Equal("Buena", NicknameInRequest(Assert.Single(handler.Requests)));
        Assert.Equal(0, service.PendingCount);
        Assert.Equal(4, FailedFiles(service).Length);
    }

    [Fact]
    public async Task FlushOutboxAsync_HostileButValidFile_IsSentSafely()
    {
        var handler = OkHandler();
        var service = CreateService(handler);
        Directory.CreateDirectory(OutboxDir);
        File.WriteAllText(Path.Combine(OutboxDir, "20260101000000-abc123.json"), """
            {
              "id": "..\\..\\otra-cosa",
              "surveyId": "@everyone",
              "questions": [ { "id": "x", "type": 99, "text": "?" }, null ],
              "answers": { "x": { "number": 5 }, "y": null },
              "nickname": null,
              "createdAtUtc": "0001-01-01T00:00:00+00:00"
            }
            """);

        Assert.Equal(1, await service.FlushOutboxAsync(Webhook, CancellationToken.None));

        using var doc = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        var embed = GetEmbed(doc);
        Assert.Equal(new[] { "Jugador", "Partida", "Encuesta" }, GetFields(embed).Select(f => f.Name));
        Assert.False(embed.TryGetProperty("timestamp", out _));
        Assert.Empty(OutboxFiles());
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
        var handler = new CapturingHttpHandler(_ =>
        {
            var response = CapturingHttpHandler.DiscordOk();
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
    public async Task FlushOutboxAsync_PreemptiveRateLimitHeader_ShortWait_WaitsAndContinues()
    {
        var handler = new CapturingHttpHandler(_ =>
        {
            var response = CapturingHttpHandler.DiscordOk();
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset-After", "0.05");
            return response;
        });
        var service = await CreateServiceWithThreeQueuedAsync(handler);

        Assert.Equal(3, await service.FlushOutboxAsync(Webhook, CancellationToken.None));
    }

    [Fact]
    public async Task FlushOutboxAsync_ConcurrentCalls_NeverSendTwice()
    {
        var handler = new CapturingHttpHandler(async (_, ct) =>
        {
            await Task.Delay(30, ct);
            return CapturingHttpHandler.DiscordOk();
        });
        var service = await CreateServiceWithThreeQueuedAsync(handler);

        var results = await Task.WhenAll(
            service.FlushOutboxAsync(Webhook, CancellationToken.None),
            service.FlushOutboxAsync(Webhook, CancellationToken.None),
            service.FlushOutboxAsync(Webhook, CancellationToken.None));

        Assert.Equal(3, results.Sum());
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(3, handler.Requests.Select(NicknameInRequest).Distinct().Count());
    }

    [Fact]
    public async Task SubmitAndFlushAtTheSameTime_NeverSendTwice()
    {
        var handler = new CapturingHttpHandler(async (_, ct) =>
        {
            await Task.Delay(30, ct);
            return CapturingHttpHandler.DiscordOk();
        });
        var service = await CreateServiceWithThreeQueuedAsync(handler);

        var submit = service.SubmitAsync(CreateSubmission(nickname: "D", createdAt: new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero)),
            Webhook, CancellationToken.None);
        var flush = service.FlushOutboxAsync(Webhook, CancellationToken.None);
        await Task.WhenAll(submit, flush);

        Assert.Equal(FeedbackSendOutcome.Sent, await submit);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(new[] { "A", "B", "C", "D" }, handler.Requests.Select(NicknameInRequest).Order());
        Assert.Equal(0, service.PendingCount);
    }

    // ───────────────────────────── PendingCount ─────────────────────────────

    [Fact]
    public async Task PendingCount_OnlyCountsOutboxFiles()
    {
        var service = CreateService(NoNetworkHandler());
        Assert.Equal(0, service.PendingCount); // la carpeta aún no existe

        await QueueAsync(service, CreateSubmission(), CreateSubmission());
        Directory.CreateDirectory(service.FailedDir);
        File.WriteAllText(Path.Combine(service.FailedDir, "20200101000000-fallida.json"), "{}");
        File.WriteAllText(Path.Combine(OutboxDir, "20200101000000-abc.json.1a2b3c4d.tmp"), "{}");
        File.WriteAllText(Path.Combine(OutboxDir, "notas.json"), "{}");
        File.WriteAllText(Path.Combine(OutboxDir, "leeme.txt"), "hola");

        Assert.Equal(2, service.PendingCount);
    }

    [Fact]
    public void Constructor_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new FeedbackService(null!, OutboxDir));
        Assert.ThrowsAny<ArgumentException>(() => new FeedbackService(new HttpClient(), " "));
    }

    // ───────────────────────────── ShouldPromptAfterSession ─────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 20, 0, 0, TimeSpan.Zero);

    private static SurveyDefinition Survey(bool ask = true, int minMinutes = 5, int minHours = 12)
        => new() { AskAfterSession = ask, MinSessionMinutes = minMinutes, MinHoursBetweenPrompts = minHours };

    [Theory]
    [InlineData(true, true, 34, null, true)]
    [InlineData(false, true, 34, null, false)]   // el usuario lo desactivó
    [InlineData(true, false, 34, null, false)]   // la encuesta no invita
    [InlineData(true, true, 4.9, null, false)]   // partida demasiado corta
    [InlineData(true, true, 5, null, true)]      // justo el mínimo
    [InlineData(true, true, 34, 11.9, false)]    // invitación reciente
    [InlineData(true, true, 34, 12.0, true)]       // justo el intervalo
    [InlineData(true, true, 34, 48.0, true)]
    [InlineData(true, true, 34, -3.0, true)]       // última invitación "en el futuro" (cambio de hora)
    public void ShouldPromptAfterSession_Rules(bool askEnabled, bool surveyAsks, double sessionMinutes, double? hoursSinceLastPrompt, bool expected)
    {
        DateTimeOffset? last = hoursSinceLastPrompt is { } h ? Now - TimeSpan.FromHours(h) : null;

        var result = FeedbackService.ShouldPromptAfterSession(askEnabled, Survey(surveyAsks), TimeSpan.FromMinutes(sessionMinutes), last, Now);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ShouldPromptAfterSession_EdgeCases()
    {
        // Sin intervalo mínimo: siempre que la partida sea larga.
        Assert.True(FeedbackService.ShouldPromptAfterSession(true, Survey(minHours: 0), TimeSpan.FromMinutes(10), Now, Now));
        // Encuesta sin sanear: nunca invita tras una partida de 0 min ni con duración negativa.
        Assert.False(FeedbackService.ShouldPromptAfterSession(true, Survey(minMinutes: 0), TimeSpan.Zero, null, Now));
        Assert.False(FeedbackService.ShouldPromptAfterSession(true, Survey(), TimeSpan.FromMinutes(-50), null, Now));
        // Valores absurdos se limitan: 10 000 h → 168 h.
        Assert.True(FeedbackService.ShouldPromptAfterSession(true, Survey(minHours: 10_000), TimeSpan.FromMinutes(10), Now - TimeSpan.FromHours(169), Now));
        // Sin encuesta: no lanza.
        Assert.False(FeedbackService.ShouldPromptAfterSession(true, null!, TimeSpan.FromHours(1), null, Now));
    }
}
