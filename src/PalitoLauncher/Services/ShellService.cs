using System.Diagnostics;
using System.IO;
using PalitoLauncher.Core.Logging;

namespace PalitoLauncher.Services;

/// <summary>Abre carpetas y URLs con el shell de Windows. Las URLs solo se abren si son http:// o https://.</summary>
public static class ShellService
{
    /// <summary>True si es una URL absoluta http/https.</summary>
    public static bool IsWebUrl(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Abre la URL en el navegador predeterminado. Devuelve false si no es http/https o si falla. Nunca lanza.</summary>
    public static bool OpenUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Log.Warn($"Se ignoró una URL no permitida: {url}");
            return false;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo abrir la URL {uri.AbsoluteUri}.", ex);
            return false;
        }
    }

    /// <summary>Abre la carpeta en el Explorador. False si no existe o falla. Nunca lanza.</summary>
    public static bool OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (!Directory.Exists(path)) return false;
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = GetExplorerPath(),
                Arguments = Quote(path),
                UseShellExecute = false,
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo abrir la carpeta {path}.", ex);
            return false;
        }
    }

    /// <summary>Abre el Explorador con el archivo seleccionado (explorer.exe /select,"ruta"). Nunca lanza.</summary>
    public static bool RevealInExplorer(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        try
        {
            if (!File.Exists(filePath)) return OpenFolder(Path.GetDirectoryName(filePath));
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = GetExplorerPath(),
                Arguments = "/select," + Quote(filePath),
                UseShellExecute = false,
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo mostrar {filePath} en el Explorador.", ex);
            return OpenFolder(Path.GetDirectoryName(filePath));
        }
    }

    private static string GetExplorerPath()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var path = string.IsNullOrEmpty(windows) ? "" : Path.Combine(windows, "explorer.exe");
        return File.Exists(path) ? path : "explorer.exe";
    }

    private static string Quote(string path) => "\"" + path.Replace("\"", "") + "\"";
}
