using PalitoLauncher.Core;
using PalitoLauncher.Core.Downloads;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Tests.TestHelpers.C;

namespace PalitoLauncher.Tests;

/// <summary>Métodos estáticos puros del instalador (no dependen del descargador).</summary>
public sealed class GameInstallerPathTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EstimateRequiredBytes_IsAssetSizeTimesFactor()
    {
        var asset = new ReleaseAsset { Id = 1, Name = "2D_0.0.2_Protype2.zip", Size = 70_782_621 };
        var release = new GameRelease(new ReleaseInfo { Id = 1, TagName = "t", Assets = [asset] }, asset, null);

        Assert.Equal((long)Math.Ceiling(70_782_621 * GameInstaller.RequiredSpaceFactor), GameInstaller.EstimateRequiredBytes(release));
    }

    [Fact]
    public void RequiredSpaceFactor_CoversPostDownloadCheckForRealBuild()
    {
        // Mismo modelo que la comprobación tras descargar: archivo (1×) + descomprimido × margen.
        Assert.True(GameInstaller.RequiredSpaceFactor >=
            1 + GameInstaller.AssumedCompressionRatio * GameInstaller.UncompressedSpaceFactor);

        // Build real 0.0.2: 70 782 621 bytes comprimidos, ~178 MiB descomprimidos.
        const long archive = 70_782_621;
        const long uncompressed = 178L * 1024 * 1024;
        Assert.True((double)uncompressed / archive <= GameInstaller.AssumedCompressionRatio);
        var asset = new ReleaseAsset { Id = 1, Name = "a.zip", Size = archive, DownloadUrl = "https://github.com/a" };
        var estimate = GameInstaller.EstimateRequiredBytes(new GameRelease(new ReleaseInfo { Id = 1, TagName = "t" }, asset, null));
        Assert.True(estimate >= archive + (long)Math.Ceiling(uncompressed * GameInstaller.UncompressedSpaceFactor));
    }

    [Fact]
    public void EstimateRequiredBytes_HugeSize_DoesNotOverflow()
    {
        var asset = new ReleaseAsset { Id = 1, Name = "a.zip", Size = long.MaxValue, DownloadUrl = "https://github.com/a" };

        Assert.Equal(long.MaxValue, GameInstaller.EstimateRequiredBytes(new GameRelease(new ReleaseInfo { Id = 1, TagName = "t" }, asset, null)));
    }

    [Fact]
    public void ResolveInstallRoot_PlainFolder_AppendsCoop2D()
    {
        var picked = _temp.Combine("Juegos");
        Directory.CreateDirectory(picked);

        Assert.Equal(Path.Combine(picked, "Coop2D"), GameInstaller.ResolveInstallRoot(picked));
        Assert.Equal(Path.Combine(picked, "Coop2D"), GameInstaller.ResolveInstallRoot(picked + "\\"));
    }

    [Fact]
    public void ResolveInstallRoot_FolderAlreadyNamedCoop2D_KeepsIt()
    {
        var picked = _temp.Combine("Juegos", "coop2d");
        Assert.Equal(picked, GameInstaller.ResolveInstallRoot(picked + "\\"));
    }

    [Fact]
    public void ResolveInstallRoot_FolderContainingInstallation_KeepsIt()
    {
        var withGame = _temp.Combine("A");
        TestBuilds.CreateGameDir(Path.Combine(withGame, "game"), "x");
        var withWork = _temp.Combine("B");
        Directory.CreateDirectory(Path.Combine(withWork, ".launcher"));

        Assert.Equal(withGame, GameInstaller.ResolveInstallRoot(withGame));
        Assert.Equal(withWork, GameInstaller.ResolveInstallRoot(withWork));
    }

    [Fact]
    public void ResolveInstallRoot_FolderInsideInstallation_ReturnsParent()
    {
        var root = _temp.Combine("Juegos", "MiJuego");
        var game = Path.Combine(root, "game");
        TestBuilds.CreateGameDir(game, "x");
        var work = Path.Combine(root, ".launcher");
        Directory.CreateDirectory(work);

        Assert.Equal(root, GameInstaller.ResolveInstallRoot(game));        // tiene palito_install.json
        Assert.Equal(root, GameInstaller.ResolveInstallRoot(work));        // se llama .launcher

        // "game" sin marcador pero con hermano .launcher
        var root2 = _temp.Combine("Otra");
        Directory.CreateDirectory(Path.Combine(root2, "game"));
        Directory.CreateDirectory(Path.Combine(root2, ".launcher"));
        Assert.Equal(root2, GameInstaller.ResolveInstallRoot(Path.Combine(root2, "game")));

        // "game" sin hermano .launcher → carpeta normal
        var lone = _temp.Combine("Suelta", "game");
        Directory.CreateDirectory(lone);
        Assert.Equal(Path.Combine(lone, "Coop2D"), GameInstaller.ResolveInstallRoot(lone));
    }

    [Fact]
    public void ResolveInstallRoot_DriveRoot_AppendsCoop2D()
    {
        Assert.Equal(@"Q:\Coop2D", GameInstaller.ResolveInstallRoot(@"Q:\"));
    }

    [Fact]
    public void ResolveInstallRoot_RelativePath_ThrowsLauncherException()
    {
        Assert.Throws<LauncherException>(() => GameInstaller.ResolveInstallRoot(@"Juegos\Coop2D"));
    }
}

/// <summary>Instalador completo sobre un FileDownloader real y un servidor HTTP falso (sin red).</summary>
public sealed class GameInstallerTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly FakeBuildServer _server = new();
    private readonly HttpClient _http;
    private readonly GameInstaller _installer;
    private readonly string _root;

    public GameInstallerTests()
    {
        _http = new HttpClient(_server) { Timeout = Timeout.InfiniteTimeSpan };
        var downloader = new FileDownloader(_http) { RetryDelayScale = 0, StallTimeout = TimeSpan.FromSeconds(30) };
        _installer = new GameInstaller(downloader, TestBuilds.Exe)
        {
            PostExtractDelay = TimeSpan.Zero,
            GameExitPollInterval = TimeSpan.FromMilliseconds(20),
            IsGameRunningFrom = (_, _) => false,
        };
        _root = _temp.Combine("Juegos", "Coop2D");
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
        _temp.Dispose();
    }

    private string GameDir => GameInstaller.GetGameDir(_root);
    private string WorkDir => GameInstaller.GetWorkDir(_root);
    private string Work(params string[] parts) => Path.Combine([WorkDir, .. parts]);

    private GameRelease Release(int n, params string[] extraFiles) =>
        TestBuilds.MakeRelease(_server, 100 + n, 1000 + n, $"0.0.{n}-Protype{n}", $"2D_0.0.{n}_Protype{n}.zip",
            TestBuilds.CreateGameZip($"2D_0.0.{n}_Protype{n}", "build" + n, extraFiles));

    private Task<InstalledGameInfo> Install(GameRelease release, IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default, bool keepArchive = false) =>
        _installer.InstallAsync(_root, release, new InstallOptions { KeepDownloadedArchive = keepArchive }, progress, ct);

    private void AssertNoLeftovers()
    {
        Assert.False(Directory.Exists(Work("staging")), "staging");
        Assert.False(Directory.Exists(Work("ready")), "ready");
        Assert.False(Directory.Exists(Work("old")), "old");
        Assert.False(Directory.Exists(Work("trash")), "trash");
        Assert.False(File.Exists(Work("swap.json")), "swap.json");
        Assert.False(File.Exists(Work("operation.lock")), "operation.lock");
    }

    // ───────────── InstallAsync ─────────────

    [Fact]
    public async Task InstallAsync_CleanInstall_InstallsGameWithMarker()
    {
        var release = Release(9);
        var progress = new RecordingProgress<InstallProgress>();

        var info = await Install(release, progress);

        Assert.True(File.Exists(Path.Combine(GameDir, TestBuilds.Exe)));
        Assert.True(File.Exists(Path.Combine(GameDir, "Coop2D_Data", "app.info")));
        Assert.True(File.Exists(Path.Combine(GameDir, "MonoBleedingEdge", "EmbedRuntime", "mono-2.0-bdwgc.dll")));
        var marker = TestBuilds.ReadMarker(GameDir);
        Assert.NotNull(marker);
        Assert.Equal(release.Release.TagName, marker!.Tag);
        Assert.Equal(release.Release.Id, marker.ReleaseId);
        Assert.Equal(release.Asset.Id, marker.AssetId);
        Assert.Equal(release.Asset.Name, marker.AssetName);
        Assert.Equal(release.Asset.Size, marker.AssetSize);
        Assert.Equal(release.Asset.Sha256, marker.Sha256);
        Assert.Equal(TestBuilds.Exe, marker.ExecutableName);
        Assert.Equal(release.Asset.Id, info.AssetId);

        Assert.True((new DirectoryInfo(WorkDir).Attributes & FileAttributes.Hidden) != 0, ".launcher debe estar oculta");
        AssertNoLeftovers();
        Assert.False(File.Exists(Work("downloads", $"{release.Asset.Id}-{release.Asset.Name}")), "el zip se borra tras instalar");

        var phases = progress.Items.Select(p => p.Phase).Distinct().ToList();
        Assert.Contains(InstallPhase.Preparing, phases);
        Assert.Contains(InstallPhase.Downloading, phases);
        Assert.Contains(InstallPhase.Verifying, phases);
        Assert.Contains(InstallPhase.Extracting, phases);
        Assert.Contains(InstallPhase.Installing, phases);
        Assert.Contains(InstallPhase.CleaningUp, phases);
        Assert.DoesNotContain(InstallPhase.WaitingForGameExit, phases);
        Assert.Contains(progress.Items, p => p.Phase == InstallPhase.Extracting && p.Status.Contains(" archivos", StringComparison.Ordinal));
        Assert.All(progress.Items, p => Assert.False(string.IsNullOrWhiteSpace(p.Status)));

        var status = _installer.GetStatus(_root);
        Assert.Equal(InstallState.Installed, status.State);
        Assert.Equal(Path.Combine(GameDir, TestBuilds.Exe), status.ExecutablePath);
        Assert.False(status.HasPendingDownload);
    }

    [Fact]
    public async Task InstallAsync_HashMismatchThenGoodDownload_ReportsDownloadingAgainAndInstalls()
    {
        var big = new byte[400 * 1024];
        new Random(5).NextBytes(big);
        var zip = TestBuilds.CreateZip(
        [
            ("2D_0.0.4/Coop2D.exe", TestBuilds.Bytes("MZ")),
            ("2D_0.0.4/Coop2D_Data/app.info", TestBuilds.Bytes("PalitoGames\nCoop2D")),
            ("2D_0.0.4/Coop2D_Data/big.bin", big),
        ]);
        var release = TestBuilds.MakeRelease(_server, 4, 44, "0.0.4", "2D_0.0.4.zip", zip);
        _server.ReadDelay = TimeSpan.FromMilliseconds(15);
        _server.CorruptNextResponse();
        var progress = new RecordingProgress<InstallProgress>();

        await Install(release, progress);

        var items = progress.Items.ToList();
        var firstVerify = items.FindIndex(p => p.Phase == InstallPhase.Verifying);
        Assert.True(firstVerify >= 0);
        // El descargador vuelve a empezar desde cero: la UI debe volver a "Descargando…", no quedarse en "Verificando".
        Assert.Contains(items.Skip(firstVerify + 1), p => p.Phase == InstallPhase.Downloading && p.Status.StartsWith("Descargando… ", StringComparison.Ordinal));
        Assert.True(_server.RequestCount >= 2);
        Assert.Equal(InstallState.Installed, _installer.GetStatus(_root).State);
        Assert.Equal(big.Length, new FileInfo(Path.Combine(GameDir, "Coop2D_Data", "big.bin")).Length);
    }

    [Fact]
    public async Task InstallAsync_KeepDownloadedArchive_KeepsZip()
    {
        var release = Release(3);

        await Install(release, keepArchive: true);

        Assert.True(File.Exists(Work("downloads", $"{release.Asset.Id}-{release.Asset.Name}")));
    }

    [Fact]
    public async Task InstallAsync_Update_ReplacesGameAndRemovesObsoleteFiles()
    {
        await Install(Release(1, "solo_v1.txt"), keepArchive: true);
        Assert.True(File.Exists(Path.Combine(GameDir, "solo_v1.txt")));
        var release2 = Release(2, "solo_v2.txt");

        await Install(release2);

        Assert.False(File.Exists(Path.Combine(GameDir, "solo_v1.txt")));
        Assert.True(File.Exists(Path.Combine(GameDir, "solo_v2.txt")));
        Assert.Equal("MZ fake exe build2", File.ReadAllText(Path.Combine(GameDir, TestBuilds.Exe)));
        Assert.Equal(release2.Asset.Id, TestBuilds.ReadMarker(GameDir)!.AssetId);
        AssertNoLeftovers();
        // La descarga de la versión anterior no es de este asset → se borró en Preparing.
        Assert.Empty(Directory.EnumerateFiles(Work("downloads")));
    }

    [Fact]
    public async Task InstallAsync_CancelDuringDownload_KeepsGameAndPartial()
    {
        await Install(Release(1));
        var release2 = Release(2);
        _server.StallOnceAt(release2.Asset.Size / 2);
        using var cts = new CancellationTokenSource();

        var task = Install(release2, ct: cts.Token);
        await _server.StallReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1001, TestBuilds.ReadMarker(GameDir)!.AssetId);
        Assert.True(File.Exists(Path.Combine(GameDir, TestBuilds.Exe)));
        var archive = Work("downloads", $"{release2.Asset.Id}-{release2.Asset.Name}");
        Assert.True(File.Exists(archive + ".part"), ".part conservado");
        Assert.False(Directory.Exists(Work("staging")));
        Assert.False(Directory.Exists(Work("ready")));
        var status = _installer.GetStatus(_root);
        Assert.Equal(InstallState.Installed, status.State);
        Assert.True(status.HasPendingDownload);

        // Reanudar completa la actualización.
        await Install(release2);
        Assert.Equal(release2.Asset.Id, TestBuilds.ReadMarker(GameDir)!.AssetId);
        AssertNoLeftovers();
        Assert.False(_installer.GetStatus(_root).HasPendingDownload);
    }

    [Fact]
    public async Task InstallAsync_WhileAnotherOperationRuns_ThrowsBusy()
    {
        var release = Release(1);
        _server.StallOnceAt(release.Asset.Size / 2);
        using var cts = new CancellationTokenSource();
        var first = Install(release, ct: cts.Token);
        await _server.StallReached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAsync<LauncherException>(() => Install(Release(2)));
        Assert.Equal("Ya hay una operación en curso.", ex.Message);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task InstallAsync_ZipWithoutExe_KeepsGameStagingCleanAndDownloadKept()
    {
        await Install(Release(1));
        var badZip = TestBuilds.CreateZip([("2D_0.0.5_Protype5/LEEME.txt", TestBuilds.Bytes("sin exe"))]);
        var bad = TestBuilds.MakeRelease(_server, 105, 1005, "0.0.5", "2D_0.0.5_Protype5.zip", badZip);

        var ex = await Assert.ThrowsAsync<LauncherException>(() => Install(bad));

        Assert.Contains("no contiene Coop2D.exe", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1001, TestBuilds.ReadMarker(GameDir)!.AssetId);
        Assert.False(Directory.Exists(Work("staging")) && Directory.EnumerateFileSystemEntries(Work("staging")).Any());
        Assert.False(Directory.Exists(Work("ready")));
        Assert.True(File.Exists(Work("downloads", "1005-2D_0.0.5_Protype5.zip")), "la descarga verificada se conserva");
        Assert.Equal(InstallState.Installed, _installer.GetStatus(_root).State);
    }

    [Fact]
    public async Task InstallAsync_OtherSingleExe_IsUsedAsExecutable()
    {
        var zip = TestBuilds.CreateZip(
        [
            ("Build/Juego.exe", TestBuilds.Bytes("MZ")),
            ("Build/UnityCrashHandler64.exe", TestBuilds.Bytes("MZ")),
            ("Build/Juego_Data/app.info", TestBuilds.Bytes("PalitoGames\nCoop2D")),
        ]);
        var release = TestBuilds.MakeRelease(_server, 7, 77, "0.7.0", "2D_0.7.0.zip", zip);

        var info = await Install(release);

        Assert.Equal("Juego.exe", info.ExecutableName);
        var status = _installer.GetStatus(_root);
        Assert.Equal(InstallState.Installed, status.State);
        Assert.Equal(Path.Combine(GameDir, "Juego.exe"), status.ExecutablePath);
    }

    [Fact]
    public async Task InstallAsync_ForeignContent_IsMovedToBackupNotDeleted()
    {
        Directory.CreateDirectory(Path.Combine(GameDir, "fotos"));
        File.WriteAllText(Path.Combine(GameDir, "notas.txt"), "mis cosas");
        File.WriteAllText(Path.Combine(GameDir, "fotos", "a.jpg"), "jpg");
        var before = _installer.GetStatus(_root);
        Assert.Equal(InstallState.Broken, before.State);
        Assert.True(before.HasForeignContent);

        await Install(Release(1));

        Assert.Equal(InstallState.Installed, _installer.GetStatus(_root).State);
        var backup = Assert.Single(Directory.GetDirectories(_root, "game.backup-*"));
        Assert.Equal("mis cosas", File.ReadAllText(Path.Combine(backup, "notas.txt")));
        Assert.True(File.Exists(Path.Combine(backup, "fotos", "a.jpg")));
        AssertNoLeftovers();
    }

    [Fact]
    public async Task InstallAsync_GameRunning_WaitsUntilItCloses()
    {
        await Install(Release(1));
        var checks = 0;
        var calls = new System.Collections.Concurrent.ConcurrentQueue<(string Dir, string Exe)>();
        _installer.IsGameRunningFrom = (dir, exe) =>
        {
            calls.Enqueue((dir, exe));
            return Interlocked.Increment(ref checks) <= 3;
        };
        var progress = new RecordingProgress<InstallProgress>();

        await Install(Release(2), progress);

        Assert.True(checks >= 4);
        Assert.All(calls, c => Assert.Equal((GameDir, TestBuilds.Exe), c));
        var waiting = Assert.Single(progress.Items.Where(p => p.Phase == InstallPhase.WaitingForGameExit).Select(p => p.Status).Distinct());
        Assert.Equal("Cierra Coop2D para terminar la actualización…", waiting);
        Assert.True(progress.Items.First(p => p.Phase == InstallPhase.WaitingForGameExit).CanCancel);
        Assert.Equal(1002, TestBuilds.ReadMarker(GameDir)!.AssetId);
    }

    [Fact]
    public async Task InstallAsync_CancelWhileWaitingForGame_KeepsGameAndDownload()
    {
        await Install(Release(1));
        _installer.IsGameRunningFrom = (_, _) => true;
        using var cts = new CancellationTokenSource();
        var progress = new RecordingProgress<InstallProgress>
        {
            OnReport = p => { if (p.Phase == InstallPhase.WaitingForGameExit) cts.Cancel(); },
        };
        var release2 = Release(2);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Install(release2, progress, cts.Token));

        Assert.Equal(1001, TestBuilds.ReadMarker(GameDir)!.AssetId);
        Assert.False(Directory.Exists(Work("ready")));
        Assert.True(File.Exists(Work("downloads", $"{release2.Asset.Id}-{release2.Asset.Name}")));
        Assert.False(File.Exists(Work("swap.json")));
    }

    [Fact]
    public async Task InstallAsync_NotEnoughSpace_FailsBeforeDownloading()
    {
        var zip = TestBuilds.CreateGameZip("2D_9.9.9", "huge");
        var huge = TestBuilds.MakeRelease(_server, 9, 99, "9.9.9", "2D_9.9.9.zip", zip, declaredSize: long.MaxValue / 8);

        var ex = await Assert.ThrowsAsync<LauncherException>(() => Install(huge));

        Assert.StartsWith("Necesitas al menos ", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, _server.RequestCount);
        Assert.False(Directory.Exists(GameDir));
    }

    [Fact]
    public async Task InstallAsync_AntivirusDeletesExeRightAfterExtraction_ReportsAntivirusNotMissingExe()
    {
        await Install(Release(1));
        var release2 = Release(2);
        // El antivirus deja escribir Coop2D.exe y lo pone en cuarentena en cuanto se cierra (antes de buscar la raíz).
        var progress = new RecordingProgress<InstallProgress>
        {
            OnReport = p =>
            {
                if (p.Phase != InstallPhase.Extracting || p.Fraction != 1 || !Directory.Exists(Work("staging")))
                    return;
                foreach (var exe in Directory.EnumerateFiles(Work("staging"), TestBuilds.Exe, SearchOption.AllDirectories).ToList())
                    File.Delete(exe);
            },
        };

        var ex = await Assert.ThrowsAsync<LauncherException>(() => Install(release2, progress));

        Assert.StartsWith("El antivirus bloqueó o borró archivos del juego (Coop2D.exe).", ex.Message, StringComparison.Ordinal);
        Assert.Contains(_root, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no contiene", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1001, TestBuilds.ReadMarker(GameDir)!.AssetId);
        Assert.True(File.Exists(Path.Combine(GameDir, TestBuilds.Exe)), "game\\ intacto");
        Assert.True(File.Exists(Work("downloads", $"{release2.Asset.Id}-{release2.Asset.Name}")), "la descarga verificada se conserva");
        Assert.False(Directory.Exists(Work("ready")));
    }

    [Fact]
    public async Task InstallAsync_OtherDownloadsArePrunedBeforeCheckingSpace()
    {
        var release = Release(1);
        Directory.CreateDirectory(Work("downloads"));
        var stale = Work("downloads", "999-2D_0.0.0_Viejo.zip");
        File.WriteAllBytes(stale, new byte[4096]);
        var required = GameInstaller.EstimateRequiredBytes(release);
        // Solo hay sitio si el launcher borra antes su propia descarga vieja.
        _installer.GetFreeSpace = _ => File.Exists(stale) ? required - 1 : long.MaxValue / 4;

        await Install(release);

        Assert.False(File.Exists(stale));
        Assert.Equal(InstallState.Installed, _installer.GetStatus(_root).State);
    }

    [Fact]
    public async Task InstallAsync_FreeSpaceExactlyAtEstimate_PassesPostDownloadCheck()
    {
        // Build con relación de compresión ~2.4 (como la real): con el factor antiguo (3.5) pasaba la comprobación previa,
        // descargaba y luego fallaba la de después de descargar.
        var random = new byte[40 * 1024];
        new Random(7).NextBytes(random);
        var zeros = new byte[60 * 1024];
        var entries = new List<(string Path, byte[] Content)>
        {
            ("2D_0.0.6/Coop2D.exe", TestBuilds.Bytes("MZ")),
            ("2D_0.0.6/Coop2D_Data/app.info", TestBuilds.Bytes("PalitoGames\nCoop2D")),
            ("2D_0.0.6/Coop2D_Data/random.bin", random),
            ("2D_0.0.6/Coop2D_Data/zeros.bin", zeros),
        };
        var zip = TestBuilds.CreateZip(entries);
        var uncompressed = entries.Sum(e => (long)e.Content.Length);
        var ratio = (double)uncompressed / zip.Length;
        Assert.InRange(ratio, 2.3, GameInstaller.AssumedCompressionRatio);
        var release = TestBuilds.MakeRelease(_server, 6, 66, "0.0.6", "2D_0.0.6.zip", zip);

        // Espacio libre = estimación previa menos lo que el launcher ya ha escrito en la carpeta.
        var budget = GameInstaller.EstimateRequiredBytes(release);
        _installer.GetFreeSpace = _ => budget - FileSystemUtil.GetDirectorySize(_root);

        await Install(release);

        Assert.Equal(InstallState.Installed, _installer.GetStatus(_root).State);
    }

    [Fact]
    public async Task InstallAsync_LockHeldByAnotherProcess_Throws()
    {
        Directory.CreateDirectory(WorkDir);
        using (new FileStream(Work("operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<LauncherException>(() => Install(Release(1)));
            Assert.Equal("Otra ventana del launcher está modificando el juego. Espera a que termine.", ex.Message);
        }
        Assert.Equal(0, _server.RequestCount);
        Assert.False(Directory.Exists(GameDir));
    }

    [Fact]
    public async Task InstallAsync_RelativeRoot_ThrowsLauncherException()
    {
        await Assert.ThrowsAsync<LauncherException>(() =>
            _installer.InstallAsync(@"Juegos\Coop2D", Release(1), new InstallOptions(), null, CancellationToken.None));
    }

    // ───────────── Recover ─────────────

    [Fact]
    public void Recover_JournalWithValidReady_PromotesReady()
    {
        TestBuilds.CreateGameDir(Work("ready"), "nueva", assetId: 2);
        TestBuilds.CreateGameDir(Work("old", "20261019120000"), "vieja", assetId: 1);
        TestBuilds.WriteJournal(WorkDir, Work("old", "20261019120000"), Work("ready"), "nueva");

        _installer.Recover(_root);

        Assert.Equal("nueva", TestBuilds.ReadMarker(GameDir)!.Tag);
        AssertNoLeftovers();
    }

    [Fact]
    public void Recover_JournalWithMissingReady_RestoresOld()
    {
        TestBuilds.CreateGameDir(Work("old", "20261019120000"), "vieja", assetId: 1);
        TestBuilds.WriteJournal(WorkDir, @"old\20261019120000", "ready", "nueva");

        _installer.Recover(_root);

        Assert.Equal("vieja", TestBuilds.ReadMarker(GameDir)!.Tag);
        Assert.Equal(InstallState.Installed, _installer.GetStatus(_root).State);
        AssertNoLeftovers();
    }

    [Fact]
    public void Recover_JournalFromAnotherLocation_IsRemapped()
    {
        // Instalación movida / letra de unidad cambiada: rutas absolutas viejas en el diario.
        TestBuilds.CreateGameDir(Work("old", "20261019120000"), "vieja");
        TestBuilds.WriteJournal(WorkDir, @"Z:\Otra\Coop2D\.launcher\old\20261019120000", @"Z:\Otra\Coop2D\.launcher\ready", "x");

        _installer.Recover(_root);

        Assert.Equal("vieja", TestBuilds.ReadMarker(GameDir)!.Tag);
    }

    [Fact]
    public void Recover_NoJournal_RestoresNewestValidOld()
    {
        TestBuilds.CreateGameDir(Work("old", "20260101000000"), "A");
        TestBuilds.CreateGameDir(Work("old", "20260202000000"), "B");
        TestBuilds.CreateGameDir(Work("old", "20260303000000"), "C-rota", withExe: false);

        _installer.Recover(_root);

        Assert.Equal("B", TestBuilds.ReadMarker(GameDir)!.Tag);
        Assert.False(Directory.Exists(Work("old")), "con game\\ válida se borra old\\");
    }

    [Fact]
    public void Recover_OnlyCorruptOld_IsNotRestored()
    {
        TestBuilds.CreateGameDir(Work("old", "20260101000000"), "rota", withExe: false);

        _installer.Recover(_root);

        Assert.False(Directory.Exists(GameDir));
        Assert.Equal(InstallState.NotInstalled, _installer.GetStatus(_root).State);
    }

    [Fact]
    public void Recover_InvalidGameAndJournal_RestoresOldAndTrashesBrokenGame()
    {
        TestBuilds.CreateGameDir(GameDir, "nueva-sin-exe", withExe: false);
        TestBuilds.CreateGameDir(Work("old", "20261019120000"), "vieja");
        TestBuilds.WriteJournal(WorkDir, @"old\20261019120000", "ready", "nueva");

        _installer.Recover(_root);

        Assert.Equal("vieja", TestBuilds.ReadMarker(GameDir)!.Tag);
        AssertNoLeftovers();
    }

    [Fact]
    public void Recover_ForeignGameAndValidOld_BacksUpForeignContent()
    {
        Directory.CreateDirectory(GameDir);
        File.WriteAllText(Path.Combine(GameDir, "documento.txt"), "importante");
        TestBuilds.CreateGameDir(Work("old", "20261019120000"), "vieja");

        _installer.Recover(_root);

        Assert.Equal("vieja", TestBuilds.ReadMarker(GameDir)!.Tag);
        var backup = Assert.Single(Directory.GetDirectories(_root, "game.backup-*"));
        Assert.Equal("importante", File.ReadAllText(Path.Combine(backup, "documento.txt")));
    }

    [Fact]
    public void Recover_BrokenGameWithoutCandidates_LeavesItForRepair()
    {
        TestBuilds.CreateGameDir(GameDir, "rota", withExe: false);

        _installer.Recover(_root);

        Assert.True(File.Exists(Path.Combine(GameDir, InstalledGameInfo.MarkerFileName)));
        Assert.Equal(InstallState.Broken, _installer.GetStatus(_root).State);
    }

    [Fact]
    public void Recover_ValidGame_CleansLeftoversButKeepsDownloads()
    {
        TestBuilds.CreateGameDir(GameDir, "actual");
        TestBuilds.CreateGameDir(Work("old", "20261019120000"), "vieja");
        TestBuilds.CreateGameDir(Work("ready"), "huerfana");
        Directory.CreateDirectory(Work("staging", "abcd1234", "sub"));
        File.WriteAllText(Work("staging", "abcd1234", "sub", "x.bin"), "x");
        Directory.CreateDirectory(Work("trash", "deadbeef"));
        File.WriteAllText(Work("trash", "deadbeef", "y.bin"), "y");
        TestBuilds.WriteJournal(WorkDir, @"old\20261019120000", "ready", "actual");
        Directory.CreateDirectory(Work("downloads"));
        File.WriteAllText(Work("downloads", "1-2D.zip.part"), "parcial");
        File.WriteAllText(Work("downloads", "2-2D.zip"), "completo");

        _installer.Recover(_root);

        Assert.Equal("actual", TestBuilds.ReadMarker(GameDir)!.Tag);
        AssertNoLeftovers();
        Assert.True(File.Exists(Work("downloads", "1-2D.zip.part")));
        Assert.True(File.Exists(Work("downloads", "2-2D.zip")));
    }

    [Fact]
    public void Recover_LockHeldByAnotherProcess_DoesNothing()
    {
        TestBuilds.CreateGameDir(Work("ready"), "nueva");
        TestBuilds.WriteJournal(WorkDir, null, "ready", "nueva");
        using (new FileStream(Work("operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            _installer.Recover(_root);

            Assert.False(Directory.Exists(GameDir));
            Assert.True(Directory.Exists(Work("ready")));
            Assert.True(File.Exists(Work("swap.json")));
        }

        _installer.Recover(_root);
        Assert.Equal("nueva", TestBuilds.ReadMarker(GameDir)!.Tag);
    }

    [Fact]
    public void Recover_MissingRootOrWorkDir_DoesNotThrow()
    {
        _installer.Recover(_root);
        _installer.Recover(@"no\absoluta");
        Assert.False(Directory.Exists(_root));
    }

    // ───────────── GetStatus ─────────────

    [Fact]
    public void GetStatus_NothingOrEmptyGame_IsNotInstalled()
    {
        Assert.Equal(InstallState.NotInstalled, _installer.GetStatus(_root).State);
        Directory.CreateDirectory(GameDir);
        var status = _installer.GetStatus(_root);
        Assert.Equal(InstallState.NotInstalled, status.State);
        Assert.Equal(_root, status.InstallRoot);
        Assert.Equal(GameDir, status.GameDir);
    }

    [Fact]
    public void GetStatus_ValidGame_IsInstalledWithInfo()
    {
        TestBuilds.CreateGameDir(GameDir, "0.0.2-Prototype-2", assetId: 42);

        var status = _installer.GetStatus(_root + "\\");

        Assert.Equal(InstallState.Installed, status.State);
        Assert.Equal(42, status.Info!.AssetId);
        Assert.Equal(Path.Combine(GameDir, TestBuilds.Exe), status.ExecutablePath);
        Assert.False(status.HasForeignContent);
    }

    [Fact]
    public void GetStatus_ExeWithoutMarker_IsBroken()
    {
        TestBuilds.CreateGameDir(GameDir, "x", withMarker: false);

        var status = _installer.GetStatus(_root);

        Assert.Equal(InstallState.Broken, status.State);
        Assert.False(status.HasForeignContent);
        Assert.Null(status.Info);
        Assert.NotNull(status.ExecutablePath);
    }

    [Fact]
    public void GetStatus_MarkerWithoutExe_IsBroken()
    {
        TestBuilds.CreateGameDir(GameDir, "x", withExe: false);

        var status = _installer.GetStatus(_root);

        Assert.Equal(InstallState.Broken, status.State);
        Assert.NotNull(status.Info);
        Assert.Null(status.ExecutablePath);
        Assert.False(status.HasForeignContent);
    }

    [Fact]
    public void GetStatus_CorruptMarker_IsBroken()
    {
        TestBuilds.CreateGameDir(GameDir, "x", withMarker: false);
        File.WriteAllText(Path.Combine(GameDir, InstalledGameInfo.MarkerFileName), "{ esto no es json");

        Assert.Equal(InstallState.Broken, _installer.GetStatus(_root).State);
    }

    [Fact]
    public void GetStatus_ForeignContent_IsBrokenWithFlag()
    {
        Directory.CreateDirectory(GameDir);
        File.WriteAllText(Path.Combine(GameDir, "otra-cosa.txt"), "hola");

        var status = _installer.GetStatus(_root);

        Assert.Equal(InstallState.Broken, status.State);
        Assert.True(status.HasForeignContent);
    }

    [Fact]
    public void GetStatus_PartialDownload_SetsHasPendingDownload()
    {
        Directory.CreateDirectory(Work("downloads"));
        File.WriteAllText(Work("downloads", "1-2D_0.0.2_Protype2.zip.part"), "abc");
        File.WriteAllText(Work("downloads", "1-2D_0.0.2_Protype2.zip.part.json"), "{}");

        var status = _installer.GetStatus(_root);

        Assert.Equal(InstallState.NotInstalled, status.State);
        Assert.True(status.HasPendingDownload);
    }

    [Fact]
    public void GetStatus_MissingDrive_IsDriveMissing()
    {
        var drive = TestBuilds.FindMissingDriveRoot();
        if (drive == null)
            return; // todas las letras existen en este equipo

        var status = _installer.GetStatus(Path.Combine(drive, "Juegos", "Coop2D"));

        Assert.Equal(InstallState.DriveMissing, status.State);
    }

    [Fact]
    public void GetStatus_InvalidPath_DoesNotThrow()
    {
        Assert.Equal(InstallState.NotInstalled, _installer.GetStatus("relativa").State);
    }

    // ───────────── Uninstall / Move ─────────────

    [Fact]
    public async Task UninstallAsync_RemovesGameWorkDirAndEmptyRoot()
    {
        await Install(Release(1), keepArchive: true);
        var progress = new RecordingProgress<InstallProgress>();

        await _installer.UninstallAsync(_root, progress, CancellationToken.None);

        Assert.False(Directory.Exists(_root));
        Assert.Contains(progress.Items, p => p.Phase == InstallPhase.Uninstalling && p.Status == "Desinstalando…");
        Assert.Equal(InstallState.NotInstalled, _installer.GetStatus(_root).State);
    }

    [Fact]
    public async Task UninstallAsync_KeepsForeignContentAndBackups()
    {
        Directory.CreateDirectory(GameDir);
        File.WriteAllText(Path.Combine(GameDir, "mio.txt"), "no borrar");
        Directory.CreateDirectory(WorkDir);

        await _installer.UninstallAsync(_root, null, CancellationToken.None);

        Assert.Equal("no borrar", File.ReadAllText(Path.Combine(GameDir, "mio.txt")));
        Assert.False(Directory.Exists(WorkDir));
    }

    [Fact]
    public async Task UninstallAsync_GameRunning_Throws()
    {
        TestBuilds.CreateGameDir(GameDir, "x");
        _installer.IsGameRunningFrom = (_, _) => true;

        var ex = await Assert.ThrowsAsync<LauncherException>(() => _installer.UninstallAsync(_root, null, CancellationToken.None));

        Assert.Equal("Cierra Coop2D antes de desinstalar.", ex.Message);
        Assert.True(File.Exists(Path.Combine(GameDir, TestBuilds.Exe)));
    }

    [Fact]
    public async Task MoveAsync_SameVolume_MovesWholeInstallation()
    {
        await Install(Release(1), keepArchive: true);
        var destination = _temp.Combine("Otra", "Coop2D");

        await _installer.MoveAsync(_root, destination, null, CancellationToken.None);

        Assert.False(Directory.Exists(_root));
        var status = _installer.GetStatus(destination);
        Assert.Equal(InstallState.Installed, status.State);
        Assert.Equal(1001, status.Info!.AssetId);
        Assert.False(File.Exists(Path.Combine(GameInstaller.GetWorkDir(destination), "operation.lock")));
    }

    [Fact]
    public async Task MoveAsync_InvalidDestinations_Throw()
    {
        TestBuilds.CreateGameDir(GameDir, "x");
        var notEmpty = _temp.Combine("Llena");
        Directory.CreateDirectory(notEmpty);
        File.WriteAllText(Path.Combine(notEmpty, "a.txt"), "a");

        await Assert.ThrowsAsync<LauncherException>(() => _installer.MoveAsync(_root, notEmpty, null, CancellationToken.None));
        await Assert.ThrowsAsync<LauncherException>(() => _installer.MoveAsync(_root, Path.Combine(_root, "dentro"), null, CancellationToken.None));
        await Assert.ThrowsAsync<LauncherException>(() => _installer.MoveAsync(_root, _root, null, CancellationToken.None));
        Assert.Equal(InstallState.Installed, _installer.GetStatus(_root).State);
    }

    private string ExePath(string root) => Path.Combine(GameInstaller.GetGameDir(root), TestBuilds.Exe);

    // Restos "<raíz>.moving" / "<raíz>.deleting-…" junto a una raíz.
    private static List<string> LeftoverSiblings(string root) =>
        Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(root)!)
            .Where(p => Path.GetFileName(p).StartsWith(Path.GetFileName(root) + ".", StringComparison.OrdinalIgnoreCase))
            .ToList();

    [Fact]
    public async Task MoveAsync_SameVolume_CommitsAfterRename()
    {
        await Install(Release(1));
        var destination = _temp.Combine("Otra", "Coop2D");
        string? committed = null;
        var sourceGone = false;
        var destinationReady = false;

        await _installer.MoveAsync(_root, destination, newRoot =>
        {
            committed = newRoot;
            sourceGone = !Directory.Exists(_root);
            destinationReady = File.Exists(ExePath(newRoot));
            return Task.CompletedTask;
        }, null, CancellationToken.None);

        Assert.Equal(destination, committed);
        Assert.True(sourceGone);
        Assert.True(destinationReady);
        Assert.Equal(InstallState.Installed, _installer.GetStatus(destination).State);
    }

    [Fact]
    public async Task MoveAsync_SameVolume_CommitFails_MovesBackAndRethrows()
    {
        await Install(Release(1));
        var destination = _temp.Combine("Otra", "Coop2D");

        var ex = await Assert.ThrowsAsync<LauncherException>(() => _installer.MoveAsync(_root, destination,
            _ => throw new LauncherException("No se pudieron guardar los ajustes."), null, CancellationToken.None));

        Assert.Equal("No se pudieron guardar los ajustes.", ex.Message);
        Assert.Equal(InstallState.Installed, _installer.GetStatus(_root).State);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task MoveAsync_CrossVolume_CommitsBeforeDeletingSourceAndDeletesViaRename()
    {
        await Install(Release(1), keepArchive: true);
        _installer.IsSameVolume = (_, _) => false;
        var destination = _temp.Combine("OtraUnidad", "Coop2D");
        var sourceIntactAtCommit = false;
        var destinationReadyAtCommit = false;
        var progress = new RecordingProgress<InstallProgress>();

        await _installer.MoveAsync(_root, destination, newRoot =>
        {
            sourceIntactAtCommit = File.Exists(ExePath(_root)) && Directory.Exists(WorkDir);
            destinationReadyAtCommit = File.Exists(ExePath(newRoot)) && !Directory.Exists(newRoot + ".moving");
            return Task.CompletedTask;
        }, progress, CancellationToken.None);

        Assert.True(sourceIntactAtCommit, "el origen no se toca hasta guardar la nueva ruta");
        Assert.True(destinationReadyAtCommit, "el destino ya está completo al guardar");
        Assert.False(Directory.Exists(_root));
        Assert.Empty(LeftoverSiblings(_root));
        Assert.Empty(LeftoverSiblings(destination));
        var status = _installer.GetStatus(destination);
        Assert.Equal(InstallState.Installed, status.State);
        Assert.Equal(1001, status.Info!.AssetId);
        Assert.False(File.Exists(Path.Combine(GameInstaller.GetWorkDir(destination), "operation.lock")));
        Assert.Contains(progress.Items, p => p.Phase == InstallPhase.Moving && p.Status == "Moviendo… 100 %");
    }

    [Fact]
    public async Task MoveAsync_CrossVolume_CommitFails_KeepsSourceAndRemovesCopy()
    {
        await Install(Release(1));
        _installer.IsSameVolume = (_, _) => false;
        var destination = _temp.Combine("OtraUnidad", "Coop2D");

        var ex = await Assert.ThrowsAsync<LauncherException>(() => _installer.MoveAsync(_root, destination,
            _ => Task.FromException(new IOException("disco lleno al guardar")), null, CancellationToken.None));

        Assert.Equal("disco lleno al guardar", Assert.IsType<IOException>(ex.InnerException).Message);
        Assert.Equal(InstallState.Installed, _installer.GetStatus(_root).State);
        Assert.False(Directory.Exists(destination));
        Assert.Empty(LeftoverSiblings(destination));
        Assert.False(File.Exists(Work("operation.lock")));
    }

    [Fact]
    public async Task MoveAsync_CrossVolume_WithoutCallback_DeletesSource()
    {
        await Install(Release(1));
        _installer.IsSameVolume = (_, _) => false;
        var destination = _temp.Combine("OtraUnidad", "Coop2D");

        await _installer.MoveAsync(_root, destination, null, CancellationToken.None);

        Assert.False(Directory.Exists(_root));
        Assert.Empty(LeftoverSiblings(_root));
        Assert.Equal(InstallState.Installed, _installer.GetStatus(destination).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveAsync_RenameFailsWithNotSameDevice_FallsBackToCopy(bool wrapped)
    {
        await Install(Release(1));
        // Misma letra, pero el destino está bajo un punto de montaje/unión a otro disco.
        _installer.IsSameVolume = (_, _) => true;
        var renameAttempts = 0;
        _installer.MoveRootDirectory = (_, _) =>
        {
            renameAttempts++;
            var notSameDevice = new IOException("No se puede mover el archivo a una unidad de disco distinta.", unchecked((int)0x80070011));
            throw wrapped ? new LauncherException("No se pudo mover «…».", notSameDevice) : notSameDevice;
        };
        var destination = _temp.Combine("Montada", "Coop2D");
        string? committed = null;

        await _installer.MoveAsync(_root, destination, newRoot => { committed = newRoot; return Task.CompletedTask; },
            null, CancellationToken.None);

        Assert.Equal(1, renameAttempts);
        Assert.Equal(destination, committed);
        Assert.False(Directory.Exists(_root));
        Assert.Equal(InstallState.Installed, _installer.GetStatus(destination).State);
    }

    [Fact]
    public async Task MoveAsync_RenameFailsForOtherReason_KeepsSourceAndDoesNotCopy()
    {
        await Install(Release(1));
        _installer.MoveRootDirectory = (_, _) => throw new LauncherException("No se pudo mover «x». Cierra el juego…",
            new IOException("en uso", unchecked((int)0x80070020)));
        var destination = _temp.Combine("Otra", "Coop2D");
        var committed = false;

        var ex = await Assert.ThrowsAsync<LauncherException>(() => _installer.MoveAsync(_root, destination,
            _ => { committed = true; return Task.CompletedTask; }, null, CancellationToken.None));

        Assert.StartsWith("No se pudo mover", ex.Message, StringComparison.Ordinal);
        Assert.False(committed);
        Assert.Equal(InstallState.Installed, _installer.GetStatus(_root).State);
        Assert.False(Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any());
    }

    [Fact]
    public async Task MoveAsync_CommitRunsOnCallerSynchronizationContext()
    {
        await Install(Release(1));
        var destination = _temp.Combine("Otra", "Coop2D");
        var context = new RecordingSyncContext();
        SynchronizationContext? seen = null;

        Task move;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            move = _installer.MoveAsync(_root, destination, _ =>
            {
                seen = SynchronizationContext.Current;
                return Task.CompletedTask;
            }, null, CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        await move;

        Assert.Same(context, seen);
        Assert.True(context.Posts >= 1);
    }

    private sealed class RecordingSyncContext : SynchronizationContext
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var previous = Current;
                SetSynchronizationContext(this);
                try { d(state); }
                finally { SetSynchronizationContext(previous); }
            });
        }
    }

    // ───────────── Mensajes de error ─────────────

    [Fact]
    public void TranslateFailure_UninstallAccessDenied_DoesNotSuggestAnotherFolderNorShowDotNetText()
    {
        var ex = GameInstaller.TranslateFailure(GameInstaller.InstallerOperation.Uninstall, _root,
            new UnauthorizedAccessException("Access to the path is denied."));

        var le = Assert.IsType<LauncherException>(ex);
        Assert.Contains(_root, le.Message, StringComparison.Ordinal);
        Assert.Contains("Cierra el juego", le.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("otra carpeta", le.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Access", le.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TranslateFailure_MoveAccessDenied_DoesNotBlameDestination()
    {
        var destination = _temp.Combine("Destino", "Coop2D");

        var ex = GameInstaller.TranslateFailure(GameInstaller.InstallerOperation.Move, _root,
            new UnauthorizedAccessException("Access to the path is denied."), destination);

        var le = Assert.IsType<LauncherException>(ex);
        Assert.DoesNotContain(destination, le.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sin permisos para escribir", le.Message, StringComparison.Ordinal);
        Assert.StartsWith("Windows no deja mover el juego.", le.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Install", "Error de disco al instalar el juego.")]
    [InlineData("Uninstall", "Error de disco al desinstalar el juego.")]
    [InlineData("Move", "Error de disco al mover el juego.")]
    public void TranslateFailure_GenericIOException_IsSpanishWithoutRawText(string operation, string start)
    {
        var raw = new IOException("The process cannot access the file because it is being used by another process.");

        var le = Assert.IsType<LauncherException>(GameInstaller.TranslateFailure(
            Enum.Parse<GameInstaller.InstallerOperation>(operation), _root, raw));

        Assert.StartsWith(start, le.Message, StringComparison.Ordinal);
        Assert.Contains("«Crear informe»", le.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("process", le.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(raw, le.InnerException);
    }

    [Fact]
    public void TranslateFailure_SharingViolationDuringInstall_SaysFilesInUse()
    {
        var le = Assert.IsType<LauncherException>(GameInstaller.TranslateFailure(GameInstaller.InstallerOperation.Install, _root,
            new IOException("in use", unchecked((int)0x80070020))));

        Assert.StartsWith($"Windows no deja modificar archivos en {_root}.", le.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TranslateFailure_DiskFullAndAntivirus_HaveSpecificMessages()
    {
        var full = Assert.IsType<LauncherException>(GameInstaller.TranslateFailure(GameInstaller.InstallerOperation.Move, _root,
            new IOException("full", unchecked((int)0x80070070))));
        Assert.StartsWith("No hay espacio suficiente en el disco.", full.Message, StringComparison.Ordinal);

        var virus = Assert.IsType<LauncherException>(GameInstaller.TranslateFailure(GameInstaller.InstallerOperation.Install, _root,
            new IOException("virus", unchecked((int)0x800700E1))));
        Assert.StartsWith("El antivirus bloqueó archivos del juego.", virus.Message, StringComparison.Ordinal);
        Assert.Contains(_root, virus.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TranslateFailure_PassThroughCases_ReturnSameInstance()
    {
        var cancel = new OperationCanceledException();
        var launcher = new LauncherException("ya en español");
        var unexpected = new InvalidOperationException("bug");

        Assert.Same(cancel, GameInstaller.TranslateFailure(GameInstaller.InstallerOperation.Install, _root, cancel));
        Assert.Same(launcher, GameInstaller.TranslateFailure(GameInstaller.InstallerOperation.Uninstall, _root, launcher));
        Assert.Same(unexpected, GameInstaller.TranslateFailure(GameInstaller.InstallerOperation.Move, _root, unexpected));
    }

    [Fact]
    public void TranslateFailure_DriveGone_SaysConnectDrive()
    {
        var drive = TestBuilds.FindMissingDriveRoot();
        if (drive == null)
            return; // todas las letras existen en este equipo

        var le = Assert.IsType<LauncherException>(GameInstaller.TranslateFailure(GameInstaller.InstallerOperation.Move, _root,
            new IOException("device not ready"), Path.Combine(drive, "Juegos", "Coop2D")));

        Assert.StartsWith($"No se encuentra la unidad {drive}.", le.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsNotSameDevice_DetectsRawAndWrappedHResult()
    {
        var raw = new IOException("x", unchecked((int)0x80070011));

        Assert.True(GameInstaller.IsNotSameDevice(raw));
        Assert.True(GameInstaller.IsNotSameDevice(new LauncherException("y", raw)));
        Assert.False(GameInstaller.IsNotSameDevice(new IOException("en uso", unchecked((int)0x80070020))));
        Assert.False(GameInstaller.IsNotSameDevice(null));
    }

    // ───────────── Otros ─────────────

    [Fact]
    public async Task UpdateMarkerIdentity_SameShaDifferentIds_RewritesIdentityOnly()
    {
        var release = Release(1);
        await Install(release);
        var recreatedAsset = new ReleaseAsset
        {
            Id = 5555, Name = "2D_0.0.1_Protype1-b.zip", Size = release.Asset.Size,
            DownloadUrl = release.Asset.DownloadUrl, Sha256 = release.Asset.Sha256!.ToUpperInvariant(),
        };
        var recreated = new GameRelease(new ReleaseInfo { Id = 777, TagName = "0.0.1-Protype1", Assets = [recreatedAsset] }, recreatedAsset, null);

        Assert.True(_installer.UpdateMarkerIdentity(_root, recreated));

        var marker = TestBuilds.ReadMarker(GameDir)!;
        Assert.Equal(5555, marker.AssetId);
        Assert.Equal(777, marker.ReleaseId);
        Assert.Equal("0.0.1-Protype1", marker.Tag);
        Assert.Equal("2D_0.0.1_Protype1-b.zip", marker.AssetName);
        Assert.Equal(release.Asset.Sha256, marker.Sha256);
        Assert.False(_installer.UpdateMarkerIdentity(_root, recreated), "ya actualizado");
        Assert.False(_installer.UpdateMarkerIdentity(_root, Release(2)), "otro contenido");
    }

    [Fact]
    public void UpdateMarkerIdentity_NoHash_ReturnsFalse()
    {
        TestBuilds.CreateGameDir(GameDir, "x", sha256: null);
        var asset = new ReleaseAsset { Id = 9, Name = "a.zip", Size = 1, DownloadUrl = "https://github.com/a", Sha256 = "abc" };

        Assert.False(_installer.UpdateMarkerIdentity(_root, new GameRelease(new ReleaseInfo { Id = 9, TagName = "t" }, asset, null)));
        Assert.False(_installer.UpdateMarkerIdentity(@"no\absoluta", new GameRelease(new ReleaseInfo { Id = 9, TagName = "t" }, asset, null)));
    }

    [Fact]
    public void ClearDownloads_RemovesPartialAndCompleteFiles()
    {
        Directory.CreateDirectory(Work("downloads"));
        File.WriteAllText(Work("downloads", "1-a.zip.part"), "p");
        File.WriteAllText(Work("downloads", "1-a.zip.part.json"), "{}");
        File.WriteAllText(Work("downloads", "2-b.zip"), "z");

        _installer.ClearDownloads(_root);

        Assert.False(Directory.Exists(Work("downloads")) && Directory.EnumerateFileSystemEntries(Work("downloads")).Any());
        Assert.False(_installer.GetStatus(_root).HasPendingDownload);
        _installer.ClearDownloads(_temp.Combine("no-existe"));
    }

    [Fact]
    public void ClearDownloads_LockHeld_DoesNothing()
    {
        Directory.CreateDirectory(Work("downloads"));
        File.WriteAllText(Work("downloads", "1-a.zip.part"), "p");
        using var held = new FileStream(Work("operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        _installer.ClearDownloads(_root);

        Assert.True(File.Exists(Work("downloads", "1-a.zip.part")));
    }

    [Fact]
    public void GetResumableBytes_NoPartial_IsZero()
    {
        Assert.Equal(0, _installer.GetResumableBytes(_root, Release(1)));
        Assert.Equal(0, _installer.GetResumableBytes(@"no\absoluta", Release(1)));
    }

    [Fact]
    public void GetUnityPersistentDataDir_ReadsAppInfo()
    {
        Assert.Null(_installer.GetUnityPersistentDataDir(_root));
        TestBuilds.CreateGameDir(GameDir, "x");

        var dir = _installer.GetUnityPersistentDataDir(_root);

        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "PalitoGames", "Coop2D");
        Assert.Equal(expected, dir);
    }

    [Fact]
    public void GetUnityPersistentDataDir_AnyDataFolder_AndBadContent()
    {
        Directory.CreateDirectory(Path.Combine(GameDir, "Otro_Data"));
        File.WriteAllText(Path.Combine(GameDir, "Otro_Data", "app.info"), "Compania\r\nProducto\r\n");
        Assert.EndsWith(Path.Combine("LocalLow", "Compania", "Producto"), _installer.GetUnityPersistentDataDir(_root));

        File.WriteAllText(Path.Combine(GameDir, "Otro_Data", "app.info"), "..\\..\nx");
        Assert.Null(_installer.GetUnityPersistentDataDir(_root));
    }
}
