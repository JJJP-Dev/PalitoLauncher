using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace PalitoLauncher.Tests.TestHelpers.M5;

/// <summary>
/// raw.githubusercontent.com falso (sin red) para UNA URL: sirve <see cref="Body"/> con un ETag propio y responde 304 si
/// llega If-None-Match con ese ETag; <see cref="Body"/> null → 404; <see cref="Fail"/> → error de red. Cualquier otra URL
/// → 404. Guarda las peticiones (URL e If-None-Match) para comprobarlas.
/// </summary>
internal sealed class FakeRawServer : HttpMessageHandler
{
    private readonly string _url;
    private readonly ConcurrentQueue<(Uri Uri, string? IfNoneMatch)> _requests = new();
    private int _version;
    private string? _body;

    public FakeRawServer(string url, string? body = null)
    {
        _url = url;
        _body = body;
    }

    /// <summary>Contenido servido (null = 404). Cambiarlo cambia también el ETag.</summary>
    public string? Body
    {
        get => Volatile.Read(ref _body);
        set
        {
            Volatile.Write(ref _body, value);
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>Si es true, cada petición falla como sin conexión.</summary>
    public bool Fail { get; set; }

    /// <summary>Si no es null, se espera a esta tarea antes de responder (para probar llamadas simultáneas).</summary>
    public Task? Gate { get; set; }

    public IReadOnlyList<(Uri Uri, string? IfNoneMatch)> Requests => _requests.ToArray();

    public int RequestsTo(string url) => Requests.Count(r => r.Uri.ToString() == url);

    private string CurrentETag => $"\"v{Volatile.Read(ref _version)}\"";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ifNoneMatch = request.Headers.TryGetValues("If-None-Match", out var values) ? string.Join(",", values) : null;
        _requests.Enqueue((request.RequestUri!, ifNoneMatch));

        if (Gate is { } gate) await gate.WaitAsync(cancellationToken);
        if (Fail) throw new HttpRequestException("sin red (simulado)");

        if (request.RequestUri!.ToString() != _url || Body is not { } body)
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404: Not Found") };

        var etag = CurrentETag;
        if (ifNoneMatch == etag)
        {
            var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
            notModified.Headers.TryAddWithoutValidation("ETag", etag);
            return notModified;
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, new UTF8Encoding(false), "text/plain"),
        };
        response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }
}
