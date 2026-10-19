using System.IO;

namespace PalitoLauncher.UI.Tests.TestHelpers;

/// <summary>Carpeta temporal única que se borra (best effort) al terminar el test.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "palito-ui-tests", "dirs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Subcarpeta (creada) dentro de la carpeta temporal.</summary>
    public string Sub(string name)
    {
        var dir = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
