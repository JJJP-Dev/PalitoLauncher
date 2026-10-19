using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace PalitoLauncher.Tests.TestHelpers.B5;

/// <summary>Una parte de un cuerpo multipart/form-data ya separada.</summary>
internal sealed record MultipartPart(string? Name, string? FileName, string? ContentType, byte[] Body)
{
    public string Text => Encoding.UTF8.GetString(Body);
}

/// <summary>Petición capturada por <see cref="MultipartCapturingHandler"/> con el multipart ya parseado.</summary>
internal sealed record CapturedMultipartRequest(
    HttpMethod Method,
    Uri Uri,
    string? ContentType,
    IReadOnlyList<MultipartPart> Parts,
    IReadOnlyDictionary<string, string> Headers,
    long BodyLength)
{
    public MultipartPart? Part(string name) => Parts.FirstOrDefault(p => p.Name == name);

    /// <summary>Texto de la parte "payload_json" (falla si no está).</summary>
    public string PayloadJson => (Part("payload_json") ?? throw new InvalidDataException("Falta payload_json.")).Text;

    /// <summary>Partes files[i] en el orden en que llegaron.</summary>
    public IReadOnlyList<MultipartPart> Files => Parts.Where(p => p.Name?.StartsWith("files[", StringComparison.Ordinal) == true).ToList();

    public IReadOnlyList<string?> FileNames => Files.Select(f => f.FileName).ToList();

    public MultipartPart File(string fileName) => Files.Single(f => f.FileName == fileName);
}

/// <summary>
/// Handler HTTP falso (sin red) para webhooks con adjuntos: lee el cuerpo, comprueba que es un multipart/form-data bien
/// formado (falla si no) y lo separa en partes; responde con una función.
/// </summary>
internal sealed class MultipartCapturingHandler : HttpMessageHandler
{
    private readonly Func<CapturedMultipartRequest, CancellationToken, Task<HttpResponseMessage>> _responder;
    private readonly ConcurrentQueue<CapturedMultipartRequest> _requests = new();

    public MultipartCapturingHandler(Func<CapturedMultipartRequest, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public MultipartCapturingHandler(Func<CapturedMultipartRequest, HttpResponseMessage> responder)
        : this((request, _) => Task.FromResult(responder(request)))
    {
    }

    /// <summary>Peticiones recibidas, en orden.</summary>
    public IReadOnlyList<CapturedMultipartRequest> Requests => _requests.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parts = new List<MultipartPart>();
        string? contentType = null;
        long length = 0;
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            length = body.Length;
            var mediaType = request.Content.Headers.ContentType;
            contentType = mediaType?.ToString();
            if (mediaType?.MediaType == "multipart/form-data")
                parts = MultipartParser.Parse(body, MultipartParser.GetBoundary(mediaType));
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers.NonValidated)
            headers[header.Key] = string.Join(",", header.Value);

        var captured = new CapturedMultipartRequest(request.Method, request.RequestUri!, contentType, parts, headers, length);
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
            $$"""{"message":"You are being rate limited.","retry_after":{{retryAfterSeconds.ToString(CultureInfo.InvariantCulture)}},"global":false}""");
        response.Headers.TryAddWithoutValidation("Retry-After", Math.Ceiling(retryAfterSeconds).ToString(CultureInfo.InvariantCulture));
        return response;
    }

    /// <summary>413 como el de Discord/Cloudflare cuando los adjuntos pesan demasiado.</summary>
    public static HttpResponseMessage TooLarge() => Status(HttpStatusCode.RequestEntityTooLarge, "<html>413 Request Entity Too Large</html>");

    public static HttpResponseMessage Status(HttpStatusCode status, string body = "")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
}

/// <summary>Parser mínimo y estricto de multipart/form-data (RFC 7578) para los tests.</summary>
internal static class MultipartParser
{
    public static string GetBoundary(MediaTypeHeaderValue mediaType)
    {
        var boundary = mediaType.Parameters.FirstOrDefault(p => string.Equals(p.Name, "boundary", StringComparison.OrdinalIgnoreCase))?.Value
                       ?? throw new InvalidDataException("multipart sin boundary.");
        return boundary.Trim('"');
    }

    public static List<MultipartPart> Parse(byte[] body, string boundary)
    {
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var crlf = Encoding.ASCII.GetBytes("\r\n");
        var nextDelimiter = Encoding.ASCII.GetBytes("\r\n--" + boundary);
        var parts = new List<MultipartPart>();

        var position = IndexOf(body, delimiter, 0);
        if (position != 0) throw new InvalidDataException("El cuerpo no empieza por el delimitador.");
        position += delimiter.Length;

        while (true)
        {
            if (Matches(body, position, "--"u8))
            {
                // Delimitador final: solo puede seguir un salto de línea opcional.
                var rest = body.Length - (position + 2);
                if (rest is not (0 or 2)) throw new InvalidDataException("Datos tras el delimitador final.");
                return parts;
            }
            if (!Matches(body, position, crlf)) throw new InvalidDataException("Falta el salto de línea tras el delimitador.");
            position += 2;

            var headersEnd = IndexOf(body, "\r\n\r\n"u8.ToArray(), position);
            if (headersEnd < 0) throw new InvalidDataException("Parte sin cabeceras completas.");
            var headerText = Encoding.UTF8.GetString(body, position, headersEnd - position);
            var contentStart = headersEnd + 4;

            var contentEnd = IndexOf(body, nextDelimiter, contentStart);
            if (contentEnd < 0) throw new InvalidDataException("Parte sin delimitador de cierre.");

            string? name = null, fileName = null, contentType = null;
            foreach (var line in headerText.Split("\r\n"))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) throw new InvalidDataException($"Cabecera no válida: {line}");
                var headerName = line[..colon].Trim();
                var headerValue = line[(colon + 1)..].Trim();
                if (headerName.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase))
                {
                    var segments = headerValue.Split(';').Select(s => s.Trim()).ToArray();
                    if (!segments[0].Equals("form-data", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Content-Disposition no es form-data: {headerValue}");
                    foreach (var segment in segments.Skip(1))
                    {
                        var eq = segment.IndexOf('=');
                        if (eq <= 0) continue;
                        var key = segment[..eq].Trim();
                        var value = segment[(eq + 1)..].Trim().Trim('"');
                        if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = value;
                        else if (key.Equals("filename", StringComparison.OrdinalIgnoreCase)) fileName = value;
                    }
                }
                else if (headerName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = headerValue;
                }
            }
            if (name is null) throw new InvalidDataException("Parte sin name.");

            parts.Add(new MultipartPart(name, fileName, contentType, body.AsSpan(contentStart, contentEnd - contentStart).ToArray()));
            position = contentEnd + nextDelimiter.Length;
        }
    }

    private static bool Matches(byte[] data, int position, ReadOnlySpan<byte> expected)
        => position + expected.Length <= data.Length && data.AsSpan(position, expected.Length).SequenceEqual(expected);

    private static int IndexOf(byte[] data, byte[] pattern, int start)
    {
        if (start > data.Length) return -1;
        var index = data.AsSpan(start).IndexOf(pattern);
        return index < 0 ? -1 : start + index;
    }
}
