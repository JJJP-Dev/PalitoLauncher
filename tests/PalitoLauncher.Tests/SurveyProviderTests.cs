using System.Net;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Net;
using PalitoLauncher.Tests.TestHelpers.F3;

namespace PalitoLauncher.Tests;

public sealed class SurveyProviderTests : IDisposable
{
    private const string Webhook = "https://discord.com/api/webhooks/123456789012345678/AbC-dEf_123xyz";

    private const string ValidSurveyJson = """
        {
          // Comentarios y comas finales permitidos.
          "id": "pre2-fun-test",
          "title": "Prueba de diversión · Prototipo 2",
          "intro": "Responde con sinceridad.",
          "askAfterSession": false,
          "minSessionMinutes": 10,
          "minHoursBetweenPrompts": 24,
          "discordWebhookUrl": "https://discord.com/api/webhooks/123456789012345678/AbC-dEf_123xyz",
          "questions": [
            { "id": "overall", "type": "RATING", "text": "En general, ¿qué tal?", "required": true },
            { "id": "combo-fun", "type": "Scale", "text": "¿Divertido?", "min": 1, "max": 5, "minLabel": "Nada", "maxLabel": "Mucho" },
            { "id": "detonate", "type": "choice", "text": "¿Entendiste cuándo detonar?", "hint": "Sin pistas.", "options": ["Sí", "Más o menos", "No"] },
            { "id": "issues", "type": "multi", "text": "¿Problemas?", "options": ["Lag", "Ninguno"] },
            { "id": "improve", "type": "text", "text": "¿Qué cambiarías?", "maxLength": 500, "placeholder": "Lo que sea…", },
          ],
        }
        """;

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    // ───────────────────────────── Encuesta por defecto ─────────────────────────────

    [Fact]
    public void CreateDefault_MatchesSpecification()
    {
        var survey = SurveyProvider.CreateDefault();

        Assert.Equal("default", survey.Id);
        Assert.Equal("Tu opinión sobre Coop2D", survey.Title);
        Assert.Null(survey.DiscordWebhookUrl);
        Assert.True(survey.AskAfterSession);
        Assert.Equal(5, survey.MinSessionMinutes);
        Assert.Equal(12, survey.MinHoursBetweenPrompts);
        Assert.Equal(new[] { "overall", "again", "difficulty", "players", "issues", "best", "improve" },
            survey.Questions.Select(q => q.Id));

        var overall = survey.Questions[0];
        Assert.Equal(SurveyQuestionType.Rating, overall.Type);
        Assert.True(overall.Required);
        Assert.Equal("En general, ¿qué tal la partida?", overall.Text);

        var again = survey.Questions[1];
        Assert.Equal(SurveyQuestionType.Choice, again.Type);
        Assert.True(again.Required);
        Assert.Equal("¿Te apetece volver a jugar?", again.Text);
        Assert.Equal(new[] { "Sí, otra partida ya", "Sí, otro día", "No mucho" }, again.Options);

        var difficulty = survey.Questions[2];
        Assert.Equal(SurveyQuestionType.Scale, difficulty.Type);
        Assert.False(difficulty.Required);
        Assert.Equal("¿Qué tal la dificultad?", difficulty.Text);
        Assert.Equal((1, 5), (difficulty.Min, difficulty.Max));
        Assert.Equal("Muy fácil", difficulty.MinLabel);
        Assert.Equal("Muy difícil", difficulty.MaxLabel);

        var players = survey.Questions[3];
        Assert.Equal(SurveyQuestionType.Choice, players.Type);
        Assert.Equal("¿Cuántos jugabais?", players.Text);
        Assert.Equal(new[] { "Solo", "2", "3", "4" }, players.Options);

        var issues = survey.Questions[4];
        Assert.Equal(SurveyQuestionType.Multi, issues.Type);
        Assert.Equal("¿Tuviste algún problema?", issues.Text);
        Assert.Equal(new[] { "Lag o desconexiones", "El juego se cerró", "Va lento o a tirones", "Fallos visuales", "Controles incómodos", "Ninguno" },
            issues.Options);

        Assert.Equal(SurveyQuestionType.Text, survey.Questions[5].Type);
        Assert.Equal("¿Qué fue lo mejor?", survey.Questions[5].Text);
        Assert.Equal(SurveyQuestionType.Text, survey.Questions[6].Type);
        Assert.Equal("¿Qué cambiarías o mejorarías?", survey.Questions[6].Text);
        Assert.All(survey.Questions.Skip(2), q => Assert.False(q.Required));
    }

    [Fact]
    public void CreateDefault_PassesItsOwnSanitizer_AndReturnsFreshInstances()
    {
        var a = SurveyProvider.CreateDefault();
        var b = SurveyProvider.CreateDefault();
        Assert.NotSame(a, b);
        Assert.NotSame(a.Questions, b.Questions);

        var sanitized = SurveyProvider.Sanitize(SurveyProvider.CreateDefault());
        Assert.Equal(a.Questions.Count, sanitized.Questions.Count);
        Assert.Equal(a.Questions.Select(q => q.Text), sanitized.Questions.Select(q => q.Text));
        Assert.Equal(a.Questions.Select(q => q.Options.Count), sanitized.Questions.Select(q => q.Options.Count));
    }

    // ───────────────────────────── Parse ─────────────────────────────

    [Fact]
    public void Parse_ValidJson_WithCommentsTrailingCommasAndMixedCaseTypes()
    {
        var survey = SurveyProvider.Parse(ValidSurveyJson);

        Assert.NotNull(survey);
        Assert.Equal("pre2-fun-test", survey.Id);
        Assert.Equal("Prueba de diversión · Prototipo 2", survey.Title);
        Assert.Equal("Responde con sinceridad.", survey.Intro);
        Assert.False(survey.AskAfterSession);
        Assert.Equal(10, survey.MinSessionMinutes);
        Assert.Equal(24, survey.MinHoursBetweenPrompts);
        Assert.Equal(Webhook, survey.DiscordWebhookUrl);
        Assert.Equal(new[] { SurveyQuestionType.Rating, SurveyQuestionType.Scale, SurveyQuestionType.Choice, SurveyQuestionType.Multi, SurveyQuestionType.Text },
            survey.Questions.Select(q => q.Type));
        Assert.True(survey.Questions[0].Required);
        Assert.Equal("Nada", survey.Questions[1].MinLabel);
        Assert.Equal("Mucho", survey.Questions[1].MaxLabel);
        Assert.Equal("Sin pistas.", survey.Questions[2].Hint);
        Assert.Equal(new[] { "Sí", "Más o menos", "No" }, survey.Questions[2].Options);
        Assert.Equal(500, survey.Questions[4].MaxLength);
        Assert.Equal("Lo que sea…", survey.Questions[4].Placeholder);
    }

    [Fact]
    public void Parse_DocsExampleFile_IsAccepted()
    {
        // El ejemplo de docs/ (lo mantiene otro agente): si existe, tiene que ser válido para el parser.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PalitoLauncher.sln"))) dir = dir.Parent;
        var example = dir is null ? null : Path.Combine(dir.FullName, "docs", "ejemplos", "feedback.example.json");
        if (example is null || !File.Exists(example)) return;

        var survey = SurveyProvider.Parse(File.ReadAllText(example));

        Assert.NotNull(survey);
        Assert.NotEmpty(survey.Questions);
        Assert.Equal(SurveyQuestionType.Rating, survey.Questions[0].Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no es json")]
    [InlineData("<html><body>Portal cautivo</body></html>")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"texto\"")]
    [InlineData("{ \"questions\": [ ")]
    [InlineData("{ \"title\": \"Sin preguntas\" }")]
    [InlineData("{ \"questions\": [] }")]
    [InlineData("{ \"questions\": \"overall\" }")]
    [InlineData("{ \"questions\": [ { \"id\": \"x\", \"type\": \"slider\", \"text\": \"¿?\" } ] }")]
    public void Parse_InvalidOrUseless_ReturnsNull(string json)
    {
        Assert.Null(SurveyProvider.Parse(json));
    }

    [Fact]
    public void Parse_Null_ReturnsNullWithoutThrowing()
    {
        Assert.Null(SurveyProvider.Parse(null!));
    }

    [Fact]
    public void Parse_HandlesBom()
    {
        var survey = SurveyProvider.Parse("﻿" + ValidSurveyJson);
        Assert.NotNull(survey);
    }

    [Fact]
    public void Parse_DiscardsInvalidQuestions_AndKeepsTheRest()
    {
        const string json = """
            {
              "questions": [
                { "type": "rating", "text": "Sin id" },
                { "id": "con espacio", "type": "rating", "text": "Id con espacio" },
                { "id": "a/b", "type": "rating", "text": "Id con barra" },
                { "id": "sin-texto", "type": "rating", "text": "   " },
                { "id": "tipo-raro", "type": "slider", "text": "Tipo desconocido" },
                { "id": "tipo-numero", "type": 2, "text": "Tipo numérico" },
                { "id": "tipo-texto-numero", "type": "2", "text": "Tipo como número en texto" },
                { "id": "sin-tipo", "text": "Sin tipo" },
                { "id": "una-opcion", "type": "choice", "text": "Una opción", "options": ["Sí"] },
                { "id": "opciones-vacias", "type": "multi", "text": "Vacías y repetidas", "options": ["", "  ", "A", "a", " A "] },
                "no soy un objeto",
                null,
                { "id": "ok", "type": "rating", "text": "Válida" },
                { "id": "OK", "type": "text", "text": "Id repetido (sin distinguir mayúsculas)" },
                { "id": "elige", "type": "choice", "text": "Válida con opciones", "options": ["Uno", "", "Dos", "uno"] },
                { "id": "ñandú_1", "type": "text", "text": "Id con letras españolas" }
              ]
            }
            """;

        var survey = SurveyProvider.Parse(json);

        Assert.NotNull(survey);
        Assert.Equal(new[] { "ok", "elige", "ñandú_1" }, survey.Questions.Select(q => q.Id));
        Assert.Equal(new[] { "Uno", "Dos" }, survey.Questions[1].Options);
    }

    [Fact]
    public void Parse_TruncatesTextsAndCounts()
    {
        var longText = new string('x', 500);
        var options = string.Join(", ", Enumerable.Range(1, 15).Select(i => $"\"Opción {i} {new string('o', 100)}\""));
        var questions = string.Join(",\n", Enumerable.Range(1, 20).Select(i =>
            i == 1
                ? $$"""{ "id": "q1", "type": "multi", "text": "{{longText}}", "hint": "{{longText}}", "options": [{{options}}] }"""
                : $$"""{ "id": "q{{i}}", "type": "rating", "text": "Pregunta {{i}}" }"""));
        var json = $$"""{ "title": "{{longText}}", "intro": "{{new string('i', 1000)}}", "questions": [ {{questions}} ] }""";

        var survey = SurveyProvider.Parse(json);

        Assert.NotNull(survey);
        Assert.Equal(12, survey.Questions.Count);
        Assert.Equal("q12", survey.Questions[^1].Id);
        Assert.True(survey.Title.Length <= SurveyProvider.MaxTitleLength);
        Assert.EndsWith("…", survey.Title);
        Assert.Equal(300, survey.Intro!.Length);
        Assert.EndsWith("…", survey.Intro);

        var first = survey.Questions[0];
        Assert.Equal(200, first.Text.Length);
        Assert.EndsWith("…", first.Text);
        Assert.Equal(200, first.Hint!.Length);
        Assert.Equal(10, first.Options.Count);
        Assert.All(first.Options, o => Assert.True(o.Length <= 80));
        Assert.All(first.Options, o => Assert.EndsWith("…", o));
    }

    [Theory]
    [InlineData(-5, 50, 0, 10)]
    [InlineData(7, 3, 3, 7)]
    [InlineData(4, 4, 1, 5)]
    [InlineData(10, 10, 1, 5)]
    [InlineData(0, 10, 0, 10)]
    [InlineData(1, 5, 1, 5)]
    public void Parse_ScaleRange_IsLimited(int min, int max, int expectedMin, int expectedMax)
    {
        var json = $$"""{ "questions": [ { "id": "s", "type": "scale", "text": "Escala", "min": {{min}}, "max": {{max}} } ] }""";

        var q = SurveyProvider.Parse(json)!.Questions.Single();

        Assert.Equal(expectedMin, q.Min);
        Assert.Equal(expectedMax, q.Max);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-10, 1)]
    [InlineData(250, 250)]
    [InlineData(5000, 1000)]
    public void Parse_TextMaxLength_IsLimited(int maxLength, int expected)
    {
        var json = $$"""{ "questions": [ { "id": "t", "type": "text", "text": "Texto", "maxLength": {{maxLength}} } ] }""";
        Assert.Equal(expected, SurveyProvider.Parse(json)!.Questions.Single().MaxLength);
    }

    [Theory]
    [InlineData(0, -3, 1, 0)]
    [InlineData(999, 1000, 120, 168)]
    [InlineData(30, 6, 30, 6)]
    public void Parse_PromptTiming_IsLimited(int minutes, int hours, int expectedMinutes, int expectedHours)
    {
        var json = $$"""
            { "minSessionMinutes": {{minutes}}, "minHoursBetweenPrompts": {{hours}},
              "questions": [ { "id": "r", "type": "rating", "text": "¿Qué tal?" } ] }
            """;

        var survey = SurveyProvider.Parse(json)!;

        Assert.Equal(expectedMinutes, survey.MinSessionMinutes);
        Assert.Equal(expectedHours, survey.MinHoursBetweenPrompts);
    }

    [Fact]
    public void Parse_ToleratesWrongValueTypes()
    {
        const string json = """
            {
              "id": 42,
              "title": ["no", "es", "texto"],
              "askAfterSession": "false",
              "minSessionMinutes": "15",
              "minHoursBetweenPrompts": 2.6,
              "discordWebhookUrl": { "url": "https://discord.com/api/webhooks/1/x" },
              "questions": [
                { "id": "r", "type": "rating", "text": "¿Qué tal?", "required": "yes" },
                { "id": "c", "type": "choice", "text": "¿Cuántos?", "required": "true", "options": ["Solo", 2, 3, true, null, 4] },
                { "id": "s", "type": "scale", "text": "Escala", "min": "0", "max": 1e30 },
                { "id": "t", "type": "text", "text": "Texto", "maxLength": "mucho" }
              ]
            }
            """;

        var survey = SurveyProvider.Parse(json);

        Assert.NotNull(survey);
        Assert.Equal("42", survey.Id);
        Assert.Equal("Tu opinión sobre Coop2D", survey.Title);
        Assert.False(survey.AskAfterSession);
        Assert.Equal(15, survey.MinSessionMinutes);
        Assert.Equal(3, survey.MinHoursBetweenPrompts);
        Assert.Null(survey.DiscordWebhookUrl);
        Assert.False(survey.Questions[0].Required);
        Assert.True(survey.Questions[1].Required);
        Assert.Equal(new[] { "Solo", "2", "3", "4" }, survey.Questions[1].Options);
        Assert.Equal((0, 10), (survey.Questions[2].Min, survey.Questions[2].Max));
        Assert.Equal(1000, survey.Questions[3].MaxLength);
    }

    [Fact]
    public void Parse_StripsControlAndBidiCharacters()
    {
        const string json = """
            {
              "title": "Tí\u0000tulo‮ falso\u0007",
              "intro": "Línea 1\r\nLínea 2\n\n\n\n\nLínea 3\u0001",
              "questions": [
                { "id": "r", "type": "rating", "text": "Pre\tgunta\ncon salto", "hint": "⁦Ayuda⁩" }
              ]
            }
            """;

        var survey = SurveyProvider.Parse(json)!;

        Assert.Equal("Título falso", survey.Title);
        Assert.Equal("Línea 1\nLínea 2\n\nLínea 3", survey.Intro);
        Assert.Equal("Pre gunta con salto", survey.Questions[0].Text);
        Assert.Equal("Ayuda", survey.Questions[0].Hint);
    }

    [Fact]
    public void Parse_HostileInputs_DoNotThrow()
    {
        // Muy grande.
        Assert.Null(SurveyProvider.Parse("{\"title\":\"" + new string('a', SurveyProvider.MaxJsonChars) + "\"}"));
        // Anidamiento absurdo.
        Assert.Null(SurveyProvider.Parse(new string('[', 10_000) + new string(']', 10_000)));
        Assert.Null(SurveyProvider.Parse("{\"questions\":[" + new string('[', 5_000) + new string(']', 5_000) + "]}"));
        // Muchísimas preguntas no válidas antes de una válida: se procesan sin problemas.
        var junk = string.Join(",", Enumerable.Repeat("{\"id\":\"\"}", 20_000));
        Assert.Null(SurveyProvider.Parse("{\"questions\":[" + junk + "]}"));
        // Escapes de surrogates sueltos: esa pregunta se pierde o se limpia, pero nunca tumba la encuesta entera.
        var survey = SurveyProvider.Parse("""
            { "questions": [
                { "id": "bad", "type": "rating", "text": "Hola \uD800 mundo" },
                { "id": "ok", "type": "rating", "text": "Bien 😀" } ] }
            """);
        Assert.NotNull(survey);
        Assert.Equal("Bien 😀", survey.Questions.Single(q => q.Id == "ok").Text);
        Assert.All(survey.Questions, q => Assert.DoesNotContain('\uD800', q.Text));
    }

    [Fact]
    public void FeedbackText_Clean_RemovesLoneSurrogatesAndTruncatesSafely()
    {
        Assert.Equal("abc😀", FeedbackText.Clean("a\uD800b\uDC00c😀", 100, multiline: false));
        Assert.Null(FeedbackText.Clean(" \u0000 \t ", 100, multiline: true));
        Assert.Null(FeedbackText.Clean(null, 100, multiline: true));

        // No parte un emoji (par de surrogates) al recortar.
        var truncated = FeedbackText.Truncate("ab😀cd", 4);
        Assert.Equal("ab…", truncated);
        Assert.Equal("abcd", FeedbackText.Truncate("abcd", 4));
        Assert.Equal("abc…", FeedbackText.Truncate("abcdef", 4));
    }

    // ───────────────────────────── Sanitize ─────────────────────────────

    [Fact]
    public void Sanitize_IsInPlace_AndFixesTitleIdAndNullLists()
    {
        var survey = new SurveyDefinition
        {
            Id = "  pre2 fun/test!  ",
            Title = "   ",
            Intro = "   ",
            Questions = null!,
        };

        var result = SurveyProvider.Sanitize(survey);

        Assert.Same(survey, result);
        Assert.Equal("pre2funtest", survey.Id);
        Assert.Equal("Tu opinión sobre Coop2D", survey.Title);
        Assert.Null(survey.Intro);
        Assert.NotNull(survey.Questions);
        Assert.Empty(survey.Questions);

        var empty = SurveyProvider.Sanitize(new SurveyDefinition { Id = "???" , Title = "X" });
        Assert.Equal("default", empty.Id);
    }

    [Fact]
    public void Sanitize_ClearsFieldsThatDoNotApplyToTheType()
    {
        var survey = new SurveyDefinition
        {
            Title = "T",
            Questions =
            {
                new SurveyQuestion
                {
                    Id = "r", Type = SurveyQuestionType.Rating, Text = "¿Qué tal?", Options = { "a", "b" },
                    Min = 3, Max = 9, MinLabel = "x", MaxLabel = "y", Placeholder = "p",
                },
                new SurveyQuestion { Id = "u", Type = (SurveyQuestionType)42, Text = "Tipo fuera del enum" },
            },
        };

        SurveyProvider.Sanitize(survey);

        var q = Assert.Single(survey.Questions);
        Assert.Empty(q.Options);
        Assert.Equal((1, 5), (q.Min, q.Max));
        Assert.Null(q.MinLabel);
        Assert.Null(q.MaxLabel);
        Assert.Null(q.Placeholder);
    }

    [Fact]
    public void Sanitize_Webhook_KeepsValidNormalizedAndDropsInvalid()
    {
        var ok = SurveyProvider.Sanitize(new SurveyDefinition { DiscordWebhookUrl = "  https://Discord.com/api/webhooks/123/tok-EN_9/  " });
        Assert.Equal("https://discord.com/api/webhooks/123/tok-EN_9", ok.DiscordWebhookUrl);

        var thread = SurveyProvider.Sanitize(new SurveyDefinition { DiscordWebhookUrl = "https://discord.com/api/webhooks/123/tok?thread_id=987&foo=bar#x" });
        Assert.Equal("https://discord.com/api/webhooks/123/tok?thread_id=987", thread.DiscordWebhookUrl);

        var bad = SurveyProvider.Sanitize(new SurveyDefinition { DiscordWebhookUrl = "https://evil.example/api/webhooks/123/tok" });
        Assert.Null(bad.DiscordWebhookUrl);
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/AbC-dEf_123xyz")]
    [InlineData("https://discordapp.com/api/webhooks/1/t")]
    [InlineData("https://ptb.discord.com/api/webhooks/1/t")]
    [InlineData("https://canary.discord.com/api/webhooks/1/t")]
    [InlineData("https://DISCORD.COM/api/webhooks/1/t")]
    [InlineData("  https://discord.com/api/webhooks/1/t  ")]
    [InlineData("https://discord.com/api/webhooks/1/t/")]
    [InlineData("https://discord.com:443/api/webhooks/1/t")]
    [InlineData("https://discord.com/api/webhooks/1/t?thread_id=5")]
    public void IsValidDiscordWebhook_AcceptsDiscordWebhooks(string url)
    {
        Assert.True(SurveyProvider.IsValidDiscordWebhook(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no es una url")]
    [InlineData("http://discord.com/api/webhooks/1/t")]
    [InlineData("ftp://discord.com/api/webhooks/1/t")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://discord.com.evil.example/api/webhooks/1/t")]
    [InlineData("https://evil.discord.com/api/webhooks/1/t")]
    [InlineData("https://evil.example/discord.com/api/webhooks/1/t")]
    [InlineData("https://discord.com@evil.example/api/webhooks/1/t")]
    [InlineData("https://user:pass@discord.com/api/webhooks/1/t")]
    [InlineData("https://discord.com:8443/api/webhooks/1/t")]
    [InlineData("https://discord.gg/api/webhooks/1/t")]
    [InlineData("https://discord.com/api/webhook/1/t")]
    [InlineData("https://discord.com/api/webhooks/abc/t")]
    [InlineData("https://discord.com/api/webhooks/1")]
    [InlineData("https://discord.com/api/webhooks/1/")]
    [InlineData("https://discord.com/api/webhooks/1/t/extra")]
    [InlineData("https://discord.com/api/v10/webhooks/1/t")]
    [InlineData("https://discord.com/api/webhooks/1/t%2F..%2Fx")]
    [InlineData("https://discord.com/api/webhooks/PEGA_AQUI_LA_URL_DE_TU_WEBHOOK")]
    [InlineData("https://discord.com/channels/1/2")]
    public void IsValidDiscordWebhook_RejectsEverythingElse(string? url)
    {
        Assert.False(SurveyProvider.IsValidDiscordWebhook(url));
    }

    [Fact]
    public void DescribeWebhook_NeverIncludesTheToken()
    {
        var text = SurveyProvider.DescribeWebhook(Webhook);
        Assert.DoesNotContain("AbC-dEf_123xyz", text);
        Assert.Contains("123456789012345678", text);
        Assert.Equal("ninguno", SurveyProvider.DescribeWebhook(null));
        Assert.Equal("no válido", SurveyProvider.DescribeWebhook("https://evil.example"));
    }

    // ───────────────────────────── GetSurveyAsync ─────────────────────────────

    private (SurveyProvider Provider, CapturingHttpHandler Handler, string AppData) CreateProvider(Func<CapturedRequest, HttpResponseMessage> responder)
    {
        var handler = new CapturingHttpHandler(responder);
        var github = new GitHubClient(HttpFactory.Create(handler), _temp.Combine("cache"));
        var appData = _temp.Combine("appdata");
        Directory.CreateDirectory(appData);
        return (new SurveyProvider(github, appData), handler, appData);
    }

    private static bool IsFeedbackUrl(CapturedRequest r) => r.Uri.ToString() == LauncherConfig.RemoteFeedbackConfigUrl;

    [Fact]
    public async Task GetSurveyAsync_LocalOverride_WinsWithoutTouchingTheNetwork()
    {
        var (provider, handler, appData) = CreateProvider(_ => throw new InvalidOperationException("No debería pedir nada"));
        File.WriteAllText(Path.Combine(appData, SurveyProvider.LocalOverrideFileName), ValidSurveyJson);

        var survey = await provider.GetSurveyAsync(CancellationToken.None);

        Assert.Equal("pre2-fun-test", survey.Id);
        Assert.Equal(Webhook, survey.DiscordWebhookUrl);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetSurveyAsync_Remote_IsUsedWhenThereIsNoLocalFile()
    {
        var (provider, handler, _) = CreateProvider(r => IsFeedbackUrl(r)
            ? CapturingHttpHandler.Text(ValidSurveyJson, "\"v1\"")
            : CapturingHttpHandler.Status(HttpStatusCode.NotFound));

        var survey = await provider.GetSurveyAsync(CancellationToken.None);

        Assert.Equal("pre2-fun-test", survey.Id);
        Assert.Equal(5, survey.Questions.Count);
        Assert.Equal(Webhook, survey.DiscordWebhookUrl);
        Assert.Single(handler.Requests, IsFeedbackUrl);
    }

    [Fact]
    public async Task GetSurveyAsync_InvalidLocal_FallsBackToRemote()
    {
        var (provider, _, appData) = CreateProvider(r => IsFeedbackUrl(r)
            ? CapturingHttpHandler.Text(ValidSurveyJson)
            : CapturingHttpHandler.Status(HttpStatusCode.NotFound));
        File.WriteAllText(Path.Combine(appData, SurveyProvider.LocalOverrideFileName), "{ esto no es json ");

        var survey = await provider.GetSurveyAsync(CancellationToken.None);

        Assert.Equal("pre2-fun-test", survey.Id);
    }

    [Fact]
    public async Task GetSurveyAsync_Remote404_ReturnsDefault()
    {
        var (provider, _, _) = CreateProvider(_ => CapturingHttpHandler.Status(HttpStatusCode.NotFound));

        var survey = await provider.GetSurveyAsync(CancellationToken.None);

        Assert.Equal("default", survey.Id);
        Assert.Equal(7, survey.Questions.Count);
        Assert.Null(survey.DiscordWebhookUrl);
    }

    [Fact]
    public async Task GetSurveyAsync_NetworkError_ReturnsDefault()
    {
        var (provider, _, _) = CreateProvider(_ => throw new HttpRequestException("sin red"));

        var survey = await provider.GetSurveyAsync(CancellationToken.None);

        Assert.Equal("default", survey.Id);
        Assert.Null(survey.DiscordWebhookUrl);
    }

    [Fact]
    public async Task GetSurveyAsync_RemoteGarbage_ReturnsDefault()
    {
        var (provider, _, _) = CreateProvider(_ => CapturingHttpHandler.Text("<html>Inicia sesión en la wifi</html>"));

        var survey = await provider.GetSurveyAsync(CancellationToken.None);

        Assert.Equal("default", survey.Id);
        Assert.Null(survey.DiscordWebhookUrl);
    }

    [Fact]
    public async Task GetSurveyAsync_RemoteWithOnlyWebhook_UsesDefaultQuestionsWithThatWebhook()
    {
        var json = $$"""{ "id": "solo-webhook", "discordWebhookUrl": "{{Webhook}}" }""";
        var (provider, _, _) = CreateProvider(r => IsFeedbackUrl(r) ? CapturingHttpHandler.Text(json) : CapturingHttpHandler.Status(HttpStatusCode.NotFound));

        var survey = await provider.GetSurveyAsync(CancellationToken.None);

        Assert.Equal("default", survey.Id);
        Assert.Equal(7, survey.Questions.Count);
        Assert.Equal(Webhook, survey.DiscordWebhookUrl);
    }

    [Fact]
    public async Task GetSurveyAsync_BrokenRemote_KeepsTheLastGoodCopy()
    {
        var broken = $$"""{ "id": "rota", "discordWebhookUrl": "{{Webhook}}", "questions": [ { "id": "x", "type": "slider", "text": "?" } ] }""";
        var serveBroken = false;
        var (provider, _, _) = CreateProvider(r => !IsFeedbackUrl(r)
            ? CapturingHttpHandler.Status(HttpStatusCode.NotFound)
            : CapturingHttpHandler.Text(serveBroken ? broken : ValidSurveyJson, serveBroken ? "\"v2\"" : "\"v1\""));

        var first = await provider.GetSurveyAsync(CancellationToken.None);
        serveBroken = true;
        var second = await provider.GetSurveyAsync(CancellationToken.None);

        Assert.Equal("pre2-fun-test", first.Id);
        Assert.Equal("pre2-fun-test", second.Id);
    }

    // Remoto con webhook válida pero ninguna pregunta válida ("label" en vez de "text").
    private const string RemoteWithWebhookButNoValidQuestions = $$"""
        {
          "id": "rota",
          "discordWebhookUrl": "{{Webhook}}",
          "questions": [
            { "id": "overall", "type": "rating", "label": "¿Qué tal?" },
            { "id": "again", "type": "choice", "label": "¿Otra?", "options": ["Sí", "No"] }
          ]
        }
        """;

    [Fact]
    public async Task GetSurveyAsync_RemoteWithWebhookButNoValidQuestions_AndNoCache_UsesDefaultWithThatWebhook()
    {
        var (provider, handler, _) = CreateProvider(r => IsFeedbackUrl(r)
            ? CapturingHttpHandler.Text(RemoteWithWebhookButNoValidQuestions, "\"v1\"")
            : CapturingHttpHandler.Status(HttpStatusCode.NotFound));

        var survey = await provider.GetSurveyAsync(CancellationToken.None);

        Assert.Equal("default", survey.Id);
        Assert.Equal(7, survey.Questions.Count);
        Assert.Equal(Webhook, survey.DiscordWebhookUrl);
        Assert.Single(handler.Requests, IsFeedbackUrl);
        // El archivo roto no se guarda en caché (no sustituiría a una copia buena).
        Assert.False(File.Exists(Path.Combine(_temp.Combine("cache"), SurveyProvider.RemoteCacheFileName)));
    }

    [Fact]
    public async Task GetSurveyAsync_RejectedRemoteWebhook_IsNotReusedByLaterCalls()
    {
        var mode = 0; // 0 = archivo roto con webhook, 1 = sin red, 2 = 404
        var (provider, _, _) = CreateProvider(r =>
        {
            if (!IsFeedbackUrl(r)) return CapturingHttpHandler.Status(HttpStatusCode.NotFound);
            return mode switch
            {
                0 => CapturingHttpHandler.Text(RemoteWithWebhookButNoValidQuestions),
                1 => throw new HttpRequestException("sin red"),
                _ => CapturingHttpHandler.Status(HttpStatusCode.NotFound),
            };
        });

        var broken = await provider.GetSurveyAsync(CancellationToken.None);
        mode = 1;
        var offline = await provider.GetSurveyAsync(CancellationToken.None);
        mode = 2;
        var deleted = await provider.GetSurveyAsync(CancellationToken.None);

        Assert.Equal(Webhook, broken.DiscordWebhookUrl);
        Assert.Equal("default", offline.Id);
        Assert.Null(offline.DiscordWebhookUrl);
        Assert.Equal("default", deleted.Id);
        Assert.Null(deleted.DiscordWebhookUrl);
    }

    [Fact]
    public async Task GetSurveyAsync_RemoteWithInvalidWebhookAndNoValidQuestions_ReturnsDefaultWithoutWebhook()
    {
        const string json = """{ "discordWebhookUrl": "https://evil.example/api/webhooks/1/x", "questions": [ { "id": "a", "type": "rating" } ] }""";
        var (provider, _, _) = CreateProvider(r => IsFeedbackUrl(r) ? CapturingHttpHandler.Text(json) : CapturingHttpHandler.Status(HttpStatusCode.NotFound));

        var survey = await provider.GetSurveyAsync(CancellationToken.None);

        Assert.Equal("default", survey.Id);
        Assert.Null(survey.DiscordWebhookUrl);
    }

    [Fact]
    public async Task GetSurveyAsync_InvalidLocalWebhook_WinsOverRejectedRemoteWebhook()
    {
        const string localWebhook = "https://discord.com/api/webhooks/987654321098765432/ZyX-wVu_987abc";
        var (provider, _, appData) = CreateProvider(r => IsFeedbackUrl(r)
            ? CapturingHttpHandler.Text(RemoteWithWebhookButNoValidQuestions)
            : CapturingHttpHandler.Status(HttpStatusCode.NotFound));
        File.WriteAllText(Path.Combine(appData, SurveyProvider.LocalOverrideFileName),
            $$"""{ "discordWebhookUrl": "{{localWebhook}}", "questions": [ { "id": "", "type": "rating", "text": "Sin id" } ] }""");

        var survey = await provider.GetSurveyAsync(CancellationToken.None);

        Assert.Equal("default", survey.Id);
        Assert.Equal(localWebhook, survey.DiscordWebhookUrl);
    }

    [Fact]
    public async Task GetSurveyAsync_ConcurrentCalls_JoinTheDownload_AndBothKeepTheRejectedRemoteWebhook()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CapturingHttpHandler(async (r, ct) =>
        {
            if (!IsFeedbackUrl(r)) return CapturingHttpHandler.Status(HttpStatusCode.NotFound);
            await gate.Task.WaitAsync(ct);
            return CapturingHttpHandler.Text(RemoteWithWebhookButNoValidQuestions);
        });
        var github = new GitHubClient(HttpFactory.Create(handler), _temp.Combine("cache"));
        var appData = _temp.Combine("appdata");
        Directory.CreateDirectory(appData);
        var provider = new SurveyProvider(github, appData);

        // Sin archivo local, ambas llegan a la descarga antes de soltarla: la segunda se une a la primera.
        var first = provider.GetSurveyAsync(CancellationToken.None);
        var second = provider.GetSurveyAsync(CancellationToken.None);
        gate.SetResult();
        var surveys = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(handler.Requests, IsFeedbackUrl);
        Assert.All(surveys, s =>
        {
            Assert.Equal("default", s.Id);
            Assert.Equal(Webhook, s.DiscordWebhookUrl);
        });
    }

    [Fact]
    public async Task GetSurveyAsync_InvalidLocalWithWebhook_AndNoRemote_KeepsThatWebhook()
    {
        var (provider, _, appData) = CreateProvider(_ => CapturingHttpHandler.Status(HttpStatusCode.NotFound));
        File.WriteAllText(Path.Combine(appData, SurveyProvider.LocalOverrideFileName),
            $$"""{ "discordWebhookUrl": "{{Webhook}}", "questions": [ { "id": "", "type": "rating", "text": "Sin id" } ] }""");

        var survey = await provider.GetSurveyAsync(CancellationToken.None);

        Assert.Equal("default", survey.Id);
        Assert.Equal(Webhook, survey.DiscordWebhookUrl);
    }

    [Fact]
    public async Task GetSurveyAsync_OversizedLocalFile_IsIgnored()
    {
        var (provider, _, appData) = CreateProvider(_ => CapturingHttpHandler.Status(HttpStatusCode.NotFound));
        var padding = new string(' ', SurveyProvider.MaxLocalFileBytes);
        File.WriteAllText(Path.Combine(appData, SurveyProvider.LocalOverrideFileName), ValidSurveyJson + padding);

        var survey = await provider.GetSurveyAsync(CancellationToken.None);

        Assert.Equal("default", survey.Id);
    }

    [Fact]
    public async Task GetSurveyAsync_Canceled_Throws()
    {
        var (provider, _, _) = CreateProvider(_ => CapturingHttpHandler.Status(HttpStatusCode.NotFound));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetSurveyAsync(cts.Token));
    }

    [Fact]
    public void Constructor_ValidatesArguments()
    {
        var github = new GitHubClient(HttpFactory.Create(new CapturingHttpHandler(_ => CapturingHttpHandler.Status(HttpStatusCode.NotFound))),
            _temp.Combine("cache"));
        Assert.Throws<ArgumentNullException>(() => new SurveyProvider(null!, _temp.Path));
        Assert.ThrowsAny<ArgumentException>(() => new SurveyProvider(github, " "));
    }
}
