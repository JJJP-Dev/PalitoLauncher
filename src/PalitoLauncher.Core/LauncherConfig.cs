namespace PalitoLauncher.Core;

/// <summary>
/// Configuración fija del launcher. Para apuntar a otro juego/repositorio basta con cambiar estos valores y recompilar.
/// </summary>
public static class LauncherConfig
{
    /// <summary>Nombre visible del launcher.</summary>
    public const string LauncherName = "Palito Launcher";

    /// <summary>Nombre visible del juego.</summary>
    public const string GameDisplayName = "Coop2D";

    /// <summary>Repositorio de GitHub donde se publican las builds del juego (releases).</summary>
    public const string GameRepoOwner = "JJJP-Dev";
    public const string GameRepoName = "Coop2D";

    /// <summary>Ejecutable del juego dentro de la build (build de Unity).</summary>
    public const string GameExecutableName = "Coop2D.exe";

    /// <summary>
    /// Patrón (regex, sin distinguir mayúsculas) que identifica la build del juego entre los assets de una release.
    /// Ej.: "2D_0.0.2_Protype3.zip", "2D_0.1.0.zip". Si ningún asset coincide se usa cualquier .zip/.7z/.rar.
    /// </summary>
    public const string GameAssetPattern = @"^2D_.+\.(zip|rar|7z)$";

    /// <summary>Repositorio de GitHub donde se publica el propio launcher (auto-actualización).</summary>
    public const string LauncherRepoOwner = "JJJP-Dev";
    public const string LauncherRepoName = "PalitoLauncher";

    /// <summary>
    /// Patrón (regex, sin distinguir mayúsculas) del asset del launcher en sus releases. Debe ser exactamente
    /// "PalitoLauncher.exe" para no confundirlo con otros binarios (instaladores, builds de debug…).
    /// </summary>
    public const string LauncherAssetPattern = @"^PalitoLauncher\.exe$";

    /// <summary>Nombre del acceso directo (menú Inicio / Escritorio). Apunta siempre al launcher, nunca al exe del juego.</summary>
    public const string ShortcutName = "Coop2D";

    /// <summary>
    /// Imagen de fondo opcional servida desde el repo del juego. Si existe se descarga (con caché) y se usa como fondo.
    /// Cambiarla en GitHub cambia el fondo de todos los launchers sin publicar una versión nueva.
    /// </summary>
    public const string RemoteBackgroundUrl =
        "https://raw.githubusercontent.com/" + GameRepoOwner + "/" + GameRepoName + "/main/launcher/background.png";

    /// <summary>Nombre de la carpeta que se crea dentro de la carpeta elegida por el usuario.</summary>
    public const string InstallFolderName = "Coop2D";

    /// <summary>
    /// Cada cuánto se vuelve a comprobar si hay versiones nuevas mientras el launcher está abierto.
    /// OJO: sin token, la API de GitHub permite 60 peticiones/hora POR IP y las respuestas 304 TAMBIÉN cuentan.
    /// Varios amigos en la misma casa comparten ese cupo, así que no bajar mucho este valor.
    /// </summary>
    public static readonly TimeSpan PeriodicCheckInterval = TimeSpan.FromMinutes(60);

    /// <summary>Antes de pulsar JUGAR, si la última consulta tiene más de esto, se comprueba rápido si hay versión nueva.</summary>
    public static readonly TimeSpan PrePlayCheckMaxAge = TimeSpan.FromMinutes(10);

    /// <summary>Respuestas de la API más recientes que esto se reutilizan sin volver a preguntar (GitHub manda max-age=60).</summary>
    public static readonly TimeSpan ApiFreshnessWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Cada cuánto, como mucho, se consulta automáticamente si hay versión nueva del launcher (al arrancar y en la comprobación
    /// periódica). "Buscar ahora" en Ajustes consulta siempre. Cuesta 1 petición a la API por consulta.
    /// </summary>
    public static readonly TimeSpan LauncherUpdateCheckInterval = TimeSpan.FromHours(2);

    /// <summary>Si quedan estas peticiones o menos, las comprobaciones automáticas se saltan (se reservan para acciones del usuario).</summary>
    public const int RateLimitReserveForUser = 10;

    public static string GameRepoUrl => $"https://github.com/{GameRepoOwner}/{GameRepoName}";
    public static string GameReleasesPageUrl => $"{GameRepoUrl}/releases";
    public static string LauncherRepoUrl => $"https://github.com/{LauncherRepoOwner}/{LauncherRepoName}";
    public static string LauncherReleasesPageUrl => $"{LauncherRepoUrl}/releases";
}
