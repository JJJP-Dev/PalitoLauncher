using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Versioning;

namespace PalitoLauncher.Core.State;

/// <summary>Estado principal de la pantalla (decide el botón grande).</summary>
public enum LauncherState
{
    /// <summary>Arrancando y aún sin saber nada del disco.</summary>
    Initializing,

    /// <summary>Hay una operación en curso (instalar, actualizar, mover, desinstalar, auto-actualizar el launcher).</summary>
    Busy,

    /// <summary>El juego está abierto.</summary>
    GameRunning,

    /// <summary>La unidad de la carpeta de instalación no está disponible.</summary>
    DriveMissing,

    /// <summary>Instalación dañada → REPARAR.</summary>
    Broken,

    /// <summary>Instalado y hay versión/revisión nueva → ACTUALIZAR.</summary>
    UpdateAvailable,

    /// <summary>Instalado y al día (o sin datos de GitHub) → JUGAR.</summary>
    Ready,

    /// <summary>No instalado y hay releases → INSTALAR.</summary>
    NotInstalled,

    /// <summary>No instalado, GitHub respondió bien pero no hay builds publicadas.</summary>
    NoReleases,

    /// <summary>No instalado y no se pudo obtener la lista (ni de caché) → REINTENTAR.</summary>
    LoadFailed,
}

public enum UpdateKind
{
    None,

    /// <summary>La última release tiene una versión mayor.</summary>
    NewVersion,

    /// <summary>Misma versión pero contenido distinto (el dev re-subió el zip con un arreglo).</summary>
    Revision,
}

/// <summary>Entradas para calcular el estado. Todas opcionales para poder llamarlo en cualquier momento.</summary>
public sealed record LauncherStateInput
{
    /// <summary>Resultado de GameInstaller.GetStatus (null = aún no se ha mirado el disco).</summary>
    public InstallStatus? Install { get; init; }

    /// <summary>Releases jugables ordenadas (null = aún no se consultó o falló sin caché).</summary>
    public IReadOnlyList<GameRelease>? Releases { get; init; }

    /// <summary>True si la última consulta falló y no había caché.</summary>
    public bool FetchFailed { get; init; }

    public bool IsBusy { get; init; }
    public bool IsGameRunning { get; init; }
}

/// <summary>Lógica pura de decisión (sin E/S), testeable.</summary>
public static class LauncherStateLogic
{
    /// <summary>
    /// Precedencia: Busy &gt; GameRunning &gt; (Install == null → Initializing) &gt; DriveMissing &gt; Broken &gt;
    /// Installed (UpdateAvailable si GetUpdateKind != None con Releases[0], si no Ready — aunque no haya releases o la
    /// consulta haya fallado: los problemas de red van al aviso secundario, nunca impiden jugar) &gt;
    /// NotInstalled (Releases con elementos) &gt; NoReleases (Releases vacía y !FetchFailed) &gt; LoadFailed.
    /// </summary>
    public static LauncherState ComputeState(LauncherStateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.IsBusy) return LauncherState.Busy;
        if (input.IsGameRunning) return LauncherState.GameRunning;

        var install = input.Install;
        if (install is null) return LauncherState.Initializing;

        switch (install.State)
        {
            case InstallState.DriveMissing:
                return LauncherState.DriveMissing;
            case InstallState.Broken:
                return LauncherState.Broken;
            case InstallState.Installed:
                // Sin datos de GitHub (o con error) se puede jugar igual: el problema va al aviso secundario.
                var latest = input.Releases is { Count: > 0 } list ? list[0] : null;
                return GetUpdateKind(install.Info, latest) != UpdateKind.None
                    ? LauncherState.UpdateAvailable
                    : LauncherState.Ready;
        }

        // No instalado.
        if (input.Releases is { Count: > 0 }) return LauncherState.NotInstalled;
        if (input.Releases is { Count: 0 } && !input.FetchFailed) return LauncherState.NoReleases;
        return LauncherState.LoadFailed;
    }

    /// <summary>
    /// ¿Hay actualización de <paramref name="installed"/> a <paramref name="latest"/>?
    /// NewVersion si GameVersion.Compare(latest.Version, installed.ParsedVersion) &gt; 0 (con ambas versiones no nulas;
    /// si alguna es null se compara por Asset.Id/fecha: distinto asset y release más nueva que InstalledAtUtc → NewVersion).
    /// Si las versiones son iguales: Revision si el contenido difiere — si latest.Asset.Sha256 e installed.Sha256 existen,
    /// se comparan los hashes (sin distinguir mayúsculas); si no, latest.Asset.Id != installed.AssetId. El tag NO influye.
    /// installed más nuevo que latest → None (nunca se ofrece bajar de versión automáticamente). Cualquier null → None.
    /// </summary>
    public static UpdateKind GetUpdateKind(InstalledGameInfo? installed, GameRelease? latest)
    {
        if (installed is null || latest?.Asset is null || latest.Release is null) return UpdateKind.None;

        var latestVersion = latest.Version;
        var installedVersion = installed.ParsedVersion;

        if (latestVersion is not null && installedVersion is not null)
        {
            var c = GameVersion.Compare(latestVersion, installedVersion);
            if (c > 0) return UpdateKind.NewVersion;
            if (c < 0) return UpdateKind.None; // nunca se ofrece bajar de versión
            return ContentDiffers(installed, latest) ? UpdateKind.Revision : UpdateKind.None;
        }

        // Sin versión comparable: distinto asset y publicado después de instalar → versión nueva.
        // Si el contenido es idéntico (mismo SHA-256) no es una actualización (ver IsSameContentDifferentIdentity).
        if (HasSameHash(installed, latest)) return UpdateKind.None;
        if (latest.Asset.Id == installed.AssetId) return UpdateKind.None;

        var latestDate = MaxDate(latest.Asset.UpdatedAt, latest.Release.PublishedAt);
        return latestDate is { } d && d > installed.InstalledAtUtc ? UpdateKind.NewVersion : UpdateKind.None;
    }

    /// <summary>
    /// True si la instalada tiene el mismo SHA-256 que <paramref name="latest"/> pero distinto AssetId (release recreada):
    /// basta con GameInstaller.UpdateMarkerIdentity, sin descargar.
    /// </summary>
    public static bool IsSameContentDifferentIdentity(InstalledGameInfo? installed, GameRelease? latest)
    {
        if (installed is null || latest?.Asset is null) return false;
        return HasSameHash(installed, latest) && latest.Asset.Id != installed.AssetId;
    }

    /// <summary>
    /// Qué reinstalar al "Reparar": la release con el mismo AssetId que la instalada si sigue publicada; si no, la de mismo
    /// Tag; si no, la primera (la última). Null si la lista está vacía.
    /// </summary>
    public static GameRelease? SelectRepairTarget(InstalledGameInfo? installed, IReadOnlyList<GameRelease> releases)
    {
        if (releases is null || releases.Count == 0) return null;
        if (installed is null) return releases[0];

        if (installed.AssetId != 0)
        {
            foreach (var r in releases)
            {
                if (r?.Asset is not null && r.Asset.Id == installed.AssetId) return r;
            }
        }

        if (!string.IsNullOrEmpty(installed.Tag))
        {
            foreach (var r in releases)
            {
                if (r?.Release is not null && string.Equals(r.Release.TagName, installed.Tag, StringComparison.Ordinal)) return r;
            }
        }

        return releases[0];
    }

    /// <summary>
    /// ¿Debe arrancar sola la actualización? Solo si: autoDownloadEnabled, state == UpdateAvailable, !isGameRunning y
    /// latest.Release.TagName != autoUpdatePausedForTag.
    /// </summary>
    public static bool ShouldAutoUpdate(LauncherState state, bool autoDownloadEnabled, bool isGameRunning,
        GameRelease? latest, string? autoUpdatePausedForTag)
    {
        if (!autoDownloadEnabled || state != LauncherState.UpdateAvailable || isGameRunning || latest?.Release is null)
            return false;
        return !string.Equals(latest.Release.TagName, autoUpdatePausedForTag, StringComparison.Ordinal);
    }

    // Revisión = mismo número de versión pero distinto contenido: hash si ambos lo tienen, si no el id del asset.
    private static bool ContentDiffers(InstalledGameInfo installed, GameRelease latest)
    {
        if (!string.IsNullOrWhiteSpace(latest.Asset.Sha256) && !string.IsNullOrWhiteSpace(installed.Sha256))
            return !string.Equals(latest.Asset.Sha256.Trim(), installed.Sha256.Trim(), StringComparison.OrdinalIgnoreCase);
        return latest.Asset.Id != installed.AssetId;
    }

    private static bool HasSameHash(InstalledGameInfo installed, GameRelease latest)
        => !string.IsNullOrWhiteSpace(latest.Asset.Sha256) && !string.IsNullOrWhiteSpace(installed.Sha256)
           && string.Equals(latest.Asset.Sha256.Trim(), installed.Sha256.Trim(), StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? MaxDate(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a.Value >= b.Value ? a : b;
    }
}
