using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace PalitoLauncher.Tests.TestHelpers.F;

/// <summary>Handler HTTP falso (sin red): responde con una función y registra las peticiones.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    private readonly ConcurrentQueue<string> _requests = new();

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    /// <summary>URLs pedidas, en orden.</summary>
    public IReadOnlyList<string> Requests => _requests.ToArray();

    public int CountRequests(string pathSuffix) =>
        _requests.Count(u => new Uri(u).AbsolutePath.EndsWith(pathSuffix, StringComparison.OrdinalIgnoreCase));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _requests.Enqueue(request.RequestUri!.ToString());
        var response = _responder(request);
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }

    /// <summary>Respuesta JSON 200 con cabeceras típicas de la API de GitHub.</summary>
    public static HttpResponseMessage Json(string json, int rateLimitRemaining = 55, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        AddRateLimitHeaders(response, rateLimitRemaining);
        return response;
    }

    public static HttpResponseMessage NotFound(int rateLimitRemaining = 55)
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"message\":\"Not Found\"}", Encoding.UTF8, "application/json"),
        };
        AddRateLimitHeaders(response, rateLimitRemaining);
        return response;
    }

    /// <summary>Respuesta binaria 200 (descargas).</summary>
    public static HttpResponseMessage Bytes(byte[] data)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = data.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static void AddRateLimitHeaders(HttpResponseMessage response, int remaining)
    {
        var now = DateTimeOffset.UtcNow;
        response.Headers.Date = now;
        response.Headers.TryAddWithoutValidation("X-RateLimit-Limit", "60");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset",
            now.AddMinutes(30).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
