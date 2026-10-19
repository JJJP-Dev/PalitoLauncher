using System.Net;
using System.Text;
using System.Text.Json;
using PalitoLauncher.Core;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Net;
using PalitoLauncher.Tests.TestHelpers.A;

namespace PalitoLauncher.Tests;

/// <summary>
/// JSON con la forma real de GET /repos/JJJP-Dev/Coop2D/milestones?state=all&amp;per_page=100 (2026-10-19): 14 milestones
/// abiertos con los títulos y el estilo de descripción del repo. Viene desordenado a propósito (GitHub ordena por due_on).
/// </summary>
internal static class Coop2DMilestonesJson
{
    public const string Pre1Description =
        "Pre-producción.\n\nPregunta: ¿pueden 2-4 jugadores conectarse por internet y moverse, con el host como autoridad?\n\n" +
        "Hecho cuando: 4 jugadores se mueven 10 minutos sin desincronizarse.";

    public const string RunsAndRoomsDescription =
        "Producción.\n\nObjetivo: partidas formadas por salas con enemigos, cofres y una salida. Cada partida dura unos 20 minutos.\n\n" +
        "Hecho cuando: se puede jugar una partida completa de principio a fin.";

    private static readonly (int Number, string Title, string Description, int Open, int Closed)[] Rows =
    {
        (5, "0.1 · Runs and rooms", RunsAndRoomsDescription, 0, 0),
        (1, "Pre 1 · Tech prototype", Pre1Description, 1, 26),
        (2, "Pre 2 · Gameplay prototype",
            "Pre-producción.\n\nPregunta: ¿es divertido combinar elementos con tus compañeros?\n\nHecho cuando: 3 de 4 jugadores quieren otra partida.", 11, 14),
        (3, "Pre 3 · First playable",
            "Pre-producción.\n\nPregunta: ¿se entiende el bucle de juego sin que nadie lo explique?\n\nHecho cuando: …", 24, 0),
        (4, "Pre 4 · Vertical slice",
            "Pre-producción.\n\nPregunta: ¿cómo se ve y se siente una partida con calidad final?\n\nHecho cuando: …", 0, 0),
        (6, "0.2 · Meta and hub", "Producción.\n\nObjetivo: un hub entre partidas con progreso permanente.\n\nHecho cuando: …", 0, 0),
        (7, "0.3 · Second map", "Producción.\n\nObjetivo: un segundo mapa con enemigos y jefe propios.\n\nHecho cuando: …", 0, 0),
        (8, "0.4 · Alpha", "Producción.\n\nObjetivo: todo el contenido principal jugable, aunque sea feo.\n\nHecho cuando: …", 0, 0),
        (9, "0.5 · Beta · CBT 1", "Producción.\n\nObjetivo: primera beta cerrada con amigos de amigos.\n\nHecho cuando: …", 0, 0),
        (10, "0.6 · CBT 1 fixes", "Producción.\n\nObjetivo: arreglar lo que salió en la CBT 1.\n\nHecho cuando: …", 0, 0),
        (11, "0.7 · Expansion and demo", "Producción.\n\nObjetivo: más contenido y una demo pública.\n\nHecho cuando: …", 0, 0),
        (12, "0.8 · Beta · CBT 2", "Producción.\n\nObjetivo: segunda beta cerrada, más grande.\n\nHecho cuando: …", 0, 0),
        (13, "0.9 · Private beta · Localization", "Producción.\n\nObjetivo: beta privada y traducciones.\n\nHecho cuando: …", 0, 0),
        (14, "1.0 · Launch", "Lanzamiento.\n\nObjetivo: publicar Coop2D.\n\nHecho cuando: está a la venta.", 0, 0),
    };

    /// <summary>Array JSON completo (UTF-8, con tildes, «·» y «…»), con todos los campos que manda GitHub.</summary>
    public static string Json(Func<int, string>? stateFor = null)
    {
        var items = Rows.Select(r => Element(r.Number, r.Title, r.Description, r.Open, r.Closed, stateFor?.Invoke(r.Number) ?? "open"));
        return "[\n" + string.Join(",\n", items) + "\n]";
    }

    public static string Element(int number, string title, string? description, int open, int closed, string state = "open",
        string? closedAt = null)
    {
        var desc = description is null ? "null" : JsonSerializer.Serialize(description, new JsonSerializerOptions
        {
            // Como GitHub: los caracteres no ASCII van tal cual, no como \uXXXX.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var titleJson = JsonSerializer.Serialize(title, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var closedAtJson = closedAt is null ? "null" : $"\"{closedAt}\"";
        return $$"""
              {
                "url": "https://api.github.com/repos/JJJP-Dev/Coop2D/milestones/{{number}}",
                "html_url": "https://github.com/JJJP-Dev/Coop2D/milestone/{{number}}",
                "labels_url": "https://api.github.com/repos/JJJP-Dev/Coop2D/milestones/{{number}}/labels",
                "id": {{13900000 + number}},
                "node_id": "MI_kwDOPalito{{number}}",
                "number": {{number}},
                "title": {{titleJson}},
                "description": {{desc}},
                "creator": { "login": "JJJP-Dev", "id": 123456, "type": "User", "site_admin": false },
                "open_issues": {{open}},
                "closed_issues": {{closed}},
                "state": "{{state}}",
                "created_at": "2026-09-01T10:00:00Z",
                "updated_at": "2026-10-18T21:13:45Z",
                "due_on": null,
                "closed_at": {{closedAtJson}}
              }
            """;
    }
}

public sealed class GitHubMilestonesTests : IDisposable
{
    private const string Owner = "JJJP-Dev";
    private const string Repo = "Coop2D";
    private const string MilestonesUrl = "https://api.github.com/repos/JJJP-Dev/Coop2D/milestones?state=all&per_page=100";

    private static readonly string MilestonesJson = Coop2DMilestonesJson.Json();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "palito-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // carpeta temporal: si queda bloqueada no pasa nada
        }
    }

    private GitHubClient CreateClient(FakeHttpHandler handler)
    {
        var client = new GitHubClient(HttpFactory.Create(handler), _dir);
        client.UtcNow = () => _clock.Now;
        return client;
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Ok(string? body = null, string? etag = "W/\"m1\"", int? remaining = 59)
        => _ => GitHubResponses.Json(body ?? MilestonesJson, etag: etag, remaining: remaining);

    private static Func<HttpRequestMessage, HttpResponseMessage> Throws(HttpRequestError error = HttpRequestError.ConnectionError)
        => _ => throw new HttpRequestException(error, "fallo simulado");

    // ───────────── Parseo ─────────────

    [Fact]
    public void ParseMilestones_RealShape_MapsAllFieldsAndSortsByNumber()
    {
        var milestones = GitHubClient.ParseMilestones(MilestonesJson);

        Assert.Equal(Enumerable.Range(1, 14), milestones.Select(m => m.Number));

        var pre1 = milestones[0];
        Assert.Equal("Pre 1 · Tech prototype", pre1.Title);
        Assert.Equal(Coop2DMilestonesJson.Pre1Description, pre1.Description);
        Assert.False(pre1.IsClosed);
        Assert.Equal(1, pre1.OpenIssues);
        Assert.Equal(26, pre1.ClosedIssues);
        Assert.Equal(27, pre1.TotalIssues);
        Assert.Equal(26.0 / 27, pre1.Progress!.Value, 6);
        Assert.Null(pre1.DueOn);
        Assert.Null(pre1.ClosedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 21, 13, 45, TimeSpan.Zero), pre1.UpdatedAt);
        Assert.Equal("https://github.com/JJJP-Dev/Coop2D/milestone/1", pre1.HtmlUrl);

        Assert.Equal("Pre 2 · Gameplay prototype", milestones[1].Title);
        Assert.Equal((11, 14), (milestones[1].OpenIssues, milestones[1].ClosedIssues));
        Assert.Equal((24, 0), (milestones[2].OpenIssues, milestones[2].ClosedIssues));
        Assert.Equal(0.0, milestones[2].Progress);
        Assert.Equal("0.1 · Runs and rooms", milestones[4].Title);
        Assert.StartsWith("Producción.\n\nObjetivo: partidas formadas por salas", milestones[4].Description);
        Assert.Null(milestones[4].Progress); // sin tareas
        Assert.Equal("1.0 · Launch", milestones[13].Title);
    }

    [Fact]
    public void ParseMilestones_ClosedState_AndDates()
    {
        var json = "[" + Coop2DMilestonesJson.Element(1, "Pre 1 · Tech prototype", null, 0, 27, state: "closed",
            closedAt: "2026-10-20T08:30:00Z").Replace("\"due_on\": null", "\"due_on\": \"2026-10-31T07:00:00Z\"") + "]";

        var m = Assert.Single(GitHubClient.ParseMilestones(json));

        Assert.True(m.IsClosed);
        Assert.Null(m.Description);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 30, 0, TimeSpan.Zero), m.ClosedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 7, 0, 0, TimeSpan.Zero), m.DueOn);
        Assert.Equal(1.0, m.Progress);
    }

    [Fact]
    public void ParseMilestones_ToleratesNullAndMissingFields()
    {
        const string json = """
            [ { "number": 3 }, 5, null, "x", [],
              { "number": "7", "title": null, "description": null, "state": null, "open_issues": "4", "closed_issues": -2,
                "due_on": "no es fecha", "closed_at": 12, "updated_at": null, "html_url": 3 },
              { "number": 99999999999, "title": "Grande", "state": "CLOSED", "open_issues": 99999999999 },
              {} ]
            """;

        var milestones = GitHubClient.ParseMilestones(json);

        Assert.Equal(new[] { 0, 3, 7, int.MaxValue }, milestones.Select(m => m.Number));
        var empty = milestones[0];
        Assert.Equal("", empty.Title);
        Assert.Null(empty.Description);
        Assert.False(empty.IsClosed);
        Assert.Equal(0, empty.TotalIssues);
        Assert.Equal("", empty.HtmlUrl);

        var seven = milestones[2];
        Assert.Equal("", seven.Title);
        Assert.False(seven.IsClosed);
        Assert.Equal(4, seven.OpenIssues);
        Assert.Equal(0, seven.ClosedIssues); // negativo → 0
        Assert.Null(seven.DueOn);
        Assert.Null(seven.ClosedAt);
        Assert.Null(seven.UpdatedAt);
        Assert.Equal("", seven.HtmlUrl);

        var big = milestones[3];
        Assert.True(big.IsClosed); // "CLOSED" sin distinguir mayúsculas
        Assert.Equal(int.MaxValue, big.OpenIssues);
    }

    [Fact]
    public void ParseMilestones_EmptyArrayAndBom()
    {
        Assert.Empty(GitHubClient.ParseMilestones("[]"));
        Assert.Single(GitHubClient.ParseMilestones("\uFEFF[{\"number\":1,\"title\":\"Pre 1\"}]"));
    }

    [Fact]
    public void ParseMilestones_SameNumber_KeepsGitHubOrder()
    {
        const string json = """[ { "number": 2, "title": "b" }, { "number": 1, "title": "a" }, { "number": 2, "title": "c" } ]""";
        Assert.Equal(new[] { "a", "b", "c" }, GitHubClient.ParseMilestones(json).Select(m => m.Title));
    }

    [Theory]
    [InlineData("[{ roto")]
    [InlineData("{\"message\": \"Not Found\"}")]
    [InlineData("<html></html>")]
    [InlineData("")]
    public void ParseMilestones_InvalidJson_Throws(string json)
    {
        Assert.ThrowsAny<JsonException>(() => GitHubClient.ParseMilestones(json));
    }

    // ───────────── Caché, frescura de 10 min, 304 ─────────────

    [Fact]
    public async Task GetMilestones_200_ThenFreshForTenMinutes_Then304()
    {
        var handler = FakeHttpHandler.Sequence(
            _ => GitHubResponses.Json(MilestonesJson, etag: "W/\"m1\"", remaining: 59),
            _ => GitHubResponses.NotModified(etag: "W/\"m1\"", remaining: 58));
        var client = CreateClient(handler);
        var t0 = _clock.Now;

        var first = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(ReleaseFetchSource.Network, first.Source);
        Assert.False(first.IsStale);
        Assert.Null(first.Warning);
        Assert.Equal(t0, first.FetchedAt);
        Assert.Equal(Enumerable.Range(1, 14), first.Milestones.Select(m => m.Number));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(MilestonesUrl, request.Url.ToString());
        Assert.Null(request.IfNoneMatch);
        Assert.Equal("application/vnd.github+json", request.Accept);
        Assert.Equal("2022-11-28", request.ApiVersion);
        Assert.StartsWith("PalitoLauncher/", request.UserAgent);
        Assert.Equal(59, client.RateLimitRemaining);

        // Mucho más que la ventana normal de la API (60 s) pero menos de 10 min: no se pregunta.
        _clock.Advance(TimeSpan.FromMinutes(9));
        var fresh = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.NotModifiedCache, fresh!.Source);
        Assert.Equal(t0, fresh.FetchedAt);
        Assert.Equal(14, fresh.Milestones.Count);
        Assert.Equal(1, handler.RequestCount);

        // Pasados 10 min: If-None-Match con el ETag débil tal cual → 304.
        _clock.Advance(TimeSpan.FromMinutes(1));
        var revalidated = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.NotModifiedCache, revalidated!.Source);
        Assert.Equal(_clock.Now, revalidated.FetchedAt);
        Assert.Equal(14, revalidated.Milestones.Count);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("W/\"m1\"", handler.Requests[1].IfNoneMatch);
        Assert.Equal(58, client.RateLimitRemaining);

        // El 304 renueva la frescura.
        _clock.Advance(TimeSpan.FromMinutes(5));
        await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetMilestones_BypassFreshness_AsksAnywayWithETag()
    {
        var handler = FakeHttpHandler.Sequence(Ok(), _ => GitHubResponses.NotModified(etag: "W/\"m1\""));
        var client = CreateClient(handler);

        await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);
        var result = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None, bypassFreshness: true);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("W/\"m1\"", handler.Requests[1].IfNoneMatch);
        Assert.Equal(ReleaseFetchSource.NotModifiedCache, result!.Source);
        Assert.Equal(14, result.Milestones.Count);
    }

    [Fact]
    public async Task GetMilestones_NewContent_ReplacesCache()
    {
        var closedPre1 = Coop2DMilestonesJson.Json(n => n == 1 ? "closed" : "open");
        var handler = FakeHttpHandler.Sequence(Ok(), Ok(closedPre1, etag: "W/\"m2\""), _ => GitHubResponses.NotModified());
        var client = CreateClient(handler);

        var first = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);
        Assert.False(first!.Milestones[0].IsClosed);

        _clock.Advance(TimeSpan.FromMinutes(11));
        var second = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.Network, second!.Source);
        Assert.True(second.Milestones[0].IsClosed);

        _clock.Advance(TimeSpan.FromMinutes(11));
        var third = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal("W/\"m2\"", handler.Requests[2].IfNoneMatch);
        Assert.True(third!.Milestones[0].IsClosed);
    }

    [Fact]
    public async Task GetMilestones_CacheSurvivesNewClientInstance()
    {
        var handler = FakeHttpHandler.Sequence(Ok(), _ => GitHubResponses.NotModified());
        await CreateClient(handler).GetMilestonesAsync(Owner, Repo, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(3));
        var fresh = await CreateClient(handler).GetMilestonesAsync(Owner, Repo, CancellationToken.None);

        Assert.Equal(ReleaseFetchSource.NotModifiedCache, fresh!.Source);
        Assert.Equal(14, fresh.Milestones.Count);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetMilestones_CacheIsSeparateFromReleases()
    {
        var handler = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/milestones", StringComparison.Ordinal)
            ? GitHubResponses.Json(MilestonesJson, remaining: 50)
            : GitHubResponses.Json("[]", remaining: 49));
        var client = CreateClient(handler);

        var releases = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        var milestones = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);

        Assert.Empty(releases.Releases);
        Assert.Equal(ReleaseFetchSource.Network, milestones!.Source);
        Assert.Equal(14, milestones.Milestones.Count);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(2, Directory.GetFiles(_dir, "api-*.json").Length);
    }

    [Fact]
    public async Task GetMilestones_EmptyRepo_ReturnsEmptyListNotNull()
    {
        var client = CreateClient(new FakeHttpHandler(Ok("[]")));

        var result = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Milestones);
        Assert.Equal(ReleaseFetchSource.Network, result.Source);
    }

    [Fact]
    public async Task GetMilestones_CorruptCache_IsDeletedAndIgnored()
    {
        var handler = FakeHttpHandler.Sequence(Ok(), Ok(etag: "W/\"m2\""));
        var client = CreateClient(handler);
        await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);
        foreach (var file in Directory.GetFiles(_dir, "api-*.json"))
            await File.WriteAllTextAsync(file, "{{{ esto no es json");

        var result = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);

        Assert.Equal(ReleaseFetchSource.Network, result!.Source);
        Assert.Equal(2, handler.RequestCount);
        Assert.Null(handler.Requests[1].IfNoneMatch);
    }

    [Fact]
    public async Task GetMilestones_CacheWithInvalidBody_IsDiscarded()
    {
        var handler = FakeHttpHandler.Sequence(Ok(), Throws());
        var client = CreateClient(handler);
        await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);
        var file = Assert.Single(Directory.GetFiles(_dir, "api-*.json"));
        var entry = JsonDocument.Parse(await File.ReadAllTextAsync(file)).RootElement;
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            url = entry.GetProperty("url").GetString(), status = 200, etag = "W/\"m1\"", fetchedAtUtc = _clock.Now, body = "{\"no\":\"array\"}",
        }));

        var result = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);

        Assert.Null(result); // sin caché válida y sin red
        Assert.Null(handler.Requests[1].IfNoneMatch);
        Assert.Empty(Directory.GetFiles(_dir, "api-*.json"));
    }

    [Fact]
    public async Task GetMilestones_304WithoutCache_RetriesOnceWithoutIfNoneMatch()
    {
        var handler = FakeHttpHandler.Sequence(_ => GitHubResponses.NotModified(), Ok());
        var client = CreateClient(handler);

        var result = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);

        Assert.Equal(ReleaseFetchSource.Network, result!.Source);
        Assert.Equal(2, handler.RequestCount);
        Assert.True(handler.Requests[1].NoCache);
    }

    // ───────────── Errores: nunca lanza ─────────────

    public static TheoryData<string, Func<HttpRequestMessage, HttpResponseMessage>> FailingResponses => new()
    {
        { "red", _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "fallo simulado") },
        { "dns", _ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "fallo simulado") },
        { "portal cautivo", _ => GitHubResponses.Html() },
        { "json roto", _ => GitHubResponses.Json("[{ roto") },
        { "objeto en vez de array", _ => GitHubResponses.Json("{\"message\":\"raro\"}") },
        { "500", _ => GitHubResponses.Status(HttpStatusCode.InternalServerError) },
        { "404", _ => GitHubResponses.Status(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}", remaining: 55) },
        { "otro 403", _ => GitHubResponses.Status(HttpStatusCode.Forbidden, remaining: 40) },
        { "rate limit", _ => GitHubResponses.Status(HttpStatusCode.Forbidden, remaining: 0,
            resetAt: new DateTimeOffset(2026, 10, 19, 12, 30, 0, TimeSpan.Zero), serverDate: new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero)) },
        { "429", _ => GitHubResponses.Status((HttpStatusCode)429) },
    };

    [Theory]
    [MemberData(nameof(FailingResponses))]
    public async Task GetMilestones_Failure_NoCache_ReturnsNullWithoutThrowing(string name,
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var client = CreateClient(new FakeHttpHandler(responder));

        var result = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);

        Assert.True(result is null, name);
        Assert.Empty(Directory.GetFiles(_dir, "api-*.json"));
    }

    [Theory]
    [MemberData(nameof(FailingResponses))]
    public async Task GetMilestones_Failure_WithCache_ReturnsOfflineCacheWithWarning(string name,
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = FakeHttpHandler.Sequence(Ok(), responder);
        var client = CreateClient(handler);
        var t0 = _clock.Now;
        await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(11));

        var result = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(ReleaseFetchSource.OfflineCache, result.Source);
        Assert.True(result.IsStale, name);
        Assert.Equal(t0, result.FetchedAt);
        Assert.Equal(14, result.Milestones.Count);
        Assert.EndsWith("Mostrando datos guardados.", result.Warning);

        // La caché buena se conserva.
        Assert.Single(Directory.GetFiles(_dir, "api-*.json"));
    }

    [Fact]
    public async Task GetMilestones_Warnings_AreSpanishAndSpecific()
    {
        var reset = _clock.Now.AddMinutes(30);
        var handler = FakeHttpHandler.Sequence(
            Ok(),
            Throws(),
            _ => GitHubResponses.Status(HttpStatusCode.NotFound),
            _ => GitHubResponses.Status(HttpStatusCode.Forbidden, remaining: 0, resetAt: reset, serverDate: _clock.Now));
        var client = CreateClient(handler);
        await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal("Sin conexión con GitHub. Mostrando datos guardados.",
            (await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None))!.Warning);

        Assert.Equal("No se encontró el repositorio JJJP-Dev/Coop2D en GitHub (¿es privado o cambió de nombre?). Mostrando datos guardados.",
            (await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None))!.Warning);

        var time = reset.ToLocalTime().ToString("HH:mm");
        Assert.Equal($"Límite de consultas a GitHub alcanzado; se restablece a las {time}. Mostrando datos guardados.",
            (await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None))!.Warning);
    }

    [Fact]
    public async Task GetMilestones_Timeout_ReturnsNull_NotCancellation()
    {
        var handler = new FakeHttpHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("inalcanzable");
        });
        var client = CreateClient(handler);
        client.RequestTimeout = TimeSpan.FromMilliseconds(200);

        Assert.Null(await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None));
    }

    [Fact]
    public async Task GetMilestones_HandlerThrowsUnexpected_ReturnsNull()
    {
        var client = CreateClient(new FakeHttpHandler(_ => throw new InvalidOperationException("bug simulado")));
        Assert.Null(await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None));
    }

    [Theory]
    [InlineData("", "Coop2D")]
    [InlineData("JJJP-Dev", " ")]
    [InlineData(null, "Coop2D")]
    [InlineData("JJJP-Dev", null)]
    public async Task GetMilestones_MissingOwnerOrRepo_ReturnsNullWithoutRequest(string? owner, string? repo)
    {
        var handler = new FakeHttpHandler(Ok());
        var client = CreateClient(handler);

        Assert.Null(await client.GetMilestonesAsync(owner!, repo!, CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetMilestones_TrimsOwnerAndRepo()
    {
        var handler = new FakeHttpHandler(Ok());
        await CreateClient(handler).GetMilestonesAsync(" JJJP-Dev ", " Coop2D ", CancellationToken.None);
        Assert.Equal(MilestonesUrl, handler.Requests[0].Url.ToString());
    }

    // ───────────── Rate limit compartido ─────────────

    [Fact]
    public async Task GetMilestones_WhileRateLimitedByReleases_DoesNotAsk()
    {
        var reset = _clock.Now.AddMinutes(30);
        var handler = FakeHttpHandler.Sequence(
            Ok(),
            _ => GitHubResponses.Status(HttpStatusCode.Forbidden, remaining: 0, resetAt: reset, serverDate: _clock.Now),
            _ => GitHubResponses.NotModified(etag: "W/\"m1\"", remaining: 59));
        var client = CreateClient(handler);
        await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<GitHubRateLimitException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));
        Assert.True(client.IsRateLimited);
        Assert.Equal(2, handler.RequestCount);

        // Con bypass (botón «Actualizar») y la caché ya caducada: caché con aviso, sin petición.
        _clock.Advance(TimeSpan.FromMinutes(10));
        var result = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None, bypassFreshness: true);
        Assert.Equal(ReleaseFetchSource.OfflineCache, result!.Source);
        Assert.StartsWith("Límite de consultas a GitHub alcanzado", result.Warning);
        Assert.Equal(2, handler.RequestCount);

        // Sin caché (otro repo): null, también sin petición.
        Assert.Null(await client.GetMilestonesAsync(Owner, "OtroRepo", CancellationToken.None));
        Assert.Equal(2, handler.RequestCount);

        // Pasado el reset se vuelve a preguntar.
        _clock.Advance(TimeSpan.FromMinutes(20));
        var after = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.NotModifiedCache, after!.Source);
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal("W/\"m1\"", handler.Requests[2].IfNoneMatch);
        Assert.Equal(59, client.RateLimitRemaining);
    }

    [Fact]
    public async Task GetMilestones_LastAllowedRequest_BlocksReleasesUntilReset()
    {
        var reset = _clock.Now.AddMinutes(15);
        var handler = new FakeHttpHandler(_ => GitHubResponses.Json(MilestonesJson, remaining: 0, resetAt: reset, serverDate: _clock.Now));
        var client = CreateClient(handler);

        var result = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);

        Assert.Equal(ReleaseFetchSource.Network, result!.Source);
        Assert.True(client.IsRateLimited);
        Assert.Equal(0, client.RateLimitRemaining);
        await Assert.ThrowsAsync<GitHubRateLimitException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
        Assert.True(File.Exists(Path.Combine(_dir, "ratelimit.json")));
    }

    // ───────────── Concurrencia y cancelación ─────────────

    [Fact]
    public async Task GetMilestones_ConcurrentCalls_AreCoalesced()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpHandler(async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return GitHubResponses.Json(MilestonesJson, remaining: 50);
        });
        var client = CreateClient(handler);

        var first = client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);
        await WaitUntilAsync(() => handler.RequestCount == 1);
        var second = client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);
        gate.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, handler.RequestCount);
        Assert.All(results, r => Assert.Equal(14, r!.Milestones.Count));
    }

    [Fact]
    public async Task GetMilestones_PreCancelledToken_ThrowsWithoutRequest()
    {
        var handler = new FakeHttpHandler(Ok());
        var client = CreateClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetMilestonesAsync(Owner, Repo, new CancellationToken(true)));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetMilestones_CancelWhileWaiting_Throws_RequestStillCompletesAndCaches()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpHandler(async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return GitHubResponses.Json(MilestonesJson);
        });
        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();

        var pending = client.GetMilestonesAsync(Owner, Repo, cts.Token);
        await WaitUntilAsync(() => handler.RequestCount == 1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        gate.SetResult();
        var result = await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(14, result!.Milestones.Count);
        Assert.Equal(1, handler.RequestCount);
    }

    // ───────────── Archivos de texto (launcher/feedback.json) ─────────────

    private const string FeedbackUrl = "https://raw.githubusercontent.com/JJJP-Dev/Coop2D/main/launcher/feedback.json";
    private const string CacheName = "feedback.json";

    private const string SurveyJson = """
        {
          "id": "pre2-diversion",
          "title": "Tu opinión sobre el Prototipo 2",
          "questions": [
            { "id": "fun", "type": "rating", "text": "¿Fue divertido combinar elementos con tus compañeros?", "required": true },
            { "id": "detonate", "type": "choice", "text": "¿Entendiste cuándo detonar una reacción?", "options": ["Sí", "A veces", "No"] }
          ]
        }
        """;

    private const string SurveyJsonV2 = """{ "id": "pre2-diversion-v2", "title": "Versión 2 · ¡gracias! 🎮", "questions": [] }""";

    /// <summary>Validación tipo SurveyProvider: JSON con un objeto que tiene "id".</summary>
    private static bool IsSurvey(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Text(string text, string? etag = "\"t1\"",
        string contentType = "text/plain; charset=utf-8")
        => _ => GitHubResponses.Bytes(Encoding.UTF8.GetBytes(text), contentType, etag);

    private static Func<HttpRequestMessage, HttpResponseMessage> RawStatus(HttpStatusCode status, string body = "404: Not Found")
        => _ => new HttpResponseMessage(status) { Content = new StringContent(body) };

    private string CachePath => Path.Combine(_dir, CacheName);
    private string MetaPath => CachePath + ".meta.json";

    [Fact]
    public async Task GetCachedText_200_ReturnsTextAndCaches_Then304UsesCache()
    {
        var handler = FakeHttpHandler.Sequence(Text(SurveyJson), _ => GitHubResponses.NotModified());
        var client = CreateClient(handler);

        var text = await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);

        Assert.Equal(SurveyJson, text);
        var request = handler.Requests[0];
        Assert.Equal(FeedbackUrl, request.Url.ToString());
        Assert.Null(request.IfNoneMatch);
        Assert.Null(request.ApiVersion); // no es la API: no gasta cupo
        Assert.Contains("text/plain", request.Accept);
        Assert.StartsWith("PalitoLauncher/", request.UserAgent);
        Assert.Equal(SurveyJson, await File.ReadAllTextAsync(CachePath, Encoding.UTF8));
        Assert.True(File.Exists(MetaPath));

        var again = await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);

        Assert.Equal(SurveyJson, again);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("\"t1\"", handler.Requests[1].IfNoneMatch);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task GetCachedText_CacheSurvivesNewClientInstance()
    {
        var handler = FakeHttpHandler.Sequence(Text(SurveyJson), _ => GitHubResponses.NotModified());
        await CreateClient(handler).GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);

        var text = await CreateClient(handler).GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);

        Assert.Equal(SurveyJson, text);
        Assert.Equal("\"t1\"", handler.Requests[1].IfNoneMatch);
    }

    [Fact]
    public async Task GetCachedText_NewContent_ReplacesCacheAndETag()
    {
        var handler = FakeHttpHandler.Sequence(Text(SurveyJson), Text(SurveyJsonV2, "\"t2\""), _ => GitHubResponses.NotModified());
        var client = CreateClient(handler);

        await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);
        var second = await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);
        var third = await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);

        Assert.Equal(SurveyJsonV2, second);
        Assert.Equal(SurveyJsonV2, third); // emoji y tildes intactos (UTF-8)
        Assert.Equal("\"t2\"", handler.Requests[2].IfNoneMatch);
    }

    [Fact]
    public async Task GetCachedText_InvalidDownload_KeepsPreviousCache()
    {
        var handler = FakeHttpHandler.Sequence(
            Text(SurveyJson),
            Text("{ \"sin\": \"id\" }", "\"t2\""),
            _ => GitHubResponses.Html(),
            _ => GitHubResponses.NotModified());
        var client = CreateClient(handler);
        await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);

        Assert.Equal(SurveyJson, await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None));
        Assert.Equal(SurveyJson, await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None));
        Assert.Equal(SurveyJson, await File.ReadAllTextAsync(CachePath, Encoding.UTF8));

        // El ETag guardado sigue siendo el de la versión buena.
        await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);
        Assert.Equal("\"t1\"", handler.Requests[3].IfNoneMatch);
    }

    [Fact]
    public async Task GetCachedText_InvalidDownload_NoCache_ReturnsNullAndWritesNothing()
    {
        var client = CreateClient(new FakeHttpHandler(Text("esto no es json")));

        Assert.Null(await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None));
        Assert.False(File.Exists(CachePath));
        Assert.False(File.Exists(MetaPath));
    }

    [Fact]
    public async Task GetCachedText_ValidateThrows_CountsAsInvalid()
    {
        var handler = FakeHttpHandler.Sequence(Text(SurveyJson), Text(SurveyJsonV2, "\"t2\""));
        var client = CreateClient(handler);
        await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);

        // La caché pasa la validación, pero lo descargado hace lanzar al validador: se conserva la caché.
        var result = await client.GetCachedTextFileAsync(FeedbackUrl, CacheName,
            t => t.Contains("v2", StringComparison.Ordinal) ? throw new InvalidOperationException("validador roto") : true,
            CancellationToken.None);

        Assert.Equal(SurveyJson, result);
    }

    [Fact]
    public async Task GetCachedText_CachedTextNoLongerValid_IsDeletedAndFetchedWithoutETag()
    {
        var handler = FakeHttpHandler.Sequence(Text(SurveyJson), Text(SurveyJsonV2, "\"t2\""));
        var client = CreateClient(handler);
        await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);

        // Un launcher más nuevo exige otra cosa: la caché vieja ya no vale y no se manda su ETag (un 304 no serviría).
        var result = await client.GetCachedTextFileAsync(FeedbackUrl, CacheName,
            t => t.Contains("v2", StringComparison.Ordinal), CancellationToken.None);

        Assert.Equal(SurveyJsonV2, result);
        Assert.Null(handler.Requests[1].IfNoneMatch);
        Assert.Equal(SurveyJsonV2, await File.ReadAllTextAsync(CachePath, Encoding.UTF8));
    }

    [Fact]
    public async Task GetCachedText_CorruptCacheBytes_AreIgnored()
    {
        var handler = FakeHttpHandler.Sequence(Text(SurveyJson), Text(SurveyJson, "\"t1\""));
        var client = CreateClient(handler);
        await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);
        await File.WriteAllBytesAsync(CachePath, new byte[] { 0xC3, 0x28, 0xFF, 0xFE }); // UTF-8 inválido

        var result = await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);

        Assert.Equal(SurveyJson, result);
        Assert.Null(handler.Requests[1].IfNoneMatch);
    }

    [Fact]
    public async Task GetCachedText_404_DeletesCacheAndMeta()
    {
        var handler = FakeHttpHandler.Sequence(Text(SurveyJson), RawStatus(HttpStatusCode.NotFound));
        var client = CreateClient(handler);
        await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);
        Assert.True(File.Exists(CachePath));

        Assert.Null(await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None));
        Assert.False(File.Exists(CachePath));
        Assert.False(File.Exists(MetaPath));
    }

    [Fact]
    public async Task GetCachedText_404_NoCache_ReturnsNull()
    {
        var client = CreateClient(new FakeHttpHandler(RawStatus(HttpStatusCode.NotFound)));
        Assert.Null(await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None));
    }

    [Theory]
    [InlineData("red")]
    [InlineData("500")]
    [InlineData("403")]
    [InlineData("timeout")]
    public async Task GetCachedText_Errors_ReturnCachedTextOrNull(string kind)
    {
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> failing = kind switch
        {
            "red" => (_, _) => throw new HttpRequestException(HttpRequestError.NameResolutionError, "sin red"),
            "500" => (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("") }),
            "403" => (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("") }),
            _ => async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("inalcanzable");
            },
        };

        var noCache = CreateClient(new FakeHttpHandler(failing));
        noCache.RequestTimeout = TimeSpan.FromMilliseconds(200);
        Assert.Null(await noCache.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None));

        var calls = 0;
        var handler = new FakeHttpHandler((req, ct) => Interlocked.Increment(ref calls) == 1
            ? Task.FromResult(Text(SurveyJson)(req))
            : failing(req, ct));
        var client = CreateClient(handler);
        client.RequestTimeout = TimeSpan.FromMilliseconds(200);
        await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);

        Assert.Equal(SurveyJson, await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None));
        Assert.True(File.Exists(CachePath));
    }

    [Fact]
    public async Task GetCachedText_TooLarge_IsRejected_KeepingCache()
    {
        var big = "{\"id\":\"x\",\"pad\":\"" + new string('a', 256 * 1024) + "\"}";
        var handler = FakeHttpHandler.Sequence(Text(SurveyJson), Text(big, "\"big\""));
        var client = CreateClient(handler);
        await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);

        Assert.Equal(SurveyJson, await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None));
        Assert.Null(await CreateClient(new FakeHttpHandler(Text(big))).GetCachedTextFileAsync(FeedbackUrl, "otro.json", IsSurvey, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_dir, "otro.json")));
    }

    [Fact]
    public async Task GetCachedText_InvalidUtf8_IsRejected()
    {
        var bytes = new byte[] { (byte)'{', (byte)'"', (byte)'i', (byte)'d', (byte)'"', (byte)':', (byte)'"', 0xC3, 0x28, (byte)'"', (byte)'}' };
        var client = CreateClient(new FakeHttpHandler(_ => GitHubResponses.Bytes(bytes, "text/plain")));

        Assert.Null(await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, _ => true, CancellationToken.None));
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task GetCachedText_Utf8Bom_IsStripped()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(SurveyJson)).ToArray();
        var handler = FakeHttpHandler.Sequence(_ => GitHubResponses.Bytes(bytes, "text/plain", "\"bom\""), _ => GitHubResponses.NotModified());
        var client = CreateClient(handler);

        Assert.Equal(SurveyJson, await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None));
        Assert.Equal(SurveyJson, await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None));
    }

    [Fact]
    public async Task GetCachedText_304WithoutCache_RetriesWithoutValidators()
    {
        var handler = FakeHttpHandler.Sequence(Text(SurveyJson), _ => GitHubResponses.NotModified(), Text(SurveyJsonV2, "\"t2\""));
        var client = CreateClient(handler);
        await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);
        File.Delete(CachePath);

        var result = await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);

        Assert.Equal(SurveyJsonV2, result);
        Assert.Equal(3, handler.RequestCount);
        Assert.Null(handler.Requests[1].IfNoneMatch);
        Assert.True(handler.Requests[2].NoCache);
    }

    [Fact]
    public async Task GetCachedText_Always304_ReturnsNullWithoutLooping()
    {
        var handler = new FakeHttpHandler(_ => GitHubResponses.NotModified());
        var client = CreateClient(handler);

        Assert.Null(await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetCachedText_MetaForAnotherUrl_SendsNoValidators()
    {
        const string otherUrl = "https://raw.githubusercontent.com/JJJP-Dev/Coop2D/dev/launcher/feedback.json";
        var handler = FakeHttpHandler.Sequence(Text(SurveyJson), _ => GitHubResponses.NotModified(), Text(SurveyJsonV2, "\"t2\""));
        var client = CreateClient(handler);
        await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);

        // Mismo archivo de caché con otra URL: no se manda el ETag ajeno; un 304 así no se cree y se repite sin validadores.
        var result = await client.GetCachedTextFileAsync(otherUrl, CacheName, IsSurvey, CancellationToken.None);

        Assert.Equal(SurveyJsonV2, result);
        Assert.Null(handler.Requests[1].IfNoneMatch);
        Assert.True(handler.Requests[2].NoCache);
    }

    [Theory]
    [InlineData("..\\evil.json")]
    [InlineData("sub/evil.json")]
    [InlineData("C:\\evil.json")]
    [InlineData("")]
    [InlineData("..")]
    public async Task GetCachedText_InvalidFileName_ReturnsNullWithoutRequest(string name)
    {
        var handler = new FakeHttpHandler(Text(SurveyJson));
        var client = CreateClient(handler);

        Assert.Null(await client.GetCachedTextFileAsync(FeedbackUrl, name, IsSurvey, CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("no es una url")]
    [InlineData("ftp://example.com/feedback.json")]
    [InlineData("file:///C:/feedback.json")]
    [InlineData("")]
    public async Task GetCachedText_InvalidUrl_ReturnsNullWithoutRequest(string url)
    {
        var handler = new FakeHttpHandler(Text(SurveyJson));
        var client = CreateClient(handler);

        Assert.Null(await client.GetCachedTextFileAsync(url, CacheName, IsSurvey, CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetCachedText_NullValidator_ReturnsNullWithoutRequest()
    {
        var handler = new FakeHttpHandler(Text(SurveyJson));
        var client = CreateClient(handler);

        Assert.Null(await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, null!, CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetCachedText_DoesNotAffectApiRateLimit()
    {
        var response = GitHubResponses.Bytes(Encoding.UTF8.GetBytes(SurveyJson), "text/plain");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", _clock.Now.AddHours(1).ToUnixTimeSeconds().ToString());
        var client = CreateClient(new FakeHttpHandler(_ => response));

        await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);

        Assert.False(client.IsRateLimited);
        Assert.Null(client.RateLimitRemaining);
    }

    [Fact]
    public async Task GetCachedText_WorksWhileApiIsRateLimited()
    {
        var handler = new FakeHttpHandler(req => req.RequestUri!.Host == "api.github.com"
            ? GitHubResponses.Status(HttpStatusCode.Forbidden, remaining: 0, resetAt: _clock.Now.AddMinutes(30), serverDate: _clock.Now)
            : Text(SurveyJson)(req));
        var client = CreateClient(handler);
        Assert.Null(await client.GetMilestonesAsync(Owner, Repo, CancellationToken.None));
        Assert.True(client.IsRateLimited);

        Assert.Equal(SurveyJson, await client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None));
    }

    [Fact]
    public async Task GetCachedText_Cancelled_Throws()
    {
        var handler = new FakeHttpHandler(Text(SurveyJson));
        var client = CreateClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, new CancellationToken(true)));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetCachedText_ConcurrentCalls_AreCoalesced()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpHandler(async (req, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return Text(SurveyJson)(req);
        });
        var client = CreateClient(handler);

        var first = client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);
        await WaitUntilAsync(() => handler.RequestCount == 1);
        var second = client.GetCachedTextFileAsync(FeedbackUrl, CacheName, IsSurvey, CancellationToken.None);
        gate.SetResult();

        Assert.All(await Task.WhenAll(first, second), t => Assert.Equal(SurveyJson, t));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetCachedText_DoesNotTouchImageCacheOfSameClient()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var handler = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath.EndsWith(".png", StringComparison.Ordinal)
            ? GitHubResponses.Bytes(png, "image/png", "\"img\"")
            : Text(SurveyJson)(req));
        var client = CreateClient(handler);

        var image = await client.GetCachedFileAsync(LauncherConfig.RemoteBackgroundUrl, "background.png", CancellationToken.None);
        var text = await client.GetCachedTextFileAsync(LauncherConfig.RemoteFeedbackConfigUrl, CacheName, IsSurvey, CancellationToken.None);

        Assert.Equal(png, await File.ReadAllBytesAsync(image!));
        Assert.Equal(SurveyJson, text);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("La condición no se cumplió a tiempo.");
            await Task.Delay(10);
        }
    }
}
