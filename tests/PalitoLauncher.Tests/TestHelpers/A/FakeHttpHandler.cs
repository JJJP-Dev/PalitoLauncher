using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace PalitoLauncher.Tests.TestHelpers.A;

/// <summary>Petición recibida por <see cref="FakeHttpHandler"/> (copia de lo que interesa comprobar).</summary>
public sealed record RecordedRequest(
    Uri Url,
    string? IfNoneMatch,
    string? Accept,
    string? ApiVersion,
    string? UserAgent,
    bool NoCache);

/// <summary>HttpMessageHandler falso (sin red): responde con la función dada y registra las peticiones.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : this((req, _) => Task.FromResult(responder(req)))
    {
    }

    public FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public int RequestCount => _requests.Count;
    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();
    public RecordedRequest? LastRequest => _requests.LastOrDefault();

    /// <summary>Responde en orden con cada función; la última se repite si hay más peticiones.</summary>
    public static FakeHttpHandler Sequence(params Func<HttpRequestMessage, HttpResponseMessage>[] responders)
    {
        var index = -1;
        return new FakeHttpHandler(req =>
        {
            var i = Math.Min(Interlocked.Increment(ref index), responders.Length - 1);
            return responders[i](req);
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(new RecordedRequest(
            request.RequestUri!,
            Header(request.Headers, "If-None-Match"),
            Header(request.Headers, "Accept"),
            Header(request.Headers, "X-GitHub-Api-Version"),
            Header(request.Headers, "User-Agent"),
            request.Headers.CacheControl?.NoCache == true));
        var response = await _responder(request, cancellationToken).ConfigureAwait(false);
        response.RequestMessage ??= request;
        return response;
    }

    private static string? Header(HttpHeaders headers, string name)
        => headers.NonValidated.TryGetValues(name, out var values) ? values.ToString() : null;
}

/// <summary>Constructores de respuestas típicas de GitHub.</summary>
public static class GitHubResponses
{
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, string? etag = null,
        int? remaining = null, DateTimeOffset? resetAt = null, DateTimeOffset? serverDate = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        AddCommon(response, etag, remaining, resetAt, serverDate);
        return response;
    }

    public static HttpResponseMessage NotModified(string? etag = null, int? remaining = null, DateTimeOffset? resetAt = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotModified) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        AddCommon(response, etag, remaining, resetAt, null);
        return response;
    }

    public static HttpResponseMessage Status(HttpStatusCode status, string body = "{\"message\":\"error\"}", int? remaining = null,
        DateTimeOffset? resetAt = null, DateTimeOffset? serverDate = null, TimeSpan? retryAfter = null)
    {
        var response = Json(body, status, null, remaining, resetAt, serverDate);
        if (retryAfter is { } ra) response.Headers.RetryAfter = new RetryConditionHeaderValue(ra);
        return response;
    }

    public static HttpResponseMessage Html(string html = "<html><body>Inicia sesión en la red wifi</body></html>")
        => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    public static HttpResponseMessage Bytes(byte[] data, string contentType, string? etag = null)
    {
        var content = new ByteArrayContent(data);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        if (etag != null) response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }

    private static void AddCommon(HttpResponseMessage response, string? etag, int? remaining, DateTimeOffset? resetAt, DateTimeOffset? serverDate)
    {
        if (etag != null) response.Headers.TryAddWithoutValidation("ETag", etag);
        if (remaining is { } r) response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", r.ToString());
        if (resetAt is { } reset) response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString());
        if (serverDate is { } d) response.Headers.Date = d;
    }
}

/// <summary>Reloj manual para tests de frescura / reset.</summary>
public sealed class ManualClock
{
    public ManualClock(DateTimeOffset start) => Now = start;

    public DateTimeOffset Now { get; set; }

    public void Advance(TimeSpan delta) => Now += delta;
}
