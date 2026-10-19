using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using PalitoLauncher.Core;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.UI.Tests.TestHelpers;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.UI.Tests;

/// <summary>
/// Diálogo "Instalar Coop2D": validación con retardo y contador de versión, carrera al confirmar mientras cambia la ruta y
/// adopción de una instalación existente solo si es la misma build. Carpetas reales en %TEMP%; el estado de instalación
/// se inyecta (getStatus).
/// </summary>
public sealed class InstallDialogViewModelTests : IDisposable
{
    private static readonly GameRelease Release = TestReleases.Make("v0.0.3", releaseId: 3, assetId: 30, sha256: "abc123");

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Resolved(string picked) => Path.Combine(picked, LauncherConfig.InstallFolderName);

    private static InstallStatus NotInstalled(string root)
        => new() { State = InstallState.NotInstalled, InstallRoot = root, GameDir = GameInstaller.GetGameDir(root) };

    private static InstallStatus Installed(string root, InstalledGameInfo info)
        => new() { State = InstallState.Installed, Info = info, InstallRoot = root, GameDir = GameInstaller.GetGameDir(root) };

    private static async Task<InstallDialogViewModel> OpenValidatedAsync(string folder, Func<string, InstallStatus> getStatus,
        bool showShortcutOption = true)
    {
        var dialog = new InstallDialogViewModel(Release, folder, getStatus, showShortcutOption);
        dialog.StartValidation();
        await WpfHost.WaitUntilAsync(() => !dialog.IsValidating, "primera validación");
        return dialog;
    }

    // ───────────── Validación: retardo y contador de versión ─────────────

    [Fact]
    public Task EditingThePath_DisablesInstallAtOnce_AndValidatesOnlyTheLastTextAfterTheDebounce() => WpfHost.RunAsync(async () =>
    {
        var (a, b, c) = (_temp.Sub("A"), _temp.Sub("B"), _temp.Sub("C"));
        var calls = new ConcurrentQueue<string>();
        var dialog = new InstallDialogViewModel(Release, a, root => { calls.Enqueue(root); return NotInstalled(root); }, true);

        dialog.StartValidation();
        Assert.True(dialog.IsValidating);
        Assert.True(dialog.IsChecking);
        Assert.False(dialog.InstallCommand.CanExecute(null));
        await WpfHost.WaitUntilAsync(() => !dialog.IsValidating, "primera validación");
        Assert.True(dialog.CanInstall);
        Assert.Equal($"Se instalará en: {Resolved(a)}", dialog.ResolvedPathText);
        Assert.StartsWith("Espacio necesario: ~", dialog.SpaceText);
        calls.Clear();

        // Cada pulsación invalida al momento lo validado (Instalar se deshabilita) y reinicia el retardo.
        dialog.PickedFolder = b;
        Assert.True(dialog.IsValidating);
        Assert.False(dialog.CanInstall);
        Assert.False(dialog.InstallCommand.CanExecute(null));
        dialog.PickedFolder = c;
        var sw = Stopwatch.StartNew();

        await Task.Delay(150);
        Assert.Empty(calls); // aún dentro del retardo de 350 ms: no se ha tocado el disco
        await WpfHost.WaitUntilAsync(() => !dialog.IsValidating, "validación tras el retardo");

        Assert.True(sw.ElapsedMilliseconds >= 300, $"Validó a los {sw.ElapsedMilliseconds} ms (retardo de 350 ms).");
        Assert.Equal(new[] { Resolved(c) }, calls); // B nunca se validó
        Assert.Equal($"Se instalará en: {Resolved(c)}", dialog.ResolvedPathText);
        Assert.True(dialog.CanInstall);
    });

    [Fact]
    public Task SlowValidationOfAnOlderPath_NeverOverwritesTheNewerResult() => WpfHost.RunAsync(async () =>
    {
        var (a, b) = (_temp.Sub("A"), _temp.Sub("B"));
        using var release = new ManualResetEventSlim(false);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var dialog = new InstallDialogViewModel(Release, a, root =>
        {
            if (root != Resolved(a)) return NotInstalled(root);
            // La carpeta A tarda (disco lento) y, si se aplicara, diría "ya está instalado ahí".
            slowStarted.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(15));
            slowFinished.TrySetResult();
            return Installed(root, TestReleases.InstalledFrom(Release));
        }, showShortcutOption: true);

        dialog.StartValidation();
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        dialog.PickedFolder = b;
        await WpfHost.WaitUntilAsync(() => !dialog.IsValidating, "validación de B mientras A sigue atascada");
        Assert.Equal($"Se instalará en: {Resolved(b)}", dialog.ResolvedPathText);

        release.Set();
        await slowFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200); // lo que queda de Validate(A) y su continuación en el hilo de UI
        await WpfHost.FlushAsync();

        Assert.Equal($"Se instalará en: {Resolved(b)}", dialog.ResolvedPathText);
        Assert.StartsWith("Espacio necesario: ~", dialog.SpaceText);
        Assert.Null(dialog.LocationMessage);
        Assert.False(dialog.IsValidating);
        Assert.True(dialog.CanInstall);

        dialog.Accept();
        var result = await dialog.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Resolved(b), result!.InstallRoot);
        Assert.False(result.AdoptExisting);
    });

    [Theory]
    [InlineData("", "Elige una carpeta para instalar el juego.")]
    [InlineData("   ", "Elige una carpeta para instalar el juego.")]
    [InlineData("Juegos\\Coop2D", "Escribe una ruta completa, por ejemplo D:\\Juegos.")]
    public Task InvalidPath_IsBlockedWithoutTouchingTheDisk(string folder, string message) => WpfHost.RunAsync(async () =>
    {
        var calls = 0;
        var dialog = await OpenValidatedAsync(folder, root => { Interlocked.Increment(ref calls); return NotInstalled(root); });

        Assert.True(dialog.IsLocationBlocked);
        Assert.Equal(message, dialog.LocationMessage);
        Assert.Equal("", dialog.ResolvedPathText);
        Assert.False(dialog.CanInstall);
        Assert.False(dialog.InstallCommand.CanExecute(null));
        Assert.Equal(0, calls);

        dialog.Accept(); // no hace nada
        await WpfHost.FlushAsync();
        Assert.False(dialog.Completion.IsCompleted);
    });

    // ───────────── Confirmar ─────────────

    [Fact]
    public Task PathEditedWhileConfirming_DoesNotCompleteWithTheOldFolder() => WpfHost.RunAsync(async () =>
    {
        var (a, b) = (_temp.Sub("A"), _temp.Sub("B"));
        var dialog = await OpenValidatedAsync(a, NotInstalled);
        Assert.True(dialog.CanInstall);

        // Instalar: entra ya en la prueba de escritura (en segundo plano) y bloquea la ruta…
        dialog.Accept();
        Assert.True(dialog.IsConfirming);
        Assert.True(dialog.IsChecking);
        Assert.False(dialog.ChangeFolderCommand.CanExecute(null));
        Assert.False(dialog.CanInstall);

        // …pero si aun así la ruta cambia antes de que termine, no se confirma con la carpeta vieja.
        dialog.PickedFolder = b;
        await WpfHost.WaitUntilAsync(() => !dialog.IsConfirming, "fin de la prueba de escritura");
        await WpfHost.FlushAsync();
        Assert.False(dialog.Completion.IsCompleted, "Se confirmó con la carpeta anterior a la edición.");

        // Se valida la nueva y el usuario vuelve a pulsar Instalar.
        await WpfHost.WaitUntilAsync(() => !dialog.IsValidating, "validación de la ruta nueva");
        Assert.Equal($"Se instalará en: {Resolved(b)}", dialog.ResolvedPathText);
        Assert.True(dialog.CanInstall);
        dialog.Accept();

        var result = await dialog.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(result);
        Assert.Equal(Resolved(b), result.InstallRoot);
        Assert.True(result.CreateDesktopShortcut);
        Assert.False(result.AdoptExisting);
    });

    [Fact]
    public Task Confirm_ReturnsTheResolvedFolder_AndHonoursTheShortcutOption() => WpfHost.RunAsync(async () =>
    {
        var a = _temp.Sub("A");
        var dialog = await OpenValidatedAsync(a, NotInstalled, showShortcutOption: false);
        Assert.True(dialog.CreateDesktopShortcut); // casilla marcada por defecto, pero oculta

        dialog.Accept();
        var result = await dialog.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new InstallDialogResult(Resolved(a), CreateDesktopShortcut: false, AdoptExisting: false), result);
        Assert.False(dialog.IsConfirming);
    });

    [Fact]
    public Task Cancel_CompletesWithNull() => WpfHost.RunAsync(async () =>
    {
        var dialog = await OpenValidatedAsync(_temp.Sub("A"), NotInstalled);
        dialog.Cancel();
        Assert.Null(await dialog.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
    });

    // ───────────── Instalación existente: adoptar solo la misma build ─────────────

    public static TheoryData<string, InstalledGameInfo> SameBuilds => new()
    {
        { "mismo SHA-256", TestReleases.InstalledFrom(Release) },
        { "mismo SHA-256 (mayúsculas y espacios)", TestReleases.InstalledFrom(Release, sha256: "  ABC123 ") },
        { "mismo SHA-256 aunque el asset se re-subió con otro id", TestReleases.InstalledFrom(Release, assetId: 999) },
        { "marcador sin SHA-256 pero mismo asset", NoSha(TestReleases.InstalledFrom(Release)) },
    };

    [Theory]
    [MemberData(nameof(SameBuilds))]
    public Task ExistingInstallOfTheSameBuild_IsAdoptedWithoutDownloading(string why, InstalledGameInfo installed)
        => WpfHost.RunAsync(async () =>
        {
            var a = _temp.Sub("A");
            var dialog = await OpenValidatedAsync(a, root => Installed(root, installed));

            Assert.Equal("Ya está instalado ahí: no hace falta descargar nada.", dialog.SpaceText);
            Assert.Contains("se usará sin volver a descargar", dialog.LocationMessage);
            Assert.False(dialog.IsLocationBlocked);
            Assert.True(dialog.CanInstall, why);

            dialog.Accept();
            var result = await dialog.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result!.AdoptExisting, why);
            Assert.Equal(Resolved(a), result.InstallRoot);
        });

    public static TheoryData<string, InstalledGameInfo, string> OtherBuilds => new()
    {
        {
            "misma versión, otro SHA-256 (build re-subida)",
            TestReleases.InstalledFrom(Release, sha256: "fff999"),
            "En esta carpeta hay otra build de 0.0.3: se reemplazará por la elegida."
        },
        {
            "mismo id de asset pero otro SHA-256 (manda el SHA)",
            TestReleases.InstalledFrom(Release, sha256: "fff999", assetId: Release.Asset.Id),
            "En esta carpeta hay otra build de 0.0.3"
        },
        {
            "otra versión",
            TestReleases.InstalledFrom(TestReleases.Make("v0.0.2", releaseId: 2, assetId: 20, sha256: "old")),
            "En esta carpeta ya está instalada la versión 0.0.2: se reemplazará por 0.0.3."
        },
        {
            "marcador sin SHA-256 y otro asset",
            NoSha(TestReleases.InstalledFrom(Release, assetId: 31)),
            "En esta carpeta hay otra build de 0.0.3"
        },
    };

    [Theory]
    [MemberData(nameof(OtherBuilds))]
    public Task ExistingInstallOfAnotherBuild_IsReplacedNotAdopted(string why, InstalledGameInfo installed, string expectedNote)
        => WpfHost.RunAsync(async () =>
        {
            var a = _temp.Sub("A");
            var dialog = await OpenValidatedAsync(a, root => Installed(root, installed));

            Assert.StartsWith("Espacio necesario: ~", dialog.SpaceText);
            Assert.Contains(expectedNote, dialog.LocationMessage);
            Assert.Contains("Tus partidas guardadas se conservan.", dialog.LocationMessage);
            Assert.True(dialog.CanInstall, why);

            dialog.Accept();
            var result = await dialog.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(result!.AdoptExisting, why);
        });

    // ───────────── IsSameBuild ─────────────

    [Theory]
    [InlineData("abc", 1, "abc", 1, true)]
    [InlineData(" ABC ", 1, "abc", 2, true)]   // el SHA manda (aunque el asset sea otro)
    [InlineData("abc", 1, "def", 1, false)]    // mismo asset, distinto contenido
    [InlineData(null, 1, "abc", 1, true)]      // sin SHA en el marcador: por asset
    [InlineData("abc", 1, null, 1, true)]      // sin SHA en GitHub: por asset
    [InlineData(null, 1, null, 2, false)]
    [InlineData(null, 0, null, 0, false)]      // id 0 = desconocido, nunca coincide
    [InlineData("  ", 7, "", 7, true)]         // SHA en blanco = sin SHA
    public void IsSameBuild_ComparesShaFirstThenAssetId(string? installedSha, long installedAsset, string? releaseSha,
        long releaseAsset, bool expected)
    {
        var release = TestReleases.Make("v0.0.3", releaseId: 3, assetId: releaseAsset, sha256: releaseSha);
        var info = new InstalledGameInfo { Tag = "v0.0.3", AssetId = installedAsset, Sha256 = installedSha };
        Assert.Equal(expected, InstallDialogViewModel.IsSameBuild(info, release));
    }

    [Fact]
    public void IsSameBuild_NoInstallation_IsFalse() => Assert.False(InstallDialogViewModel.IsSameBuild(null, Release));

    private static InstalledGameInfo NoSha(InstalledGameInfo info)
    {
        info.Sha256 = null;
        return info;
    }
}
