using System.Text.RegularExpressions;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Versioning;

namespace PalitoLauncher.Core.GitHub;

/// <summary>Lógica pura (sin red) para elegir builds del juego entre las releases.</summary>
public static class ReleaseSelector
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Elige el asset con la build del juego:
    /// 1) assets cuyo nombre coincide con <paramref name="assetPattern"/> (regex, IgnoreCase);
    /// 2) si ninguno, cualquier asset .zip/.7z/.rar.
    /// Entre varios candidatos prefiere .zip &gt; .7z &gt; .rar y luego el de mayor tamaño. Null si no hay ninguno.
    /// </summary>
    public static ReleaseAsset? SelectGameAsset(ReleaseInfo release, string assetPattern)
    {
        ArgumentNullException.ThrowIfNull(release);

        var assets = (release.Assets ?? Array.Empty<ReleaseAsset>())
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Name))
            .ToList();
        if (assets.Count == 0) return null;

        var candidates = string.IsNullOrWhiteSpace(assetPattern)
            ? new List<ReleaseAsset>()
            : assets.Where(a => MatchesPattern(a.Name, assetPattern)).ToList();
        if (candidates.Count == 0)
            candidates = assets.Where(a => GetExtensionRank(a.Name) < OtherExtensionRank).ToList();
        if (candidates.Count == 0) return null;

        // Desempates finales (nombre, id) solo para que el resultado sea determinista.
        return candidates
            .OrderBy(a => GetExtensionRank(a.Name))
            .ThenByDescending(a => a.Size)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Id)
            .First();
    }

    /// <summary>
    /// Releases jugables ordenadas de más nueva a más vieja:
    /// excluye drafts; excluye prereleases de GitHub si <paramref name="includePrereleases"/> es false;
    /// excluye releases sin asset de juego. Versión = GameVersion.FromRelease(asset.Name, tag, name).
    /// Orden: versión descendente (las que no tienen versión van al final), desempate por PublishedAt descendente.
    /// </summary>
    public static IReadOnlyList<GameRelease> GetGameReleases(IEnumerable<ReleaseInfo> releases, string assetPattern, bool includePrereleases)
    {
        ArgumentNullException.ThrowIfNull(releases);

        var result = new List<GameRelease>();
        foreach (var release in releases)
        {
            if (release is null || release.IsDraft) continue;
            if (release.IsPrerelease && !includePrereleases) continue;

            var asset = SelectGameAsset(release, assetPattern);
            if (asset is null) continue;

            result.Add(new GameRelease(release, asset, GameVersion.FromRelease(asset.Name, release.TagName, release.Name)));
        }

        result.Sort(CompareNewestFirst);
        return result.AsReadOnly();
    }

    // Más nueva primero: versión (sin versión al final), luego fecha de publicación (sin fecha al final), luego id.
    private static int CompareNewestFirst(GameRelease a, GameRelease b)
    {
        if ((a.Version is null) != (b.Version is null)) return a.Version is null ? 1 : -1;
        var c = GameVersion.Compare(b.Version, a.Version);
        if (c != 0) return c;

        var pa = a.Release.PublishedAt;
        var pb = b.Release.PublishedAt;
        if (pa.HasValue != pb.HasValue) return pa.HasValue ? -1 : 1;
        if (pa.HasValue && pb.HasValue)
        {
            c = pb.Value.CompareTo(pa.Value);
            if (c != 0) return c;
        }

        c = b.Release.Id.CompareTo(a.Release.Id);
        return c != 0 ? c : b.Asset.Id.CompareTo(a.Asset.Id);
    }

    private const int OtherExtensionRank = 3;

    private static int GetExtensionRank(string name)
    {
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)) return 2;
        return OtherExtensionRank;
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        try
        {
            return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            // Patrón mal escrito en la configuración: se registra y se usa el criterio por extensión.
            Log.Error($"Patrón de asset inválido: \"{pattern}\".", ex);
            return false;
        }
        catch (RegexMatchTimeoutException ex)
        {
            Log.Warn($"Tiempo agotado comparando el asset \"{name}\" con el patrón.", ex);
            return false;
        }
    }
}
