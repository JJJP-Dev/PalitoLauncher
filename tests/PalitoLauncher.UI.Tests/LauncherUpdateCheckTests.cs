using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using PalitoLauncher.Core.Downloads;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Update;
using PalitoLauncher.Core.Versioning;
using PalitoLauncher.UI.Tests.TestHelpers;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.UI.Tests;

/// <summary>
/// «Buscar ahora» (Ajustes) y la comprobación de versión nueva del launcher. Sin red: el self-updater del MainViewModel se
/// sustituye por uno sobre un GitHub falso que siempre publica la v9.9.9.
/// </summary>
public sealed class LauncherUpdateCheckTests
{
    private const string LatestPath = "/repos/JJJP-Dev/PalitoLauncher/releases/latest";

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_selfUpdater")]
    private static extern ref LauncherSelfUpdater SelfUpdater(MainViewModel vm);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_lastSuccessfulFetchUtc")]
    private static extern ref DateTimeOffset? LastSuccessfulFetchUtc(MainViewModel vm);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CheckForUpdatesAsync")]
    private static extern Task CheckForUpdatesAsync(MainViewModel vm, bool includeLauncher);

    // Regresión 1.0.3: en el primer minuto tras abrir (lista del juego recién consultada) «Buscar ahora» mostraba
    // «Comprobado hace un momento» y se saltaba también la búsqueda de versión nueva del launcher.
    [Fact]
    public Task BuscarAhora_JustAfterStartup_StillChecksTheLauncher() => WithFakeGitHub(async (vm, github) =>
    {
        LastSuccessfulFetchUtc(vm) = DateTimeOffset.UtcNow; // la lista del juego se acaba de consultar (arranque)

        await CheckForUpdatesAsync(vm, includeLauncher: true);

        Assert.Equal(1, github.Count(LatestPath));
        Assert.True(vm.IsLauncherUpdateVisible);
        Assert.Contains("v9.9.9", vm.LauncherUpdateText);
        Assert.Equal(0, github.Count("/repos/JJJP-Dev/Coop2D/releases")); // la lista del juego no se repite
    });

    [Fact]
    public Task BuscarAhora_ClickedTwiceInAMinute_AsksGitHubOnlyOnce() => WithFakeGitHub(async (vm, github) =>
    {
        LastSuccessfulFetchUtc(vm) = DateTimeOffset.UtcNow;

        await CheckForUpdatesAsync(vm, includeLauncher: true);
        await CheckForUpdatesAsync(vm, includeLauncher: true);

        Assert.Equal(1, github.Count(LatestPath));
        Assert.True(vm.IsLauncherUpdateVisible);
    });

    [Fact]
    public Task ActualizarLista_InVersions_DoesNotCheckTheLauncher() => WithFakeGitHub(async (vm, github) =>
    {
        LastSuccessfulFetchUtc(vm) = DateTimeOffset.UtcNow;

        await CheckForUpdatesAsync(vm, includeLauncher: false);

        Assert.Equal(0, github.Count(LatestPath));
        Assert.False(vm.IsLauncherUpdateVisible);
    });

    private static Task WithFakeGitHub(Func<MainViewModel, FakeGitHub, Task> test) => WpfHost.RunAsync(async () =>
    {
        using var temp = new TempDir();
        using var vm = new MainViewModel();
        var github = new FakeGitHub();
        var http = new HttpClient(github);
        SelfUpdater(vm) = new LauncherSelfUpdater(new GitHubClient(http, temp.Sub("gh")), new FileDownloader(http), temp.Sub("updater"))
        {
            CurrentVersionOverride = new GameVersion(1, 0, 3),
        };
        await test(vm, github);
    });

    /// <summary>GitHub falso: /releases/latest del launcher devuelve la v9.9.9 con PalitoLauncher.exe; el resto, 404.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly List<string> _paths = new();

        public int Count(string path)
        {
            lock (_paths) return _paths.Count(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (_paths) _paths.Add(path);

            if (!string.Equals(path, LatestPath, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });

            const string json = """
                {
                  "id": 99, "tag_name": "v9.9.9", "name": "Palito Launcher v9.9.9", "draft": false, "prerelease": false,
                  "published_at": "2026-10-19T00:00:00Z", "html_url": "https://github.com/JJJP-Dev/PalitoLauncher/releases/tag/v9.9.9",
                  "body": "Notas", "assets": [ { "id": 999, "name": "PalitoLauncher.exe", "size": 1000, "state": "uploaded",
                  "browser_download_url": "https://github.com/JJJP-Dev/PalitoLauncher/releases/download/v9.9.9/PalitoLauncher.exe",
                  "content_type": "application/x-msdownload", "updated_at": "2026-10-19T00:00:00Z" } ]
                }
                """;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "55");
            return Task.FromResult(response);
        }
    }
}
