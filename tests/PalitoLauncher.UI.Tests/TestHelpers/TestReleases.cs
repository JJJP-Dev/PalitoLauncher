using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Versioning;

namespace PalitoLauncher.UI.Tests.TestHelpers;

/// <summary>Builds de prueba (releases de GitHub) sin red.</summary>
internal static class TestReleases
{
    public static readonly DateTimeOffset BaseDate = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Release jugable. La versión se deduce del tag como hace ReleaseSelector (FromRelease); <paramref name="tag"/> sin
    /// números → sin versión.
    /// </summary>
    public static GameRelease Make(string tag, long releaseId, long assetId, string? sha256 = null, long size = 1_000_000,
        DateTimeOffset? published = null, DateTimeOffset? assetUpdated = null, bool prerelease = false, string? body = null,
        string? assetName = null)
    {
        var asset = new ReleaseAsset
        {
            Id = assetId,
            Name = assetName ?? $"2D_{tag.TrimStart('v')}.zip",
            Size = size,
            DownloadUrl = $"https://example.invalid/{tag}/{assetId}.zip",
            Sha256 = sha256,
            UpdatedAt = assetUpdated,
        };
        var info = new ReleaseInfo
        {
            Id = releaseId,
            TagName = tag,
            Name = tag,
            Body = body,
            IsPrerelease = prerelease,
            PublishedAt = published,
            HtmlUrl = $"https://github.com/JJJP-Dev/Coop2D/releases/tag/{tag}",
            Assets = new[] { asset },
        };
        return new GameRelease(info, asset, GameVersion.FromRelease(asset.Name, tag, info.Name));
    }

    /// <summary>Marcador de una instalación de <paramref name="release"/> (o con otros sha/asset si se indican).</summary>
    public static InstalledGameInfo InstalledFrom(GameRelease release, string? sha256 = null, long? assetId = null) => new()
    {
        Tag = release.Release.TagName,
        ReleaseId = release.Release.Id,
        Version = release.Version?.ToString(),
        VersionDisplay = release.VersionDisplay,
        AssetName = release.Asset.Name,
        AssetId = assetId ?? release.Asset.Id,
        AssetSize = release.Asset.Size,
        Sha256 = sha256 ?? release.Asset.Sha256,
        InstalledAtUtc = BaseDate,
    };
}
