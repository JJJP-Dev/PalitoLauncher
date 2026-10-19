namespace PalitoLauncher.Core.Settings;

/// <summary>Qué hace el launcher después de abrir el juego.</summary>
public enum AfterLaunchBehavior
{
    KeepOpen,
    Minimize,
    Close,
}

/// <summary>Ajustes persistidos en %LOCALAPPDATA%\PalitoLauncher\settings.json (System.Text.Json, enums como texto).</summary>
public sealed class LauncherSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Carpeta de instalación (InstallRoot). Null = aún no elegida (se usa AppPaths.DefaultInstallRoot si ahí hay juego).</summary>
    public string? InstallRoot { get; set; }

    /// <summary>Incluir releases marcadas como "pre-release" en GitHub (canal de pruebas).</summary>
    public bool IncludePrereleases { get; set; }

    /// <summary>Descargar e instalar automáticamente las actualizaciones del juego al detectarlas.</summary>
    public bool AutoDownloadUpdates { get; set; }

    /// <summary>
    /// Si el usuario instaló a propósito una versión que no es la última (desde "Versiones"), aquí se guarda el tag de la
    /// última de ese momento para que la auto-actualización no la reemplace. Se borra cuando aparece una versión aún más nueva.
    /// </summary>
    public string? AutoUpdatePausedForTag { get; set; }

    /// <summary>Comprobar si hay versión nueva del propio launcher.</summary>
    public bool CheckLauncherUpdates { get; set; } = true;

    public AfterLaunchBehavior AfterLaunch { get; set; } = AfterLaunchBehavior.Minimize;

    /// <summary>Argumentos extra para el juego (p.ej. "-screen-fullscreen 0 -screen-width 1280 -screen-height 720").</summary>
    public string LaunchArguments { get; set; } = "";

    /// <summary>Límite de descarga en KB/s (0 = sin límite).</summary>
    public int MaxDownloadSpeedKBps { get; set; }

    public bool KeepDownloadedArchives { get; set; }

    /// <summary>Último tag de release cuyas notas vio el usuario (para marcar "Nuevo").</summary>
    public string? LastSeenReleaseTag { get; set; }

    /// <summary>Versión del launcher que el usuario decidió omitir.</summary>
    public string? SkippedLauncherVersion { get; set; }

    /// <summary>Pedir opinión (encuesta) al cerrar el juego tras una partida suficientemente larga.</summary>
    public bool AskFeedbackAfterSession { get; set; } = true;

    /// <summary>Nombre o apodo opcional que se adjunta al feedback (se recuerda para la próxima vez).</summary>
    public string? FeedbackNickname { get; set; }

    /// <summary>Última vez (UTC) que se mostró la invitación a dar opinión tras una partida (para no preguntar demasiado).</summary>
    public DateTimeOffset? LastFeedbackPromptUtc { get; set; }

    /// <summary>Ids de anuncios (launcher.json) que el usuario cerró. Máx. 200 (se quitan los más viejos).</summary>
    public List<string> DismissedAnnouncementIds { get; set; } = new();

    /// <summary>Ids de sesiones de prueba de las que ya se avisó. Máx. 200.</summary>
    public List<string> RemindedPlaytestIds { get; set; } = new();

    /// <summary>Avisar (notificación de Windows) unos minutos antes de una sesión de prueba si el launcher está abierto.</summary>
    public bool PlaytestRemindersEnabled { get; set; } = true;

    /// <summary>Perfil de lanzamiento del juego (ventana, pantalla completa…). Se combina con LaunchArguments.</summary>
    public Launch.LaunchProfile LaunchProfile { get; set; } = Launch.LaunchProfile.Default;

    /// <summary>Muestra las herramientas de desarrollo (abrir varias instancias del juego, etc.).</summary>
    public bool DeveloperMode { get; set; }

    /// <summary>Copia profunda (todas las propiedades son inmutables o valores).</summary>
    public LauncherSettings Clone()
    {
        var copy = (LauncherSettings)MemberwiseClone();
        // Las listas se copian para que la copia no comparta estado con el original.
        copy.DismissedAnnouncementIds = new List<string>(DismissedAnnouncementIds ?? new List<string>());
        copy.RemindedPlaytestIds = new List<string>(RemindedPlaytestIds ?? new List<string>());
        return copy;
    }
}
