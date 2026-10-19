using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.State;
using PalitoLauncher.Core.Versioning;

namespace PalitoLauncher.Tests;

public class LauncherStateLogicTests
{
    private static readonly DateTimeOffset InstalledAt = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static GameRelease Game(string assetName, long assetId = 1, string tag = "tag", string? sha = null,
        DateTimeOffset? published = null, DateTimeOffset? assetUpdated = null)
    {
        var asset = new ReleaseAsset { Id = assetId, Name = assetName, Size = 1000, Sha256 = sha, UpdatedAt = assetUpdated };
        var release = new ReleaseInfo { Id = assetId * 10, TagName = tag, PublishedAt = published, Assets = new[] { asset } };
        return new GameRelease(release, asset, GameVersion.FromRelease(assetName, tag, null));
    }

    private static InstalledGameInfo Installed(string assetName, long assetId = 1, string tag = "tag", string? sha = null, string? version = "")
    {
        var info = new InstalledGameInfo
        {
            AssetName = assetName,
            AssetId = assetId,
            Tag = tag,
            Sha256 = sha,
            InstalledAtUtc = InstalledAt,
        };
        // version "" = la que salga del nombre del asset; null = sin versión guardada.
        info.Version = version == "" ? GameVersion.FromRelease(assetName, tag, null)?.ToString() : version;
        return info;
    }

    private static InstallStatus Status(InstallState state, InstalledGameInfo? info = null)
        => new() { State = state, Info = info, InstallRoot = @"C:\Juegos\Coop2D", GameDir = @"C:\Juegos\Coop2D\game" };

    // ───────────── ComputeState ─────────────

    [Fact]
    public void ComputeState_BusyWinsOverEverything()
    {
        var input = new LauncherStateInput
        {
            IsBusy = true,
            IsGameRunning = true,
            Install = Status(InstallState.Broken),
            FetchFailed = true,
        };
        Assert.Equal(LauncherState.Busy, LauncherStateLogic.ComputeState(input));
        Assert.Equal(LauncherState.Busy, LauncherStateLogic.ComputeState(new LauncherStateInput { IsBusy = true }));
    }

    [Fact]
    public void ComputeState_GameRunningBeatsInitializingAndDisk()
    {
        Assert.Equal(LauncherState.GameRunning, LauncherStateLogic.ComputeState(new LauncherStateInput { IsGameRunning = true }));
        Assert.Equal(LauncherState.GameRunning, LauncherStateLogic.ComputeState(new LauncherStateInput
        {
            IsGameRunning = true,
            Install = Status(InstallState.DriveMissing),
        }));
    }

    [Fact]
    public void ComputeState_NoInstallInfo_IsInitializing()
    {
        Assert.Equal(LauncherState.Initializing, LauncherStateLogic.ComputeState(new LauncherStateInput()));
        Assert.Equal(LauncherState.Initializing, LauncherStateLogic.ComputeState(new LauncherStateInput
        {
            Releases = new[] { Game("2D_0.1.0.zip") },
            FetchFailed = true,
        }));
    }

    [Fact]
    public void ComputeState_DriveMissing_BeforeBroken()
    {
        var releases = new[] { Game("2D_0.1.0.zip") };
        Assert.Equal(LauncherState.DriveMissing, LauncherStateLogic.ComputeState(new LauncherStateInput
        {
            Install = Status(InstallState.DriveMissing),
            Releases = releases,
        }));
        Assert.Equal(LauncherState.Broken, LauncherStateLogic.ComputeState(new LauncherStateInput
        {
            Install = Status(InstallState.Broken),
            Releases = releases,
        }));
        Assert.Equal(LauncherState.Broken, LauncherStateLogic.ComputeState(new LauncherStateInput
        {
            Install = Status(InstallState.Broken),
            FetchFailed = true,
        }));
    }

    [Fact]
    public void ComputeState_Installed_UpdateAvailableOrReady()
    {
        var installed = Status(InstallState.Installed, Installed("2D_0.0.2_Protype2.zip", assetId: 2));

        Assert.Equal(LauncherState.UpdateAvailable, LauncherStateLogic.ComputeState(new LauncherStateInput
        {
            Install = installed,
            Releases = new[] { Game("2D_0.0.2_Protype3.zip", assetId: 3), Game("2D_0.0.2_Protype2.zip", assetId: 2) },
        }));

        Assert.Equal(LauncherState.Ready, LauncherStateLogic.ComputeState(new LauncherStateInput
        {
            Install = installed,
            Releases = new[] { Game("2D_0.0.2_Protype2.zip", assetId: 2) },
        }));
    }

    [Fact]
    public void ComputeState_Installed_WithoutReleasesOrFetchFailed_IsReady()
    {
        var installed = Status(InstallState.Installed, Installed("2D_0.0.2_Protype2.zip"));

        Assert.Equal(LauncherState.Ready, LauncherStateLogic.ComputeState(new LauncherStateInput { Install = installed }));
        Assert.Equal(LauncherState.Ready, LauncherStateLogic.ComputeState(new LauncherStateInput { Install = installed, FetchFailed = true }));
        Assert.Equal(LauncherState.Ready, LauncherStateLogic.ComputeState(new LauncherStateInput
        {
            Install = installed,
            Releases = Array.Empty<GameRelease>(),
        }));
        // Marcador sin Info (no debería pasar): se puede jugar igual.
        Assert.Equal(LauncherState.Ready, LauncherStateLogic.ComputeState(new LauncherStateInput
        {
            Install = Status(InstallState.Installed),
            Releases = new[] { Game("2D_0.1.0.zip") },
        }));
    }

    [Fact]
    public void ComputeState_NotInstalled_Variants()
    {
        var notInstalled = Status(InstallState.NotInstalled);

        Assert.Equal(LauncherState.NotInstalled, LauncherStateLogic.ComputeState(new LauncherStateInput
        {
            Install = notInstalled,
            Releases = new[] { Game("2D_0.1.0.zip") },
        }));
        // Con caché offline (FetchFailed false) y releases → INSTALAR.
        Assert.Equal(LauncherState.NotInstalled, LauncherStateLogic.ComputeState(new LauncherStateInput
        {
            Install = notInstalled,
            Releases = new[] { Game("2D_0.1.0.zip") },
            FetchFailed = true,
        }));
        Assert.Equal(LauncherState.NoReleases, LauncherStateLogic.ComputeState(new LauncherStateInput
        {
            Install = notInstalled,
            Releases = Array.Empty<GameRelease>(),
        }));
        Assert.Equal(LauncherState.LoadFailed, LauncherStateLogic.ComputeState(new LauncherStateInput
        {
            Install = notInstalled,
            Releases = Array.Empty<GameRelease>(),
            FetchFailed = true,
        }));
        Assert.Equal(LauncherState.LoadFailed, LauncherStateLogic.ComputeState(new LauncherStateInput
        {
            Install = notInstalled,
            FetchFailed = true,
        }));
        Assert.Equal(LauncherState.LoadFailed, LauncherStateLogic.ComputeState(new LauncherStateInput { Install = notInstalled }));
    }

    // ───────────── GetUpdateKind ─────────────

    [Fact]
    public void GetUpdateKind_HigherVersion_IsNewVersion()
    {
        var installed = Installed("2D_0.0.2_Protype2.zip", assetId: 2);
        Assert.Equal(UpdateKind.NewVersion, LauncherStateLogic.GetUpdateKind(installed, Game("2D_0.0.2_Protype3.zip", assetId: 3)));
        Assert.Equal(UpdateKind.NewVersion, LauncherStateLogic.GetUpdateKind(installed, Game("2D_0.0.2.zip", assetId: 4)));
        Assert.Equal(UpdateKind.NewVersion, LauncherStateLogic.GetUpdateKind(installed, Game("2D_0.1.0.zip", assetId: 5)));
    }

    [Fact]
    public void GetUpdateKind_LowerVersion_IsNone_NeverDowngrade()
    {
        var installed = Installed("2D_0.1.0.zip", assetId: 5);
        Assert.Equal(UpdateKind.None, LauncherStateLogic.GetUpdateKind(installed, Game("2D_0.0.2_Protype3.zip", assetId: 3)));
        Assert.Equal(UpdateKind.None, LauncherStateLogic.GetUpdateKind(installed, Game("2D_0.0.9.zip", assetId: 3, sha: "aa")));
    }

    [Fact]
    public void GetUpdateKind_SameVersion_HashComparison()
    {
        var installed = Installed("2D_0.0.2_Protype2.zip", assetId: 2, sha: "ABCDEF");

        Assert.Equal(UpdateKind.None, LauncherStateLogic.GetUpdateKind(installed, Game("2D_0.0.2_Protype2.zip", assetId: 2, sha: "abcdef")));
        // Mismo hash aunque cambie el asset (release recreada) → no es revisión.
        Assert.Equal(UpdateKind.None, LauncherStateLogic.GetUpdateKind(installed, Game("2D_0.0.2_Protype2.zip", assetId: 99, sha: "abcdef")));
        // Hash distinto → revisión, aunque el asset id sea el mismo.
        Assert.Equal(UpdateKind.Revision, LauncherStateLogic.GetUpdateKind(installed, Game("2D_0.0.2_Protype2.zip", assetId: 2, sha: "123456")));
    }

    [Fact]
    public void GetUpdateKind_SameVersion_WithoutHash_UsesAssetId()
    {
        var installedNoHash = Installed("2D_0.0.2_Protype2.zip", assetId: 2);
        Assert.Equal(UpdateKind.None, LauncherStateLogic.GetUpdateKind(installedNoHash, Game("2D_0.0.2_Protype2.zip", assetId: 2, sha: "abc")));
        Assert.Equal(UpdateKind.Revision, LauncherStateLogic.GetUpdateKind(installedNoHash, Game("2D_0.0.2_Protype2.zip", assetId: 7, sha: "abc")));

        var installedWithHash = Installed("2D_0.0.2_Protype2.zip", assetId: 2, sha: "abc");
        Assert.Equal(UpdateKind.Revision, LauncherStateLogic.GetUpdateKind(installedWithHash, Game("2D_0.0.2_Protype2.zip", assetId: 7)));
        Assert.Equal(UpdateKind.None, LauncherStateLogic.GetUpdateKind(installedWithHash, Game("2D_0.0.2_Protype2.zip", assetId: 2)));
    }

    [Fact]
    public void GetUpdateKind_TagDoesNotMatter()
    {
        var installed = Installed("2D_0.0.2_Protype2.zip", assetId: 2, tag: "0.0.2-Prototype-2", sha: "abc");
        Assert.Equal(UpdateKind.None, LauncherStateLogic.GetUpdateKind(installed, Game("2D_0.0.2_Protype2.zip", assetId: 2, tag: "otro-tag", sha: "abc")));
    }

    [Fact]
    public void GetUpdateKind_UsesStoredVersionFirst()
    {
        // El marcador guarda la versión: manda sobre el nombre del asset.
        var installed = Installed("Coop2D.zip", assetId: 2, version: "0.0.2-prototype.2");
        Assert.Equal(UpdateKind.NewVersion, LauncherStateLogic.GetUpdateKind(installed, Game("2D_0.0.2_Protype3.zip", assetId: 3)));
        Assert.Equal(UpdateKind.None, LauncherStateLogic.GetUpdateKind(installed, Game("2D_0.0.1_Protype1.zip", assetId: 1)));
    }

    [Fact]
    public void GetUpdateKind_LatestWithoutVersion_ComparesAssetAndDate()
    {
        var installed = Installed("2D_0.0.2_Protype2.zip", assetId: 2);

        Assert.Equal(UpdateKind.NewVersion, LauncherStateLogic.GetUpdateKind(installed,
            Game("Coop2D.zip", assetId: 3, published: InstalledAt.AddDays(1))));
        Assert.Equal(UpdateKind.NewVersion, LauncherStateLogic.GetUpdateKind(installed,
            Game("Coop2D.zip", assetId: 3, published: InstalledAt.AddDays(-5), assetUpdated: InstalledAt.AddHours(1))));
        Assert.Equal(UpdateKind.None, LauncherStateLogic.GetUpdateKind(installed,
            Game("Coop2D.zip", assetId: 3, published: InstalledAt.AddDays(-1))));
        Assert.Equal(UpdateKind.None, LauncherStateLogic.GetUpdateKind(installed,
            Game("Coop2D.zip", assetId: 2, published: InstalledAt.AddDays(1))));
        Assert.Equal(UpdateKind.None, LauncherStateLogic.GetUpdateKind(installed,
            Game("Coop2D.zip", assetId: 3, published: null)));
    }

    [Fact]
    public void GetUpdateKind_InstalledWithoutVersion_ComparesAssetAndDate()
    {
        var installed = Installed("Coop2D.zip", assetId: 2, tag: "latest", version: null);
        Assert.Null(installed.ParsedVersion);

        Assert.Equal(UpdateKind.NewVersion, LauncherStateLogic.GetUpdateKind(installed,
            Game("2D_0.1.0.zip", assetId: 3, published: InstalledAt.AddDays(1))));
        Assert.Equal(UpdateKind.None, LauncherStateLogic.GetUpdateKind(installed,
            Game("2D_0.1.0.zip", assetId: 3, published: InstalledAt.AddDays(-1))));
        // Mismo contenido (hash) → no es actualización.
        var installedWithHash = Installed("Coop2D.zip", assetId: 2, tag: "latest", sha: "abc", version: null);
        Assert.Equal(UpdateKind.None, LauncherStateLogic.GetUpdateKind(installedWithHash,
            Game("Coop2D.zip", assetId: 3, sha: "ABC", published: InstalledAt.AddDays(1))));
    }

    [Fact]
    public void GetUpdateKind_Nulls_AreNone()
    {
        Assert.Equal(UpdateKind.None, LauncherStateLogic.GetUpdateKind(null, Game("2D_0.1.0.zip")));
        Assert.Equal(UpdateKind.None, LauncherStateLogic.GetUpdateKind(Installed("2D_0.0.1_Protype1.zip"), null));
        Assert.Equal(UpdateKind.None, LauncherStateLogic.GetUpdateKind(null, null));
    }

    // ───────────── IsSameContentDifferentIdentity ─────────────

    [Fact]
    public void IsSameContentDifferentIdentity_Cases()
    {
        var installed = Installed("2D_0.0.2_Protype2.zip", assetId: 2, sha: "ABC123");

        Assert.True(LauncherStateLogic.IsSameContentDifferentIdentity(installed, Game("2D_0.0.2_Protype2.zip", assetId: 9, sha: "abc123")));
        Assert.False(LauncherStateLogic.IsSameContentDifferentIdentity(installed, Game("2D_0.0.2_Protype2.zip", assetId: 2, sha: "abc123")));
        Assert.False(LauncherStateLogic.IsSameContentDifferentIdentity(installed, Game("2D_0.0.2_Protype2.zip", assetId: 9, sha: "ffff")));
        Assert.False(LauncherStateLogic.IsSameContentDifferentIdentity(installed, Game("2D_0.0.2_Protype2.zip", assetId: 9)));
        Assert.False(LauncherStateLogic.IsSameContentDifferentIdentity(Installed("2D_0.0.2_Protype2.zip", assetId: 2),
            Game("2D_0.0.2_Protype2.zip", assetId: 9, sha: "abc123")));
        Assert.False(LauncherStateLogic.IsSameContentDifferentIdentity(null, Game("2D_0.0.2_Protype2.zip")));
        Assert.False(LauncherStateLogic.IsSameContentDifferentIdentity(installed, null));
    }

    // ───────────── SelectRepairTarget ─────────────

    [Fact]
    public void SelectRepairTarget_PrefersSameAsset_ThenSameTag_ThenLatest()
    {
        var latest = Game("2D_0.0.3.zip", assetId: 30, tag: "v0.0.3");
        var sameTag = Game("2D_0.0.2_Protype2.zip", assetId: 21, tag: "0.0.2-Prototype-2");
        var sameAsset = Game("2D_0.0.2_Protype2.zip", assetId: 20, tag: "recreada");
        var releases = new[] { latest, sameTag, sameAsset };

        Assert.Same(sameAsset, LauncherStateLogic.SelectRepairTarget(Installed("x.zip", assetId: 20, tag: "0.0.2-Prototype-2"), releases));
        Assert.Same(sameTag, LauncherStateLogic.SelectRepairTarget(Installed("x.zip", assetId: 999, tag: "0.0.2-Prototype-2"), releases));
        Assert.Same(latest, LauncherStateLogic.SelectRepairTarget(Installed("x.zip", assetId: 999, tag: "desaparecida"), releases));
        Assert.Same(latest, LauncherStateLogic.SelectRepairTarget(null, releases));
    }

    [Fact]
    public void SelectRepairTarget_EmptyList_ReturnsNull()
    {
        Assert.Null(LauncherStateLogic.SelectRepairTarget(Installed("x.zip"), Array.Empty<GameRelease>()));
        Assert.Null(LauncherStateLogic.SelectRepairTarget(null, Array.Empty<GameRelease>()));
    }

    // ───────────── ShouldAutoUpdate ─────────────

    [Fact]
    public void ShouldAutoUpdate_RequiresAllConditions()
    {
        var latest = Game("2D_0.0.3.zip", tag: "v0.0.3");

        Assert.True(LauncherStateLogic.ShouldAutoUpdate(LauncherState.UpdateAvailable, true, false, latest, null));
        Assert.True(LauncherStateLogic.ShouldAutoUpdate(LauncherState.UpdateAvailable, true, false, latest, "v0.0.2"));

        Assert.False(LauncherStateLogic.ShouldAutoUpdate(LauncherState.UpdateAvailable, false, false, latest, null));
        Assert.False(LauncherStateLogic.ShouldAutoUpdate(LauncherState.UpdateAvailable, true, true, latest, null));
        Assert.False(LauncherStateLogic.ShouldAutoUpdate(LauncherState.UpdateAvailable, true, false, latest, "v0.0.3"));
        Assert.False(LauncherStateLogic.ShouldAutoUpdate(LauncherState.UpdateAvailable, true, false, null, null));
        Assert.False(LauncherStateLogic.ShouldAutoUpdate(LauncherState.Ready, true, false, latest, null));
        Assert.False(LauncherStateLogic.ShouldAutoUpdate(LauncherState.Busy, true, false, latest, null));
        Assert.False(LauncherStateLogic.ShouldAutoUpdate(LauncherState.NotInstalled, true, false, latest, null));
    }
}
