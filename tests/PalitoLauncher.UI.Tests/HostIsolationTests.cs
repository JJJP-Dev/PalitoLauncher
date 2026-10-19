using System.IO;
using System.Net.Http;
using System.Windows;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Net;
using PalitoLauncher.UI.Tests.TestHelpers;

namespace PalitoLauncher.UI.Tests;

/// <summary>
/// Garantías del entorno de tests: el arranque real del launcher nunca se ejecuta (sin ventanas, sin instancia única, sin
/// log ni ajustes), los datos van a %TEMP% y la red está cortada.
/// </summary>
public sealed class HostIsolationTests
{
    [Fact]
    public Task RealAppStartup_NeverRuns() => WpfHost.RunAsync(async () =>
    {
        await WpfHost.FlushAsync(); // si se hubiera programado un OnStartup, ya se habría ejecutado

        Assert.IsNotType<App>(Application.Current);
        Assert.NotNull(Application.Current.TryFindResource("TextBrush")); // pero con el tema real
        Assert.Null(Log.LogsDirectory);
        Assert.DoesNotContain(Application.Current.Windows.Cast<Window>(), w => w.IsVisible);
        Assert.False(File.Exists(AppPaths.SettingsFile));
        Assert.False(Directory.Exists(AppPaths.LogsDir));
    });

    [Fact]
    public void LauncherData_GoesToTemp()
    {
        _ = WpfHost.Dispatcher;
        var temp = Path.GetFullPath(Path.GetTempPath());
        Assert.StartsWith(temp, Path.GetFullPath(AppPaths.AppDataDir), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(temp, Path.GetFullPath(AppPaths.CacheDir), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(temp, Path.GetFullPath(AppPaths.SettingsFile), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Network_IsCutOff()
    {
        _ = WpfHost.Dispatcher;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => HttpFactory.Shared.GetAsync("https://api.github.com/rate_limit", timeout.Token));
    }
}
