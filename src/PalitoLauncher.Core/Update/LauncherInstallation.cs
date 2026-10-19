using System.Diagnostics;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Core.Versioning;

namespace PalitoLauncher.Core.Update;

public enum LauncherInstallOutcome
{
    /// <summary>Seguir arrancando este proceso con normalidad.</summary>
    Continue,

    /// <summary>Se arrancó la copia instalada; este proceso debe cerrarse ya (sin mostrar ventana).</summary>
    RelaunchedInstalledCopy,
}

/// <summary>Decisión pura de <see cref="LauncherInstallation.EnsureInstalled"/> (separada para tests).</summary>
internal enum LauncherInstallDecision
{
    /// <summary>Arrancar en el sitio sin tocar nada.</summary>
    Continue,

    /// <summary>Ya somos la copia instalada: arrancar y asegurar el acceso del menú Inicio.</summary>
    ContinueAsInstalled,

    /// <summary>La copia instalada es igual o más nueva: arrancarla tal cual.</summary>
    LaunchInstalled,

    /// <summary>No hay copia instalada o es más vieja: copiar este exe allí y arrancarlo.</summary>
    CopyAndLaunch,
}

/// <summary>
/// El exe descargado actúa como "instalador portátil": si se ejecuta desde otro sitio (Descargas, Escritorio…), se copia a
/// AppPaths.LauncherInstalledExePath y se relanza desde ahí. Así los accesos directos y la auto-actualización usan siempre
/// una ruta estable y escribible, y aparece en el menú Inicio.
/// </summary>
public static class LauncherInstallation
{
    public const string PortableArgument = "--portable";

    /// <summary>
    /// Variable de entorno que se pasa a la copia relanzada para evitar bucles de relanzamiento (p.ej. si la ruta instalada
    /// llega con otra forma por una unión/enlace de carpetas).
    /// </summary>
    internal const string RelaunchGuardVariable = "PALITO_LAUNCHER_RELAUNCHED";

    private const string ShortcutDescription = "Palito Launcher: instala, actualiza y abre " + LauncherConfig.GameDisplayName;

    /// <summary>
    /// Llamar al principio de App.OnStartup, ANTES de tomar el mutex de instancia única. Nunca lanza (ante cualquier error
    /// registra y devuelve Continue para arrancar en el sitio).
    /// - Devuelve Continue si: args contiene --portable o LauncherSelfUpdater.PostUpdateArgument; ProcessPath es null o
    ///   "dotnet.exe"; o ProcessPath ya es la ruta instalada (en ese caso, además, crea el acceso directo del menú Inicio si
    ///   falta).
    /// - Si no: si la copia instalada existe y su versión (FileVersionInfo.ProductVersion → GameVersion) es ≥ la actual,
    ///   simplemente la arranca. Si no existe o es más vieja, copia el exe actual allí (a "&lt;destino&gt;.tmp" + MoveFileWithRetry
    ///   con overwrite); si el destino está bloqueado (esa copia está abierta), arranca la copia existente igualmente.
    ///   Crea/actualiza el acceso directo LauncherConfig.ShortcutName en el menú Inicio (Programs) apuntando a la copia.
    ///   Arranca la copia instalada pasándole los mismos argumentos (UseShellExecute = false, WorkingDirectory = su carpeta)
    ///   y devuelve RelaunchedInstalledCopy.
    /// </summary>
    public static LauncherInstallOutcome EnsureInstalled(string[] args)
    {
        try
        {
            args ??= Array.Empty<string>();

            // Somos una copia recién relanzada: nunca volver a relanzar.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RelaunchGuardVariable)))
            {
                Environment.SetEnvironmentVariable(RelaunchGuardVariable, null);
                Log.Info("Arranque desde una copia relanzada por el propio launcher.");
                EnsureStartMenuShortcutIfMissing();
                return LauncherInstallOutcome.Continue;
            }

            var processPath = Environment.ProcessPath;
            var installedPath = AppPaths.LauncherInstalledExePath;

            // Builds de desarrollo o hosts ajenos (testhost, exe + dll): nunca se copian.
            if (processPath != null && !LauncherSelfUpdater.IsDotnetHost(processPath) && !IsSamePath(processPath, installedPath) &&
                !LauncherSelfUpdater.IsPublishedLauncherExe(processPath))
            {
                Log.Info($"Ejecución de desarrollo ({processPath}): no se instala la copia del launcher.");
                return LauncherInstallOutcome.Continue;
            }

            var installedVersion = File.Exists(installedPath) ? ReadFileVersion(installedPath) : null;
            var decision = Decide(processPath, installedPath, LauncherSelfUpdater.CurrentVersion, installedVersion,
                File.Exists(installedPath), args);
            Log.Info($"Instalación del launcher: {decision} (actual {LauncherSelfUpdater.CurrentVersionText}, " +
                     $"instalada {installedVersion?.ToString() ?? "ninguna"}, ruta {processPath}).");

            switch (decision)
            {
                case LauncherInstallDecision.Continue:
                    return LauncherInstallOutcome.Continue;

                case LauncherInstallDecision.ContinueAsInstalled:
                    EnsureStartMenuShortcutIfMissing();
                    return LauncherInstallOutcome.Continue;

                case LauncherInstallDecision.LaunchInstalled:
                    // La copia instalada asegura su acceso del menú Inicio al arrancar.
                    return StartInstalledCopy(installedPath, args)
                        ? LauncherInstallOutcome.RelaunchedInstalledCopy
                        : LauncherInstallOutcome.Continue;

                case LauncherInstallDecision.CopyAndLaunch:
                    if (!TryCopyTo(processPath!, installedPath) && !File.Exists(installedPath))
                        return LauncherInstallOutcome.Continue;

                    EnsureStartMenuShortcut();
                    return StartInstalledCopy(installedPath, args)
                        ? LauncherInstallOutcome.RelaunchedInstalledCopy
                        : LauncherInstallOutcome.Continue;

                default:
                    return LauncherInstallOutcome.Continue;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Error preparando la instalación del launcher; se arranca en el sitio.", ex);
            return LauncherInstallOutcome.Continue;
        }
    }

    /// <summary>Ruta del exe a la que deben apuntar los accesos directos: la copia instalada si existe; si no, el exe actual.</summary>
    public static string? GetPreferredLauncherExePath()
    {
        try
        {
            var installed = AppPaths.LauncherInstalledExePath;
            if (File.Exists(installed))
                return installed;

            var current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current) || LauncherSelfUpdater.IsDotnetHost(current))
                return null;
            return current;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo determinar la ruta del launcher para los accesos directos.", ex);
            return null;
        }
    }

    /// <summary>
    /// Crea o reemplaza "&lt;Escritorio&gt;\&lt;LauncherConfig.ShortcutName&gt;.lnk" apuntando a GetPreferredLauncherExePath().
    /// LauncherException si no se puede.
    /// </summary>
    public static void CreateDesktopShortcut()
    {
        var target = GetPreferredLauncherExePath()
                     ?? throw new LauncherException("No se pudo crear el acceso directo: no se encuentra el ejecutable del launcher.");

        string desktop;
        try
        {
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop))
                desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo obtener la carpeta del Escritorio.", ex);
            desktop = "";
        }

        if (string.IsNullOrEmpty(desktop))
            throw new LauncherException("No se pudo crear el acceso directo: no se encuentra la carpeta del Escritorio.");

        ShortcutUtil.CreateShortcut(GetShortcutPath(desktop), target, null, ShortcutDescription);
    }

    /// <summary>Crea o reemplaza el acceso directo en el menú Inicio (carpeta Programs del usuario). Nunca lanza.</summary>
    public static void EnsureStartMenuShortcut()
    {
        try
        {
            var target = GetPreferredLauncherExePath();
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(programs))
            {
                Log.Warn("No se crea el acceso del menú Inicio (sin ruta del launcher o del menú).");
                return;
            }

            ShortcutUtil.CreateShortcut(GetShortcutPath(programs), target, null, ShortcutDescription);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo crear el acceso directo del menú Inicio.", ex);
        }
    }

    // ---------------------------------------------------------------- internos

    /// <summary>
    /// Decisión pura de EnsureInstalled. <paramref name="installedVersion"/> null con <paramref name="installedExists"/> true
    /// = copia instalada con versión ilegible (se trata como más vieja y se reemplaza).
    /// </summary>
    internal static LauncherInstallDecision Decide(string? processPath, string installedPath, GameVersion current,
        GameVersion? installedVersion, bool installedExists, IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, PortableArgument, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, LauncherSelfUpdater.PostUpdateArgument, StringComparison.OrdinalIgnoreCase))
                return LauncherInstallDecision.Continue;
        }

        if (string.IsNullOrWhiteSpace(processPath) || LauncherSelfUpdater.IsDotnetHost(processPath))
            return LauncherInstallDecision.Continue;

        if (IsSamePath(processPath, installedPath))
            return LauncherInstallDecision.ContinueAsInstalled;

        if (installedExists && installedVersion != null && installedVersion >= current)
            return LauncherInstallDecision.LaunchInstalled;

        return LauncherInstallDecision.CopyAndLaunch;
    }

    internal static string GetShortcutPath(string folder) => Path.Combine(folder, LauncherConfig.ShortcutName + ".lnk");

    /// <summary>Versión de un exe (ProductVersion sin "+metadata" → GameVersion). Null si no se puede leer.</summary>
    internal static GameVersion? ReadFileVersion(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var text = info.ProductVersion;
            if (string.IsNullOrWhiteSpace(text))
                text = info.FileVersion;
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var plus = text.IndexOf('+');
            if (plus >= 0)
                text = text[..plus];
            return GameVersion.TryParse(text, out var v) ? v : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo leer la versión de {exePath}.", ex);
            return null;
        }
    }

    /// <summary>
    /// Copia <paramref name="source"/> a "&lt;dest&gt;.tmp" (por streams: no arrastra la marca "descargado de Internet") y la
    /// mueve sobre <paramref name="dest"/>. False si falla (p.ej. destino bloqueado porque esa copia está abierta).
    /// </summary>
    internal static bool TryCopyTo(string source, string dest, int moveAttempts = 3, int moveDelayMs = 300)
    {
        var tmp = dest + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output, 81920);
                output.Flush(true);
            }

            FileSystemUtil.MoveFileWithRetry(tmp, dest, overwrite: true, moveAttempts, moveDelayMs);
            Log.Info($"Launcher copiado a {dest}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo copiar el launcher a {dest}.", ex);
            FileSystemUtil.TryDeleteFile(tmp);
            return false;
        }
    }

    private static bool StartInstalledCopy(string installedPath, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(installedPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(installedPath) ?? "",
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            psi.Environment[RelaunchGuardVariable] = "1";

            using var process = Process.Start(psi);
            if (process == null)
            {
                Log.Warn($"No se pudo arrancar la copia instalada {installedPath}.");
                return false;
            }

            Log.Info($"Copia instalada arrancada: {installedPath} (pid {process.Id}).");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo arrancar la copia instalada {installedPath}; se arranca en el sitio.", ex);
            return false;
        }
    }

    private static void EnsureStartMenuShortcutIfMissing()
    {
        try
        {
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (!string.IsNullOrEmpty(programs) && File.Exists(GetShortcutPath(programs)))
                return;
        }
        catch (Exception ex)
        {
            Log.Debug($"No se pudo comprobar el acceso del menú Inicio: {ex.Message}");
        }

        EnsureStartMenuShortcut();
    }

    private static bool IsSamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;

        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
