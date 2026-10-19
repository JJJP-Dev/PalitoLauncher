using System.Net;
using System.Net.Http.Headers;

namespace PalitoLauncher.Tests.TestHelpers.B;

/// <summary>
/// HttpMessageHandler falso que sirve un array de bytes como una CDN: admite Range (206 + Content-Range), 416 si el rango
/// empieza al final, ETag/Last-Modified, y permite simular cortes (IOException tras N bytes), cuelgues y respuestas a medida.
/// Sin red.
/// </summary>
public sealed class FakeFileServer : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly List<RequestRecord> _requests = new();
    private int _requestCount;

    public FakeFileServer(byte[] content) => Content = content;

    public byte[] Content { get; set; }
    public string? ETag { get; set; } = "\"v1\"";
    public DateTimeOffset? LastModified { get; set; } = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Responde 200 con todo el contenido aunque se pida un rango.</summary>
    public bool IgnoreRange { get; set; }

    /// <summary>No envía Content-Length.</summary>
    public bool OmitContentLength { get; set; }

    /// <summary>Número de respuestas siguientes cuyo cuerpo lanza IOException tras <see cref="FailAfterBytes"/> bytes.</summary>
    public int FailuresRemaining { get; set; }

    public long FailAfterBytes { get; set; }

    /// <summary>Espera antes de cada bloque del cuerpo.</summary>
    public TimeSpan ChunkDelay { get; set; }

    public int ChunkSize { get; set; } = 16 * 1024;

    /// <summary>Uri final simulada (como si HttpClient hubiera seguido una redirección a la CDN).</summary>
    public Uri? FinalUri { get; set; }

    /// <summary>Respuesta a medida: (petición, índice 0-based, ct) → respuesta, o null para el comportamiento normal.</summary>
    public Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage?>>? Override { get; set; }

    public IReadOnlyList<RequestRecord> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToList();
            }
        }
    }

    public int RequestCount
    {
        get
        {
            lock (_lock)
            {
                return _requestCount;
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int index;
        lock (_lock)
        {
            index = _requestCount++;
        }

        var rangeFrom = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
        HttpResponseMessage? response = null;
        if (Override is not null)
        {
            response = await Override(request, index, cancellationToken).ConfigureAwait(false);
        }
        response ??= BuildNormalResponse(rangeFrom);
        response.RequestMessage ??= new HttpRequestMessage(request.Method, FinalUri ?? request.RequestUri);

        lock (_lock)
        {
            _requests.Add(new RequestRecord(request.RequestUri!, rangeFrom, (int)response.StatusCode));
        }
        return response;
    }

    /// <summary>Respuesta 200 con un cuerpo a medida (útil desde <see cref="Override"/>).</summary>
    public static HttpResponseMessage Ok(Stream body, string contentType = "application/octet-stream", long? contentLength = null)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (contentLength is not null)
        {
            content.Headers.ContentLength = contentLength;
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    private HttpResponseMessage BuildNormalResponse(long? rangeFrom)
    {
        byte[] content;
        string? etag;
        DateTimeOffset? lastModified;
        bool fail;
        long failAfter;
        lock (_lock)
        {
            content = Content;
            etag = ETag;
            lastModified = LastModified;
            fail = FailuresRemaining > 0;
            if (fail)
            {
                FailuresRemaining--;
            }
            failAfter = FailAfterBytes;
        }

        HttpResponseMessage response;
        byte[] body;
        if (rangeFrom is long from && !IgnoreRange)
        {
            if (from >= content.Length)
            {
                response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(content.Length);
                AddValidators(response, etag, lastModified);
                return response;
            }

            body = content[(int)from..];
            response = new HttpResponseMessage(HttpStatusCode.PartialContent);
            response.Content = CreateBody(body, fail ? failAfter : -1);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, content.Length - 1, content.Length);
        }
        else
        {
            body = content;
            response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = CreateBody(body, fail ? failAfter : -1);
        }

        AddValidators(response, etag, lastModified);
        return response;
    }

    private HttpContent CreateBody(byte[] body, long failAfter)
    {
        var stream = new FakeBodyStream(body) { FailAfter = failAfter, ChunkDelay = ChunkDelay, ChunkSize = ChunkSize };
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(ContentType);
        if (!OmitContentLength)
        {
            content.Headers.ContentLength = body.Length;
        }
        return content;
    }

    private static void AddValidators(HttpResponseMessage response, string? etag, DateTimeOffset? lastModified)
    {
        if (etag is not null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        }
        if (lastModified is not null)
        {
            response.Content.Headers.LastModified = lastModified;
        }
    }

    public sealed record RequestRecord(Uri Uri, long? RangeFrom, int Status);
}

/// <summary>Cuerpo de respuesta no buscable con fallos/cuelgues/pausas simuladas. Respeta la cancelación.</summary>
public sealed class FakeBodyStream : Stream
{
    private readonly byte[] _data;
    private long _position;
    private bool _paused;

    public FakeBodyStream(byte[] data) => _data = data;

    /// <summary>Lanza IOException al llegar a este número de bytes (-1 = nunca).</summary>
    public long FailAfter { get; init; } = -1;

    /// <summary>Se queda colgado (hasta cancelar) al llegar a este número de bytes (-1 = nunca).</summary>
    public long HangAfter { get; init; } = -1;

    /// <summary>Pausa una vez al llegar a <see cref="PauseAt"/> bytes durante <see cref="PauseDuration"/>.</summary>
    public long PauseAt { get; init; } = -1;

    public TimeSpan PauseDuration { get; init; }
    public TimeSpan ChunkDelay { get; init; }
    public int ChunkSize { get; init; } = 16 * 1024;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ChunkDelay > TimeSpan.Zero)
        {
            await Task.Delay(ChunkDelay, cancellationToken).ConfigureAwait(false);
        }
        if (PauseAt >= 0 && !_paused && _position >= PauseAt)
        {
            _paused = true;
            await Task.Delay(PauseDuration, cancellationToken).ConfigureAwait(false);
        }
        if (HangAfter >= 0 && _position >= HangAfter)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        if (FailAfter >= 0 && _position >= FailAfter)
        {
            throw new IOException("Conexión cortada (simulada).");
        }

        var remaining = _data.Length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        var limit = Math.Min(Math.Min(buffer.Length, ChunkSize), remaining);
        if (FailAfter >= 0)
        {
            limit = Math.Min(limit, FailAfter - _position);
        }
        if (HangAfter >= 0)
        {
            limit = Math.Min(limit, HangAfter - _position);
        }
        if (PauseAt >= 0 && !_paused)
        {
            limit = Math.Min(limit, Math.Max(1, PauseAt - _position));
        }

        var count = (int)Math.Max(1, limit);
        _data.AsMemory((int)_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
