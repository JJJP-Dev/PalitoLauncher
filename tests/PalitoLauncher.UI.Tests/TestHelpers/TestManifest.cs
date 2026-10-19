using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Remote;

namespace PalitoLauncher.UI.Tests.TestHelpers;

/// <summary>launcher.json de prueba (saneado como lo dejaría el ManifestProvider) y releases para los tests de UI.</summary>
internal static class TestManifest
{
    /// <summary>Reloj fijo de los tests: sábado 24/10/2026 17:00 UTC.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 24, 17, 0, 0, TimeSpan.Zero);

    public const string BossReason = "Se cierra al entrar en la sala del jefe";

    /// <summary>v0.0.3 (la última), v0.0.2 y v0.0.1, con notas.</summary>
    public static IReadOnlyList<GameRelease> Releases() => new[]
    {
        TestReleases.Make("v0.0.3", releaseId: 3, assetId: 30, sha256: "c3", published: TestReleases.BaseDate.AddDays(2),
            body: "## Tercera\n- Jefe nuevo"),
        TestReleases.Make("v0.0.2", releaseId: 2, assetId: 20, sha256: "c2", published: TestReleases.BaseDate.AddDays(1),
            body: "Segunda: arreglos varios."),
        TestReleases.Make("v0.0.1", releaseId: 1, assetId: 10, sha256: "c1", published: TestReleases.BaseDate, body: "Primera."),
    };

    /// <summary>Manifiesto saneado con lo que añada <paramref name="fill"/>.</summary>
    public static LauncherManifest Make(Action<LauncherManifest> fill)
    {
        var manifest = new LauncherManifest();
        fill(manifest);
        return ManifestProvider.Sanitize(manifest);
    }

    public static Playtest Session(TimeSpan startsIn, string? requiredVersion = null, string id = "pre2", int durationMinutes = 120) => new()
    {
        Id = id,
        Title = "Prueba del Pre 2",
        Description = "Probamos el jefe final en cooperativo.",
        StartsUtc = Now + startsIn,
        DurationMinutes = durationMinutes,
        RequiredVersion = requiredVersion,
        DiscordUrl = "https://discord.gg/coop2d",
    };

    /// <summary>Un poco de todo: anuncios de los tres niveles, builds con problemas, enlaces y una sesión dentro de 2 días.</summary>
    public static LauncherManifest Full(params string[] blockedTags) => Make(m =>
    {
        m.Announcements.Add(new Announcement
        {
            Id = "bienvenida", Level = AnnouncementLevel.Info, Title = "¡Bienvenido a las pruebas de Coop2D!",
            Message = "Entra en el Discord para quedar con el resto.", Url = "https://discord.gg/coop2d", LinkText = "Abrir Discord",
        });
        m.Announcements.Add(new Announcement { Id = "mantenimiento", Level = AnnouncementLevel.Warning, Title = "Hoy cambiamos de servidor" });
        m.Announcements.Add(new Announcement { Id = "urgente", Level = AnnouncementLevel.Critical, Title = "Guardad la partida antes de salir" });
        foreach (var tag in blockedTags) m.BlockedBuilds.Add(new BlockedBuild { Match = tag, Reason = BossReason });
        m.Links.Add(new CommunityLink { Label = "Discord", Url = "https://discord.gg/coop2d", Icon = "discord" });
        m.Links.Add(new CommunityLink { Label = "itch.io", Url = "https://palito.itch.io/coop2d", Icon = "itch" });
        m.Links.Add(new CommunityLink { Label = "Código", Url = "https://github.com/JJJP-Dev/Coop2D", Icon = "github" });
        m.Playtests.Add(Session(TimeSpan.FromDays(2), requiredVersion: "v0.0.3"));
    });
}
