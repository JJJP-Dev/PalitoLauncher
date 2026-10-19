using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace PalitoLauncher.Tests.TestHelpers.C;

/// <summary>
/// HttpMessageHandler falso (sin red) que sirve archivos en memoria como la CDN de GitHub: 200 completo o 206 con
/// Range, ETag fuerte y Last-Modified. Puede "colgarse" una vez al llegar a un desplazamiento (para probar la cancelación).
/// </summary>
internal sealed class FakeBuildServer : HttpMessageHandler
{
    private static readonly DateTimeOffset LastModified = new(2026, 10, 18, 22, 58, 0, TimeSpan.Zero);
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private int _requestCount;
    private long _stallAtOffset = -1;
    private int _corruptNext;

    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Espera por cada lectura del cuerpo (lecturas de 16 KB como máximo) para simular una conexión lenta.</summary>
    public TimeSpan ReadDelay { get; set; }

    /// <summary>La próxima respuesta sirve los bytes con uno cambiado (mismo tamaño, hash distinto).</summary>
    public void CorruptNextResponse() => Interlocked.Exchange(ref _corruptNext, 1);

    /// <summary>Se completa cuando una respuesta llega al punto de cuelgue.</summary>
    public TaskCompletionSource StallReached { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Add(string url, byte[] content)
    {
        _files[url] = content;
        return url;
    }

    /// <summary>La próxima respuesta que alcance este desplazamiento se cuelga hasta que se cancele (solo una vez).</summary>
    public void StallOnceAt(long offset)
    {
        StallReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _stallAtOffset, offset);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        var url = request.RequestUri!.ToString();
        if (!_files.TryGetValue(url, out var data))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request, Content = new ByteArrayContent([]) });

        var etag = new EntityTagHeaderValue("\"0x" + Convert.ToHexString(SHA256.HashData(data))[..16] + "\"");
        long start = 0;
        HttpResponseMessage response;
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        if (range?.From is long from)
        {
            if (from >= data.Length)
            {
                response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent([]),
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(data.Length);
                return Task.FromResult(response);
            }
            start = from;
            response = new HttpResponseMessage(HttpStatusCode.PartialContent) { RequestMessage = request };
        }
        else
        {
            response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        }

        var body = data;
        if (Interlocked.Exchange(ref _corruptNext, 0) == 1)
        {
            body = (byte[])data.Clone();
            body[body.Length / 2] ^= 0xFF;
        }

        var stallAt = Interlocked.Exchange(ref _stallAtOffset, -1);
        var stream = new ServeStream(body, (int)start, stallAt >= start ? stallAt : -1, () => StallReached.TrySetResult(), ReadDelay);
        if (stallAt >= 0 && stallAt < start)
            Interlocked.Exchange(ref _stallAtOffset, stallAt); // no aplicaba a esta respuesta

        response.Content = new StreamContent(stream, 16 * 1024);
        response.Content.Headers.ContentLength = data.Length - start;
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        response.Content.Headers.LastModified = LastModified;
        if (response.StatusCode == HttpStatusCode.PartialContent)
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, data.Length - 1, data.Length);
        response.Headers.ETag = etag;
        response.Headers.AcceptRanges.Add("bytes");
        return Task.FromResult(response);
    }

    private sealed class ServeStream(byte[] data, int start, long stallAt, Action onStall, TimeSpan readDelay) : Stream
    {
        private readonly int _start = start;
        private int _position = start;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length - _start;
        public override long Position { get => _position - _start; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stallAt >= 0 && _position >= stallAt)
            {
                onStall();
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            if (readDelay > TimeSpan.Zero)
            {
                await Task.Delay(readDelay, cancellationToken).ConfigureAwait(false);
                return CopyTo(buffer.Span[..Math.Min(buffer.Length, 16 * 1024)]);
            }
            return CopyTo(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (stallAt >= 0 && _position >= stallAt)
            {
                onStall();
                throw new IOException("Conexión colgada (simulada).");
            }
            return CopyTo(buffer.AsSpan(offset, count));
        }

        private int CopyTo(Span<byte> destination)
        {
            var available = data.Length - _position;
            if (stallAt >= 0 && _position < stallAt)
                available = (int)Math.Min(available, stallAt - _position);
            var count = Math.Min(destination.Length, Math.Max(0, available));
            data.AsSpan(_position, count).CopyTo(destination);
            _position += count;
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
