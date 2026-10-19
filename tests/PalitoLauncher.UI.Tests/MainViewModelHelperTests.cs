using PalitoLauncher.Core.GitHub;
using PalitoLauncher.UI.Tests.TestHelpers;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.UI.Tests;

/// <summary>
/// Lógica del MainViewModel que no necesita ventana ni red: el aviso al pausar una descarga, la build de reemplazo tras
/// un fallo de verificación y cuándo se levanta la pausa de la auto-actualización. Se crea un MainViewModel real (sin
/// InitializeAsync, así que nunca consulta GitHub) y se le dan las releases como si llegaran de una consulta.
/// </summary>
public sealed class MainViewModelHelperTests
{
    private const string Continue = "Descarga en pausa. Pulsa CONTINUAR cuando quieras.";
    private const string FromVersions = "Descarga en pausa. Vuelve a pulsar «Instalar esta versión» en Versiones para continuar.";
    private const string FromRepair = "Descarga en pausa. Vuelve a pulsar «Reparar» para continuar.";

    private static readonly DateTimeOffset D = TestReleases.BaseDate;

    private static Task WithViewModel(Func<MainViewModel, Task> test) => WpfHost.RunAsync(async () =>
    {
        using var vm = new MainViewModel();
        await test(vm);
    });

    private static Task WithViewModel(Action<MainViewModel> test) => WpfHost.RunAsync(() =>
    {
        using var vm = new MainViewModel();
        test(vm);
        return Task.CompletedTask;
    });

    // ───────────── Aviso al pausar ─────────────

    [Theory]
    [InlineData(OperationKind.Install, 30L, Continue)]
    [InlineData(OperationKind.Update, 30L, Continue)]
    [InlineData(OperationKind.Install, 20L, FromVersions)]      // la última cambió durante la descarga: CONTINUAR sería otra build
    [InlineData(OperationKind.Update, 20L, FromVersions)]
    [InlineData(OperationKind.Install, null, FromVersions)]
    [InlineData(OperationKind.InstallOther, 20L, FromVersions)]
    [InlineData(OperationKind.InstallOther, 30L, FromVersions)]
    [InlineData(OperationKind.Repair, 30L, FromRepair)]          // CONTINUAR no repara: se vuelve a pulsar «Reparar»
    [InlineData(OperationKind.Repair, 20L, FromRepair)]
    public Task PausedToast_OffersContinueOnlyWhenThatButtonWillAppear(OperationKind kind, long? targetAssetId, string expected)
        => WithViewModel(async vm =>
        {
            await MainViewModelAccess.ApplyReleasesAsync(vm, new[]
            {
                TestReleases.Make("v0.0.3", releaseId: 3, assetId: 30, published: D.AddDays(2)),
                TestReleases.Make("v0.0.2", releaseId: 2, assetId: 20, published: D.AddDays(1)),
            });
            Assert.Equal(expected, MainViewModelAccess.BuildPausedToast(vm, kind, targetAssetId));
        });

    [Fact]
    public Task PausedToast_WithoutReleases_NeverOffersContinue() => WithViewModel(vm =>
    {
        Assert.Equal(FromVersions, MainViewModelAccess.BuildPausedToast(vm, OperationKind.Install, 30));
        Assert.Equal(FromVersions, MainViewModelAccess.BuildPausedToast(vm, OperationKind.Update, 30));
    });

    // ───────────── Build de reemplazo (FindReplacement) ─────────────

    /// <summary>Build que falló al verificarse: v0.0.3, release 3, asset 30, 1 MB, SHA "aaa", publicada en D.</summary>
    private static GameRelease Original(string? sha = "aaa")
        => TestReleases.Make("v0.0.3", releaseId: 3, assetId: 30, sha256: sha, size: 1_000_000, published: D);

    public static TheoryData<string> NoReplacementCases => new()
    {
        "original-still-published-unchanged",
        "identical-zip-reuploaded-with-new-id",
        "same-version-but-older",
        "same-version-without-dates",
        "only-a-newer-version",
        "refresh-failed",
        "original-without-dates",
    };

    [Theory]
    [MemberData(nameof(NoReplacementCases))]
    public Task FindReplacement_RejectsBuildsThatDidNotReallyChange(string scenario) => WithViewModel(vm =>
    {
        var old = Original();
        IReadOnlyList<GameRelease>? refreshed = scenario switch
        {
            // El asset sigue publicado igual → el fallo fue local (red, antivirus…): se muestra el error, aunque haya
            // otra build más nueva de la misma versión.
            "original-still-published-unchanged" => new[]
            {
                TestReleases.Make("v0.0.3", releaseId: 4, assetId: 40, sha256: "bbb", published: D.AddDays(1)),
                Original(),
            },
            // Mismo zip (tamaño y SHA) con otro id: volvería a fallar igual.
            "identical-zip-reuploaded-with-new-id" => new[]
            {
                TestReleases.Make("v0.0.3", releaseId: 3, assetId: 31, sha256: "AAA", size: 1_000_000, published: D),
            },
            // Nunca una build más antigua de la misma versión.
            "same-version-but-older" => new[]
            {
                TestReleases.Make("v0.0.3", releaseId: 5, assetId: 50, sha256: "ccc", published: D.AddDays(-1)),
            },
            "same-version-without-dates" => new[]
            {
                TestReleases.Make("v0.0.3", releaseId: 5, assetId: 50, sha256: "ccc"),
            },
            // Otra versión no es un reemplazo (la actualización a ella es otra decisión).
            "only-a-newer-version" => new[]
            {
                TestReleases.Make("v0.0.4", releaseId: 6, assetId: 60, sha256: "ddd", published: D.AddDays(3)),
            },
            "refresh-failed" => null,
            _ => null,
        };

        if (scenario == "original-without-dates")
        {
            // Sin fechas en la original no se puede saber cuál es más nueva (y no hay otra en su misma release).
            old = TestReleases.Make("v0.0.3", releaseId: 3, assetId: 30, sha256: "aaa", size: 1_000_000);
            refreshed = new[] { TestReleases.Make("v0.0.3", releaseId: 7, assetId: 70, sha256: "eee", published: D.AddDays(5)) };
        }

        MainViewModelAccess.GameReleases(vm) = refreshed;
        Assert.Null(MainViewModelAccess.FindReplacement(vm, old));
    });

    [Fact]
    public Task FindReplacement_PrefersANewBuildInTheSameRelease() => WithViewModel(vm =>
    {
        var sameRelease = TestReleases.Make("v0.0.3", releaseId: 3, assetId: 31, sha256: "fff", published: D);
        var newerRelease = TestReleases.Make("v0.0.3", releaseId: 4, assetId: 40, sha256: "bbb", published: D.AddDays(1));
        MainViewModelAccess.GameReleases(vm) = new[] { newerRelease, sameRelease };

        Assert.Same(sameRelease, MainViewModelAccess.FindReplacement(vm, Original()));
    });

    [Fact]
    public Task FindReplacement_AcceptsTheSameAssetIdWhenItsContentChanged() => WithViewModel(vm =>
    {
        var changed = TestReleases.Make("v0.0.3", releaseId: 3, assetId: 30, sha256: "nuevo", size: 1_000_500, published: D);
        MainViewModelAccess.GameReleases(vm) = new[] { changed };

        Assert.Same(changed, MainViewModelAccess.FindReplacement(vm, Original()));
    });

    [Fact]
    public Task FindReplacement_AcceptsALaterReleaseOfTheSameVersion() => WithViewModel(vm =>
    {
        var older = TestReleases.Make("v0.0.3", releaseId: 2, assetId: 20, sha256: "old", published: D.AddDays(-2));
        var later = TestReleases.Make("v0.0.3", releaseId: 4, assetId: 40, sha256: "bbb", published: D,
            assetUpdated: D.AddHours(3)); // la fecha del asset cuenta aunque la release se publicara a la vez
        MainViewModelAccess.GameReleases(vm) = new[] { older, later };

        Assert.Same(later, MainViewModelAccess.FindReplacement(vm, Original()));
    });

    [Fact]
    public Task FindReplacement_WithoutVersion_MatchesByTag() => WithViewModel(vm =>
    {
        var old = TestReleases.Make("nightly", releaseId: 3, assetId: 30, sha256: "aaa", published: D);
        Assert.Null(old.Version);
        var otherTag = TestReleases.Make("nightly-b", releaseId: 4, assetId: 40, sha256: "bbb", published: D.AddDays(1));
        var sameTag = TestReleases.Make("nightly", releaseId: 5, assetId: 50, sha256: "ccc", published: D.AddDays(1));

        MainViewModelAccess.GameReleases(vm) = new[] { otherTag };
        Assert.Null(MainViewModelAccess.FindReplacement(vm, old));

        MainViewModelAccess.GameReleases(vm) = new[] { otherTag, sameTag };
        Assert.Same(sameTag, MainViewModelAccess.FindReplacement(vm, old));
    });

    // ───────────── Pausa de la auto-actualización (AutoUpdatePausedForTag) ─────────────

    private static readonly GameRelease V1 = TestReleases.Make("v0.0.1", releaseId: 1, assetId: 10, published: D);
    private static readonly GameRelease V2 = TestReleases.Make("v0.0.2", releaseId: 2, assetId: 20, published: D.AddDays(1));
    private static readonly GameRelease V3 = TestReleases.Make("v0.0.3", releaseId: 3, assetId: 30, published: D.AddDays(2));
    private static readonly GameRelease V2Beta = TestReleases.Make("v0.0.2-beta", releaseId: 21, assetId: 21, published: D.AddDays(1), prerelease: true);
    private static readonly GameRelease V4Beta = TestReleases.Make("v0.0.4-beta", releaseId: 41, assetId: 41, published: D.AddDays(3), prerelease: true);

    /// <summary>(tag en pausa, lista visible, todo lo descargado, ¿se levanta la pausa?).</summary>
    public static TheoryData<string, string?, GameRelease[], GameRelease[], bool> PauseCases => new()
    {
        { "la última es más nueva que la pausada", "v0.0.2", new[] { V3, V2, V1 }, new[] { V3, V2, V1 }, true },
        { "la última es justo la pausada", "v0.0.3", new[] { V3, V2, V1 }, new[] { V3, V2, V1 }, false },
        { "la pausada ya no existe (borrada o en borrador)", "v0.0.9", new[] { V3, V2, V1 }, new[] { V3, V2, V1 }, false },
        {
            "la pausada es una prueba oculta MÁS nueva que la última estable",
            "v0.0.4-beta", new[] { V3, V2, V1 }, new[] { V4Beta, V3, V2, V1 }, false
        },
        {
            "la pausada es una prueba oculta y la última es más nueva",
            "v0.0.2-beta", new[] { V3, V2, V1 }, new[] { V3, V2Beta, V2, V1 }, true
        },
        { "la lista visible es más antigua que la pausada", "v0.0.3", new[] { V2, V1 }, new[] { V3, V2, V1 }, false },
        { "sin releases", "v0.0.2", Array.Empty<GameRelease>(), Array.Empty<GameRelease>(), false },
        { "sin pausa", null, new[] { V3, V2, V1 }, new[] { V3, V2, V1 }, false },
    };

    [Theory]
    [MemberData(nameof(PauseCases))]
    public Task AutoUpdatePause_IsClearedOnlyWhenAStrictlyNewerReleaseAppears(string why, string? pausedTag,
        GameRelease[] visible, GameRelease[] all, bool cleared) => WithViewModel(async vm =>
    {
        vm.CurrentSettings.AutoUpdatePausedForTag = pausedTag;
        await MainViewModelAccess.ApplyReleasesAsync(vm, visible, all);
        Assert.True((cleared ? null : pausedTag) == vm.CurrentSettings.AutoUpdatePausedForTag,
            $"{why}: se esperaba {(cleared ? "null" : pausedTag)} y quedó {vm.CurrentSettings.AutoUpdatePausedForTag ?? "null"}.");
    });

    [Theory]
    [InlineData(1, true)]   // la última (sin versión) es posterior a la pausada → se levanta
    [InlineData(-1, false)] // anterior → se conserva
    public Task AutoUpdatePause_WithoutVersions_ComparesDates(int latestOffsetDays, bool cleared) => WithViewModel(async vm =>
    {
        var paused = TestReleases.Make("nightly-a", releaseId: 1, assetId: 10, published: D);
        var latest = TestReleases.Make("nightly-b", releaseId: 2, assetId: 20, published: D.AddDays(latestOffsetDays));
        Assert.Null(paused.Version);
        Assert.Null(latest.Version);

        vm.CurrentSettings.AutoUpdatePausedForTag = "nightly-a";
        await MainViewModelAccess.ApplyReleasesAsync(vm, new[] { latest, paused });
        Assert.Equal(cleared ? null : "nightly-a", vm.CurrentSettings.AutoUpdatePausedForTag);
    });
}
