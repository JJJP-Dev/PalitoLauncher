using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using PalitoLauncher.Core.Logging;

namespace PalitoLauncher.Core.Util;

/// <summary>
/// Creación de accesos directos .lnk mediante COM (IShellLinkW + IPersistFile, CLSID_ShellLink
/// {00021401-0000-0000-C000-000000000046}), sin paquetes externos.
/// </summary>
public static class ShortcutUtil
{
    // Límites de IShellLink (INFOTIPSIZE para la descripción; los argumentos admiten algo más que MAX_PATH).
    private const int MaxDescriptionLength = 1023;
    private const int MaxArgumentsLength = 1023;

    /// <summary>
    /// Crea o sobrescribe <paramref name="shortcutPath"/> (.lnk). WorkingDirectory = carpeta del destino; icono = el propio
    /// exe (índice 0) salvo que se indique otro. Crea la carpeta si no existe. Lanza LauncherException si falla.
    /// </summary>
    public static void CreateShortcut(string shortcutPath, string targetPath, string? arguments = null,
        string? description = null, string? iconPath = null)
    {
        const string failMessage = "No se pudo crear el acceso directo.";

        if (string.IsNullOrWhiteSpace(shortcutPath) || string.IsNullOrWhiteSpace(targetPath))
            throw new LauncherException(failMessage);

        string fullShortcut;
        string fullTarget;
        try
        {
            fullShortcut = Path.GetFullPath(shortcutPath);
            fullTarget = Path.GetFullPath(targetPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"Ruta de acceso directo inválida ({shortcutPath} → {targetPath}).", ex);
            throw new LauncherException(failMessage, ex);
        }

        var folder = Path.GetDirectoryName(fullShortcut);
        try
        {
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo crear la carpeta del acceso directo: {folder}", ex);
            throw new LauncherException($"{failMessage} No se puede escribir en {folder}.", ex);
        }

        object? link = null;
        try
        {
            link = new ShellLinkCoClass();
            var shellLink = (IShellLinkW)link;
            shellLink.SetPath(fullTarget);
            shellLink.SetWorkingDirectory(Path.GetDirectoryName(fullTarget) ?? "");
            shellLink.SetArguments(Truncate(arguments ?? "", MaxArgumentsLength));
            if (!string.IsNullOrEmpty(description))
                shellLink.SetDescription(Truncate(description, MaxDescriptionLength));
            shellLink.SetIconLocation(string.IsNullOrWhiteSpace(iconPath) ? fullTarget : iconPath, 0);

            // Si había uno de solo lectura, se quita el atributo para poder sobrescribirlo.
            if (File.Exists(fullShortcut))
            {
                var attrs = File.GetAttributes(fullShortcut);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(fullShortcut, attrs & ~FileAttributes.ReadOnly);
            }

            ((IPersistFile)link).Save(fullShortcut, true);
            Log.Info($"Acceso directo creado: {fullShortcut} → {fullTarget}");
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo crear el acceso directo {fullShortcut} → {fullTarget}.", ex);
            throw new LauncherException(failMessage, ex);
        }
        finally
        {
            if (link != null && Marshal.IsComObject(link))
            {
                try
                {
                    Marshal.FinalReleaseComObject(link);
                }
                catch (Exception ex)
                {
                    Log.Debug($"Error liberando IShellLink: {ex.Message}");
                }
            }
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    // ---------------------------------------------------------------- COM

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
