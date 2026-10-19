namespace PalitoLauncher.Tests.TestHelpers.B5;

/// <summary>Carpeta temporal única para un test; se borra (best effort) al liberar.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "palito-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    /// <summary>Crea un archivo de texto UTF-8 (y sus carpetas) y devuelve su ruta.</summary>
    public string WriteFile(string relativePath, string contents)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    /// <summary>Crea un archivo binario (y sus carpetas) y devuelve su ruta.</summary>
    public string WriteBytes(string relativePath, byte[] contents)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, contents);
        return full;
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(Path))
                    return;

                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
