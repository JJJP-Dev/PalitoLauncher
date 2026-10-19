using PalitoLauncher.Core;
using PalitoLauncher.Core.GitHub;

namespace PalitoLauncher.Tests;

public class ReleaseSelectorTests
{
    private const string Pattern = LauncherConfig.GameAssetPattern;

    private static long _nextId = 1000;

    private static ReleaseAsset Asset(string name, long size = 1000, long? id = null)
        => new() { Id = id ?? Interlocked.Increment(ref _nextId), Name = name, Size = size, DownloadUrl = "https://example.invalid/" + name };

    private static ReleaseInfo Release(string tag, string? name = null, DateTimeOffset? published = null, bool draft = false,
        bool prerelease = false, long? id = null, params ReleaseAsset[] assets)
        => new()
        {
            Id = id ?? Interlocked.Increment(ref _nextId),
            TagName = tag,
            Name = name,
            PublishedAt = published,
            IsDraft = draft,
            IsPrerelease = prerelease,
            Assets = assets,
        };

    private static DateTimeOffset Day(int day) => new(2026, 10, day, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SelectGameAsset_PrefersPatternMatch()
    {
        var release = Release("t", assets: new[] { Asset("Otro.zip", 9_999_999), Asset("2D_0.0.2_Protype2.zip", 70_782_621), Asset("notas.txt") });

        var asset = ReleaseSelector.SelectGameAsset(release, Pattern);

        Assert.NotNull(asset);
        Assert.Equal("2D_0.0.2_Protype2.zip", asset.Name);
    }

    [Fact]
    public void SelectGameAsset_PatternIsCaseInsensitive()
    {
        var release = Release("t", assets: new[] { Asset("Otro.zip", 9_999_999), Asset("2d_0.1.0.ZIP") });
        Assert.Equal("2d_0.1.0.ZIP", ReleaseSelector.SelectGameAsset(release, Pattern)?.Name);
    }

    [Fact]
    public void SelectGameAsset_FallsBackToAnyArchive()
    {
        var release = Release("t", assets: new[] { Asset("Coop2D.exe", 5000), Asset("Coop2D.zip", 100), Asset("readme.md") });
        Assert.Equal("Coop2D.zip", ReleaseSelector.SelectGameAsset(release, Pattern)?.Name);
    }

    [Fact]
    public void SelectGameAsset_PrefersZipThen7zThenRar()
    {
        var release = Release("t", assets: new[] { Asset("2D_0.1.0.rar", 10_000), Asset("2D_0.1.0.7z", 5_000), Asset("2D_0.1.0.zip", 1_000) });
        Assert.Equal("2D_0.1.0.zip", ReleaseSelector.SelectGameAsset(release, Pattern)?.Name);

        var noZip = Release("t", assets: new[] { Asset("2D_0.1.0.rar", 10_000), Asset("2D_0.1.0.7z", 5_000) });
        Assert.Equal("2D_0.1.0.7z", ReleaseSelector.SelectGameAsset(noZip, Pattern)?.Name);

        var fallback = Release("t", assets: new[] { Asset("game.rar", 10_000), Asset("game.7z", 5_000) });
        Assert.Equal("game.7z", ReleaseSelector.SelectGameAsset(fallback, Pattern)?.Name);
    }

    [Fact]
    public void SelectGameAsset_SameExtension_PrefersLargest()
    {
        var release = Release("t", assets: new[] { Asset("2D_0.1.0_small.zip", 10), Asset("2D_0.1.0.zip", 1_000), Asset("2D_0.1.0_mid.zip", 100) });
        Assert.Equal("2D_0.1.0.zip", ReleaseSelector.SelectGameAsset(release, Pattern)?.Name);
    }

    [Fact]
    public void SelectGameAsset_NoCandidates_ReturnsNull()
    {
        Assert.Null(ReleaseSelector.SelectGameAsset(Release("t", assets: new[] { Asset("Coop2D.exe"), Asset("notes.txt") }), Pattern));
        Assert.Null(ReleaseSelector.SelectGameAsset(Release("t"), Pattern));
    }

    [Fact]
    public void SelectGameAsset_InvalidPattern_FallsBackToExtension()
    {
        var release = Release("t", assets: new[] { Asset("Coop2D.zip") });
        Assert.Equal("Coop2D.zip", ReleaseSelector.SelectGameAsset(release, "([unclosed")?.Name);
        Assert.Equal("Coop2D.zip", ReleaseSelector.SelectGameAsset(release, "")?.Name);
    }

    [Fact]
    public void GetGameReleases_RealData_OrderedNewestFirst()
    {
        var p1 = Release("0.0.1-Protype1", "Prototype 1", Day(1), assets: Asset("2D_0.0.1_Protype1.zip", 39_841_913));
        var p2 = Release("0.0.2-Prototype-2", "Prototype 2 Test", Day(10), assets: Asset("2D_0.0.2_Protype2.zip", 70_782_621));

        var list = ReleaseSelector.GetGameReleases(new[] { p1, p2 }, Pattern, includePrereleases: false);

        Assert.Equal(2, list.Count);
        Assert.Same(p2, list[0].Release);
        Assert.Equal("0.0.2-prototype.2", list[0].Version?.ToString());
        Assert.Equal("0.0.2 · Prototipo 2", list[0].VersionDisplay);
        Assert.Equal("2D_0.0.2_Protype2.zip", list[0].Asset.Name);
        Assert.Same(p1, list[1].Release);
        Assert.Equal("0.0.1-prototype.1", list[1].Version?.ToString());
    }

    [Fact]
    public void GetGameReleases_OrdersByVersionNotByDate()
    {
        // La 0.0.2 se publicó antes que una revisión tardía de la 0.0.1: manda la versión.
        var older = Release("a", published: Day(20), assets: Asset("2D_0.0.1_Protype1.zip"));
        var newer = Release("b", published: Day(5), assets: Asset("2D_0.0.2_Protype3.zip"));
        var stable = Release("c", published: Day(3), assets: Asset("2D_0.0.2.zip"));

        var list = ReleaseSelector.GetGameReleases(new[] { older, newer, stable }, Pattern, false);

        Assert.Equal(new[] { "c", "b", "a" }, list.Select(r => r.Release.TagName));
    }

    [Fact]
    public void GetGameReleases_ExcludesDraftsAndReleasesWithoutGameAsset()
    {
        var draft = Release("draft", draft: true, assets: Asset("2D_0.9.0.zip"));
        var noAsset = Release("empty", published: Day(2), assets: Asset("notes.txt"));
        var ok = Release("ok", published: Day(1), assets: Asset("2D_0.1.0.zip"));

        var list = ReleaseSelector.GetGameReleases(new[] { draft, noAsset, ok }, Pattern, true);

        Assert.Single(list);
        Assert.Same(ok, list[0].Release);
    }

    [Fact]
    public void GetGameReleases_GitHubPrereleases_OnlyWhenIncluded()
    {
        var pre = Release("pre", published: Day(5), prerelease: true, assets: Asset("2D_0.2.0.zip"));
        var stable = Release("stable", published: Day(1), assets: Asset("2D_0.1.0.zip"));

        var without = ReleaseSelector.GetGameReleases(new[] { pre, stable }, Pattern, includePrereleases: false);
        Assert.Single(without);
        Assert.Same(stable, without[0].Release);

        var with = ReleaseSelector.GetGameReleases(new[] { pre, stable }, Pattern, includePrereleases: true);
        Assert.Equal(new[] { "pre", "stable" }, with.Select(r => r.Release.TagName));
    }

    [Fact]
    public void GetGameReleases_PrototypeNamedReleases_AreNotGitHubPrereleases()
    {
        // "Prototipo" en el nombre no las excluye: solo cuenta el flag de GitHub.
        var proto = Release("0.0.2-Prototype-2", published: Day(1), assets: Asset("2D_0.0.2_Protype2.zip"));
        var list = ReleaseSelector.GetGameReleases(new[] { proto }, Pattern, includePrereleases: false);
        Assert.Single(list);
    }

    [Fact]
    public void GetGameReleases_WithoutVersion_GoLast_TieByPublishedAt()
    {
        var noVersionNew = Release("latest", "Build especial", Day(25), assets: Asset("Coop2D.zip"));
        var noVersionOld = Release("old", "Build vieja", Day(2), assets: Asset("Coop2D.zip"));
        var versioned = Release("v", published: Day(1), assets: Asset("2D_0.0.1_Protype1.zip"));

        var list = ReleaseSelector.GetGameReleases(new[] { noVersionOld, versioned, noVersionNew }, Pattern, false);

        Assert.Equal(new[] { "v", "latest", "old" }, list.Select(r => r.Release.TagName));
        Assert.Null(list[1].Version);
        Assert.Equal("latest", list[1].VersionDisplay);
    }

    [Fact]
    public void GetGameReleases_SameVersion_NewestPublishedFirst()
    {
        var a = Release("a", published: Day(1), assets: Asset("2D_0.1.0.zip"));
        var b = Release("b", published: Day(9), assets: Asset("2D_0.1.0.zip"));
        var c = Release("c", published: null, assets: Asset("2D_0.1.0.zip"));

        var list = ReleaseSelector.GetGameReleases(new[] { a, c, b }, Pattern, false);

        Assert.Equal(new[] { "b", "a", "c" }, list.Select(r => r.Release.TagName));
    }

    [Fact]
    public void GetGameReleases_VersionFromAssetThenTagThenName()
    {
        var fromTag = Release("v0.5.0", "Lo que sea", Day(1), assets: Asset("Coop2D.zip"));
        var fromName = Release("latest", "Versión 0.4.0", Day(1), assets: Asset("Coop2D.zip"));
        var fromAsset = Release("v9.9.9", "Versión 8.8.8", Day(1), assets: Asset("2D_0.3.0.zip"));

        var list = ReleaseSelector.GetGameReleases(new[] { fromTag, fromName, fromAsset }, Pattern, false);

        Assert.Equal(new[] { "0.5.0", "0.4.0", "0.3.0" }, list.Select(r => r.Version!.ToString()));
    }

    [Fact]
    public void GetGameReleases_IsDeterministic()
    {
        var releases = Enumerable.Range(0, 20)
            .Select(i => Release($"t{i}", published: Day(1 + i % 3), id: i, assets: Asset(i % 2 == 0 ? "2D_0.1.0.zip" : "Coop2D.zip", id: 100 + i)))
            .ToArray();

        var first = ReleaseSelector.GetGameReleases(releases, Pattern, false).Select(r => r.Release.TagName).ToArray();
        var second = ReleaseSelector.GetGameReleases(releases.Reverse(), Pattern, false).Select(r => r.Release.TagName).ToArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public void GetGameReleases_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(ReleaseSelector.GetGameReleases(Array.Empty<ReleaseInfo>(), Pattern, true));
    }
}
