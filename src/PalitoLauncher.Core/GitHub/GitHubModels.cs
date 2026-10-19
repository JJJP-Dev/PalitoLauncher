using PalitoLauncher.Core.Versioning;

namespace PalitoLauncher.Core.GitHub;

/// <summary>Archivo adjunto a una release de GitHub.</summary>
public sealed class ReleaseAsset
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public long Size { get; init; }

    /// <summary>browser_download_url (redirige a la CDN de GitHub; admite Range).</summary>
    public string DownloadUrl { get; init; } = "";

    public string? ContentType { get; init; }

    /// <summary>SHA-256 en hex minúsculas, obtenido del campo "digest" ("sha256:...") de la API. Null si GitHub no lo da.</summary>
    public string? Sha256 { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Release de GitHub (subset de campos que usa el launcher).</summary>
public sealed class ReleaseInfo
{
    public long Id { get; init; }
    public string TagName { get; init; } = "";
    public string? Name { get; init; }

    /// <summary>Notas de la release en Markdown.</summary>
    public string? Body { get; init; }

    public bool IsDraft { get; init; }
    public bool IsPrerelease { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string HtmlUrl { get; init; } = "";
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = Array.Empty<ReleaseAsset>();

    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name! : TagName;
}

/// <summary>Release que contiene una build del juego utilizable.</summary>
public sealed class GameRelease
{
    public GameRelease(ReleaseInfo release, ReleaseAsset asset, GameVersion? version)
    {
        Release = release;
        Asset = asset;
        Version = version;
    }

    public ReleaseInfo Release { get; }
    public ReleaseAsset Asset { get; }

    /// <summary>Versión parseada (asset → tag → título). Null si no se pudo deducir.</summary>
    public GameVersion? Version { get; }

    /// <summary>Texto para la UI ("0.0.2 · Prototipo 2") o el tag si no hay versión parseable.</summary>
    public string VersionDisplay => Version?.ToDisplayString() ?? Release.TagName;
}

public enum ReleaseFetchSource
{
    /// <summary>Datos recién descargados de GitHub.</summary>
    Network,

    /// <summary>GitHub respondió 304 (sin cambios); se usó la caché. Equivale a datos al día.</summary>
    NotModifiedCache,

    /// <summary>No se pudo contactar/consultar GitHub; se usaron datos guardados (posiblemente desactualizados).</summary>
    OfflineCache,
}

public sealed class ReleaseFetchResult
{
    public IReadOnlyList<ReleaseInfo> Releases { get; init; } = Array.Empty<ReleaseInfo>();
    public ReleaseFetchSource Source { get; init; }

    /// <summary>Momento en que se obtuvieron los datos de GitHub por última vez (de red o de caché).</summary>
    public DateTimeOffset? FetchedAt { get; init; }

    /// <summary>Aviso para la UI cuando Source == OfflineCache (p.ej. "Sin conexión con GitHub, mostrando datos guardados").</summary>
    public string? Warning { get; init; }

    public bool IsStale => Source == ReleaseFetchSource.OfflineCache;
}
