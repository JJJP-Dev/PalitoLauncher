using System.Reflection;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Update;

namespace PalitoLauncher.Helpers;

/// <summary>Datos del propio launcher para la UI.</summary>
public static class AppInfo
{
    private static string? _versionText;

    /// <summary>Versión en ejecución ("1.0.0").</summary>
    public static string VersionText => _versionText ??= ComputeVersionText();

    private static string ComputeVersionText()
    {
        try
        {
            var text = LauncherSelfUpdater.CurrentVersionText;
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo leer la versión del launcher desde el Core.", ex);
        }

        try
        {
            var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo leer la versión del ensamblado.", ex);
        }
        return "1.0.0";
    }
}
