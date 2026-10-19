using PalitoLauncher.Core;
using PalitoLauncher.Core.Downloads;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Net;
using PalitoLauncher.Tests.TestHelpers.C;

namespace PalitoLauncher.Tests;

/// <summary>
/// Prueba real contra GitHub (JJJP-Dev/Coop2D): descarga ~110 MB. Solo se ejecuta con PALITO_RUN_INTEGRATION=1;
/// si no, termina enseguida y pasa.
/// </summary>
public sealed class IntegrationTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("PALITO_RUN_INTEGRATION") == "1";

    [Fact]
    public async Task RealGitHub_InstallOldestThenLatest()
    {
        if (!Enabled)
            return;

        using var temp = new TempDir();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;

        var github = new GitHubClient(HttpFactory.Shared, temp.Combine("cache"));
        var fetch = await github.GetReleasesAsync(LauncherConfig.GameRepoOwner, LauncherConfig.GameRepoName, ct, bypassFreshness: true);
        var releases = ReleaseSelector.GetGameReleases(fetch.Releases, LauncherConfig.GameAssetPattern, includePrereleases: false);
        Assert.NotEmpty(releases);

        var installer = new GameInstaller(new FileDownloader(HttpFactory.Shared), LauncherConfig.GameExecutableName)
        {
            PostExtractDelay = TimeSpan.FromMilliseconds(500),
        };
        var root = GameInstaller.ResolveInstallRoot(temp.Combine("Juegos"));
        var gameDir = GameInstaller.GetGameDir(root);
        var workDir = GameInstaller.GetWorkDir(root);

        // 1. La más vieja (0.0.1, ~40 MB).
        var oldest = releases[^1];
        var first = await installer.InstallAsync(root, oldest, new InstallOptions(), null, ct);
        Assert.True(File.Exists(Path.Combine(gameDir, LauncherConfig.GameExecutableName)));
        var status1 = installer.GetStatus(root);
        Assert.Equal(InstallState.Installed, status1.State);
        Assert.Equal(oldest.Asset.Id, status1.Info!.AssetId);
        Assert.Equal(oldest.Asset.Sha256, first.Sha256);
        Assert.Equal(oldest.Version?.ToString(), status1.Info.Version);
        Assert.NotNull(installer.GetUnityPersistentDataDir(root));

        // 2. La última (0.0.2): actualización completa.
        var latest = releases[0];
        if (latest.Asset.Id != oldest.Asset.Id)
        {
            var progress = new RecordingProgress<InstallProgress>();
            await installer.InstallAsync(root, latest, new InstallOptions(), progress, ct);

            var status2 = installer.GetStatus(root);
            Assert.Equal(InstallState.Installed, status2.State);
            Assert.Equal(latest.Asset.Id, status2.Info!.AssetId);
            Assert.Equal(latest.Release.TagName, status2.Info.Tag);
            Assert.Contains(progress.Items, p => p.Phase == InstallPhase.Downloading && p.Download != null);
        }

        // Sin restos en .launcher.
        foreach (var leftover in new[] { "staging", "ready", "old", "trash" })
            Assert.False(Directory.Exists(Path.Combine(workDir, leftover)), leftover);
        Assert.False(File.Exists(Path.Combine(workDir, "swap.json")));
        Assert.False(File.Exists(Path.Combine(workDir, "operation.lock")));
        var downloads = Path.Combine(workDir, "downloads");
        Assert.True(!Directory.Exists(downloads) || !Directory.EnumerateFileSystemEntries(downloads).Any());

        await installer.UninstallAsync(root, null, ct);
        Assert.False(Directory.Exists(root));
    }
}
