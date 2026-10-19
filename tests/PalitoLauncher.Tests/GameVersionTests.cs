using PalitoLauncher.Core.Versioning;

namespace PalitoLauncher.Tests;

public class GameVersionTests
{
    // texto → forma canónica esperada (null = sin versión)
    public static TheoryData<string?, string?> ParseCases => new()
    {
        { "2D_0.0.1_Protype1.zip", "0.0.1-prototype.1" },
        { "2D_0.0.2_Protype2.zip", "0.0.2-prototype.2" },
        { "2D_0.0.2_Protype3.zip", "0.0.2-prototype.3" },
        { "2D_0.1.0.zip", "0.1.0" },
        { "2D_1.0.zip", "1.0.0" },
        { "2D_1.zip", null },
        { "0.0.1-Protype1", "0.0.1-prototype.1" },
        { "0.0.2-Prototype-2", "0.0.2-prototype.2" },
        { "Prototype 2 Test", null },
        { "Prototype 1", null },
        { "v1.2.3", "1.2.3" },
        { "V1.2.3", "1.2.3" },
        { "1.2", "1.2.0" },
        { "1.0.0-beta.2", "1.0.0-beta.2" },
        { "1.0.0 RC1", "1.0.0-rc.1" },
        { "1.2.3.4", "1.2.3.4" },
        { "1.2.3.0", "1.2.3" },
        { "PalitoLauncher-v1.0.1.exe", "1.0.1" },
        { "2D_0.0.2_Prototipe3.zip", "0.0.2-prototype.3" },
        { "2D_0.0.2_Prototipo4.rar", "0.0.2-prototype.4" },
        { "2D_0.0.2_proto.zip", "0.0.2-prototype" },
        { "2D_0.1.0_win64.zip", "0.1.0" },
        { "2D_0.1.0_x64.zip", "0.1.0" },
        { "2D_0.1.0_Windows.zip", "0.1.0" },
        { "0.2.0-Release", "0.2.0" },
        { "1.0.0+build.5", "1.0.0" },
        { "2D_0.3.0_test2.zip", "0.3.0-test.2" },
        { "0.3.0_alpha", "0.3.0-alpha" },
        { "1.0.0-alfa", "1.0.0-alpha" },
        { "1.0.0-pre2", "1.0.0-preview.2" },
        { "1.0.0-Preview.2", "1.0.0-preview.2" },
        { "1.0.0-dev1", "1.0.0-dev.1" },
        { "2D_0.0.2.zip", "0.0.2" },
        { "2D_0.0.2.7z", "0.0.2" },
        { "2D_0.0.2.RAR", "0.0.2" },
        { "  2D_0.0.2.zip  ", "0.0.2" },
        { "Coop2D.zip", null },
        { "99999999999.0.1", null },
        { "99999999999.0.1 y luego 1.2.3", "1.2.3" },
        { "", null },
        { "   ", null },
        { null, null },
    };

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void TryParse_ProducesExpectedCanonicalForm(string? text, string? expected)
    {
        var ok = GameVersion.TryParse(text, out var version);

        if (expected is null)
        {
            Assert.False(ok);
            Assert.Null(version);
        }
        else
        {
            Assert.True(ok);
            Assert.NotNull(version);
            Assert.Equal(expected, version.ToString());
        }
    }

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void RoundTrip_TryParseOfToString_IsEqual(string? text, string? expected)
    {
        if (expected is null || !GameVersion.TryParse(text, out var version)) return;

        Assert.True(GameVersion.TryParse(version.ToString(), out var again));
        Assert.Equal(version, again);
        Assert.Equal(version.GetHashCode(), again.GetHashCode());
    }

    [Fact]
    public void TryParse_ExposesComponents()
    {
        Assert.True(GameVersion.TryParse("2D_0.0.2_Protype3.zip", out var v));
        Assert.Equal(0, v.Major);
        Assert.Equal(0, v.Minor);
        Assert.Equal(2, v.Patch);
        Assert.Equal(0, v.Revision);
        Assert.Equal("prototype", v.PreLabel);
        Assert.Equal(3, v.PreNumber);
        Assert.True(v.IsPrerelease);

        Assert.True(GameVersion.TryParse("2D_0.1.0.zip", out var stable));
        Assert.False(stable.IsPrerelease);
        Assert.Null(stable.PreLabel);
        Assert.Null(stable.PreNumber);
    }

    [Fact]
    public void UnknownLabel_IsPrerelease_NeverStable()
    {
        Assert.True(GameVersion.TryParse("2D_0.3.0_test2.zip", out var v));
        Assert.True(v.IsPrerelease);
        Assert.Equal("test", v.PreLabel);
        Assert.Equal(2, v.PreNumber);
        Assert.True(v < V("0.3.0"));
    }

    [Fact]
    public void Ordering_FollowsSpec()
    {
        string[] ordered =
        {
            "2D_0.0.1_Protype1.zip",
            "2D_0.0.2_Protype2.zip",
            "2D_0.0.2_Protype3.zip",
            "2D_0.0.2_Protype4.zip",
            "2D_0.0.2.zip",
            "2D_0.0.3.zip",
            "2D_0.1.0.zip",
        };
        AssertStrictlyAscending(ordered);
    }

    [Fact]
    public void Ordering_PrereleaseLabels()
    {
        AssertStrictlyAscending(new[]
        {
            "1.0.0-dev", "1.0.0-prototype", "1.0.0-alpha", "1.0.0-beta", "1.0.0-preview", "1.0.0-rc.1", "1.0.0",
        });
    }

    [Fact]
    public void Ordering_PrototypeWithoutNumber_IsBelowPrototype1()
    {
        AssertStrictlyAscending(new[] { "0.0.2-proto", "0.0.2-prototype.1", "0.0.2-prototype.2", "0.0.2-prototype.10" });
    }

    [Fact]
    public void Ordering_PrototipeTypo_BetweenProtype2AndStable()
    {
        var p2 = V("2D_0.0.2_Protype2.zip");
        var p3 = V("2D_0.0.2_Prototipe3.zip");
        var stable = V("2D_0.0.2.zip");
        Assert.True(p3 > p2);
        Assert.True(p3 < stable);
    }

    [Fact]
    public void Ordering_UnknownLabels_SameRankAsPrototype_TieBrokenOrdinally()
    {
        var proto = V("1.0.0-prototype.5");
        var test = V("1.0.0-test.1");
        var alpha = V("1.0.0-alpha");
        var dev = V("1.0.0-dev.9");

        Assert.True(dev < test);
        Assert.True(test < alpha);
        Assert.True(proto < test); // "prototype" < "test" (ordinal)
        Assert.NotEqual(0, proto.CompareTo(test));
    }

    [Fact]
    public void Ordering_NumericComponentsAndRevision()
    {
        AssertStrictlyAscending(new[] { "0.9.9", "0.10.0", "1.0.0", "1.0.0.1", "1.0.1", "1.2.0", "2.0" });
    }

    [Fact]
    public void Ordering_SortsListCorrectly()
    {
        var list = new[] { "2D_0.1.0.zip", "2D_0.0.2_Protype2.zip", "2D_0.0.2.zip", "2D_0.0.1_Protype1.zip", "2D_0.0.2_Protype3.zip" }
            .Select(V).ToList();
        list.Sort();
        Assert.Equal(new[] { "0.0.1-prototype.1", "0.0.2-prototype.2", "0.0.2-prototype.3", "0.0.2", "0.1.0" },
            list.Select(v => v.ToString()));
    }

    [Fact]
    public void NullHandling_NullIsLowest()
    {
        var v = V("0.0.1");
        Assert.True(v.CompareTo(null) > 0);
        Assert.True(GameVersion.Compare(null, v) < 0);
        Assert.True(GameVersion.Compare(v, null) > 0);
        Assert.Equal(0, GameVersion.Compare(null, null));
        Assert.True(null < v);
        Assert.True(v > null);
        Assert.False(v == null);
        Assert.True(v != null);
        Assert.False(v.Equals(null));
    }

    [Fact]
    public void Equality_DifferentSourcesSameVersion()
    {
        var fromAsset = V("2D_0.0.2_Protype2.zip");
        var fromTag = V("0.0.2-Prototype-2");

        Assert.Equal(fromAsset, fromTag);
        Assert.True(fromAsset == fromTag);
        Assert.True(fromAsset.Equals((object)fromTag));
        Assert.Equal(fromAsset.GetHashCode(), fromTag.GetHashCode());
        Assert.Equal(0, fromAsset.CompareTo(fromTag));
        Assert.True(fromAsset <= fromTag && fromAsset >= fromTag);

        var set = new HashSet<GameVersion> { fromAsset, fromTag };
        Assert.Single(set);
    }

    [Fact]
    public void Equality_StableVsPrereleaseAndNumberMatter()
    {
        Assert.NotEqual(V("0.0.2"), V("0.0.2-prototype.2"));
        Assert.NotEqual(V("0.0.2-prototype"), V("0.0.2-prototype.1"));
        Assert.NotEqual(V("1.2.3.4"), V("1.2.3"));
    }

    [Theory]
    [InlineData("2D_0.0.2_Protype3.zip", "0.0.2 · Prototipo 3")]
    [InlineData("2D_0.0.2_Protype2.zip", "0.0.2 · Prototipo 2")]
    [InlineData("2D_0.0.2_proto.zip", "0.0.2 · Prototipo")]
    [InlineData("2D_0.1.0.zip", "0.1.0")]
    [InlineData("1.0.0-beta.2", "1.0.0 · Beta 2")]
    [InlineData("1.0.0 RC1", "1.0.0 · RC 1")]
    [InlineData("1.0.0-alpha", "1.0.0 · Alpha")]
    [InlineData("1.0.0-dev.1", "1.0.0 · Dev 1")]
    [InlineData("1.0.0-preview.2", "1.0.0 · Preview 2")]
    [InlineData("1.0.0-test.2", "1.0.0 · test 2")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("2D_1.0.zip", "1.0.0")]
    public void ToDisplayString_IsFriendly(string text, string expected)
    {
        Assert.Equal(expected, V(text).ToDisplayString());
    }

    [Theory]
    [InlineData("2D_0.1.0.zip", "0.0.1-Protype1", "Prototype 1", "0.1.0")]
    [InlineData("Coop2D.zip", "0.0.2-Prototype-2", "Prototype 2 Test", "0.0.2-prototype.2")]
    [InlineData("Coop2D.zip", "latest", "Build 1.5", "1.5.0")]
    [InlineData(null, null, "v2.0.0", "2.0.0")]
    public void FromRelease_UsesAssetThenTagThenName(string? asset, string? tag, string? name, string expected)
    {
        var v = GameVersion.FromRelease(asset, tag, name);
        Assert.NotNull(v);
        Assert.Equal(expected, v.ToString());
    }

    [Fact]
    public void FromRelease_NoVersionAnywhere_ReturnsNull()
    {
        Assert.Null(GameVersion.FromRelease("Coop2D.zip", "latest", "Prototype 2 Test"));
        Assert.Null(GameVersion.FromRelease(null, null, null));
    }

    [Fact]
    public void Constructor_NormalizesLabel()
    {
        var v = new GameVersion(1, 0, 0, 0, "Beta", 2);
        Assert.Equal("beta", v.PreLabel);
        Assert.Equal("1.0.0-beta.2", v.ToString());

        Assert.Equal("prototype", new GameVersion(0, 0, 2, preLabel: "Protype", preNumber: 3).PreLabel);
        Assert.Equal("alpha", new GameVersion(1, 0, 0, preLabel: "alfa").PreLabel);
        Assert.Null(new GameVersion(1, 0, 0, preLabel: "win").PreLabel);
        Assert.Null(new GameVersion(1, 0, 0, preLabel: "  ").PreLabel);
    }

    [Fact]
    public void Constructor_NumberWithoutLabel_IsDropped()
    {
        var v = new GameVersion(1, 0, 0, preNumber: 5);
        Assert.Null(v.PreNumber);
        Assert.Equal(V("1.0.0"), v);
    }

    [Fact]
    public void Constructor_RejectsInvalidValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameVersion(-1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameVersion(0, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameVersion(0, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameVersion(0, 0, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameVersion(0, 0, 0, 0, "beta", -1));
        Assert.Throws<ArgumentException>(() => new GameVersion(0, 0, 0, 0, "beta-1"));
    }

    [Fact]
    public void Constructor_RoundTripsThroughToString()
    {
        var versions = new[]
        {
            new GameVersion(0, 0, 2, 0, "prototype", 3),
            new GameVersion(1, 2, 3, 4, "beta"),
            new GameVersion(1, 2, 3, 4),
            new GameVersion(10, 20, 30, 0, "rc", 12),
            new GameVersion(0, 3, 0, 0, "test", 2),
        };
        foreach (var v in versions)
        {
            Assert.True(GameVersion.TryParse(v.ToString(), out var again));
            Assert.Equal(v, again);
        }
    }

    /// <summary>Atajo de test: parsea o falla.</summary>
    private static GameVersion V(string text)
        => GameVersion.TryParse(text, out var v) ? v : throw new FormatException($"Sin versión: {text}");

    private static void AssertStrictlyAscending(IReadOnlyList<string> texts)
    {
        var versions = texts.Select(V).ToList();
        for (var i = 0; i < versions.Count - 1; i++)
        {
            Assert.True(versions[i] < versions[i + 1], $"{versions[i]} debería ser < {versions[i + 1]}");
            Assert.True(versions[i + 1] > versions[i]);
            Assert.True(versions[i].CompareTo(versions[i + 1]) < 0);
            Assert.True(versions[i + 1].CompareTo(versions[i]) > 0);
        }
    }
}
