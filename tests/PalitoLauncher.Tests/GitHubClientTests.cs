using System.Net;
using System.Text.Json;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Net;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Tests.TestHelpers.A;

namespace PalitoLauncher.Tests;

public sealed class GitHubClientTests : IDisposable
{
    private const string Owner = "JJJP-Dev";
    private const string Repo = "Coop2D";

    private static readonly string Sha2 = "EFE4E9BA".PadRight(64, 'C');
    private static readonly string Sha1 = "26abfdf6".PadRight(64, '0');

    private static readonly string ReleasesJson = $$"""
        [
          {
            "id": 2002,
            "tag_name": "0.0.2-Prototype-2",
            "name": "Prototype 2 Test",
            "body": "Notas **2**",
            "draft": false,
            "prerelease": false,
            "published_at": "2026-10-12T18:22:31Z",
            "html_url": "https://github.com/JJJP-Dev/Coop2D/releases/tag/0.0.2-Prototype-2",
            "assets": [
              {
                "id": 302,
                "name": "2D_0.0.2_Protype2.zip",
                "size": 70782621,
                "state": "uploaded",
                "browser_download_url": "https://github.com/JJJP-Dev/Coop2D/releases/download/0.0.2-Prototype-2/2D_0.0.2_Protype2.zip",
                "content_type": "application/x-zip-compressed",
                "updated_at": "2026-10-12T18:20:00Z",
                "digest": "sha256:{{Sha2}}"
              }
            ]
          },
          {
            "id": 2001,
            "tag_name": "0.0.1-Protype1",
            "name": "Prototype 1",
            "body": null,
            "draft": false,
            "prerelease": false,
            "published_at": "2026-09-01T10:00:00Z",
            "html_url": "https://github.com/JJJP-Dev/Coop2D/releases/tag/0.0.1-Protype1",
            "assets": [
              {
                "id": 301,
                "name": "2D_0.0.1_Protype1.zip",
                "size": 39841913,
                "browser_download_url": "https://github.com/JJJP-Dev/Coop2D/releases/download/0.0.1-Protype1/2D_0.0.1_Protype1.zip",
                "content_type": "application/zip",
                "updated_at": "2026-09-01T09:59:00Z",
                "digest": "sha256:{{Sha1}}"
              }
            ]
          }
        ]
        """;

    private const string LatestJson = """
        {
          "id": 9001,
          "tag_name": "v1.1.0",
          "name": "Palito Launcher 1.1.0",
          "draft": false,
          "prerelease": false,
          "published_at": "2026-10-18T10:00:00Z",
          "html_url": "https://github.com/JJJP-Dev/PalitoLauncher/releases/tag/v1.1.0",
          "assets": [ { "id": 77, "name": "PalitoLauncher.exe", "size": 123456, "browser_download_url": "https://example.invalid/PalitoLauncher.exe" } ]
        }
        """;

    private static readonly byte[] PngBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5 };
    private static readonly byte[] JpegBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 9, 8, 7 };

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

    private static Func<HttpRequestMessage, HttpResponseMessage> Ok(string body = "", string? etag = null, int? remaining = 59)
        => _ => GitHubResponses.Json(body.Length == 0 ? ReleasesJson : body, etag: etag, remaining: remaining);

    private static Func<HttpRequestMessage, HttpResponseMessage> Throws(HttpRequestError error = HttpRequestError.ConnectionError)
        => _ => throw new HttpRequestException(error, "fallo simulado");

    // ───────────── Parseo ─────────────

    [Fact]
    public void ParseReleases_MapsAllFields()
    {
        var releases = GitHubClient.ParseReleases(ReleasesJson);

        Assert.Equal(2, releases.Count);
        var r = releases[0];
        Assert.Equal(2002, r.Id);
        Assert.Equal("0.0.2-Prototype-2", r.TagName);
        Assert.Equal("Prototype 2 Test", r.Name);
        Assert.Equal("Notas **2**", r.Body);
        Assert.False(r.IsDraft);
        Assert.False(r.IsPrerelease);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 18, 22, 31, TimeSpan.Zero), r.PublishedAt);
        Assert.Equal("https://github.com/JJJP-Dev/Coop2D/releases/tag/0.0.2-Prototype-2", r.HtmlUrl);

        var a = Assert.Single(r.Assets);
        Assert.Equal(302, a.Id);
        Assert.Equal("2D_0.0.2_Protype2.zip", a.Name);
        Assert.Equal(70_782_621, a.Size);
        Assert.Equal("https://github.com/JJJP-Dev/Coop2D/releases/download/0.0.2-Prototype-2/2D_0.0.2_Protype2.zip", a.DownloadUrl);
        Assert.Equal("application/x-zip-compressed", a.ContentType);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 18, 20, 0, TimeSpan.Zero), a.UpdatedAt);
        Assert.Equal(Sha2.ToLowerInvariant(), a.Sha256);

        Assert.Null(releases[1].Body);
        Assert.Equal(Sha1, releases[1].Assets[0].Sha256);
    }

    [Fact]
    public void ParseReleases_DiscardsAssetsNotUploadedOrEmpty()
    {
        const string json = """
            [ { "id": 1, "tag_name": "t", "assets": [
                { "id": 1, "name": "ok.zip", "size": 10, "state": "uploaded" },
                { "id": 2, "name": "sin-estado.zip", "size": 10 },
                { "id": 3, "name": "subiendo.zip", "size": 10, "state": "starter" },
                { "id": 4, "name": "abierto.zip", "size": 10, "state": "open" },
                { "id": 5, "name": "vacio.zip", "size": 0, "state": "uploaded" },
                { "id": 6, "name": "sin-tamano.zip" },
                "basura", null
            ] } ]
            """;

        var assets = GitHubClient.ParseReleases(json)[0].Assets;

        Assert.Equal(new[] { "ok.zip", "sin-estado.zip" }, assets.Select(a => a.Name));
    }

    [Fact]
    public void ParseReleases_ToleratesNullAndMissingFields()
    {
        const string json = """
            [ { "id": 1 }, 5, null, "x",
              { "id": "7", "tag_name": null, "name": null, "draft": null, "prerelease": "yes", "published_at": "no es fecha",
                "assets": null, "html_url": 3 } ]
            """;

        var releases = GitHubClient.ParseReleases(json);

        Assert.Equal(2, releases.Count);
        Assert.Equal(1, releases[0].Id);
        Assert.Equal("", releases[0].TagName);
        Assert.Empty(releases[0].Assets);
        Assert.Equal(7, releases[1].Id);
        Assert.Equal("", releases[1].TagName);
        Assert.Null(releases[1].Name);
        Assert.False(releases[1].IsDraft);
        Assert.False(releases[1].IsPrerelease);
        Assert.Null(releases[1].PublishedAt);
        Assert.Equal("", releases[1].HtmlUrl);
    }

    [Theory]
    [InlineData("sha256:abc")]
    [InlineData("sha512:EFE4E9BACCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC")]
    [InlineData("sha256:ZZZZ9BACCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC")]
    [InlineData("")]
    public void ParseReleases_InvalidDigest_IsNull(string digest)
    {
        var json = $$"""[ { "id": 1, "assets": [ { "id": 1, "name": "a.zip", "size": 5, "digest": "{{digest}}" } ] } ]""";
        Assert.Null(GitHubClient.ParseReleases(json)[0].Assets[0].Sha256);
    }

    [Theory]
    [InlineData("[{ roto")]
    [InlineData("{\"message\": \"no es un array\"}")]
    [InlineData("<html></html>")]
    public void ParseReleases_InvalidJson_Throws(string json)
    {
        Assert.ThrowsAny<JsonException>(() => GitHubClient.ParseReleases(json));
    }

    [Fact]
    public void ParseRelease_SingleObject()
    {
        var r = GitHubClient.ParseRelease(LatestJson);
        Assert.Equal("v1.1.0", r.TagName);
        Assert.Equal("PalitoLauncher.exe", Assert.Single(r.Assets).Name);
        Assert.ThrowsAny<JsonException>(() => GitHubClient.ParseRelease("[]"));
    }

    // ───────────── Caché, frescura, 304 ─────────────

    [Fact]
    public async Task GetReleases_200_ThenFreshCache_Then304()
    {
        var handler = FakeHttpHandler.Sequence(
            _ => GitHubResponses.Json(ReleasesJson, etag: "W/\"abc\"", remaining: 59),
            _ => GitHubResponses.NotModified(etag: "W/\"abc\"", remaining: 58));
        var client = CreateClient(handler);
        var t0 = _clock.Now;

        var first = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.Network, first.Source);
        Assert.Equal(2, first.Releases.Count);
        Assert.Equal(t0, first.FetchedAt);
        Assert.Null(first.Warning);
        Assert.Equal(1, handler.RequestCount);

        var request = handler.Requests[0];
        Assert.Equal("https://api.github.com/repos/JJJP-Dev/Coop2D/releases?per_page=100", request.Url.ToString());
        Assert.Null(request.IfNoneMatch);
        Assert.Equal("application/vnd.github+json", request.Accept);
        Assert.Equal("2022-11-28", request.ApiVersion);
        Assert.StartsWith("PalitoLauncher/", request.UserAgent);

        // Dentro de la ventana de frescura: no se pregunta.
        _clock.Advance(TimeSpan.FromSeconds(30));
        var fresh = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.NotModifiedCache, fresh.Source);
        Assert.Equal(2, fresh.Releases.Count);
        Assert.Equal(t0, fresh.FetchedAt);
        Assert.Equal(1, handler.RequestCount);

        // Fuera de la ventana: If-None-Match con el ETag tal cual → 304.
        _clock.Advance(TimeSpan.FromSeconds(31));
        var revalidated = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.NotModifiedCache, revalidated.Source);
        Assert.Equal(2, revalidated.Releases.Count);
        Assert.Equal(_clock.Now, revalidated.FetchedAt);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("W/\"abc\"", handler.Requests[1].IfNoneMatch);
        Assert.Equal(58, client.RateLimitRemaining);

        // El 304 renueva la frescura.
        _clock.Advance(TimeSpan.FromSeconds(10));
        await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetReleases_CacheSurvivesNewClientInstance()
    {
        var handler = FakeHttpHandler.Sequence(Ok(etag: "\"e1\""), _ => GitHubResponses.NotModified());
        await CreateClient(handler).GetReleasesAsync(Owner, Repo, CancellationToken.None);

        var second = CreateClient(handler);
        var fresh = await second.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.NotModifiedCache, fresh.Source);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetReleases_BypassFreshness_AsksAnyway()
    {
        var handler = FakeHttpHandler.Sequence(Ok(etag: "\"e1\""), _ => GitHubResponses.NotModified(etag: "\"e1\""));
        var client = CreateClient(handler);

        await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        var result = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None, bypassFreshness: true);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("\"e1\"", handler.Requests[1].IfNoneMatch);
        Assert.Equal(ReleaseFetchSource.NotModifiedCache, result.Source);
    }

    [Fact]
    public async Task GetReleases_NewContent_ReplacesCache()
    {
        const string newer = """[ { "id": 3, "tag_name": "0.0.3", "assets": [ { "id": 303, "name": "2D_0.0.3.zip", "size": 5 } ] } ]""";
        var handler = FakeHttpHandler.Sequence(Ok(etag: "\"e1\""), Ok(newer, etag: "\"e2\""), _ => GitHubResponses.NotModified());
        var client = CreateClient(handler);

        await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(2));
        var second = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.Network, second.Source);
        Assert.Equal("0.0.3", Assert.Single(second.Releases).TagName);

        _clock.Advance(TimeSpan.FromMinutes(2));
        var third = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal("\"e2\"", handler.Requests[2].IfNoneMatch);
        Assert.Equal("0.0.3", Assert.Single(third.Releases).TagName);
    }

    [Fact]
    public async Task GetReleases_CorruptCache_IsDeletedAndIgnored()
    {
        var handler = FakeHttpHandler.Sequence(Ok(etag: "\"e1\""), Ok(etag: "\"e2\""));
        var client = CreateClient(handler);
        await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);

        foreach (var file in Directory.GetFiles(_dir, "api-*.json"))
            await File.WriteAllTextAsync(file, "{{{ esto no es json");

        _clock.Advance(TimeSpan.FromMinutes(2));
        var result = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);

        Assert.Equal(ReleaseFetchSource.Network, result.Source);
        Assert.Equal(2, handler.RequestCount);
        Assert.Null(handler.Requests[1].IfNoneMatch);
    }

    [Fact]
    public async Task GetReleases_CorruptCache_AndOffline_ThrowsWithoutCrashingOnCache()
    {
        Directory.CreateDirectory(_dir);
        var handler = FakeHttpHandler.Sequence(Ok(), Throws());
        var client = CreateClient(handler);
        await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        foreach (var file in Directory.GetFiles(_dir, "api-*.json"))
            await File.WriteAllTextAsync(file, "[]garbage");

        _clock.Advance(TimeSpan.FromMinutes(2));
        await Assert.ThrowsAsync<LauncherException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_dir, "api-*.json"));
    }

    [Fact]
    public async Task GetReleases_304WithoutCache_RetriesOnceWithoutIfNoneMatch()
    {
        var handler = FakeHttpHandler.Sequence(_ => GitHubResponses.NotModified(), Ok());
        var client = CreateClient(handler);

        var result = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);

        Assert.Equal(ReleaseFetchSource.Network, result.Source);
        Assert.Equal(2, handler.RequestCount);
        Assert.Null(handler.Requests[0].IfNoneMatch);
        Assert.Null(handler.Requests[1].IfNoneMatch);
        Assert.True(handler.Requests[1].NoCache);
    }

    [Fact]
    public async Task GetReleases_Always304WithoutCache_FailsAfterOneRetry()
    {
        var handler = new FakeHttpHandler(_ => GitHubResponses.NotModified());
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<LauncherException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));
        Assert.Equal(2, handler.RequestCount);
    }

    // ───────────── Sin conexión ─────────────

    [Fact]
    public async Task GetReleases_Offline_WithCache_ReturnsOfflineCacheWithWarning()
    {
        var handler = FakeHttpHandler.Sequence(Ok(), Throws());
        var client = CreateClient(handler);
        var t0 = _clock.Now;
        await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(5));
        var result = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);

        Assert.Equal(ReleaseFetchSource.OfflineCache, result.Source);
        Assert.True(result.IsStale);
        Assert.Equal(2, result.Releases.Count);
        Assert.Equal(t0, result.FetchedAt);
        Assert.Equal("Sin conexión con GitHub. Mostrando datos guardados.", result.Warning);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "Sin conexión a Internet (no se encuentra github.com).")]
    [InlineData(HttpRequestError.SecureConnectionError, "No se pudo establecer una conexión segura con GitHub. Revisa la fecha y hora de Windows, el antivirus o si la red wifi requiere iniciar sesión.")]
    [InlineData(HttpRequestError.ConnectionError, "No se pudo conectar con GitHub.")]
    [InlineData(HttpRequestError.Unknown, "No se pudo conectar con GitHub.")]
    public async Task GetReleases_Offline_NoCache_ThrowsSpanishMessage(HttpRequestError error, string expected)
    {
        var client = CreateClient(new FakeHttpHandler(Throws(error)));

        var ex = await Assert.ThrowsAsync<LauncherException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));

        Assert.Equal(expected, ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task GetReleases_Timeout_IsNetworkError_NotCancellation()
    {
        var handler = new FakeHttpHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("inalcanzable");
        });
        var client = CreateClient(handler);
        client.RequestTimeout = TimeSpan.FromMilliseconds(200);

        var ex = await Assert.ThrowsAsync<LauncherException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));
        Assert.Contains("No se pudo conectar con GitHub", ex.Message);
    }

    [Fact]
    public async Task GetReleases_HtmlPortal_NoCache_Throws_WithCache_Offline()
    {
        var client = CreateClient(new FakeHttpHandler(_ => GitHubResponses.Html()));
        await Assert.ThrowsAsync<LauncherException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));

        var handler = FakeHttpHandler.Sequence(Ok(), _ => GitHubResponses.Html());
        var cached = CreateClient(handler);
        await cached.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(2));
        var result = await cached.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.OfflineCache, result.Source);
        Assert.EndsWith("Mostrando datos guardados.", result.Warning);
    }

    [Theory]
    [InlineData("[{ roto")]
    [InlineData("{\"message\": \"objeto en vez de array\"}")]
    [InlineData("")]
    public async Task GetReleases_InvalidJson_NoCache_Throws(string body)
    {
        var client = CreateClient(new FakeHttpHandler(_ => GitHubResponses.Json(body.Length == 0 ? " " : body)));
        await Assert.ThrowsAsync<LauncherException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_dir, "api-*.json"));
    }

    [Fact]
    public async Task GetReleases_ServerError_WithCache_IsOffline()
    {
        var handler = FakeHttpHandler.Sequence(Ok(), _ => GitHubResponses.Status(HttpStatusCode.BadGateway));
        var client = CreateClient(handler);
        await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(2));

        var result = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);

        Assert.Equal(ReleaseFetchSource.OfflineCache, result.Source);
        Assert.Equal("GitHub no responde ahora mismo. Mostrando datos guardados.", result.Warning);
    }

    [Fact]
    public async Task GetReleases_ServerError_NoCache_Throws()
    {
        var client = CreateClient(new FakeHttpHandler(_ => GitHubResponses.Status(HttpStatusCode.ServiceUnavailable)));
        var ex = await Assert.ThrowsAsync<LauncherException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));
        Assert.IsNotType<GitHubRateLimitException>(ex);
        Assert.Contains("GitHub no responde", ex.Message);
    }

    // ───────────── 404 ─────────────

    [Fact]
    public async Task GetReleases_NotFound_NoCache_Throws()
    {
        var client = CreateClient(new FakeHttpHandler(_ => GitHubResponses.Status(HttpStatusCode.NotFound, remaining: 50)));

        var ex = await Assert.ThrowsAsync<LauncherException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));

        Assert.Equal("No se encontró el repositorio JJJP-Dev/Coop2D en GitHub (¿es privado o cambió de nombre?).", ex.Message);
    }

    [Fact]
    public async Task GetReleases_NotFound_WithCache_IsOfflineWithWarning()
    {
        var handler = FakeHttpHandler.Sequence(Ok(), _ => GitHubResponses.Status(HttpStatusCode.NotFound));
        var client = CreateClient(handler);
        await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(2));

        var result = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);

        Assert.Equal(ReleaseFetchSource.OfflineCache, result.Source);
        Assert.Equal(2, result.Releases.Count);
        Assert.StartsWith("No se encontró el repositorio JJJP-Dev/Coop2D en GitHub", result.Warning);

        // La caché buena se conserva para la siguiente vez.
        _clock.Advance(TimeSpan.FromMinutes(2));
        var again = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(2, again.Releases.Count);
    }

    // ───────────── Rate limit ─────────────

    [Fact]
    public async Task GetReleases_PrimaryRateLimit_NoCache_ThrowsAndStopsAsking()
    {
        var reset = _clock.Now.AddMinutes(30);
        var handler = FakeHttpHandler.Sequence(
            _ => GitHubResponses.Status(HttpStatusCode.Forbidden, remaining: 0, resetAt: reset, serverDate: _clock.Now),
            Ok(remaining: 59));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<GitHubRateLimitException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));

        Assert.Equal(reset, ex.ResetAtUtc);
        Assert.Contains(reset.ToLocalTime().ToString("HH:mm"), ex.Message);
        Assert.StartsWith("Límite de consultas a GitHub alcanzado", ex.Message);
        Assert.True(client.IsRateLimited);
        Assert.Equal(0, client.RateLimitRemaining);
        Assert.Equal(reset, client.RateLimitResetUtc);

        // Mientras dure el límite no se hace ninguna petición (ni con bypassFreshness).
        _clock.Advance(TimeSpan.FromMinutes(10));
        await Assert.ThrowsAsync<GitHubRateLimitException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None, bypassFreshness: true));
        Assert.Equal(1, handler.RequestCount);

        // Pasado el reset se vuelve a preguntar.
        _clock.Advance(TimeSpan.FromMinutes(21));
        Assert.False(client.IsRateLimited);
        Assert.Null(client.RateLimitRemaining);
        var result = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.Network, result.Source);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetReleases_PrimaryRateLimit_WithCache_IsOfflineWithResetTime()
    {
        var reset = _clock.Now.AddMinutes(40);
        var handler = FakeHttpHandler.Sequence(
            Ok(),
            _ => GitHubResponses.Status((HttpStatusCode)429, remaining: 0, resetAt: reset, serverDate: _clock.Now));
        var client = CreateClient(handler);
        await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(2));

        var result = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);

        Assert.Equal(ReleaseFetchSource.OfflineCache, result.Source);
        var time = reset.ToLocalTime().ToString("HH:mm");
        Assert.Equal($"Límite de consultas a GitHub alcanzado; se restablece a las {time}. Mostrando datos guardados.", result.Warning);

        // Siguiente llamada: caché con aviso y sin petición.
        _clock.Advance(TimeSpan.FromMinutes(2));
        var again = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.OfflineCache, again.Source);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetReleases_LastAllowedRequest_BlocksFurtherRequestsUntilReset()
    {
        var reset = _clock.Now.AddMinutes(15);
        var handler = new FakeHttpHandler(_ => GitHubResponses.Json(ReleasesJson, remaining: 0, resetAt: reset, serverDate: _clock.Now));
        var client = CreateClient(handler);

        var first = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.Network, first.Source);
        Assert.True(client.IsRateLimited);

        _clock.Advance(TimeSpan.FromMinutes(2));
        var second = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.OfflineCache, second.Source);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetReleases_SecondaryRateLimit_UsesRetryAfter()
    {
        var handler = FakeHttpHandler.Sequence(
            _ => GitHubResponses.Status(HttpStatusCode.Forbidden, remaining: 30, resetAt: _clock.Now.AddMinutes(50),
                serverDate: _clock.Now, retryAfter: TimeSpan.FromSeconds(120)),
            Ok());
        var client = CreateClient(handler);
        var t0 = _clock.Now;

        var ex = await Assert.ThrowsAsync<GitHubRateLimitException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));
        Assert.Equal(t0.AddSeconds(120), ex.ResetAtUtc);
        Assert.True(client.IsRateLimited);

        _clock.Advance(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<GitHubRateLimitException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);

        _clock.Advance(TimeSpan.FromSeconds(61));
        await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetReleases_429WithoutHeaders_IsSecondaryLimitOfOneMinute()
    {
        var handler = new FakeHttpHandler(_ => GitHubResponses.Status((HttpStatusCode)429));
        var client = CreateClient(handler);
        var t0 = _clock.Now;

        var ex = await Assert.ThrowsAsync<GitHubRateLimitException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));

        Assert.Equal(t0.AddSeconds(60), ex.ResetAtUtc);
    }

    [Fact]
    public async Task GetReleases_Other403_IsRejected_NotRateLimit()
    {
        var client = CreateClient(new FakeHttpHandler(_ => GitHubResponses.Status(HttpStatusCode.Forbidden, remaining: 40)));
        var ex = await Assert.ThrowsAsync<LauncherException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));
        Assert.IsNotType<GitHubRateLimitException>(ex);
        Assert.StartsWith("GitHub rechazó la consulta", ex.Message);
        Assert.False(client.IsRateLimited);
        Assert.Equal(40, client.RateLimitRemaining);

        var handler = FakeHttpHandler.Sequence(Ok(), _ => GitHubResponses.Status((HttpStatusCode)451));
        var cached = CreateClient(handler);
        await cached.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(2));
        var result = await cached.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.OfflineCache, result.Source);
        Assert.Equal("GitHub rechazó la consulta. Mostrando datos guardados.", result.Warning);
    }

    [Fact]
    public async Task RateLimitReset_IsCorrectedWithServerDateHeader()
    {
        // El reloj del PC va 3 h adelantado respecto al servidor: lo que importa es la diferencia reset − Date.
        var serverNow = _clock.Now.AddHours(-3);
        var handler = new FakeHttpHandler(_ =>
            GitHubResponses.Json(ReleasesJson, remaining: 0, resetAt: serverNow.AddMinutes(10), serverDate: serverNow));
        var client = CreateClient(handler);

        await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);

        Assert.True(client.IsRateLimited);
        Assert.Equal(_clock.Now.AddMinutes(10), client.RateLimitResetUtc);
    }

    [Fact]
    public async Task RateLimitRemaining_IsTrackedAndForgottenAfterReset()
    {
        var client = CreateClient(new FakeHttpHandler(_ =>
            GitHubResponses.Json(ReleasesJson, remaining: 42, resetAt: _clock.Now.AddMinutes(10), serverDate: _clock.Now)));
        Assert.Null(client.RateLimitRemaining);
        Assert.Null(client.RateLimitResetUtc);

        await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(42, client.RateLimitRemaining);
        Assert.False(client.IsRateLimited);

        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(client.RateLimitRemaining);
        Assert.Null(client.RateLimitResetUtc);
    }

    [Fact]
    public async Task RateLimitState_IsPersistedAcrossInstances()
    {
        var reset = _clock.Now.AddMinutes(20);
        var first = CreateClient(new FakeHttpHandler(_ =>
            GitHubResponses.Status(HttpStatusCode.Forbidden, remaining: 0, resetAt: reset, serverDate: _clock.Now)));
        await Assert.ThrowsAsync<GitHubRateLimitException>(() => first.GetReleasesAsync(Owner, Repo, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(_dir, "ratelimit.json")));

        var handler = new FakeHttpHandler(Ok());
        var second = CreateClient(handler);

        Assert.True(second.IsRateLimited);
        Assert.Equal(reset, second.RateLimitResetUtc);
        var ex = await Assert.ThrowsAsync<GitHubRateLimitException>(() => second.GetReleasesAsync(Owner, Repo, CancellationToken.None));
        Assert.Equal(reset, ex.ResetAtUtc);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void CorruptRateLimitFile_IsIgnored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "ratelimit.json"), "no es json");

        var client = CreateClient(new FakeHttpHandler(Ok()));

        Assert.False(client.IsRateLimited);
        Assert.Null(client.RateLimitRemaining);
    }

    // ───────────── Rate limit y cambios de hora ─────────────

    [Fact]
    public async Task RateLimit_ClockSetBackHours_InSession_StopsBlocking()
    {
        // El reloj del PC va 3 h adelantado cuando GitHub corta (reset en 30 min) y luego Windows lo corrige.
        var serverNow = _clock.Now.AddHours(-3);
        var handler = FakeHttpHandler.Sequence(
            _ => GitHubResponses.Status(HttpStatusCode.Forbidden, remaining: 0, resetAt: serverNow.AddMinutes(30), serverDate: serverNow),
            Ok(remaining: 59));
        var client = CreateClient(handler);
        await Assert.ThrowsAsync<GitHubRateLimitException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));
        Assert.True(client.IsRateLimited);

        _clock.Now = serverNow;

        // Sin el arreglo: bloqueado 3 h 30 min con "se restablece a las …" de dentro de horas.
        Assert.False(client.IsRateLimited);
        Assert.Null(client.RateLimitRemaining);
        Assert.Null(client.RateLimitResetUtc);
        var result = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.Network, result.Source);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(59, client.RateLimitRemaining);
    }

    [Fact]
    public async Task RateLimit_ClockSetBackOneHour_SendsRequestAgain()
    {
        var handler = FakeHttpHandler.Sequence(
            _ => GitHubResponses.Status(HttpStatusCode.Forbidden, remaining: 0, resetAt: _clock.Now.AddMinutes(30), serverDate: _clock.Now),
            Ok(remaining: 59));
        var client = CreateClient(handler);
        await Assert.ThrowsAsync<GitHubRateLimitException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));

        // El reset queda a 1 h 30 min (creíble por sí solo), pero el estado se guardó "en el futuro": ya no es fiable.
        // Se llama directamente a GetReleasesAsync (sin pasar antes por las propiedades).
        _clock.Advance(TimeSpan.FromHours(-1));
        var result = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);

        Assert.Equal(ReleaseFetchSource.Network, result.Source);
        Assert.Equal(2, handler.RequestCount);
        Assert.False(client.IsRateLimited);
    }

    [Fact]
    public async Task RateLimit_SmallClockAdjustment_KeepsBlocking()
    {
        var reset = _clock.Now.AddMinutes(30);
        var handler = FakeHttpHandler.Sequence(
            _ => GitHubResponses.Status(HttpStatusCode.Forbidden, remaining: 0, resetAt: reset, serverDate: _clock.Now),
            Ok());
        var client = CreateClient(handler);
        await Assert.ThrowsAsync<GitHubRateLimitException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));

        // Ajuste típico de la sincronización de hora: unos segundos atrás. El límite sigue vigente.
        _clock.Advance(TimeSpan.FromSeconds(-40));

        Assert.True(client.IsRateLimited);
        Assert.Equal(0, client.RateLimitRemaining);
        Assert.Equal(reset, client.RateLimitResetUtc);
        await Assert.ThrowsAsync<GitHubRateLimitException>(() => client.GetReleasesAsync(Owner, Repo, CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RateLimitFile_SavedBeforeClockWasSetBack_IsDiscarded()
    {
        var first = CreateClient(new FakeHttpHandler(_ =>
            GitHubResponses.Status(HttpStatusCode.Forbidden, remaining: 0, resetAt: _clock.Now.AddMinutes(30), serverDate: _clock.Now)));
        await Assert.ThrowsAsync<GitHubRateLimitException>(() => first.GetReleasesAsync(Owner, Repo, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(_dir, "ratelimit.json")));

        // Se reinicia el launcher después de que Windows atrase la hora 3 h.
        _clock.Advance(TimeSpan.FromHours(-3));
        var handler = new FakeHttpHandler(Ok());
        var second = CreateClient(handler);

        Assert.False(second.IsRateLimited);
        Assert.Null(second.RateLimitRemaining);
        Assert.Null(second.RateLimitResetUtc);
        var result = await second.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(ReleaseFetchSource.Network, result.Source);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("9999-12-31T23:59:59+00:00", 0)]    // reset absurdo (archivo editado o dañado)
    [InlineData("2026-10-19T15:00:00+00:00", 0)]    // reset a 3 h (más de lo que GitHub pide nunca)
    [InlineData("2026-10-19T12:30:00+00:00", 1440)] // reset creíble, pero guardado "mañana"
    public async Task RateLimitFile_WithImpossibleDates_IsDiscarded(string resetUtc, int updatedMinutesAhead)
    {
        Directory.CreateDirectory(_dir);
        var updatedUtc = _clock.Now.AddMinutes(updatedMinutesAhead).ToString("O");
        File.WriteAllText(Path.Combine(_dir, "ratelimit.json"),
            $$"""{"remaining":0,"resetUtc":"{{resetUtc}}","updatedUtc":"{{updatedUtc}}"}""");
        var handler = new FakeHttpHandler(Ok(remaining: 59));
        var client = CreateClient(handler);

        // Primero la petición (camino de EnsureNotRateLimited), luego las propiedades.
        var result = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);

        Assert.Equal(ReleaseFetchSource.Network, result.Source);
        Assert.Equal(1, handler.RequestCount);
        Assert.False(client.IsRateLimited);
        Assert.Equal(59, client.RateLimitRemaining);
    }

    [Fact]
    public void RateLimitFile_WithFarFutureReset_PropertiesDoNotThrowOrBlock()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "ratelimit.json"),
            $$"""{"remaining":0,"resetUtc":"9999-12-31T23:59:59+00:00","updatedUtc":"{{_clock.Now:O}}"}""");

        var client = CreateClient(new FakeHttpHandler(Ok()));

        Assert.Null(client.RateLimitResetUtc);
        Assert.False(client.IsRateLimited);
        Assert.Null(client.RateLimitRemaining);
    }

    // ───────────── Concurrencia y cancelación ─────────────

    [Fact]
    public async Task GetReleases_ConcurrentCalls_AreCoalesced()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpHandler(async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return GitHubResponses.Json(ReleasesJson, remaining: 50);
        });
        var client = CreateClient(handler);

        var first = client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        await WaitUntilAsync(() => handler.RequestCount == 1);
        var second = client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        gate.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, handler.RequestCount);
        Assert.All(results, r => Assert.Equal(ReleaseFetchSource.Network, r.Source));
        Assert.All(results, r => Assert.Equal(2, r.Releases.Count));
    }

    [Fact]
    public async Task GetReleases_PreCancelledToken_ThrowsOperationCanceled()
    {
        var handler = new FakeHttpHandler(Ok());
        var client = CreateClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetReleasesAsync(Owner, Repo, new CancellationToken(true)));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetReleases_CancelWhileWaiting_ThrowsOperationCanceled_RequestStillCompletes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpHandler(async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return GitHubResponses.Json(ReleasesJson);
        });
        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();

        var pending = client.GetReleasesAsync(Owner, Repo, cts.Token);
        await WaitUntilAsync(() => handler.RequestCount == 1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        gate.SetResult();
        var result = await client.GetReleasesAsync(Owner, Repo, CancellationToken.None);
        Assert.Equal(2, result.Releases.Count);
        Assert.Equal(1, handler.RequestCount);
    }

    // ───────────── Última release ─────────────

    [Fact]
    public async Task GetLatestRelease_200_ReturnsAndCaches()
    {
        var handler = FakeHttpHandler.Sequence(_ => GitHubResponses.Json(LatestJson, etag: "\"l1\""), _ => GitHubResponses.NotModified());
        var client = CreateClient(handler);

        var release = await client.GetLatestReleaseAsync("JJJP-Dev", "PalitoLauncher", CancellationToken.None);
        Assert.NotNull(release);
        Assert.Equal("v1.1.0", release.TagName);
        Assert.Equal("https://api.github.com/repos/JJJP-Dev/PalitoLauncher/releases/latest", handler.Requests[0].Url.ToString());

        var fresh = await client.GetLatestReleaseAsync("JJJP-Dev", "PalitoLauncher", CancellationToken.None);
        Assert.Equal("v1.1.0", fresh?.TagName);
        Assert.Equal(1, handler.RequestCount);

        _clock.Advance(TimeSpan.FromMinutes(5));
        var revalidated = await client.GetLatestReleaseAsync("JJJP-Dev", "PalitoLauncher", CancellationToken.None);
        Assert.Equal("v1.1.0", revalidated?.TagName);
        Assert.Equal("\"l1\"", handler.Requests[1].IfNoneMatch);
    }

    [Fact]
    public async Task GetLatestRelease_404_IsCachedForSixHours()
    {
        var handler = new FakeHttpHandler(_ => GitHubResponses.Status(HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        Assert.Null(await client.GetLatestReleaseAsync("JJJP-Dev", "PalitoLauncher", CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);

        _clock.Advance(TimeSpan.FromHours(5));
        Assert.Null(await client.GetLatestReleaseAsync("JJJP-Dev", "PalitoLauncher", CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);

        _clock.Advance(TimeSpan.FromHours(1.1));
        Assert.Null(await client.GetLatestReleaseAsync("JJJP-Dev", "PalitoLauncher", CancellationToken.None));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetLatestRelease_Errors_ReturnNull()
    {
        Assert.Null(await CreateClient(new FakeHttpHandler(Throws())).GetLatestReleaseAsync("o", "r", CancellationToken.None));
        Assert.Null(await CreateClient(new FakeHttpHandler(_ => GitHubResponses.Html())).GetLatestReleaseAsync("o", "r2", CancellationToken.None));
        Assert.Null(await CreateClient(new FakeHttpHandler(_ => GitHubResponses.Status(HttpStatusCode.InternalServerError)))
            .GetLatestReleaseAsync("o", "r3", CancellationToken.None));
        Assert.Null(await CreateClient(new FakeHttpHandler(_ => GitHubResponses.Json("[1,2]")))
            .GetLatestReleaseAsync("o", "r4", CancellationToken.None));
        Assert.Null(await CreateClient(new FakeHttpHandler(Ok())).GetLatestReleaseAsync("", "r", CancellationToken.None));
    }

    [Fact]
    public async Task GetLatestRelease_RateLimited_ReturnsNullWithoutRequest()
    {
        var handler = FakeHttpHandler.Sequence(
            _ => GitHubResponses.Status(HttpStatusCode.Forbidden, remaining: 0, resetAt: _clock.Now.AddMinutes(30), serverDate: _clock.Now),
            _ => GitHubResponses.Json(LatestJson));
        var client = CreateClient(handler);

        Assert.Null(await client.GetLatestReleaseAsync("JJJP-Dev", "PalitoLauncher", CancellationToken.None));
        Assert.Null(await client.GetLatestReleaseAsync("JJJP-Dev", "PalitoLauncher", CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetLatestRelease_Cancelled_Throws()
    {
        var client = CreateClient(new FakeHttpHandler(_ => GitHubResponses.Json(LatestJson)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetLatestReleaseAsync("JJJP-Dev", "PalitoLauncher", new CancellationToken(true)));
    }

    // ───────────── Archivos (fondo) ─────────────

    private const string BackgroundUrl = "https://raw.githubusercontent.com/JJJP-Dev/Coop2D/main/launcher/background.png";

    [Fact]
    public async Task GetCachedFile_ValidPng_IsSavedThenRevalidated()
    {
        var handler = FakeHttpHandler.Sequence(_ => GitHubResponses.Bytes(PngBytes, "image/png", "\"img1\""), _ => GitHubResponses.NotModified());
        var client = CreateClient(handler);

        var path = await client.GetCachedFileAsync(BackgroundUrl, "background.png", CancellationToken.None);
        Assert.Equal(Path.Combine(_dir, "background.png"), path);
        Assert.Equal(PngBytes, await File.ReadAllBytesAsync(path!));
        Assert.Null(handler.Requests[0].IfNoneMatch);

        var again = await client.GetCachedFileAsync(BackgroundUrl, "background.png", CancellationToken.None);
        Assert.Equal(path, again);
        Assert.Equal("\"img1\"", handler.Requests[1].IfNoneMatch);
        Assert.Equal(PngBytes, await File.ReadAllBytesAsync(path!));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task GetCachedFile_Jpeg_IsAccepted()
    {
        var client = CreateClient(new FakeHttpHandler(_ => GitHubResponses.Bytes(JpegBytes, "image/jpeg")));
        var path = await client.GetCachedFileAsync(BackgroundUrl, "background.png", CancellationToken.None);
        Assert.NotNull(path);
        Assert.Equal(JpegBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task GetCachedFile_Html_IsRejected_KeepingPreviousCache()
    {
        var handler = FakeHttpHandler.Sequence(
            _ => GitHubResponses.Bytes(PngBytes, "image/png", "\"img1\""),
            _ => GitHubResponses.Html());
        var client = CreateClient(handler);
        var path = await client.GetCachedFileAsync(BackgroundUrl, "background.png", CancellationToken.None);

        var again = await client.GetCachedFileAsync(BackgroundUrl, "background.png", CancellationToken.None);

        Assert.Equal(path, again);
        Assert.Equal(PngBytes, await File.ReadAllBytesAsync(again!));
    }

    [Fact]
    public async Task GetCachedFile_WithoutCache_InvalidContent_ReturnsNull()
    {
        Assert.Null(await CreateClient(new FakeHttpHandler(_ => GitHubResponses.Html()))
            .GetCachedFileAsync(BackgroundUrl, "a.png", CancellationToken.None));
        // Content-Type de imagen pero bytes que no son PNG/JPEG.
        Assert.Null(await CreateClient(new FakeHttpHandler(_ => GitHubResponses.Bytes("GIF89a..."u8.ToArray(), "image/png")))
            .GetCachedFileAsync(BackgroundUrl, "b.png", CancellationToken.None));
        // Bytes PNG pero Content-Type que no es de imagen.
        Assert.Null(await CreateClient(new FakeHttpHandler(_ => GitHubResponses.Bytes(PngBytes, "text/plain")))
            .GetCachedFileAsync(BackgroundUrl, "c.png", CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_dir, "a.png")));
        Assert.False(File.Exists(Path.Combine(_dir, "b.png")));
        Assert.False(File.Exists(Path.Combine(_dir, "c.png")));
    }

    [Fact]
    public async Task GetCachedFile_404_DeletesCache()
    {
        var handler = FakeHttpHandler.Sequence(
            _ => GitHubResponses.Bytes(PngBytes, "image/png", "\"img1\""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404: Not Found") });
        var client = CreateClient(handler);
        var path = await client.GetCachedFileAsync(BackgroundUrl, "background.png", CancellationToken.None);
        Assert.True(File.Exists(path));

        Assert.Null(await client.GetCachedFileAsync(BackgroundUrl, "background.png", CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task GetCachedFile_NetworkError_ReturnsCachedPathOrNull()
    {
        Assert.Null(await CreateClient(new FakeHttpHandler(Throws())).GetCachedFileAsync(BackgroundUrl, "background.png", CancellationToken.None));

        var handler = FakeHttpHandler.Sequence(_ => GitHubResponses.Bytes(PngBytes, "image/png"), Throws());
        var client = CreateClient(handler);
        var path = await client.GetCachedFileAsync(BackgroundUrl, "background.png", CancellationToken.None);
        Assert.Equal(path, await client.GetCachedFileAsync(BackgroundUrl, "background.png", CancellationToken.None));
    }

    [Fact]
    public async Task GetCachedFile_304WithoutCachedFile_RetriesWithoutValidators()
    {
        var handler = FakeHttpHandler.Sequence(
            _ => GitHubResponses.Bytes(PngBytes, "image/png", "\"img1\""),
            _ => GitHubResponses.NotModified(),
            _ => GitHubResponses.Bytes(JpegBytes, "image/jpeg", "\"img2\""));
        var client = CreateClient(handler);
        var path = await client.GetCachedFileAsync(BackgroundUrl, "background.png", CancellationToken.None);

        // Sin imagen en caché no se mandan validadores; si aun así llega un 304 se repite sin ellos.
        File.Delete(path!);
        var again = await client.GetCachedFileAsync(BackgroundUrl, "background.png", CancellationToken.None);

        Assert.Equal(path, again);
        Assert.Equal(JpegBytes, await File.ReadAllBytesAsync(again!));
        Assert.Equal(3, handler.RequestCount);
        Assert.Null(handler.Requests[1].IfNoneMatch);
        Assert.True(handler.Requests[2].NoCache);
    }

    [Fact]
    public async Task GetCachedFile_TooLarge_IsRejected()
    {
        var big = new byte[20 * 1024 * 1024 + 1];
        PngBytes.CopyTo(big, 0);
        var client = CreateClient(new FakeHttpHandler(_ => GitHubResponses.Bytes(big, "image/png")));

        Assert.Null(await client.GetCachedFileAsync(BackgroundUrl, "background.png", CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_dir, "background.png")));
    }

    [Theory]
    [InlineData("..\\evil.png")]
    [InlineData("sub/evil.png")]
    [InlineData("C:\\evil.png")]
    [InlineData("")]
    [InlineData("..")]
    public async Task GetCachedFile_InvalidFileName_ReturnsNullWithoutRequest(string name)
    {
        var handler = new FakeHttpHandler(_ => GitHubResponses.Bytes(PngBytes, "image/png"));
        var client = CreateClient(handler);

        Assert.Null(await client.GetCachedFileAsync(BackgroundUrl, name, CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetCachedFile_DoesNotAffectApiRateLimit()
    {
        var response = GitHubResponses.Bytes(PngBytes, "image/png");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", _clock.Now.AddHours(1).ToUnixTimeSeconds().ToString());
        var client = CreateClient(new FakeHttpHandler(_ => response));

        await client.GetCachedFileAsync(BackgroundUrl, "background.png", CancellationToken.None);

        Assert.False(client.IsRateLimited);
        Assert.Null(client.RateLimitRemaining);
    }

    [Fact]
    public async Task GetCachedFile_Cancelled_Throws()
    {
        var client = CreateClient(new FakeHttpHandler(_ => GitHubResponses.Bytes(PngBytes, "image/png")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetCachedFileAsync(BackgroundUrl, "background.png", new CancellationToken(true)));
    }

    // ───────────── HttpFactory ─────────────

    [Fact]
    public void HttpFactory_UserAgent_HasProductVersionAndComment()
    {
        Assert.Matches(@"^PalitoLauncher/[0-9A-Za-z.\-_]+ \(\+https://github\.com/JJJP-Dev/PalitoLauncher\)$", HttpFactory.UserAgent);
    }

    [Fact]
    public void HttpFactory_Create_SetsInfiniteTimeoutAndUserAgent()
    {
        using var client = HttpFactory.Create(new FakeHttpHandler(Ok()));
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Equal(HttpFactory.UserAgent, client.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public void HttpFactory_Shared_IsSingleInstance()
    {
        Assert.Same(HttpFactory.Shared, HttpFactory.Shared);
        Assert.Equal(Timeout.InfiniteTimeSpan, HttpFactory.Shared.Timeout);
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
