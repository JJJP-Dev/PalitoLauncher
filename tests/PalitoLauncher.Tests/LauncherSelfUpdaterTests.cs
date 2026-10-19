using System.Diagnostics;
using System.Security.Cryptography;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Downloads;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Update;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Core.Versioning;
using PalitoLauncher.Tests.TestHelpers.F;

namespace PalitoLauncher.Tests;

public class LauncherSelfUpdaterTests
{
    private const string LatestPath = "/repos/JJJP-Dev/PalitoLauncher/releases/latest";
    private static readonly string Sha = new('a', 64);

    private static string ReleaseJson(string tag, string assetName = "PalitoLauncher.exe") => $$"""
        {
          "id": 100,
          "tag_name": "{{tag}}",
          "name": "Palito Launcher {{tag}}",
          "body": "Cambios",
          "draft": false,
          "prerelease": false,
          "published_at": "2026-10-01T10:00:00Z",
          "html_url": "https://github.com/JJJP-Dev/PalitoLauncher/releases/tag/{{tag}}",
          "assets": [
            {
              "id": 200,
              "name": "{{assetName}}",
              "size": 123456,
              "state": "uploaded",
              "content_type": "application/x-msdownload",
              "browser_download_url": "https://github.com/JJJP-Dev/PalitoLauncher/releases/download/{{tag}}/{{assetName}}",
              "updated_at": "2026-10-01T10:00:00Z",
              "digest": "sha256:{{Sha}}"
            }
          ]
        }
        """;

    /// <summary>Handler que sirve /releases/latest con la release dada (o 404 si es null).</summary>
    private static FakeHttpHandler LatestHandler(string? json, int remaining = 55) => new(req =>
        req.RequestUri!.AbsolutePath.EndsWith(LatestPath, StringComparison.OrdinalIgnoreCase) && json != null
            ? FakeHttpHandler.Json(json, remaining)
            : FakeHttpHandler.NotFound(remaining));

    private static LauncherSelfUpdater CreateUpdater(FakeHttpHandler handler, string githubCacheDir, string updaterCacheDir,
        GameVersion? current = null)
    {
        var http = new HttpClient(handler);
        return new LauncherSelfUpdater(new GitHubClient(http, githubCacheDir), new FileDownloader(http) { RetryDelayScale = 0 },
            updaterCacheDir)
        {
            CurrentVersionOverride = current ?? new GameVersion(1, 0, 0),
        };
    }

    private static LauncherUpdateInfo MakeInfo(byte[] payload, string version = "9.0.0")
    {
        var asset = new ReleaseAsset
        {
            Id = 200,
            Name = "PalitoLauncher.exe",
            Size = payload.Length,
            DownloadUrl = $"https://github.com/JJJP-Dev/PalitoLauncher/releases/download/v{version}/PalitoLauncher.exe",
            Sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
        };
        var release = new ReleaseInfo { Id = 100, TagName = "v" + version, Assets = new[] { asset } };
        Assert.True(GameVersion.TryParse(version, out var v));
        return new LauncherUpdateInfo(v, release, asset);
    }

    // ---------------------------------------------------------------- versión / entorno

    [Fact]
    public void CurrentVersion_IsReadable()
    {
        Assert.NotNull(LauncherSelfUpdater.CurrentVersion);
        Assert.True(LauncherSelfUpdater.CurrentVersion > new GameVersion(0, 0, 0));
        Assert.Equal(LauncherSelfUpdater.CurrentVersion.ToString(), LauncherSelfUpdater.CurrentVersionText);
        Assert.Equal(Environment.ProcessPath, LauncherSelfUpdater.CurrentExePath);
    }

    [Fact]
    public void CanSelfUpdate_InTestHost_IsFalseWithReason()
    {
        Assert.False(LauncherSelfUpdater.CanSelfUpdate(out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void SignalAlive_WithoutWaiter_DoesNotThrow()
    {
        LauncherSelfUpdater.SignalAlive();
    }

    [Fact]
    public void SignalAlive_SetsExistingEvent()
    {
        using var ev = new EventWaitHandle(false, EventResetMode.ManualReset, LauncherSelfUpdater.UpdateAliveEventName);
        ev.Reset();

        LauncherSelfUpdater.SignalAlive();

        Assert.True(ev.WaitOne(0));
    }

    // ---------------------------------------------------------------- Evaluate (puro)

    [Fact]
    public void Evaluate_NewerWithExactAsset_ReturnsInfo()
    {
        var release = new ReleaseInfo
        {
            TagName = "v1.2.0",
            Assets = new[]
            {
                new ReleaseAsset { Name = "PalitoLauncher.pdb", DownloadUrl = "https://x/pdb" },
                new ReleaseAsset { Name = "palitolauncher.EXE", DownloadUrl = "https://x/exe" },
            },
        };

        var info = LauncherSelfUpdater.Evaluate(release, new GameVersion(1, 0, 0));

        Assert.NotNull(info);
        Assert.Equal("1.2.0", info.VersionText);
        Assert.Equal("https://x/exe", info.Asset.DownloadUrl);
    }

    [Fact]
    public void Evaluate_RejectsDraftsOlderAndMissingAsset()
    {
        var current = new GameVersion(1, 0, 0);
        var exe = new[] { new ReleaseAsset { Name = "PalitoLauncher.exe", DownloadUrl = "https://x/exe" } };

        Assert.Null(LauncherSelfUpdater.Evaluate(new ReleaseInfo { TagName = "v2.0.0", IsDraft = true, Assets = exe }, current));
        Assert.Null(LauncherSelfUpdater.Evaluate(new ReleaseInfo { TagName = "v1.0.0", Assets = exe }, current));
        Assert.Null(LauncherSelfUpdater.Evaluate(new ReleaseInfo { TagName = "v0.9.0", Assets = exe }, current));
        Assert.Null(LauncherSelfUpdater.Evaluate(new ReleaseInfo { TagName = "sin-version", Assets = exe }, current));
        Assert.Null(LauncherSelfUpdater.Evaluate(new ReleaseInfo
        {
            TagName = "v2.0.0",
            Assets = new[] { new ReleaseAsset { Name = "PalitoLauncher-setup.exe", DownloadUrl = "https://x/setup" } },
        }, current));
    }

    [Fact]
    public void Evaluate_TagWithoutVersion_FallsBackToName()
    {
        var release = new ReleaseInfo
        {
            TagName = "ultima",
            Name = "Palito Launcher 1.5.0",
            Assets = new[] { new ReleaseAsset { Name = "PalitoLauncher.exe", DownloadUrl = "https://x/exe" } },
        };

        Assert.Equal("1.5.0", LauncherSelfUpdater.Evaluate(release, new GameVersion(1, 0, 0))?.VersionText);
    }

    // ---------------------------------------------------------------- CheckAsync (handler falso, sin red)

    [Fact]
    public async Task CheckAsync_NewerVersion_ReturnsInfo()
    {
        using var tmp = new TempDir();
        var handler = LatestHandler(ReleaseJson("v9.0.0"));
        var updater = CreateUpdater(handler, tmp.Combine("gh"), tmp.Combine("cache"));

        var info = await updater.CheckAsync(CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal("9.0.0", info.VersionText);
        Assert.Equal("PalitoLauncher.exe", info.Asset.Name);
        Assert.Equal(Sha, info.Asset.Sha256);
        Assert.Equal(1, handler.CountRequests(LatestPath));
        Assert.True(File.Exists(tmp.Combine("cache", LauncherSelfUpdater.CheckStateFileName)));
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("v0.9.0")]
    [InlineData("1.0.0")]
    public async Task CheckAsync_SameOrOlderVersion_ReturnsNull(string tag)
    {
        using var tmp = new TempDir();
        var updater = CreateUpdater(LatestHandler(ReleaseJson(tag)), tmp.Combine("gh"), tmp.Combine("cache"));

        Assert.Null(await updater.CheckAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("PalitoLauncher-setup.exe")]
    [InlineData("PalitoLauncher.exe.zip")]
    [InlineData("OtroLauncher.exe")]
    public async Task CheckAsync_WithoutExactAsset_ReturnsNull(string assetName)
    {
        using var tmp = new TempDir();
        var updater = CreateUpdater(LatestHandler(ReleaseJson("v9.0.0", assetName)), tmp.Combine("gh"), tmp.Combine("cache"));

        Assert.Null(await updater.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_NotFound_ReturnsNull()
    {
        using var tmp = new TempDir();
        var updater = CreateUpdater(LatestHandler(null), tmp.Combine("gh"), tmp.Combine("cache"));

        Assert.Null(await updater.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_RespectsIntervalUnlessForced()
    {
        using var tmp = new TempDir();
        var cache = tmp.Combine("cache");
        var json = ReleaseJson("v9.0.0");

        // 1) Primera comprobación: consulta y guarda el resultado.
        var h1 = LatestHandler(json);
        Assert.NotNull(await CreateUpdater(h1, tmp.Combine("gh1"), cache).CheckAsync(CancellationToken.None));
        Assert.Equal(1, h1.CountRequests(LatestPath));

        // 2) Dentro del intervalo (otra instancia, mismo archivo de estado): resultado guardado sin preguntar.
        var h2 = LatestHandler(json);
        var saved = await CreateUpdater(h2, tmp.Combine("gh2"), cache).CheckAsync(CancellationToken.None);
        Assert.Equal("9.0.0", saved?.VersionText);
        Assert.Equal("PalitoLauncher.exe", saved?.Asset.Name);
        Assert.Empty(h2.Requests);

        // 3) El resultado guardado ya no es mayor que la versión actual → null, sin preguntar.
        var h3 = LatestHandler(json);
        Assert.Null(await CreateUpdater(h3, tmp.Combine("gh3"), cache, new GameVersion(9, 0, 0)).CheckAsync(CancellationToken.None));
        Assert.Empty(h3.Requests);

        // 4) force ignora el intervalo.
        var h4 = LatestHandler(json);
        Assert.NotNull(await CreateUpdater(h4, tmp.Combine("gh4"), cache).CheckAsync(CancellationToken.None, force: true));
        Assert.Equal(1, h4.CountRequests(LatestPath));

        // 5) Pasado el intervalo vuelve a preguntar.
        var h5 = LatestHandler(json);
        var later = CreateUpdater(h5, tmp.Combine("gh5"), cache);
        later.UtcNow = () => DateTimeOffset.UtcNow + LauncherConfig.LauncherUpdateCheckInterval + TimeSpan.FromMinutes(5);
        Assert.NotNull(await later.CheckAsync(CancellationToken.None));
        Assert.Equal(1, h5.CountRequests(LatestPath));
    }

    [Fact]
    public async Task CheckAsync_LowRateLimit_SkipsUnlessForced()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith(LatestPath, StringComparison.OrdinalIgnoreCase)
                ? FakeHttpHandler.Json(ReleaseJson("v9.0.0"), rateLimitRemaining: 4)
                : FakeHttpHandler.Json("[]", rateLimitRemaining: 5));
        var http = new HttpClient(handler);
        var github = new GitHubClient(http, tmp.Combine("gh"));
        var updater = new LauncherSelfUpdater(github, new FileDownloader(http), tmp.Combine("cache"))
        {
            CurrentVersionOverride = new GameVersion(1, 0, 0),
        };

        // Una consulta previa deja el cupo en 5 (≤ reserva).
        await github.GetReleasesAsync("JJJP-Dev", "Coop2D", CancellationToken.None);
        Assert.Equal(5, github.RateLimitRemaining);

        Assert.Null(await updater.CheckAsync(CancellationToken.None));
        Assert.Equal(0, handler.CountRequests(LatestPath));

        Assert.NotNull(await updater.CheckAsync(CancellationToken.None, force: true));
        Assert.Equal(1, handler.CountRequests(LatestPath));
    }

    [Fact]
    public async Task CheckAsync_CorruptStateFile_IsIgnored()
    {
        using var tmp = new TempDir();
        tmp.WriteFile(Path.Combine("cache", LauncherSelfUpdater.CheckStateFileName), "{ esto no es json");
        var handler = LatestHandler(ReleaseJson("v9.0.0"));
        var updater = CreateUpdater(handler, tmp.Combine("gh"), tmp.Combine("cache"));

        Assert.NotNull(await updater.CheckAsync(CancellationToken.None));
        Assert.Equal(1, handler.CountRequests(LatestPath));
    }

    [Fact]
    public async Task CheckAsync_Cancelled_ThrowsOperationCanceled()
    {
        using var tmp = new TempDir();
        var updater = CreateUpdater(LatestHandler(ReleaseJson("v9.0.0")), tmp.Combine("gh"), tmp.Combine("cache"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.CheckAsync(cts.Token));
    }

    // ---------------------------------------------------------------- renombrados / limpieza

    [Fact]
    public void SwapExecutables_MovesCurrentToOldAndNewToCurrent()
    {
        using var tmp = new TempDir();
        var exe = tmp.WriteFile("PalitoLauncher.exe", "viejo");
        tmp.WriteFile("PalitoLauncher.exe.new", "nuevo");
        tmp.WriteFile("PalitoLauncher.exe.old", "resto de otra actualización");

        LauncherSelfUpdater.SwapExecutables(exe, exe + ".new", exe + ".old", attempts: 2, delayMs: 10);

        Assert.Equal("nuevo", File.ReadAllText(exe));
        Assert.Equal("viejo", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));
    }

    [Fact]
    public void SwapExecutables_SecondStepFails_RollsBack()
    {
        using var tmp = new TempDir();
        var exe = tmp.WriteFile("PalitoLauncher.exe", "viejo");

        // No existe ".new": el segundo paso falla y se deshace el primero.
        Assert.Throws<LauncherException>(() =>
            LauncherSelfUpdater.SwapExecutables(exe, exe + ".new", exe + ".old", attempts: 2, delayMs: 10));

        Assert.Equal("viejo", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public void SwapExecutables_CurrentLocked_ThrowsAndKeepsEverything()
    {
        using var tmp = new TempDir();
        var exe = tmp.WriteFile("PalitoLauncher.exe", "viejo");
        tmp.WriteFile("PalitoLauncher.exe.new", "nuevo");

        using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<LauncherException>(() =>
                LauncherSelfUpdater.SwapExecutables(exe, exe + ".new", exe + ".old", attempts: 2, delayMs: 10));
        }

        Assert.Equal("viejo", File.ReadAllText(exe));
        Assert.Equal("nuevo", File.ReadAllText(exe + ".new"));
    }

    [Fact]
    public void RestoreAfterFailedStart_PutsOldBackAndKeepsFailed()
    {
        using var tmp = new TempDir();
        var exe = tmp.WriteFile("PalitoLauncher.exe", "nuevo roto");
        tmp.WriteFile("PalitoLauncher.exe.old", "viejo");

        Assert.True(LauncherSelfUpdater.RestoreAfterFailedStart(exe, exe + ".old", exe + ".failed", attempts: 2, delayMs: 10));

        Assert.Equal("viejo", File.ReadAllText(exe));
        Assert.Equal("nuevo roto", File.ReadAllText(exe + ".failed"));
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public void RestoreAfterFailedStart_WithoutOldCopy_ReturnsFalseAndKeepsExe()
    {
        using var tmp = new TempDir();
        var exe = tmp.WriteFile("PalitoLauncher.exe", "nuevo roto");

        Assert.False(LauncherSelfUpdater.RestoreAfterFailedStart(exe, exe + ".old", exe + ".failed", attempts: 2, delayMs: 10));

        Assert.Equal("nuevo roto", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".failed"));
    }

    [Fact]
    public void RestoreAfterFailedStart_OldCannotBeRenamed_CopiesIt()
    {
        using var tmp = new TempDir();
        var exe = tmp.WriteFile("PalitoLauncher.exe", "nuevo roto");
        var old = tmp.WriteFile("PalitoLauncher.exe.old", "viejo");

        // Abierto solo para leer (sin FILE_SHARE_DELETE): no se puede renombrar pero sí copiar.
        bool restored;
        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            restored = LauncherSelfUpdater.RestoreAfterFailedStart(exe, old, exe + ".failed", attempts: 2, delayMs: 10);
        }

        Assert.True(restored);
        Assert.Equal("viejo", File.ReadAllText(exe));
        Assert.Equal("nuevo roto", File.ReadAllText(exe + ".failed"));
    }

    [Fact]
    public void RestoreAfterFailedStart_OldLocked_ReturnsFalse()
    {
        using var tmp = new TempDir();
        var exe = tmp.WriteFile("PalitoLauncher.exe", "nuevo roto");
        var old = tmp.WriteFile("PalitoLauncher.exe.old", "viejo");

        bool restored;
        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            restored = LauncherSelfUpdater.RestoreAfterFailedStart(exe, old, exe + ".failed", attempts: 2, delayMs: 10);
        }

        Assert.False(restored);
        Assert.False(File.Exists(exe));
        Assert.Equal("viejo", File.ReadAllText(old));
    }

    [Fact]
    public void TryRestorePreviousVersion_WithOldCopy_PutsItBack()
    {
        using var tmp = new TempDir();
        var exe = tmp.WriteFile("PalitoLauncher.exe", "nuevo con ventana rota");
        tmp.WriteFile("PalitoLauncher.exe.old", "viejo");

        Assert.True(LauncherSelfUpdater.TryRestorePreviousVersion(exe, attempts: 2, delayMs: 10));

        Assert.Equal("viejo", File.ReadAllText(exe));
        Assert.Equal("nuevo con ventana rota", File.ReadAllText(exe + ".failed"));
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public void TryRestorePreviousVersion_WithoutOldCopyOrPath_DoesNothing()
    {
        using var tmp = new TempDir();
        var exe = tmp.WriteFile("PalitoLauncher.exe", "actual");

        Assert.False(LauncherSelfUpdater.TryRestorePreviousVersion(exe, attempts: 2, delayMs: 10));
        Assert.False(LauncherSelfUpdater.TryRestorePreviousVersion(null));
        Assert.False(LauncherSelfUpdater.TryRestorePreviousVersion(""));

        // Con dotnet como host nunca se toca nada.
        var dotnet = tmp.WriteFile("dotnet.exe", "host");
        tmp.WriteFile("dotnet.exe.old", "x");
        Assert.False(LauncherSelfUpdater.TryRestorePreviousVersion(dotnet, attempts: 2, delayMs: 10));

        Assert.Equal("actual", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".failed"));
        Assert.Equal("host", File.ReadAllText(dotnet));
    }

    [Fact]
    public async Task CleanupLeftoversAsync_DeletesAllLeftovers()
    {
        using var tmp = new TempDir();
        var exe = tmp.WriteFile("PalitoLauncher.exe", "actual");
        foreach (var path in LauncherSelfUpdater.GetLeftoverPaths(exe))
            File.WriteAllText(path, "x");

        await LauncherSelfUpdater.CleanupLeftoversAsync(exe, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(20), CancellationToken.None);

        Assert.All(LauncherSelfUpdater.GetLeftoverPaths(exe), p => Assert.False(File.Exists(p)));
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public async Task CleanupLeftoversAsync_LockedFile_GivesUpAfterTimeout()
    {
        using var tmp = new TempDir();
        var exe = tmp.WriteFile("PalitoLauncher.exe", "actual");
        File.WriteAllText(exe + ".old", "bloqueado");
        File.WriteAllText(exe + ".failed", "x");

        var sw = Stopwatch.StartNew();
        using (new FileStream(exe + ".old", FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await LauncherSelfUpdater.CleanupLeftoversAsync(exe, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50),
                CancellationToken.None);
        }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(exe + ".old"));
        Assert.False(File.Exists(exe + ".failed"));
    }

    [Fact]
    public async Task CleanupLeftoversAsync_FileUnlockedLater_IsDeletedOnRetry()
    {
        using var tmp = new TempDir();
        var exe = tmp.WriteFile("PalitoLauncher.exe", "actual");
        File.WriteAllText(exe + ".old", "bloqueado un rato");

        var locker = new FileStream(exe + ".old", FileMode.Open, FileAccess.Read, FileShare.None);
        var cleanup = LauncherSelfUpdater.CleanupLeftoversAsync(exe, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50),
            CancellationToken.None);
        await Task.Delay(300);
        await locker.DisposeAsync();
        await cleanup;

        Assert.False(File.Exists(exe + ".old"));
    }

    // ---------------------------------------------------------------- confirmación de arranque

    [Fact]
    public void WaitForAliveSignal_ProcessExitsWithoutSignal_ReturnsFalseQuickly()
    {
        using var ev = new ManualResetEvent(false);
        using var p = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/c exit 1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var sw = Stopwatch.StartNew();
        Assert.False(LauncherSelfUpdater.WaitForAliveSignal(ev, p, TimeSpan.FromSeconds(20)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void WaitForAliveSignal_Signaled_ReturnsTrue()
    {
        using var ev = new ManualResetEvent(true);
        using var p = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        Assert.True(LauncherSelfUpdater.WaitForAliveSignal(ev, p, TimeSpan.FromSeconds(5)));
    }

    // ---------------------------------------------------------------- DownloadAndApply (handler falso)

    [Fact]
    public async Task DownloadAndApplyAsync_InTestHost_ThrowsLauncherException()
    {
        using var tmp = new TempDir();
        var updater = CreateUpdater(LatestHandler(null), tmp.Combine("gh"), tmp.Combine("cache"));

        await Assert.ThrowsAsync<LauncherException>(() =>
            updater.DownloadAndApplyAsync(MakeInfo(new byte[] { 0x4D, 0x5A }), null, CancellationToken.None));
    }

    [Fact]
    public async Task DownloadAndApplyCore_NotAnExecutable_KeepsCurrentExe()
    {
        using var tmp = new TempDir();
        var exe = tmp.WriteFile("app\\PalitoLauncher.exe", "MZ version actual");
        var payload = "<html>portal cautivo</html>"u8.ToArray();
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Bytes(payload));
        var updater = CreateUpdater(handler, tmp.Combine("gh"), tmp.Combine("cache"));

        var ex = await Assert.ThrowsAsync<LauncherException>(() =>
            updater.DownloadAndApplyCoreAsync(MakeInfo(payload), exe, null, CancellationToken.None));

        Assert.Contains("no es un ejecutable", ex.Message);
        Assert.IsNotType<LauncherStartFailedException>(ex);
        Assert.Equal("MZ version actual", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".new"));
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public async Task DownloadAndApplyCore_DownloadFails_IsNotStartFailed()
    {
        using var tmp = new TempDir();
        var exe = tmp.WriteFile("app\\PalitoLauncher.exe", "MZ version actual");
        var updater = CreateUpdater(new FakeHttpHandler(_ => FakeHttpHandler.NotFound()), tmp.Combine("gh"), tmp.Combine("cache"));

        var ex = await Assert.ThrowsAnyAsync<LauncherException>(() =>
            updater.DownloadAndApplyCoreAsync(MakeInfo(new byte[] { 0x4D, 0x5A, 1, 2 }), exe, null, CancellationToken.None));

        // Un fallo de descarga no debe hacer que la UI omita la versión.
        Assert.IsNotType<LauncherStartFailedException>(ex);
        Assert.Equal("MZ version actual", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public async Task DownloadAndApplyCore_NewVersionDoesNotConfirm_RestoresPrevious()
    {
        using var tmp = new TempDir();
        var exe = tmp.WriteFile("app\\PalitoLauncher.exe", "MZ version actual");
        // Un exe real que no señala el evento (whoami sale enseguida con error por el argumento desconocido).
        var payload = File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe"));
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Bytes(payload));
        var updater = CreateUpdater(handler, tmp.Combine("gh"), tmp.Combine("cache"));
        updater.AliveTimeout = TimeSpan.FromSeconds(10);

        // Tipo exacto: la UI solo omite la versión ante este fallo.
        var ex = await Assert.ThrowsAsync<LauncherStartFailedException>(() =>
            updater.DownloadAndApplyCoreAsync(MakeInfo(payload), exe, null, CancellationToken.None));

        Assert.Equal("La nueva versión del launcher no pudo iniciarse; se restauró la anterior.", ex.Message);
        Assert.Equal("MZ version actual", File.ReadAllText(exe));
        Assert.Equal(payload, File.ReadAllBytes(exe + ".failed"));
        Assert.False(File.Exists(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));
    }
}
