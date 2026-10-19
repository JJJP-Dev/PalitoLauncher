using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Remote;
using PalitoLauncher.Core.Versioning;

namespace PalitoLauncher.Tests.TestHelpers.M5;

/// <summary>launcher.json de ejemplo (como lo escribiría Palito) y constructores de releases/instalaciones para los tests.</summary>
internal static class ManifestSamples
{
    public const string Webhook = "https://discord.com/api/webhooks/123456789012345678/AbC-dEf_123xyz";

    /// <summary>Manifiesto completo y realista: comentarios, comas finales, mayúsculas variadas en nombres y enums.</summary>
    public const string Full = """
        {
          // Avisos que salen arriba en el launcher.
          "announcements": [
            {
              "id": "bienvenida-pre2",
              "level": "info",
              "title": "¡Bienvenidos al Prototipo 2!",
              "message": "Gracias por probar Coop2D.\nSi algo falla, usa «Reportar un problema».",
              "url": "https://discord.gg/coop2d",
              "linkText": "Abrir Discord",
              "dismissible": true,
            },
            {
              "id": "mantenimiento.servidor_1",
              "Level": "WARNING",
              "title": "Mantenimiento el sábado",
              "startsUtc": "2026-10-20T08:00:00Z",
              "endsUtc": "2026-10-25T22:00:00+02:00",
            },
            {
              "id": "parche-urgente",
              "level": "Critical",
              "title": "Actualiza ya: la 0.0.3 corrompe partidas",
              "dismissible": true, // se fuerza a false por ser crítico
            },
          ],
          "blockedBuilds": [
            { "match": "v0.0.3", "reason": "Se desconecta al entrar en la sala del jefe" },
            { "match": "2D_0.0.2_Protype3.zip", "reason": "Corrompe la partida guardada" },
          ],
          "links": [
            { "label": "Discord", "url": "https://discord.gg/coop2d", "icon": "discord" },
            { "label": "itch.io", "url": "https://palito.itch.io/coop2d", "icon": "ITCH" },
            { "label": "Web", "url": "https://palitogames.example/", "icon": "blog" },
          ],
          "playtests": [
            {
              "id": "pre2-diversion",
              "title": "Prueba de diversión del Pre 2",
              "description": "Probamos el jefe nuevo y el modo 4 jugadores.\nEntrad al canal de voz 10 min antes.",
              "startsUtc": "2026-10-24T21:00:00+02:00",
              "durationMinutes": 90,
              "requiredVersion": "0.0.4_Prototype2",
              "discordUrl": "https://discord.com/events/111/222",
            },
          ],
          "hosting": { "port": 7770, "notes": "Abre el puerto UDP 7770 en tu router o usad Radmin VPN." },
          "bugReportWebhookUrl": "https://discord.com/api/webhooks/123456789012345678/AbC-dEf_123xyz",
        }
        """;

    public static GameRelease Release(string tag, string assetName, long assetId = 0, string? name = null)
        => new(new ReleaseInfo { Id = assetId, TagName = tag, Name = name },
            new ReleaseAsset { Id = assetId, Name = assetName, Size = 1024, DownloadUrl = "https://example.invalid/" + assetName },
            GameVersion.FromRelease(assetName, tag, name));

    public static InstalledGameInfo Installed(string tag, string assetName, string? version = "auto")
    {
        var parsed = GameVersion.FromRelease(assetName, tag, null);
        return new InstalledGameInfo
        {
            Tag = tag,
            AssetName = assetName,
            Version = version == "auto" ? parsed?.ToString() : version,
            VersionDisplay = parsed?.ToDisplayString() ?? tag,
        };
    }

    public static Announcement NewAnnouncement(string id, AnnouncementLevel level = AnnouncementLevel.Info,
        DateTimeOffset? starts = null, DateTimeOffset? ends = null, bool dismissible = true)
        => new() { Id = id, Level = level, Title = "Anuncio " + id, StartsUtc = starts, EndsUtc = ends, Dismissible = dismissible };

    public static Playtest NewPlaytest(string id, DateTimeOffset starts, int durationMinutes = 120, string? requiredVersion = null)
        => new() { Id = id, Title = "Sesión " + id, StartsUtc = starts, DurationMinutes = durationMinutes, RequiredVersion = requiredVersion };
}
