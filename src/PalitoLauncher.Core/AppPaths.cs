namespace PalitoLauncher.Core;

/// <summary>Rutas de datos del launcher (todo bajo %LOCALAPPDATA%\PalitoLauncher).</summary>
public static class AppPaths
{
    private static string? _overrideRoot;

    /// <summary>Carpeta raíz de datos del launcher: %LOCALAPPDATA%\PalitoLauncher.</summary>
    public static string AppDataDir => _overrideRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PalitoLauncher");

    public static string SettingsFile => Path.Combine(AppDataDir, "settings.json");
    public static string LogsDir => Path.Combine(AppDataDir, "logs");
    public static string CacheDir => Path.Combine(AppDataDir, "cache");

    /// <summary>Carpeta "biblioteca" por defecto: %LOCALAPPDATA%\PalitoGames.</summary>
    public static string DefaultLibraryDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PalitoGames");

    /// <summary>Instalación por defecto: %LOCALAPPDATA%\PalitoGames\Coop2D.</summary>
    public static string DefaultInstallRoot => Path.Combine(DefaultLibraryDir, LauncherConfig.InstallFolderName);

    /// <summary>
    /// Carpeta donde el launcher se "instala" a sí mismo: %LOCALAPPDATA%\Programs\PalitoLauncher.
    /// Si el usuario lo ejecuta desde Descargas, se copia aquí y se relanza (ver LauncherInstallation).
    /// </summary>
    public static string LauncherInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PalitoLauncher");

    /// <summary>%LOCALAPPDATA%\Programs\PalitoLauncher\PalitoLauncher.exe</summary>
    public static string LauncherInstalledExePath => Path.Combine(LauncherInstallDir, "PalitoLauncher.exe");

    /// <summary>Crea las carpetas de datos si no existen. No lanza excepciones.</summary>
    public static void EnsureCreated()
    {
        foreach (var dir in new[] { AppDataDir, LogsDir, CacheDir })
        {
            try { Directory.CreateDirectory(dir); }
            catch { /* se reintentará al usarlas */ }
        }
    }

    /// <summary>Solo para tests: redirige todas las rutas de datos a otra carpeta.</summary>
    internal static void OverrideRootForTests(string? root) => _overrideRoot = root;
}
