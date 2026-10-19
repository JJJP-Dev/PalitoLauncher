using System.Runtime.CompilerServices;
using PalitoLauncher.Core.GitHub;
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
