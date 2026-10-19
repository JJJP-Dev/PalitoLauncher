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

    /// <summary>Copia profunda (todas las propiedades son inmutables o valores).</summary>
    public LauncherSettings Clone() => (LauncherSettings)MemberwiseClone();
}
