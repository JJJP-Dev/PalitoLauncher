using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace PalitoLauncher.Tests.TestHelpers.F3;

/// <summary>Petición capturada por <see cref="CapturingHttpHandler"/> (cuerpo ya leído como texto).</summary>
internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body, string? ContentType, IReadOnlyDictionary<string, string> Headers);

/// <summary>Handler HTTP falso (sin red): captura cada petición (incluido el JSON enviado) y responde con una función.</summary>
internal sealed class CapturingHttpHandler : HttpMessageHandler
{
    private readonly Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> _responder;
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();

    public CapturingHttpHandler(Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public CapturingHttpHandler(Func<CapturedRequest, HttpResponseMessage> responder)
        : this((request, _) => Task.FromResult(responder(request)))
    {
    }

    /// <summary>Peticiones recibidas, en orden.</summary>
    public IReadOnlyList<CapturedRequest> Requests => _requests.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? body = null;
        string? contentType = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
            contentType = request.Content.Headers.ContentType?.ToString();
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers.NonValidated)
            headers[header.Key] = string.Join(",", header.Value);

        var captured = new CapturedRequest(request.Method, request.RequestUri!, body, contentType, headers);
        _requests.Enqueue(captured);

        var response = await _responder(captured, cancellationToken);
        response.RequestMessage ??= request;
        return response;
    }

    /// <summary>200 con el JSON de un mensaje de Discord (como con ?wait=true).</summary>
    public static HttpResponseMessage DiscordOk() => Json(HttpStatusCode.OK, """{"id":"1234567890","type":0,"content":""}""");

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    /// <summary>429 de Discord con retry_after en el cuerpo y cabecera Retry-After.</summary>
    public static HttpResponseMessage TooManyRequests(double retryAfterSeconds)
    {
        var response = Json((HttpStatusCode)429,
            $$"""{"message":"You are being rate limited.","retry_after":{{retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"global":false}""");
        response.Headers.TryAddWithoutValidation("Retry-After", Math.Ceiling(retryAfterSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }

    /// <summary>Texto plano (p.ej. raw.githubusercontent.com).</summary>
    public static HttpResponseMessage Text(string text, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(text, Encoding.UTF8, "text/plain"),
        };
        if (etag is not null) response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }

    public static HttpResponseMessage Status(HttpStatusCode status, string body = "")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
}
