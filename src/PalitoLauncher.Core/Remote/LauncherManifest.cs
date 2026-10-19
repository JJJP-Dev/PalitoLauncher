namespace PalitoLauncher.Core.Remote;

// Modelos de launcher/launcher.json (repo del juego). En JSON los nombres van en camelCase y los enums como texto.
// Todo es opcional: un archivo vacío "{}" es válido y equivale a "no hay nada que mostrar".

public enum AnnouncementLevel
{
    /// <summary>Información (azul).</summary>
    Info,

    /// <summary>Aviso (ámbar).</summary>
    Warning,

    /// <summary>Crítico (rojo). No se puede descartar.</summary>
    Critical,
}

/// <summary>Anuncio que se muestra arriba del contenido del launcher.</summary>
public sealed class Announcement
{
    /// <summary>Identificador estable (letras, números, '-', '_', '.'; máx. 64). Sirve para recordar si el usuario lo descartó.</summary>
    public string Id { get; set; } = "";

    public AnnouncementLevel Level { get; set; } = AnnouncementLevel.Info;

    /// <summary>Título corto (máx. 80).</summary>
    public string Title { get; set; } = "";

    /// <summary>Texto plano opcional (máx. 400).</summary>
    public string? Message { get; set; }

    /// <summary>Enlace opcional (solo https).</summary>
    public string? Url { get; set; }

    /// <summary>Texto del enlace (máx. 40; por defecto «Más información»).</summary>
    public string? LinkText { get; set; }

    /// <summary>Desde cuándo se muestra (UTC, opcional).</summary>
    public DateTimeOffset? StartsUtc { get; set; }

    /// <summary>Hasta cuándo se muestra (UTC, opcional).</summary>
    public DateTimeOffset? EndsUtc { get; set; }

    /// <summary>Si el usuario puede cerrarlo. Los Critical nunca se pueden cerrar (se fuerza a false al sanear).</summary>
    public bool Dismissible { get; set; } = true;
}

/// <summary>Build del juego con un problema grave: se avisa a quien la tenga instalada y se marca en Versiones.</summary>
public sealed class BlockedBuild
{
    /// <summary>
    /// Qué build: el tag de la release ("v0.0.4"), el nombre exacto del asset ("2D_0.0.4_Prototype2.zip") o la versión en
    /// forma canónica ("0.0.4-prototype.2"). Sin distinguir mayúsculas. Máx. 120.
    /// </summary>
    public string Match { get; set; } = "";

    /// <summary>Motivo para el jugador (máx. 300), p.ej. «Se desconecta al entrar en la sala del jefe».</summary>
    public string Reason { get; set; } = "";
}

/// <summary>Enlace de comunidad (Discord, itch.io, Steam…).</summary>
public sealed class CommunityLink
{
    /// <summary>Texto del botón (máx. 30).</summary>
    public string Label { get; set; } = "";

    /// <summary>Solo https.</summary>
    public string Url { get; set; } = "";

    /// <summary>Icono: "discord", "itch", "steam", "youtube", "github" o "web" (por defecto).</summary>
    public string Icon { get; set; } = "web";
}

/// <summary>Sesión de prueba planificada.</summary>
public sealed class Playtest
{
    /// <summary>Identificador estable (como Announcement.Id). Sirve para no avisar dos veces.</summary>
    public string Id { get; set; } = "";

    /// <summary>Título (máx. 80), p.ej. «Prueba de diversión del Pre 2».</summary>
    public string Title { get; set; } = "";

    /// <summary>Texto plano opcional (máx. 500): qué se va a probar, cómo conectarse…</summary>
    public string? Description { get; set; }

    /// <summary>Inicio en UTC (obligatorio). En el JSON con zona: "2026-10-24T19:00:00Z" o "2026-10-24T21:00:00+02:00".</summary>
    public DateTimeOffset StartsUtc { get; set; }

    /// <summary>Duración prevista (15..720 minutos, por defecto 120).</summary>
    public int DurationMinutes { get; set; } = 120;

    /// <summary>Build que hay que tener (mismo formato que BlockedBuild.Match). Opcional.</summary>
    public string? RequiredVersion { get; set; }

    /// <summary>Enlace al canal/evento de Discord (solo https). Opcional.</summary>
    public string? DiscordUrl { get; set; }
}

/// <summary>Datos para hostear partidas (antes de Steam).</summary>
public sealed class HostingInfo
{
    /// <summary>Puerto que usa el juego para hostear (1..65535), si se quiere mostrar.</summary>
    public int? Port { get; set; }

    /// <summary>Indicaciones cortas (máx. 300), p.ej. «Abre el puerto UDP 7770 en tu router o usa Radmin VPN».</summary>
    public string? Notes { get; set; }
}

/// <summary>Contenido de launcher/launcher.json.</summary>
public sealed class LauncherManifest
{
    /// <summary>Máx. 10.</summary>
    public List<Announcement> Announcements { get; set; } = new();

    /// <summary>Máx. 50.</summary>
    public List<BlockedBuild> BlockedBuilds { get; set; } = new();

    /// <summary>Máx. 6.</summary>
    public List<CommunityLink> Links { get; set; } = new();

    /// <summary>Máx. 10.</summary>
    public List<Playtest> Playtests { get; set; } = new();

    public HostingInfo? Hosting { get; set; }

    /// <summary>
    /// Webhook de Discord para «Reportar un problema» (canal #bugs). Si falta o no es válido se usa el webhook de
    /// launcher/feedback.json; si tampoco hay, los reportes se guardan en la bandeja de salida.
    /// </summary>
    public string? BugReportWebhookUrl { get; set; }
}

/// <summary>La próxima sesión de prueba (en curso o futura) ya calculada.</summary>
public sealed record PlaytestStatus(Playtest Playtest, bool IsOngoing, TimeSpan UntilStart, DateTimeOffset EndsUtc);
