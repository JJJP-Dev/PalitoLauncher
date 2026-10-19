using System.Runtime.CompilerServices;
using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.UI.Tests.TestHelpers;

/// <summary>
/// Acceso a miembros privados del MainViewModel para probar su lógica sin red ni ventana. Con UnsafeAccessor: si se
/// renombra uno de estos miembros, el test falla con MissingFieldException/MissingMethodException (nunca pasa en falso).
/// </summary>
internal static class MainViewModelAccess
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_gameReleases")]
    public static extern ref IReadOnlyList<GameRelease>? GameReleases(MainViewModel vm);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_lastFetchResult")]
    public static extern ref ReleaseFetchResult? LastFetchResult(MainViewModel vm);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "BuildPausedToast")]
    public static extern string BuildPausedToast(MainViewModel vm, OperationKind kind, long? targetAssetId);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "FindReplacement")]
    public static extern GameRelease? FindReplacement(MainViewModel vm, GameRelease old);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "AfterReleasesChangedAsync")]
    public static extern Task AfterReleasesChangedAsync(MainViewModel vm);

    /// <summary>Encuesta ya cargada (así los tests no esperan a la remota).</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_survey")]
    public static extern ref SurveyDefinition? Survey(MainViewModel vm);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_crashNotice")]
    public static extern ref string? CrashNotice(MainViewModel vm);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "UpdateState")]
    public static extern void UpdateState(MainViewModel vm);

    // ───────────── Partida (sin juego real) ─────────────

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_installStatus")]
    public static extern ref InstallStatus? InstallStatus(MainViewModel vm);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_gameSessionStartUtc")]
    public static extern ref DateTimeOffset? GameSessionStartUtc(MainViewModel vm);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_suppressNextCrashNotice")]
    public static extern ref bool SuppressNextCrashNotice(MainViewModel vm);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_operationKind")]
    public static extern ref OperationKind OperationKind(MainViewModel vm);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "MarkGameSessionStarted")]
    public static extern void MarkGameSessionStarted(MainViewModel vm, bool restart);

    /// <summary>Lo que hace el launcher cuando el proceso del juego termina (sin carpeta de instalación no toca el disco).</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "HandleGameExitedAsync")]
    public static extern Task HandleGameExitedAsync(MainViewModel vm, int exitCode);

    /// <summary>Simula una partida de <paramref name="minutes"/> minutos con <paramref name="version"/> instalada.</summary>
    public static void StartSession(MainViewModel vm, string version, string display, int minutes)
    {
        InstallStatus(vm) = Installed(version, display);
        MarkGameSessionStarted(vm, restart: true);
        GameSessionStartUtc(vm) = DateTimeOffset.UtcNow.AddMinutes(-minutes);
    }

    public static InstallStatus Installed(string version, string display) => new()
    {
        State = InstallState.Installed,
        InstallRoot = @"C:\Juegos\Coop2D",
        GameDir = @"C:\Juegos\Coop2D\game",
        Info = new InstalledGameInfo { Tag = "v" + version, Version = version, VersionDisplay = display, AssetName = "Coop2D.zip" },
    };

    /// <summary>
    /// Simula una consulta a GitHub ya aplicada: <paramref name="all"/> es lo descargado y <paramref name="visible"/> la
    /// lista jugable (por defecto todas). Recalcula la lista de Versiones y el estado como tras una consulta real.
    /// </summary>
    public static Task ApplyReleasesAsync(MainViewModel vm, IReadOnlyList<GameRelease> visible, IReadOnlyList<GameRelease>? all = null)
    {
        GameReleases(vm) = visible;
        LastFetchResult(vm) = new ReleaseFetchResult
        {
            Releases = (all ?? visible).Select(r => r.Release).ToList(),
            Source = ReleaseFetchSource.Network,
            FetchedAt = TestReleases.BaseDate,
        };
        return AfterReleasesChangedAsync(vm);
    }
}
