using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using PalitoLauncher.Core.Downloads;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Tests.TestHelpers.B;

namespace PalitoLauncher.Tests;

public sealed class FileDownloaderTests : IDisposable
{
    private const string Url = "https://github.com/JJJP-Dev/Coop2D/releases/download/0.0.9/2D_0.0.9_Protype9.zip";

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private static byte[] RandomBytes(int length, int seed = 42)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private string Dest => _temp.Combine("downloads", "123-2D_0.0.9_Protype9.zip");

    private DownloadRequest Request(byte[] data, string? sha = "auto", long? size = -1, string? resumeKey = "asset:123:test", long maxBps = 0)
        => new()
        {
            Url = Url,
            DestinationPath = Dest,
            ExpectedSize = size == -1 ? data.Length : size,
            ExpectedSha256 = sha == "auto" ? Sha(data) : sha,
            ResumeKey = resumeKey,
            MaxBytesPerSecond = maxBps,
        };

    private static FileDownloader Downloader(HttpMessageHandler handler, double retryScale = 0, TimeSpan? stall = null)
        => new(new HttpClient(handler)) { RetryDelayScale = retryScale, StallTimeout = stall ?? TimeSpan.FromSeconds(10) };

    /// <summary>Escribe un .part y su .part.json con el formato documentado en el contrato.</summary>
    private void WritePartial(byte[] partContent, string resumeKey, long? expectedSize, string? etag = "\"v1\"")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Dest)!);
        File.WriteAllBytes(Dest + ".part", partContent);
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["url"] = Url,
            ["resumeKey"] = resumeKey,
            ["expectedSize"] = expectedSize,
            ["expectedSha256"] = null,
            ["cdnETag"] = etag,
            ["cdnLastModified"] = null,
            ["createdAtUtc"] = DateTimeOffset.UtcNow,
        });
        File.WriteAllText(Dest + ".part.json", json);
    }

    private void AssertNoPartials()
    {
        Assert.False(File.Exists(Dest + ".part"), ".part debería haberse borrado");
        Assert.False(File.Exists(Dest + ".part.json"), ".part.json debería haberse borrado");
    }

    [Fact]
    public async Task Download_Fresh_WritesVerifiedFileAndReportsFinalProgress()
    {
        var data = RandomBytes(300_000);
        var server = new FakeFileServer(data);
        var downloader = Downloader(server);
        var verifications = 0;
        downloader.VerificationStarted += (_, _) => verifications++;
        var progress = new SyncProgress<DownloadProgress>();

        await downloader.DownloadAsync(Request(data), progress, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
        AssertNoPartials();
        Assert.Single(server.Requests);
        Assert.Null(server.Requests[0].RangeFrom);
        Assert.Equal(1, verifications);
        var last = progress.Values.Last();
        Assert.Equal(data.Length, last.BytesReceived);
        Assert.Equal(data.Length, last.TotalBytes);
        Assert.Equal(1.0, last.Fraction);
    }

    [Fact]
    public async Task Download_CutMidStream_ResumesWithRange()
    {
        var data = RandomBytes(400_000);
        var server = new FakeFileServer(data) { FailuresRemaining = 1, FailAfterBytes = 150_000 };
        var downloader = Downloader(server);
        var retries = new List<DownloadRetryInfo>();
        downloader.RetryScheduled += (_, info) => retries.Add(info);

        await downloader.DownloadAsync(Request(data), null, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
        AssertNoPartials();
        var requests = server.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Null(requests[0].RangeFrom);
        Assert.Equal(150_000, requests[1].RangeFrom);
        Assert.Equal(206, requests[1].Status);
        var retry = Assert.Single(retries);
        Assert.Equal(2, retry.Attempt);
        Assert.Equal(FileDownloader.MaxAttemptsWithoutProgress, retry.MaxAttempts);
        Assert.False(string.IsNullOrWhiteSpace(retry.Reason));
    }

    [Fact]
    public async Task Download_TenCutsWithProgress_CompletesBecauseProgressResetsCounter()
    {
        var data = RandomBytes(12 * 1024 * 1024, seed: 7);
        var server = new FakeFileServer(data)
        {
            FailuresRemaining = 10,
            FailAfterBytes = 1_100_000,
            ChunkSize = 81920,
        };
        var downloader = Downloader(server);
        var retries = new List<DownloadRetryInfo>();
        downloader.RetryScheduled += (_, info) => retries.Add(info);

        await downloader.DownloadAsync(Request(data), null, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
        Assert.Equal(11, server.RequestCount);
        Assert.Equal(10, retries.Count);
        Assert.All(retries, r => Assert.Equal(2, r.Attempt));
    }

    [Fact]
    public async Task Download_FailuresWithoutProgress_GivesUpAfterEightAttempts_KeepsPartAndUsesBackoff()
    {
        var data = RandomBytes(200_000);
        var server = new FakeFileServer(data) { FailuresRemaining = int.MaxValue, FailAfterBytes = 1000 };
        var downloader = Downloader(server, retryScale: 0.001);
        var retries = new List<DownloadRetryInfo>();
        downloader.RetryScheduled += (_, info) => retries.Add(info);

        var ex = await Assert.ThrowsAsync<LauncherException>(() => downloader.DownloadAsync(Request(data), null, CancellationToken.None));

        Assert.Equal(FileDownloader.GiveUpMessage, ex.Message);
        Assert.Equal(8, server.RequestCount);
        Assert.Equal(7, retries.Count);
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7, 8 }, retries.Select(r => r.Attempt));
        Assert.Equal(new double[] { 1, 2, 4, 8, 16, 30, 30 }, retries.Select(r => r.Delay.TotalMilliseconds));
        Assert.True(File.Exists(Dest + ".part"));
        Assert.Equal(8000, new FileInfo(Dest + ".part").Length);
        Assert.Equal(8000, FileDownloader.GetResumableBytes(Request(data)));
        Assert.False(File.Exists(Dest));
    }

    [Fact]
    public async Task Download_RangeIgnoredAndCutsEveryTime_NoNetProgress_GivesUp()
    {
        // Proxy que ignora Range y una conexión que se corta siempre a 1,5 MB: cada intento reescribe lo mismo, no avanza.
        var data = RandomBytes(3 * 1024 * 1024, seed: 3);
        var server = new FakeFileServer(data)
        {
            IgnoreRange = true,
            FailuresRemaining = int.MaxValue,
            FailAfterBytes = 1_500_000,
            ChunkSize = 81920,
        };

        var ex = await Assert.ThrowsAsync<LauncherException>(() => Downloader(server).DownloadAsync(Request(data), null, CancellationToken.None));

        Assert.Equal(FileDownloader.GiveUpMessage, ex.Message);
        Assert.Equal(8, server.RequestCount);
    }

    [Fact]
    public async Task Download_ServerIgnoresRange_RestartsFromZeroAndFileIsCorrect()
    {
        var data = RandomBytes(250_000);
        var server = new FakeFileServer(data) { FailuresRemaining = 1, FailAfterBytes = 60_000, IgnoreRange = true };
        var downloader = Downloader(server);

        await downloader.DownloadAsync(Request(data), null, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
        var requests = server.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Equal(60_000, requests[1].RangeFrom);
        Assert.Equal(200, requests[1].Status);
    }

    [Fact]
    public async Task Download_HtmlPage_DoesNotTouchPart_AndEndsWithPortalMessage()
    {
        var data = RandomBytes(200_000);
        var server = new FakeFileServer(data) { FailuresRemaining = 1, FailAfterBytes = 64_000 };
        server.Override = (_, index, _) => Task.FromResult(index == 0
            ? null
            : FakeFileServer.Ok(new MemoryStream("<html>Inicia sesión en la wifi</html>"u8.ToArray()), "text/html"));
        var downloader = Downloader(server);

        var ex = await Assert.ThrowsAsync<LauncherException>(() => downloader.DownloadAsync(Request(data), null, CancellationToken.None));

        Assert.Equal(FileDownloader.PortalMessage, ex.Message);
        Assert.Equal(8, server.RequestCount);
        var part = await File.ReadAllBytesAsync(Dest + ".part");
        Assert.Equal(data.AsSpan(0, 64_000).ToArray(), part);
        Assert.Equal(64_000, FileDownloader.GetResumableBytes(Request(data)));
    }

    [Fact]
    public async Task Download_416WithCompletePart_VerifiesWithoutDownloadingAgain()
    {
        var data = RandomBytes(100_000);
        // El primer cuerpo llega entero pero la conexión "se corta" antes del final: el .part queda completo.
        var server = new FakeFileServer(data) { FailuresRemaining = 1, FailAfterBytes = data.Length };
        var downloader = Downloader(server);

        await downloader.DownloadAsync(Request(data, size: null), null, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
        var requests = server.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Equal(data.Length, requests[1].RangeFrom);
        Assert.Equal(416, requests[1].Status);
        AssertNoPartials();
    }

    [Fact]
    public async Task Download_PartWithExpectedSize_GoesStraightToVerification()
    {
        var data = RandomBytes(50_000);
        WritePartial(data, "asset:123:test", data.Length);
        var server = new FakeFileServer(data);
        var downloader = Downloader(server);

        await downloader.DownloadAsync(Request(data), null, CancellationToken.None);

        Assert.Equal(0, server.RequestCount);
        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
        AssertNoPartials();
    }

    [Fact]
    public async Task Download_206WithDifferentETag_RestartsWithoutRange()
    {
        var oldData = RandomBytes(200_000, seed: 1);
        var newData = RandomBytes(200_000, seed: 2);
        var server = new FakeFileServer(oldData) { ETag = "\"A\"", FailuresRemaining = 1, FailAfterBytes = 80_000 };
        server.Override = (_, index, _) =>
        {
            if (index == 1)
            {
                // El desarrollador re-subió la build (mismo tamaño) entre el corte y la reanudación.
                server.Content = newData;
                server.ETag = "\"B\"";
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        var downloader = Downloader(server);

        await downloader.DownloadAsync(Request(newData, sha: null), null, CancellationToken.None);

        Assert.Equal(newData, await File.ReadAllBytesAsync(Dest));
        var requests = server.Requests;
        Assert.Equal(3, requests.Count);
        Assert.Null(requests[0].RangeFrom);
        Assert.Equal(80_000, requests[1].RangeFrom);
        Assert.Equal(206, requests[1].Status);
        Assert.Null(requests[2].RangeFrom);
        Assert.Equal(200, requests[2].Status);
    }

    [Fact]
    public async Task Download_ContentLengthDiffers_ThrowsRemoteAssetChanged()
    {
        var data = RandomBytes(10_000);
        var server = new FakeFileServer(data);
        var downloader = Downloader(server);

        await Assert.ThrowsAsync<RemoteAssetChangedException>(
            () => downloader.DownloadAsync(Request(data, sha: null, size: 20_000), null, CancellationToken.None));

        Assert.Equal(1, server.RequestCount);
        Assert.False(File.Exists(Dest));
    }

    [Fact]
    public async Task Download_ContentRangeTotalDiffers_ThrowsRemoteAssetChangedWithoutReadingBody()
    {
        var expected = RandomBytes(5_000);
        WritePartial(expected.AsSpan(0, 1_000).ToArray(), "asset:123:test", expected.Length);
        var server = new FakeFileServer(RandomBytes(6_000));
        var downloader = Downloader(server);

        await Assert.ThrowsAsync<RemoteAssetChangedException>(
            () => downloader.DownloadAsync(Request(expected, sha: null), null, CancellationToken.None));

        var request = Assert.Single(server.Requests);
        Assert.Equal(1_000, request.RangeFrom);
        Assert.Equal(1_000, new FileInfo(Dest + ".part").Length);
    }

    [Fact]
    public async Task Download_WrongHash_RetriesOnceFromZeroThenThrows()
    {
        var data = RandomBytes(80_000);
        var server = new FakeFileServer(data);
        var downloader = Downloader(server);
        var verifications = 0;
        downloader.VerificationStarted += (_, _) => verifications++;

        // Tipo propio: la UI lo distingue (por tipo, no por texto) de los demás errores.
        var ex = await Assert.ThrowsAsync<DownloadVerificationException>(
            () => downloader.DownloadAsync(Request(data, sha: Sha(RandomBytes(10))), null, CancellationToken.None));

        Assert.Equal(FileDownloader.CorruptedMessage, ex.Message);
        Assert.Equal(2, server.RequestCount);
        Assert.All(server.Requests, r => Assert.Null(r.RangeFrom));
        Assert.Equal(2, verifications);
        AssertNoPartials();
        Assert.False(File.Exists(Dest));
    }

    [Fact]
    public async Task Download_CorruptedFirstTime_SucceedsOnSecondDownload()
    {
        var data = RandomBytes(80_000);
        var corrupted = (byte[])data.Clone();
        corrupted[500] ^= 0xFF;
        var server = new FakeFileServer(corrupted);
        server.Override = (_, index, _) =>
        {
            if (index == 1)
            {
                server.Content = data;
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        var downloader = Downloader(server);

        await downloader.DownloadAsync(Request(data), null, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task Download_DestinationAlreadyComplete_MakesNoRequestAndCleansPartials()
    {
        var data = RandomBytes(30_000);
        Directory.CreateDirectory(Path.GetDirectoryName(Dest)!);
        await File.WriteAllBytesAsync(Dest, data);
        WritePartial(data.AsSpan(0, 100).ToArray(), "otra-clave", data.Length);
        var server = new FakeFileServer(data);
        var progress = new SyncProgress<DownloadProgress>();

        await Downloader(server).DownloadAsync(Request(data), progress, CancellationToken.None);

        Assert.Equal(0, server.RequestCount);
        AssertNoPartials();
        Assert.Equal(data.Length, progress.Values.Last().BytesReceived);
    }

    [Fact]
    public async Task Download_DestinationWithWrongContent_IsDownloadedAgain()
    {
        var data = RandomBytes(30_000);
        Directory.CreateDirectory(Path.GetDirectoryName(Dest)!);
        await File.WriteAllBytesAsync(Dest, RandomBytes(30_000, seed: 99));
        var server = new FakeFileServer(data);

        await Downloader(server).DownloadAsync(Request(data), null, CancellationToken.None);

        Assert.Equal(1, server.RequestCount);
        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
    }

    [Fact]
    public async Task Download_Cancelled_LeavesResumablePart_ThenResumes()
    {
        var data = RandomBytes(400_000);
        var server = new FakeFileServer(data) { ChunkDelay = TimeSpan.FromMilliseconds(15), ChunkSize = 8 * 1024 };
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<DownloadProgress>(p =>
        {
            if (p.BytesReceived >= 40_000)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Downloader(server).DownloadAsync(Request(data), progress, cts.Token));

        Assert.True(File.Exists(Dest + ".part"));
        Assert.True(File.Exists(Dest + ".part.json"));
        var partLength = new FileInfo(Dest + ".part").Length;
        Assert.InRange(partLength, 40_000, data.Length - 1);
        Assert.Equal(partLength, FileDownloader.GetResumableBytes(Request(data)));
        Assert.False(File.Exists(Dest));

        var server2 = new FakeFileServer(data);
        await Downloader(server2).DownloadAsync(Request(data), null, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
        Assert.Equal(partLength, Assert.Single(server2.Requests).RangeFrom);
    }

    [Fact]
    public void GetResumableBytes_DifferentResumeKeyOrCorruptMetadata_ReturnsZero()
    {
        var data = RandomBytes(10_000);
        WritePartial(data.AsSpan(0, 4_000).ToArray(), "asset:123:test", data.Length);

        Assert.Equal(4_000, FileDownloader.GetResumableBytes(Request(data)));
        Assert.Equal(0, FileDownloader.GetResumableBytes(Request(data, resumeKey: "asset:999:otro")));

        File.WriteAllText(Dest + ".part.json", "{ esto no es json");
        Assert.Equal(0, FileDownloader.GetResumableBytes(Request(data)));

        File.Delete(Dest + ".part.json");
        Assert.Equal(0, FileDownloader.GetResumableBytes(Request(data)));
    }

    [Fact]
    public void GetResumableBytes_NoPart_ReturnsZero()
    {
        Assert.Equal(0, FileDownloader.GetResumableBytes(Request(RandomBytes(10))));
    }

    [Fact]
    public async Task Download_CorruptMetadata_StartsFromZeroWithoutThrowing()
    {
        var data = RandomBytes(60_000);
        Directory.CreateDirectory(Path.GetDirectoryName(Dest)!);
        await File.WriteAllBytesAsync(Dest + ".part", RandomBytes(20_000, seed: 5));
        await File.WriteAllTextAsync(Dest + ".part.json", "\0\0basura");
        var server = new FakeFileServer(data);

        await Downloader(server).DownloadAsync(Request(data), null, CancellationToken.None);

        Assert.Null(Assert.Single(server.Requests).RangeFrom);
        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
    }

    [Fact]
    public async Task Download_DifferentResumeKey_DiscardsOldPart()
    {
        var data = RandomBytes(60_000);
        WritePartial(RandomBytes(20_000, seed: 5), "asset:1:viejo", data.Length);
        var server = new FakeFileServer(data);

        await Downloader(server).DownloadAsync(Request(data), null, CancellationToken.None);

        Assert.Null(Assert.Single(server.Requests).RangeFrom);
        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
    }

    [Fact]
    public async Task Download_PartLargerThanExpected_IsDeletedAndRestarted()
    {
        var data = RandomBytes(10_000);
        WritePartial(RandomBytes(12_000), "asset:123:test", data.Length);
        var server = new FakeFileServer(data);

        await Downloader(server).DownloadAsync(Request(data), null, CancellationToken.None);

        Assert.Null(Assert.Single(server.Requests).RangeFrom);
        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
    }

    [Fact]
    public async Task Download_SendNeverResponds_StallRetriesAndFails()
    {
        var data = RandomBytes(1_000);
        var server = new FakeFileServer(data)
        {
            Override = async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            },
        };
        var downloader = Downloader(server, stall: TimeSpan.FromMilliseconds(200));
        var retries = new List<DownloadRetryInfo>();
        downloader.RetryScheduled += (_, info) => retries.Add(info);

        var ex = await Assert.ThrowsAsync<LauncherException>(() => downloader.DownloadAsync(Request(data), null, CancellationToken.None));

        Assert.Equal(FileDownloader.GiveUpMessage, ex.Message);
        Assert.Equal(8, server.RequestCount);
        Assert.Equal(7, retries.Count);
    }

    [Fact]
    public async Task Download_BodyNeverSendsBytes_StallRetriesAndFails()
    {
        var data = RandomBytes(5_000);
        var server = new FakeFileServer(data)
        {
            Override = (_, _, _) => Task.FromResult<HttpResponseMessage?>(
                FakeFileServer.Ok(new FakeBodyStream(data) { HangAfter = 0 }, contentLength: data.Length)),
        };
        var downloader = Downloader(server, stall: TimeSpan.FromMilliseconds(200));

        var ex = await Assert.ThrowsAsync<LauncherException>(() => downloader.DownloadAsync(Request(data), null, CancellationToken.None));

        Assert.Equal(FileDownloader.GiveUpMessage, ex.Message);
        Assert.Equal(8, server.RequestCount);
    }

    [Fact]
    public async Task Download_CancelWhileWaitingForHeaders_ThrowsOperationCanceled()
    {
        var server = new FakeFileServer(RandomBytes(10))
        {
            Override = async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            },
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Downloader(server).DownloadAsync(Request(RandomBytes(10)), null, cts.Token));
    }

    [Fact]
    public async Task Download_SpeedLimit_IsApproximatelyRespected()
    {
        var data = RandomBytes(200_000);
        var server = new FakeFileServer(data) { ChunkSize = 81920 };
        var stopwatch = Stopwatch.StartNew();

        await Downloader(server).DownloadAsync(Request(data, maxBps: 400_000), null, CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
        // 200 KB a 400 KB/s ≈ 0,5 s (con algo de margen por la ráfaga inicial del cubo).
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 0.3, 5);
    }

    [Fact]
    public async Task Download_NotFoundOnGitHub_FailsImmediately()
    {
        var server = new FakeFileServer(RandomBytes(10))
        {
            Override = (_, _, _) => Task.FromResult<HttpResponseMessage?>(FakeFileServer.Status(HttpStatusCode.NotFound)),
        };

        var ex = await Assert.ThrowsAsync<LauncherException>(
            () => Downloader(server).DownloadAsync(Request(RandomBytes(10)), null, CancellationToken.None));

        Assert.Equal(FileDownloader.NotAvailableMessage, ex.Message);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Download_ForbiddenOnCdn_RetriedOnceThenFails()
    {
        var server = new FakeFileServer(RandomBytes(10))
        {
            FinalUri = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/123?sig=caducada"),
            Override = (_, _, _) => Task.FromResult<HttpResponseMessage?>(FakeFileServer.Status(HttpStatusCode.Forbidden)),
        };

        var ex = await Assert.ThrowsAsync<LauncherException>(
            () => Downloader(server).DownloadAsync(Request(RandomBytes(10)), null, CancellationToken.None));

        Assert.Equal(FileDownloader.NotAvailableMessage, ex.Message);
        Assert.Equal(2, server.RequestCount);
        // Siempre se vuelve a pedir la URL original, nunca la firmada.
        Assert.All(server.Requests, r => Assert.Equal(Url, r.Uri.ToString()));
    }

    [Fact]
    public async Task Download_ForbiddenOnCdnOnce_ThenSucceeds()
    {
        var data = RandomBytes(20_000);
        var server = new FakeFileServer(data)
        {
            FinalUri = new Uri("https://release-assets.githubusercontent.com/asset"),
        };
        server.Override = (_, index, _) => Task.FromResult(index == 0 ? FakeFileServer.Status(HttpStatusCode.Forbidden) : null);

        await Downloader(server).DownloadAsync(Request(data), null, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task Download_ServerErrorThenOk_Succeeds()
    {
        var data = RandomBytes(20_000);
        var server = new FakeFileServer(data);
        server.Override = (_, index, _) => Task.FromResult(index < 2 ? FakeFileServer.Status(HttpStatusCode.ServiceUnavailable) : null);
        var retries = new List<DownloadRetryInfo>();
        var downloader = Downloader(server);
        downloader.RetryScheduled += (_, info) => retries.Add(info);

        await downloader.DownloadAsync(Request(data), null, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
        Assert.Equal(3, server.RequestCount);
        Assert.Equal(new[] { 2, 3 }, retries.Select(r => r.Attempt));
    }

    [Fact]
    public async Task Download_TlsErrorTwice_ThrowsTlsMessage()
    {
        var server = new FakeFileServer(RandomBytes(10))
        {
            Override = (_, _, _) => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "TLS simulado"),
        };

        var ex = await Assert.ThrowsAsync<LauncherException>(
            () => Downloader(server).DownloadAsync(Request(RandomBytes(10)), null, CancellationToken.None));

        Assert.Equal(FileDownloader.TlsMessage, ex.Message);
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task Download_NoDns_EndsWithNoInternetMessage()
    {
        var server = new FakeFileServer(RandomBytes(10))
        {
            Override = (_, _, _) => throw new HttpRequestException(HttpRequestError.NameResolutionError, "DNS simulado"),
        };
        var downloader = Downloader(server);
        var reasons = new List<string>();
        downloader.RetryScheduled += (_, info) => reasons.Add(info.Reason);

        var ex = await Assert.ThrowsAsync<LauncherException>(
            () => downloader.DownloadAsync(Request(RandomBytes(10)), null, CancellationToken.None));

        Assert.Equal(FileDownloader.NoInternetMessage, ex.Message);
        Assert.Equal(8, server.RequestCount);
        Assert.All(reasons, r => Assert.Equal("Sin conexión a Internet", r));
    }

    [Fact]
    public async Task Download_MoreBytesThanExpected_AbortsAttemptAndRestarts()
    {
        var data = RandomBytes(30_000);
        var server = new FakeFileServer(data);
        server.Override = (_, index, _) => Task.FromResult(index == 0
            ? FakeFileServer.Ok(new FakeBodyStream(RandomBytes(45_000)))
            : null);

        await Downloader(server).DownloadAsync(Request(data), null, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
        Assert.Equal(2, server.RequestCount);
        Assert.Null(server.Requests[1].RangeFrom);
    }

    [Fact]
    public async Task Download_WithoutExpectedSizeOrHash_DownloadsWhatServerSends()
    {
        var data = RandomBytes(25_000);
        var server = new FakeFileServer(data) { OmitContentLength = true };

        await Downloader(server).DownloadAsync(Request(data, sha: null, size: null, resumeKey: null), null, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
        AssertNoPartials();
    }

    [Fact]
    public async Task Download_PausedBody_ReportsHeartbeatWithoutEta()
    {
        var data = RandomBytes(100_000);
        var server = new FakeFileServer(data)
        {
            Override = (_, _, _) => Task.FromResult<HttpResponseMessage?>(FakeFileServer.Ok(
                new FakeBodyStream(data) { PauseAt = 20_000, PauseDuration = TimeSpan.FromMilliseconds(2500) },
                contentLength: data.Length)),
        };
        var progress = new SyncProgress<DownloadProgress>();

        await Downloader(server).DownloadAsync(Request(data), progress, CancellationToken.None);

        var stalled = progress.Values.Where(p => p.BytesReceived == 20_000).ToList();
        Assert.True(stalled.Count >= 2, $"Se esperaban latidos durante la pausa; hubo {stalled.Count}");
        Assert.Contains(stalled, p => p.Eta is null);
        // Nunca más de ~1,1 s entre reportes mientras dura la descarga.
        var timeline = progress.Timeline;
        for (var i = 1; i < timeline.Count; i++)
        {
            Assert.True((timeline[i].At - timeline[i - 1].At).TotalSeconds < 1.5, "Hueco de más de 1,5 s sin reportes");
        }
    }

    [Fact]
    public async Task Download_ShaWithPrefixAndUppercase_IsAccepted()
    {
        var data = RandomBytes(5_000);
        var server = new FakeFileServer(data);

        await Downloader(server).DownloadAsync(Request(data, sha: "sha256:" + Sha(data).ToUpperInvariant()), null, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
    }

    [Fact]
    public async Task Download_InvalidRequest_ThrowsArgumentException()
    {
        var downloader = Downloader(new FakeFileServer([]));
        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync(
            new DownloadRequest { Url = "no-es-una-url", DestinationPath = Dest }, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync(
            new DownloadRequest { Url = Url, DestinationPath = Dest, ExpectedSha256 = "xyz" }, null, CancellationToken.None));
    }

    [Fact]
    public void DeletePartial_RemovesPartAndMetadata_AndNeverThrows()
    {
        WritePartial(RandomBytes(100), "k", 1000);
        File.SetAttributes(Dest + ".part", FileAttributes.ReadOnly);

        FileDownloader.DeletePartial(Dest);

        AssertNoPartials();
        FileDownloader.DeletePartial(Dest);
        FileDownloader.DeletePartial("");
    }

    [Fact]
    public async Task ComputeSha256Async_MatchesSha256_EvenWhenFileIsOpenForWriting()
    {
        var data = RandomBytes(123_456);
        var path = _temp.WriteFile("hash.bin", data);
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        var hash = await FileDownloader.ComputeSha256Async(path, CancellationToken.None);

        Assert.Equal(Sha(data), hash);
    }

    [Fact]
    public async Task Download_DiskWriteUnauthorized_ThrowsPermissionMessage()
    {
        var data = RandomBytes(1_000);
        Directory.CreateDirectory(Path.GetDirectoryName(Dest)!);
        // Una carpeta con el nombre del .part impide crear el archivo (UnauthorizedAccessException en Windows).
        Directory.CreateDirectory(Dest + ".part");
        var server = new FakeFileServer(data);

        var ex = await Assert.ThrowsAsync<LauncherException>(
            () => Downloader(server).DownloadAsync(Request(data), null, CancellationToken.None));

        Assert.StartsWith("Sin permisos para escribir en", ex.Message);
    }

    // ───────────────────────── Fallos que no son de red ni de la suma ─────────────────────────

    /// <summary>Descarga dentro de "&lt;raíz&gt;\.launcher\downloads" como la del instalador (la exclusión sugerida es la raíz).</summary>
    private (string Root, DownloadRequest Request) InstallerLikeRequest(byte[] data)
    {
        var root = _temp.Combine("Coop2D");
        return (root, new DownloadRequest
        {
            Url = Url,
            DestinationPath = Path.Combine(root, ".launcher", "downloads", "123-build.zip"),
            ExpectedSize = data.Length,
            ExpectedSha256 = Sha(data),
            ResumeKey = "asset:123:test",
        });
    }

    [Fact]
    public async Task Download_PartQuarantinedBeforeVerification_ReportsAntivirusWithoutDownloadingAgain()
    {
        var data = RandomBytes(60_000);
        var (root, request) = InstallerLikeRequest(data);
        var server = new FakeFileServer(data);
        var downloader = Downloader(server);
        // El antivirus se lleva el archivo justo al cerrarlo.
        downloader.VerificationStarted += (_, _) => File.Delete(request.DestinationPath + ".part");

        var ex = await Assert.ThrowsAsync<LauncherException>(() => downloader.DownloadAsync(request, null, CancellationToken.None));

        Assert.Equal(FileDownloader.AntivirusMessage(root), ex.Message);
        Assert.Equal(1, server.RequestCount);
        Assert.False(File.Exists(request.DestinationPath));
    }

    [Fact]
    public async Task Download_PartReadDeniedAtVerification_ReportsAntivirusKeepsPartAndResumesLater()
    {
        var data = RandomBytes(60_000);
        var (root, request) = InstallerLikeRequest(data);
        var part = request.DestinationPath + ".part";
        var server = new FakeFileServer(data);
        var downloader = Downloader(server);
        IDisposable? block = null;
        downloader.VerificationStarted += (_, _) => block ??= FileBlocks.DenyRead(part);

        try
        {
            var ex = await Assert.ThrowsAsync<LauncherException>(() => downloader.DownloadAsync(request, null, CancellationToken.None));

            Assert.Equal(FileDownloader.AntivirusMessage(root), ex.Message);
            Assert.DoesNotContain("suma de verificación", ex.Message);
            Assert.Equal(1, server.RequestCount);
            Assert.Equal(data.Length, new FileInfo(part).Length);
        }
        finally
        {
            block?.Dispose();
        }

        // Con la exclusión puesta, se verifica lo ya descargado sin pedir nada.
        var server2 = new FakeFileServer(data);
        await Downloader(server2).DownloadAsync(request, null, CancellationToken.None);

        Assert.Equal(0, server2.RequestCount);
        Assert.Equal(data, await File.ReadAllBytesAsync(request.DestinationPath));
    }

    [Fact]
    public async Task Download_PartLockedByAnotherProgramAtVerification_ReportsReadErrorAndKeepsPart()
    {
        var data = RandomBytes(40_000);
        var server = new FakeFileServer(data);
        var downloader = Downloader(server);
        FileStream? holder = null;
        downloader.VerificationStarted += (_, _) => holder ??= FileBlocks.LockExclusive(Dest + ".part");

        try
        {
            var ex = await Assert.ThrowsAsync<LauncherException>(() => downloader.DownloadAsync(Request(data), null, CancellationToken.None));

            Assert.Equal(FileDownloader.ReadErrorMessage, ex.Message);
            Assert.Equal(1, server.RequestCount);
        }
        finally
        {
            holder?.Dispose();
        }

        Assert.Equal(data.Length, new FileInfo(Dest + ".part").Length);
        var server2 = new FakeFileServer(data);
        await Downloader(server2).DownloadAsync(Request(data), null, CancellationToken.None);
        Assert.Equal(0, server2.RequestCount);
        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
    }

    [Fact]
    public async Task Download_ExistingDestinationBlockedByAntivirus_ReportsAntivirusWithoutRequests()
    {
        var data = RandomBytes(30_000);
        var (root, request) = InstallerLikeRequest(data);
        Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
        await File.WriteAllBytesAsync(request.DestinationPath, data);
        var server = new FakeFileServer(data);

        using (FileBlocks.DenyRead(request.DestinationPath))
        {
            var ex = await Assert.ThrowsAsync<LauncherException>(
                () => Downloader(server).DownloadAsync(request, null, CancellationToken.None));

            Assert.Equal(FileDownloader.AntivirusMessage(root), ex.Message);
        }

        Assert.Equal(0, server.RequestCount);
        Assert.True(File.Exists(request.DestinationPath));
    }

    [Fact]
    public async Task Download_DriveMissing_FailsImmediatelyWithDriveMessageAndNoRequests()
    {
        var missing = FileBlocks.FindMissingDriveLetter();
        if (missing is null)
        {
            return;
        }
        var data = RandomBytes(1_000);
        var server = new FakeFileServer(data);
        var request = new DownloadRequest
        {
            Url = Url,
            DestinationPath = $@"{missing}:\Juegos\Coop2D\.launcher\downloads\x.zip",
            ExpectedSize = data.Length,
        };

        var ex = await Assert.ThrowsAsync<LauncherException>(() => Downloader(server).DownloadAsync(request, null, CancellationToken.None));

        Assert.Equal(FileDownloader.DriveMissingMessage($@"{missing}:\"), ex.Message);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task Download_PersistentLocalWriteError_GivesUpAfterOneRetryWithDiskMessage()
    {
        var data = RandomBytes(20_000);
        var server = new FakeFileServer(data);
        FileStream? holder = null;
        server.Override = (_, index, _) =>
        {
            if (index == 0)
            {
                // Otro programa se queda el .part en exclusiva: escribir falla con un error de disco "desconocido".
                holder = FileBlocks.LockExclusive(Dest + ".part", FileMode.CreateNew);
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        var downloader = Downloader(server);
        var retries = new List<DownloadRetryInfo>();
        downloader.RetryScheduled += (_, info) => retries.Add(info);

        try
        {
            var ex = await Assert.ThrowsAsync<LauncherException>(() => downloader.DownloadAsync(Request(data), null, CancellationToken.None));

            Assert.Equal(FileDownloader.DiskErrorMessage(Path.GetPathRoot(Dest)!), ex.Message);
            Assert.Equal(FileDownloader.MaxConsecutiveLocalFileFailures, server.RequestCount);
            Assert.Single(retries);
        }
        finally
        {
            holder?.Dispose();
        }
    }

    [Fact]
    public async Task Download_TlsFailuresSeparatedByProgress_DoNotAbort()
    {
        var data = RandomBytes(300_000);
        // Primera respuesta normal cortada a 100 KB; fallos TLS antes y después (no seguidos).
        var server = new FakeFileServer(data) { FailuresRemaining = 1, FailAfterBytes = 100_000 };
        server.Override = (_, index, _) => index is 0 or 2
            ? throw new HttpRequestException(HttpRequestError.SecureConnectionError, "TLS simulado")
            : Task.FromResult<HttpResponseMessage?>(null);

        await Downloader(server).DownloadAsync(Request(data), null, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
        Assert.Equal(4, server.RequestCount);
    }

    [Fact]
    public async Task Download_CdnSignatureExpiresTwiceWithProgressBetween_RetriesEachTime()
    {
        var data = RandomBytes(300_000);
        var server = new FakeFileServer(data)
        {
            FinalUri = new Uri("https://release-assets.githubusercontent.com/asset"),
            FailuresRemaining = 1,
            FailAfterBytes = 100_000,
        };
        server.Override = (_, index, _) => Task.FromResult(index is 0 or 2 ? FakeFileServer.Status(HttpStatusCode.Forbidden) : null);

        await Downloader(server).DownloadAsync(Request(data), null, CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
        Assert.Equal(4, server.RequestCount);
    }

    [Fact]
    public async Task Download_ResumeRequestHeaders_UseOriginalUrlAndRange()
    {
        var data = RandomBytes(60_000);
        WritePartial(data.AsSpan(0, 10_000).ToArray(), "asset:123:test", data.Length);
        RangeHeaderValue? seenRange = null;
        var server = new FakeFileServer(data);
        server.Override = (request, _, _) =>
        {
            seenRange = request.Headers.Range;
            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await Downloader(server).DownloadAsync(Request(data), null, CancellationToken.None);

        Assert.NotNull(seenRange);
        Assert.Equal("bytes=10000-", seenRange!.ToString());
        Assert.Equal(data, await File.ReadAllBytesAsync(Dest));
    }
}
